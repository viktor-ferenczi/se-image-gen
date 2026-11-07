#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.12"
# dependencies = ["Pillow>=10"]
# ///
"""HTTP service that generates images for the LcdImageGen plugins.

    lcd_imagegen_service.py                  # MOSS-Image-SFT through image-gen on cuda:1
    lcd_imagegen_service.py --stub 3         # test pattern after 3 seconds, no GPU

POST /generate with {"prompt": "...", "columns": C, "rows": R} answers C x R x 3 bytes of RGB:
the image generated at the grid's aspect ratio and scaled down to one pixel per character.
Errors answer {"error": "..."}. GET /health answers {"status": "ok", "busy": bool, "queued": n}.

One job runs at a time, the others wait in a queue. Each job runs image-gen as a separate process,
which loads the model, generates and exits, so the GPU is free between jobs. See README.md.
"""

import argparse
import hashlib
import json
import logging
import math
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from PIL import Image, ImageDraw

log = logging.getLogger("lcd-imagegen")

MAX_PROMPT_LENGTH = 4000
MAX_CELLS = 100_000


class ServiceError(Exception):
    def __init__(self, status: HTTPStatus, message: str):
        super().__init__(message)
        self.status = status


def image_size(columns: int, rows: int, pixels: int) -> tuple[int, int]:
    """Width and height in multiples of 16 with about `pixels` pixels and the grid's aspect ratio."""
    width = math.sqrt(pixels * columns / rows)
    height = pixels / width
    return max(64, round(width / 16) * 16), max(64, round(height / 16) * 16)


def parse_request(body: bytes) -> tuple[str, int, int]:
    try:
        request = json.loads(body)
        prompt, columns, rows = request["prompt"], request["columns"], request["rows"]
    except (ValueError, KeyError, TypeError) as error:
        raise ServiceError(HTTPStatus.BAD_REQUEST, f"bad request: {error}")
    if not isinstance(prompt, str) or not prompt.strip():
        raise ServiceError(HTTPStatus.BAD_REQUEST, "prompt must be a non-empty string")
    if len(prompt) > MAX_PROMPT_LENGTH:
        raise ServiceError(HTTPStatus.BAD_REQUEST, f"prompt is longer than {MAX_PROMPT_LENGTH} characters")
    if not all(isinstance(n, int) and n > 0 for n in (columns, rows)) or columns * rows > MAX_CELLS:
        raise ServiceError(HTTPStatus.BAD_REQUEST, f"columns and rows must be positive, at most {MAX_CELLS} cells")
    return prompt.strip(), columns, rows


def stub_image(prompt: str, width: int, height: int) -> Image.Image:
    """Deterministic test pattern: color bars, a gradient and a frame, tinted by the prompt."""
    tint = hashlib.sha256(prompt.encode()).digest()[:3]
    image = Image.new("RGB", (width, height))
    pixels = image.load()
    for y in range(height):
        for x in range(width):
            pixels[x, y] = (x * 255 // width, y * 255 // height, tint[2])
    draw = ImageDraw.Draw(image)
    bars = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 255, 255), tuple(tint)]
    for i, color in enumerate(bars):
        draw.rectangle([i * width // len(bars), 0, (i + 1) * width // len(bars) - 1, height // 6], fill=color)
    draw.rectangle([0, 0, width - 1, height - 1], outline=(255, 255, 255), width=max(1, width // 64))
    return image


def free_gpu_gib(device: str) -> float | None:
    """Free memory of a cuda:N device in GiB, None if nvidia-smi can't tell."""
    if not device.startswith("cuda:") or not shutil.which("nvidia-smi"):
        return None
    result = subprocess.run(
        ["nvidia-smi", "--query-gpu=memory.free", "--format=csv,noheader,nounits", "-i", device[5:]],
        capture_output=True,
        text=True,
    )
    return int(result.stdout.strip()) / 1024 if result.returncode == 0 else None


class Generator:
    def __init__(self, args: argparse.Namespace):
        self.args = args
        self.lock = threading.Lock()
        self.queued = 0
        self.queued_lock = threading.Lock()

    @property
    def busy(self) -> bool:
        return self.lock.locked()

    def generate(self, prompt: str, columns: int, rows: int) -> bytes:
        with self.queued_lock:
            if self.queued >= self.args.max_queue:
                raise ServiceError(HTTPStatus.SERVICE_UNAVAILABLE, f"busy: {self.queued} requests are waiting already")
            self.queued += 1
        with self.lock:
            with self.queued_lock:
                self.queued -= 1
            return self._generate(prompt, columns, rows)

    def _generate(self, prompt: str, columns: int, rows: int) -> bytes:
        width, height = image_size(columns, rows, self.args.pixels)
        started = time.monotonic()
        if self.args.stub is not None:
            time.sleep(self.args.stub)
            image = stub_image(prompt, width, height)
        else:
            image = self._run_model(prompt, width, height)
        log.info("generated %dx%d for %dx%d in %.1f s: %s", width, height, columns, rows, time.monotonic() - started, prompt)
        if self.args.keep:
            self.args.keep.mkdir(parents=True, exist_ok=True)
            image.save(self.args.keep / f"{time.strftime('%Y%m%d-%H%M%S')}-{columns}x{rows}.png")
        return image.convert("RGB").resize((columns, rows), Image.Resampling.LANCZOS).tobytes()

    def _run_model(self, prompt: str, width: int, height: int) -> Image.Image:
        free = free_gpu_gib(self.args.device)
        if free is not None and free < self.args.min_free_gib:
            raise ServiceError(
                HTTPStatus.SERVICE_UNAVAILABLE,
                f"GPU {self.args.device} is busy: {free:.1f} GiB free, {self.args.min_free_gib:g} GiB needed",
            )
        job = {"prompt": prompt, "negative": self.args.negative, "width": width, "height": height, "output": "image.png"}
        with tempfile.TemporaryDirectory(prefix="lcd-imagegen-") as folder:
            command = [
                *self.args.image_gen.split(),
                "--json", "-",
                "-o", folder,
                "--steps", str(self.args.steps),
                "--cfg", str(self.args.cfg),
                "--device", self.args.device,
            ]  # fmt: skip
            # The prompt goes in through stdin, so a prompt starting with "-" is not taken for an option
            result = subprocess.run(command, input=json.dumps([job]), capture_output=True, text=True)
            if result.returncode != 0:
                log.error("image-gen failed with exit code %d:\n%s", result.returncode, result.stderr[-4000:])
                if "OutOfMemoryError" in result.stderr or "out of memory" in result.stderr:
                    raise ServiceError(HTTPStatus.SERVICE_UNAVAILABLE, f"GPU {self.args.device} ran out of memory, is it busy?")
                raise ServiceError(HTTPStatus.INTERNAL_SERVER_ERROR, f"image generation failed (exit code {result.returncode})")
            with Image.open(Path(folder) / "image.png") as image:
                return image.convert("RGB")


def make_handler(generator: Generator):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format, *args):
            log.debug(format, *args)

        def send(self, status: HTTPStatus, body: bytes, content_type: str):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def send_json(self, status: HTTPStatus, value: dict):
            self.send(status, json.dumps(value).encode(), "application/json")

        def do_GET(self):
            if self.path == "/health":
                self.send_json(HTTPStatus.OK, {"status": "ok", "busy": generator.busy, "queued": generator.queued})
            else:
                self.send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})

        def do_POST(self):
            if self.path != "/generate":
                self.send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})
                return
            try:
                length = int(self.headers.get("Content-Length", 0))
                prompt, columns, rows = parse_request(self.rfile.read(length))
                self.send(HTTPStatus.OK, generator.generate(prompt, columns, rows), "application/octet-stream")
            except ServiceError as error:
                log.info("%s: %s", error.status.phrase, error)
                self.send_json(error.status, {"error": str(error)})
            except Exception as error:
                log.exception("generation failed")
                self.send_json(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": f"internal error: {error}"})

    return Handler


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1", help="listen address")
    parser.add_argument("--port", type=int, default=8183)
    parser.add_argument("--image-gen", default=str(Path.home() / "bin" / "image-gen"), help="image-gen command")
    parser.add_argument("--device", default="cuda:1", help="GPU the model runs on")
    parser.add_argument("--min-free-gib", type=float, default=17, help="free VRAM needed before a job starts")
    parser.add_argument("--pixels", type=int, default=512 * 512, help="pixels of a generated image")
    parser.add_argument("--steps", type=int, default=8, help="sampling steps")
    parser.add_argument("--cfg", type=float, default=3.0, help="guidance scale")
    parser.add_argument("--negative", default="", help="negative prompt")
    parser.add_argument("--max-queue", type=int, default=8, help="requests waiting for the running job")
    parser.add_argument("--stub", type=float, metavar="SECONDS", help="no model: answer a test pattern after SECONDS")
    parser.add_argument("--keep", type=Path, metavar="DIR", help="save each generated image into DIR")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        stream=sys.stderr,
    )
    server = ThreadingHTTPServer((args.host, args.port), make_handler(Generator(args)))
    mode = f"stub, {args.stub:g} s" if args.stub is not None else f"{args.image_gen} on {args.device}"
    log.info("listening on http://%s:%d (%s)", args.host, args.port, mode)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()

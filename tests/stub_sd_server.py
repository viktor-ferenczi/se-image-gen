#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.12"
# dependencies = ["Pillow>=10"]
# ///
"""Stand-in for a stable-diffusion.cpp sd-server, for testing the plugins without a GPU.

    tests/stub_sd_server.py --port 1234 --seconds 4

Answers POST /sdapi/v1/txt2img like sd-server does, with a test pattern after SECONDS,
one request at a time. Logs "generated WxH in S s: prompt" per image and "in flight N" per
request, N counting the ones it has received and not answered yet, this one included.
"""

import argparse
import base64
import hashlib
import io
import json
import logging
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from PIL import Image, ImageDraw

log = logging.getLogger("stub-sd-server")


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


def make_handler(seconds: float, keep: Path | None):
    lock = threading.Lock()
    in_flight = [0]
    counter_lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format, *args):
            log.debug(format, *args)

        def send_json(self, status: int, value: dict):
            body = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_POST(self):
            if self.path != "/sdapi/v1/txt2img":
                self.send_json(404, {"error": "not found"})
                return
            try:
                request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
                prompt, width, height = request["prompt"], int(request["width"]), int(request["height"])
            except (ValueError, KeyError, TypeError) as error:
                self.send_json(400, {"error": f"invalid request: {error}"})
                return
            with counter_lock:
                in_flight[0] += 1
                log.info("in flight %d", in_flight[0])
            try:
                with lock:
                    started = time.monotonic()
                    time.sleep(seconds)
                    image = stub_image(prompt, width, height)
                    log.info("generated %dx%d in %.1f s: %s", width, height, time.monotonic() - started, prompt)
            finally:
                with counter_lock:
                    in_flight[0] -= 1
            if keep:
                keep.mkdir(parents=True, exist_ok=True)
                image.save(keep / f"{time.strftime('%Y%m%d-%H%M%S')}-{width}x{height}.png")
            png = io.BytesIO()
            image.save(png, "PNG")
            self.send_json(200, {"images": [base64.b64encode(png.getvalue()).decode()], "info": ""})

    return Handler


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=1234)
    parser.add_argument("--seconds", type=float, default=4, help="time each image takes")
    parser.add_argument("--keep", type=Path, metavar="DIR", help="save each image into DIR")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", stream=sys.stderr)
    server = ThreadingHTTPServer((args.host, args.port), make_handler(args.seconds, args.keep))
    log.info("listening on http://%s:%d", args.host, args.port)
    server.serve_forever()


if __name__ == "__main__":
    main()

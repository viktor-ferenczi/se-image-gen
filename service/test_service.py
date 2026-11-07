"""Tests of the image generation service in stub mode, no GPU needed: uvx --with Pillow pytest service"""

import base64
import io
import json
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest
from PIL import Image

sys.path.insert(0, str(Path(__file__).parent))
from lcd_imagegen_service import image_size  # noqa: E402

SERVICE = Path(__file__).parent / "lcd_imagegen_service.py"


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def start_service(*args: str):
    port = free_port()
    process = subprocess.Popen(
        [sys.executable, str(SERVICE), "--port", str(port), *args], stderr=subprocess.PIPE, text=True
    )
    base = f"http://127.0.0.1:{port}"
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        try:
            urllib.request.urlopen(base + "/health", timeout=1)
            break
        except OSError:
            time.sleep(0.1)
    return process, base


@pytest.fixture(scope="module")
def url():
    process, base = start_service("--stub", "0.5", "--max-queue", "1")
    yield base
    process.terminate()
    process.wait()


@pytest.fixture(scope="module")
def sd_server():
    """Fake stable-diffusion.cpp sd-server: answers a red image of the requested size."""
    requests = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format, *args):
            pass

        def do_POST(self):
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            requests.append((self.path, request))
            png = io.BytesIO()
            Image.new("RGB", (request["width"], request["height"]), (255, 0, 0)).save(png, "PNG")
            body = json.dumps({"images": [base64.b64encode(png.getvalue()).decode()]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    yield f"http://127.0.0.1:{server.server_port}", requests
    server.shutdown()


def post(url: str, body) -> tuple[int, bytes]:
    data = body if isinstance(body, bytes) else json.dumps(body).encode()
    request = urllib.request.Request(url + "/generate", data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


@pytest.mark.parametrize(
    "columns,rows,expected",
    [(178, 178, (512, 512)), (355, 177, (720, 368)), (100, 400, (256, 1024)), (1, 1, (512, 512))],
)
def test_image_size_keeps_aspect_and_area(columns, rows, expected):
    assert image_size(columns, rows, 512 * 512) == expected


def test_generate_returns_rgb_of_the_grid(url):
    status, body = post(url, {"prompt": "a red fox", "columns": 91, "rows": 37})
    assert status == 200
    assert len(body) == 91 * 37 * 3


def test_prompt_starting_with_dash(url):
    status, body = post(url, {"prompt": "--steps 1000", "columns": 4, "rows": 4})
    assert status == 200 and len(body) == 48


@pytest.mark.parametrize(
    "body",
    [
        b"not json",
        {"prompt": "", "columns": 1, "rows": 1},
        {"prompt": "x", "columns": 0, "rows": 1},
        {"prompt": "x", "columns": 1000, "rows": 1000},
        {"prompt": "x" * 5000, "columns": 1, "rows": 1},
        {"columns": 1, "rows": 1},
    ],
)
def test_bad_requests(url, body):
    status, answer = post(url, body)
    assert status == 400
    assert json.loads(answer)["error"]


def test_one_job_at_a_time_and_full_queue(url):
    results = []

    def request():
        started = time.monotonic()
        status, _ = post(url, {"prompt": "x", "columns": 2, "rows": 2})
        results.append((status, time.monotonic() - started))

    threads = [threading.Thread(target=request) for _ in range(3)]
    for thread in threads:
        thread.start()
        time.sleep(0.05)
    for thread in threads:
        thread.join()

    statuses = sorted(status for status, _ in results)
    # One runs, one waits in the queue of 1, the third is turned away
    assert statuses == [200, 200, 503]
    assert max(elapsed for status, elapsed in results if status == 200) >= 0.9


def test_generate_through_sd_server(sd_server):
    sd_url, requests = sd_server
    process, base = start_service("--sd-server", sd_url, "--steps", "8", "--cfg", "3")
    try:
        status, answer = post(base, {"prompt": "-a fox", "columns": 355, "rows": 177})
    finally:
        process.terminate()
        process.wait()
    assert status == 200
    assert answer == bytes([255, 0, 0]) * 355 * 177
    path, request = requests[-1]
    assert path == "/sdapi/v1/txt2img"
    assert request["prompt"] == "-a fox"
    assert (request["width"], request["height"]) == (720, 368)
    assert (request["steps"], request["cfg_scale"]) == (8, 3.0)


def test_sd_server_down_is_bad_gateway():
    process, base = start_service("--sd-server", f"http://127.0.0.1:{free_port()}")
    try:
        status, answer = post(base, {"prompt": "x", "columns": 2, "rows": 2})
    finally:
        process.terminate()
        process.wait()
    assert status == 502
    assert "sd-server failed" in json.loads(answer)["error"]

# Image service

`lcd_imagegen_service.py` is the HTTP service the LcdImageGen plugins call. It wraps the
MOSS-Image-SFT model through the `image-gen` script of the `~/ws/llm` workspace on sirius
(`src/bin/image_gen.py`, notes in `notes/moss-image-sft/NOTE.md` there). Its only dependency
is Pillow; uv installs it on the first run.

```bash
service/lcd_imagegen_service.py                    # MOSS on cuda:1, port 8183
service/lcd_imagegen_service.py --device cuda:0 --steps 20
service/lcd_imagegen_service.py --stub 3           # test pattern after 3 s, no GPU
```

It listens on 127.0.0.1 only. Run it on the machine of the dedicated server or the game that
hosts, or put it behind something that adds authentication before opening it up.

## API

`POST /generate` with `{"prompt": "...", "columns": C, "rows": R}` answers `C * R * 3` bytes of
RGB, row by row: the image generated at the aspect ratio of the character grid and scaled down
to one pixel per character. Prompts are at most 4000 characters, grids at most 100000 cells.

Errors answer `{"error": "..."}`: 400 for a bad request, 503 when the queue is full or the GPU
is busy, 500 when generation failed. `GET /health` answers `{"status", "busy", "queued"}`.

## Behavior

- One job runs at a time, up to `--max-queue` (8) requests wait for it, more get a 503.
- Each job runs `image-gen` as its own process with the prompt on stdin (`--json -`), so a
  prompt that starts with `-` is never taken for an option. The process loads the text
  encoder, encodes, frees it, loads the DiT and VAE, samples and exits, so the GPU is free
  between jobs. One 512 x 512 image at 8 steps took 13 s that way on the RTX 4090.
- Before a job it asks `nvidia-smi` for the device's free memory. Below `--min-free-gib` (17,
  the model peaks at 16.3 GiB) it answers 503 with "GPU cuda:1 is busy: ... GiB free", which
  the plugin shows on the screen. A CUDA out-of-memory error during the run gives a similar
  message.
- `--pixels` (512 x 512), `--steps` (8), `--cfg` (3) and `--negative` set the generation.
  Width and height are multiples of 16 with the grid's aspect ratio.
- `--keep DIR` saves every generated image, before the downscale, for checking.

## Tests

```bash
uvx --with Pillow pytest service
```

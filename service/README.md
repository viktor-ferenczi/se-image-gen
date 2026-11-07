# Image service

`lcd_imagegen_service.py` is the HTTP service the LcdImageGen plugins call. It wraps the
MOSS-Image-SFT model through the `image-gen` script of the `~/ws/llm` workspace on sirius
(`src/bin/image_gen.py`, notes in `notes/moss-image-sft/NOTE.md` there). Its only dependency
is Pillow; uv installs it on the first run.

```bash
service/lcd_imagegen_service.py                    # MOSS on cuda:1, port 8183
service/lcd_imagegen_service.py --device cuda:0 --steps 20
service/lcd_imagegen_service.py --stub 3           # test pattern after 3 s, no GPU
service/lcd_imagegen_service.py --sd-server http://127.0.0.1:18860   # see below
```

It listens on 127.0.0.1 only. Run it on the machine of the dedicated server or the game that
hosts, or put it behind something that adds authentication before opening it up.

## API

`POST /generate` with `{"prompt": "...", "columns": C, "rows": R}` answers `C * R * 3` bytes of
RGB, row by row: the image generated at the aspect ratio of the character grid and scaled down
to one pixel per character. Prompts are at most 4000 characters, grids at most 100000 cells.

Errors answer `{"error": "..."}`: 400 for a bad request, 503 when the queue is full or the GPU
is busy, 500 when generation failed, 502 when the sd-server fails or can't be reached. `GET /health` answers `{"status", "busy", "queued"}`.

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
- With `--sd-server URL` the service sends each job to a running sd-server instead (below).
  The GPU checks above don't apply then.
- `--keep DIR` saves every generated image, before the downscale, for checking.

## stable-diffusion.cpp sd-server (AMD, Vulkan)

[stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) runs MOSS-Image-SFT
natively and builds with Vulkan, so it needs no CUDA or ROCm. Its `sd-server` keeps the model
loaded and answers `POST /sdapi/v1/txt2img`, which the service calls with `--sd-server`.

Tested on Linux with a Radeon AI PRO R9700 (RADV) at stable-diffusion.cpp master-1053 (5a1ba5d):
the model loads in 5 s and holds 13.8 GB of VRAM, and a 720 x 368 image at 8 steps takes 8 s.
The PyTorch ROCm path of `image-gen` works on that card as well, but loading the weights took
21 minutes there: ROCm copies memory-mapped tensors to the card at about 0.2 GiB/s.

```bash
git clone --recursive https://github.com/leejet/stable-diffusion.cpp sd
cmake -S sd -B sd/build -G Ninja -DSD_VULKAN=ON -DCMAKE_BUILD_TYPE=Release && cmake --build sd/build
```

Weights (15.6 GB):

- `MOSS-Image-SFT-Q8_0.gguf` from `leejet/MOSS-Image-SFT-GGUF`
- `Qwen3.5-9B-Q8_0.gguf` from `unsloth/Qwen3.5-9B-GGUF` (text encoder)
- `vae/flux2-vae.safetensors` from `Comfy-Org/MOSS-Image`
- `tokenizer/tokenizer.json` from `OpenMOSS-Team/MOSS-Image-SFT`

```bash
GGML_VK_VISIBLE_DEVICES=0 sd/build/bin/sd-server --listen-port 18860 --eager-load \
  --backend vulkan0 --diffusion-model MOSS-Image-SFT-Q8_0.gguf --llm Qwen3.5-9B-Q8_0.gguf \
  --vae flux2-vae.safetensors --tokenizer tokenizer.json --cfg-scale 3 --steps 8
service/lcd_imagegen_service.py --sd-server http://127.0.0.1:18860
```

`GGML_VK_VISIBLE_DEVICES` picks the card by its Vulkan index; `vulkaninfo` lists the
indices with their PCI bus numbers.

## Tests

```bash
uvx --with Pillow pytest service
```

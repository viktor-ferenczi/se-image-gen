# Running the image model

The model runs in `sd-server` from stable-diffusion.cpp, which keeps it loaded and answers
`POST /sdapi/v1/txt2img`. The plugin only needs its URL in `ImageServerUrl`, so the server can
run on the game host or on any other machine the game host can reach. For the client plugin in an offline
or friends game, or on a server without the server plugin, run it on the same PC: the command below listens where the default
`ImageServerUrl` points.

## Host

A GPU with 16 GB of memory holds the whole model (13.8 GB in use with the weights below).
stable-diffusion.cpp has Vulkan, CUDA, ROCm, Metal and SYCL backends; Vulkan works on AMD,
NVIDIA and Intel cards without a vendor compute stack. On less memory, `--offload-to-cpu`
keeps the weights in RAM and moves them to the card as needed, at a cost in speed.

Tested on Linux with a Radeon AI PRO R9700 over Vulkan (RADV), stable-diffusion.cpp master-1053:
the model loads in 5 s and an image takes 8 s at 8 steps.

## Build

Release archives for Linux, Windows and macOS are on the
[releases page](https://github.com/leejet/stable-diffusion.cpp/releases). To build it, with
CMake, a C++ compiler, the Vulkan headers and `glslc`:

```bash
git clone --recursive https://github.com/leejet/stable-diffusion.cpp
cd stable-diffusion.cpp
cmake -B build -G Ninja -DSD_VULKAN=ON -DCMAKE_BUILD_TYPE=Release   # or -DSD_CUDA=ON
cmake --build build
```

MOSS-Image needs master-975 or newer.

## Weights

15.6 GB in four files:

| File | From |
|---|---|
| `MOSS-Image-SFT-Q8_0.gguf` (5.7 GB) | [leejet/MOSS-Image-SFT-GGUF](https://huggingface.co/leejet/MOSS-Image-SFT-GGUF) |
| `Qwen3.5-9B-Q8_0.gguf` (9.5 GB), the text encoder | [unsloth/Qwen3.5-9B-GGUF](https://huggingface.co/unsloth/Qwen3.5-9B-GGUF) |
| `vae/flux2-vae.safetensors` (0.3 GB) | [Comfy-Org/MOSS-Image](https://huggingface.co/Comfy-Org/MOSS-Image) |
| `tokenizer/tokenizer.json` | [OpenMOSS-Team/MOSS-Image-SFT](https://huggingface.co/OpenMOSS-Team/MOSS-Image-SFT) |

```bash
H=https://huggingface.co
curl -LO $H/leejet/MOSS-Image-SFT-GGUF/resolve/main/MOSS-Image-SFT-Q8_0.gguf
curl -LO $H/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q8_0.gguf
curl -LO $H/Comfy-Org/MOSS-Image/resolve/main/vae/flux2-vae.safetensors
curl -LO $H/OpenMOSS-Team/MOSS-Image-SFT/resolve/main/tokenizer/tokenizer.json
```

Smaller quantizations of the image model (Q4_K, Q5_K, Q6_K) are in the same repository and work
with the same command.

## Run

```bash
build/bin/sd-server --listen-ip 127.0.0.1 --listen-port 1234 --eager-load \
  --diffusion-model MOSS-Image-SFT-Q8_0.gguf --llm Qwen3.5-9B-Q8_0.gguf \
  --vae flux2-vae.safetensors --tokenizer tokenizer.json --cfg-scale 3 --steps 8
```

- `--eager-load` loads the model at start, so the first image doesn't wait for it.
- With several GPUs, `--backend Vulkan1` picks one; `sd-cli --list-devices` lists the names.
- The server runs one image at a time and queues the rest. The plugin sends the size, steps and
  guidance scale with every request.
- Check it with
  `curl -s localhost:1234/sdapi/v1/txt2img -d '{"prompt":"a red fox","width":512,"height":512}' | head -c 100`.

`sd-server` has no authentication. It listens on 127.0.0.1 by default; to serve a game on
another machine, listen on a private network address (`--listen-ip`) and limit who can reach the
port with a firewall, or put it behind a reverse proxy.

To start it with the machine, a systemd unit like this one works (paths to adjust):

```ini
[Unit]
Description=sd-server for Image Generation
After=network-online.target

[Service]
User=sd
WorkingDirectory=/opt/sd-models/moss-image-sft
ExecStart=/opt/stable-diffusion.cpp/build/bin/sd-server --listen-ip 127.0.0.1 --listen-port 1234 --eager-load --diffusion-model MOSS-Image-SFT-Q8_0.gguf --llm Qwen3.5-9B-Q8_0.gguf --vae flux2-vae.safetensors --tokenizer tokenizer.json --cfg-scale 3 --steps 8
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

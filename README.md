# LCD Image Generation

Space Engineers 1 plugins that turn a prompt written on an LCD surface into an image on that
surface. Write `/ig <prompt>` as the text of a screen, for example `/ig a red fox in the snow`.
The [MOSS-Image-SFT](https://huggingface.co/OpenMOSS-Team/MOSS-Image-SFT) model generates the
picture in the background, and the screen then shows it as color-character art at the highest
resolution it can hold.

- **Server plugin** (Magnetar): generates on a dedicated server.
- **Client plugin** (Pulsar): generates where the local game is the server, so offline and
  when hosting a friends game. Players who join get the images through the game's normal LCD
  sync and need no plugin. On a client that joined someone else's game it does nothing.
- **Image server**: the model runs in an `sd-server` of
  [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp), on any host with a
  suitable GPU. The plugins call its HTTP API, so the game and the model can be on different
  machines. See [Running the image model](#running-the-image-model).

The same works from chat: look at a screen within 10 m and send `/ig <prompt>` or `!ig <prompt>`.
On a block with several screens, `/ig #2 <prompt>` picks screen 2 (counted from 0, as in the
Custom Data history); without a number it is screen 0. The plugin answers in chat, also when
no screen is in sight, the player has no access to the block or has an image in progress.

Both plugins share all of their logic (`Shared/`) and read the same configuration.

## How it works

- Any change of a surface's text goes through `MyTextPanelComponent.UpdateText`: a player's
  edit, a programmable block's `WriteText`, network sync and loading the block. A postfix there
  notes texts starting with `/ig ` when the local game is the server, and the next update
  handles them on the game thread.
- A chat command ends there too: the server casts a ray from the player's eyes, takes the
  block it hits, checks terminal access and writes `/ig <prompt>` on the screen. The server
  can't tell which screen of a cockpit is under the crosshair (the screen areas exist only in
  the client's render models), hence the `#<screen>` number. Vanilla clients send unknown
  `/` commands to the server like normal chat, so both prefixes reach it. Messages typed on the
  host or offline arrive through `MessageEnteredSender`, other players' through the
  multiplayer `ChatMessageReceived` event.
- Single LCDs and every screen of a multi-screen block (cockpits, programmable blocks, button
  panels) work the same way, each with its own size and aspect ratio. The screen's index is
  its index in the block's surface list, the one the terminal and scripts use.
- The prompt goes into the block's Custom Data as `Screen[<index>].Prompt: <prompt>` and a
  blank line, with `\n` line endings like the rest of Custom Data. Line breaks in the prompt
  become spaces there. A screen's earlier entry is replaced in place. If the entry does not fit
  into the 64000 characters Custom Data allows, the prompt is discarded and nothing is
  generated.
- The HTTP call and the conversion to characters run on a worker thread. The game thread only
  starts a job and later applies the result, which takes well under a millisecond.
- The plugin asks the image server for a picture at the screen's aspect ratio (about 512 x 512
  pixels by default), decodes the PNG it answers with and scales it down to one pixel per
  character, averaging the pixels each character covers. It then maps each pixel to one of
  the Monospace font's 512 color characters (U+E100 + r * 64 + g * 8 + b, 3 bits per channel),
  with Floyd-Steinberg dithering.
- It then switches the screen to text, the Monospace font, white font color, centered, no
  padding, and the font size that fills the screen. The image replaces the prompt text. If the
  screen's text changed while the image was generated, the result is dropped.
- While the image is generated the prompt stays on the screen. Failures replace it with one
  line starting with `[LcdImageGen]`. A prompt still on a screen when the world is saved is
  generated again after loading.

### Resolution

A color character is a 37 x 37 unit square in the Monospace font (36 advance plus 1 spacing,
37 line height). The game draws surface text at `FontSize * min(texture width, height) / 512`
and the font at 28.8 / 37 of its size, so a character cell is
`28.8 * FontSize * min(texture) / 512` texture pixels.

Two limits decide the grid: the text length (`MaxCharacters`, the game itself cuts surface text
at 100000) and the smallest cell worth drawing (`MinCellPixels`, 2.88 texture pixels, which is
font size 0.1 on a 512 pixel screen, the usual resolution of LCD art). The game rounds font
sizes to 3 decimals, so the font size is picked on that grid. Examples:

| Screen | Texture | Visible | Grid | Font size |
|---|---|---|---|---|
| LCD Panel | 512 x 512 | 512 x 512 | 177 x 177 | 0.1 |
| Wide LCD Panel | 1024 x 512 | 1024 x 512 | 355 x 177 | 0.1 |
| Programmable block, large screen | 512 x 512 | 512 x 320 | 177 x 111 | 0.1 |
| Programmable block, keyboard | 512 x 256 | 512 x 205 | 177 x 71 | 0.2 |
| Cockpit seat, top center | 256 x 256 | 256 x 177 | 88 x 61 | 0.2 |

## Configuration

`LcdImageGen.cfg` in the game's user data folder (the Magnetar instance's data folder for the
server, the game's AppData folder for the client), written with the defaults on first start:

| Option | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Turns the plugin on |
| `ImageServerUrl` | `http://127.0.0.1:1234` | The sd-server running the model |
| `TimeoutSeconds` | `300` | Wait for an image, including time queued in the image server |
| `ImagePixels` | `262144` | Pixels of a generated image (512 x 512), before it is scaled down to the grid |
| `Steps` | `8` | Sampling steps, fewer is faster |
| `CfgScale` | `3` | Guidance scale |
| `MaxPendingPerPlayer` | `1` | Prompts a player may have waiting, 0 for no limit. Prompts written by scripts or at world load have no player and no limit. |
| `MaxCharacters` | `100000` | Longest art, the game's limit is 100000 |
| `MinCellPixels` | `2.88` | Smallest character cell in texture pixels |
| `Dither` | `true` | Floyd-Steinberg dithering |
| `Moderation/Enabled` | `false` | Content moderation of the prompts, **not implemented yet**: does nothing, only logs a warning when enabled |
| `Moderation/Prompt` | empty | Instructions for the moderating model, needed once moderation is implemented |

There is no extra permission check: anyone who can change a screen's text can generate an image,
and the chat command needs terminal access to the block.

## Running the image model

The model runs in `sd-server` from stable-diffusion.cpp, which keeps it loaded and answers
`POST /sdapi/v1/txt2img`. The plugin only needs its URL in `ImageServerUrl`, so the server can
run on the game host or on any other machine the game host can reach. For the client plugin in an offline
or friends game, run it on the same PC: the command below listens where the default
`ImageServerUrl` points.

### Host

A GPU with 16 GB of memory holds the whole model (13.8 GB in use with the weights below).
stable-diffusion.cpp has Vulkan, CUDA, ROCm, Metal and SYCL backends; Vulkan works on AMD,
NVIDIA and Intel cards without a vendor compute stack. On less memory, `--offload-to-cpu`
keeps the weights in RAM and moves them to the card as needed, at a cost in speed.

Tested on Linux with a Radeon AI PRO R9700 over Vulkan (RADV), stable-diffusion.cpp master-1053:
the model loads in 5 s and an image takes 8 s at 8 steps.

### Build

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

### Weights

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

### Run

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
Description=sd-server for LCD Image Generation
After=network-online.target

[Service]
User=sd
WorkingDirectory=/opt/sd-models/moss-image-sft
ExecStart=/opt/stable-diffusion.cpp/build/bin/sd-server --listen-ip 127.0.0.1 --listen-port 1234 --eager-load --diffusion-model MOSS-Image-SFT-Q8_0.gguf --llm Qwen3.5-9B-Q8_0.gguf --vae flux2-vae.safetensors --tokenizer tokenizer.json --cfg-scale 3 --steps 8
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

## Build and test

```bash
dotnet build                               # both plugins
dotnet test Tests                          # fitting, art, PNG decoding and Custom Data logic
uv run --no-project --with httpx --with Pillow python tests/game/rig.py offline
uv run --no-project --with httpx --with Pillow python tests/game/rig.py ds
```

`tests/game/rig.py` runs the game: `offline` loads a world with the client plugin, `ds` starts an
isolated Magnetar with the server plugin and joins it with a client that has no plugin. Both
paste a station with a programmable block, a square and a wide LCD and a cockpit seat, write
prompts on six screens with the block's script and check the grids, fonts, Custom Data, the
discard of an entry that does not fit, and that the game thread never waited. It follows the
workspace's slot and RAM rules. Without `--sd-server URL` it starts `tests/stub_sd_server.py`,
which answers like sd-server with a test pattern after 4 s, so no GPU is needed.

The plugins load from this folder as development folders: `LcdImageGenServer.xml` in Magnetar,
`LcdImageGenClient.xml` in Pulsar.

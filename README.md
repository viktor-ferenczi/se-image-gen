# LCD Image Generation

Space Engineers 1 plugins that turn a prompt written on an LCD surface into an image on that
surface. Write `/ig <prompt>` as the text of a screen, for example `/ig a red fox in the snow`.
A local image service generates the picture in the background, and the screen then shows it
as color-character art at the highest resolution it can hold.

- **Server plugin** (Magnetar): generates on a dedicated server.
- **Client plugin** (Pulsar): generates where the local game is the server, so offline and
  when hosting a friends game. Players who join get the images through the game's normal LCD
  sync and need no plugin. On a client that joined someone else's game it does nothing.
- **Service** (`service/`): a small HTTP service that runs the
  [MOSS-Image-SFT](https://huggingface.co/OpenMOSS-Team/MOSS-Image-SFT) model through
  `image-gen`. See [service/README.md](service/README.md).

Both plugins share all of their logic (`Shared/`) and read the same configuration.

## How it works

- Any change of a surface's text goes through `MyTextPanelComponent.UpdateText`: a player's
  edit, a programmable block's `WriteText`, network sync and loading the block. A postfix there
  notes texts starting with `/ig ` when the local game is the server, and the next update
  handles them on the game thread.
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
- The service generates the image at the screen's aspect ratio (about 512 x 512 pixels by
  default) and scales it down to one pixel per character. The plugin maps each pixel to one of
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
| `ServiceUrl` | `http://127.0.0.1:8183` | The image service |
| `TimeoutSeconds` | `300` | Wait for an image, including time queued in the service |
| `MaxPendingPerPlayer` | `1` | Prompts a player may have waiting, 0 for no limit. Prompts written by scripts or at world load have no player and no limit. |
| `MaxCharacters` | `100000` | Longest art, the game's limit is 100000 |
| `MinCellPixels` | `2.88` | Smallest character cell in texture pixels |
| `Dither` | `true` | Floyd-Steinberg dithering |
| `Moderation/Enabled` | `false` | Content moderation of the prompts, **not implemented yet**: does nothing, only logs a warning when enabled |
| `Moderation/Prompt` | empty | Instructions for the moderating model, needed once moderation is implemented |

There is no permission check: anyone who can change a screen's text can generate an image.

## Build and test

```bash
dotnet build                               # both plugins
dotnet test Tests                          # fitting, art and Custom Data logic
uvx --with Pillow pytest service           # the service, in stub mode
uv run --no-project --with httpx --with Pillow python tests/game/rig.py offline
uv run --no-project --with httpx --with Pillow python tests/game/rig.py ds
```

`tests/game/rig.py` runs the game: `offline` loads a world with the client plugin, `ds` starts an
isolated Magnetar with the server plugin and joins it with a client that has no plugin. Both
paste a station with a programmable block, a square and a wide LCD and a cockpit seat, write
prompts on six screens with the block's script and check the grids, fonts, Custom Data, the
discard of an entry that does not fit, and that the game thread never waited. It follows the
workspace's slot and RAM rules and uses the service in stub mode unless given `--service`.

The plugins load from this folder as development folders: `LcdImageGenServer.xml` in Magnetar,
`LcdImageGenClient.xml` in Pulsar.

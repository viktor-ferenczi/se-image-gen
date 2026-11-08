# Configuration

`ImageGen.cfg` in the game's user data folder (the Magnetar instance's data folder for the
server, the game's AppData folder for the client), written with the defaults on first start.
Version 1.0.0 called it `LcdImageGen.cfg`; the plugin renames that file on start, keeping its
settings.

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
| `WriteCustomData` | `true` | Keeps each screen's prompt in the block's Custom Data. Set it to `false` where scripts or mods keep their own data there; the plugin then leaves Custom Data alone |
| `Moderation/Enabled` | `false` | Content moderation of the prompts, **not implemented yet**: does nothing, only logs a warning when enabled |
| `Moderation/Prompt` | empty | Instructions for the moderating model, needed once moderation is implemented |

There is no extra permission check: anyone who can change a screen's text can generate an image,
and the chat command needs terminal access to the block.

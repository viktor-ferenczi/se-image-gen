# How it works

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
- A client joined to a server asks whether the server runs Image Generation: it sends a mod
  message, which the server plugin answers while it is enabled. If no answer comes within 10
  s, the client does the work for its own player, and only for prompts that player writes:
  in a screen's text editor (a prefix of the blocks' `SendChangeDescriptionMessage`) or by
  chat. It waits until the server sends the text back, so the prompt has passed the server's
  access check, then generates with its own `ImageServerUrl` and writes the image, font and
  Custom Data through the same synced calls a player's edits use. The server keeps the text
  and passes it on to the other players. Chat commands stay on the client then: they never
  reach the server or the other players, and the replies are shown only locally. Where the
  server has the plugin, the client leaves everything to it, so no prompt is generated twice.
  A server with version 1.0.0 doesn't answer, so there both may generate a prompt; the image
  that comes first stays, the other is dropped because the screen's text changed.
- Single LCDs and every screen of a multi-screen block (cockpits, programmable blocks, button
  panels) work the same way, each with its own size and aspect ratio. The screen's index is
  its index in the block's surface list, the one the terminal and scripts use.
- The prompt goes into the block's Custom Data as `Screen[<index>].Prompt: <prompt>` and a
  blank line, with `\n` line endings like the rest of Custom Data. Line breaks in the prompt
  become spaces there. A screen's earlier entry is replaced in place. If the entry does not fit
  into the 64000 characters Custom Data allows, the prompt is discarded and nothing is
  generated.
  With `WriteCustomData` set to `false` the plugin skips this step and never touches Custom
  Data.
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
  line starting with `[ImageGen]`. A prompt still on a screen when the world is saved is
  generated again after loading.

## Resolution

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

# Usage

## On the screen

Write `/ig <prompt>` as the text of a screen, in the terminal or from a programmable block's
`WriteText`, for example `/ig a red fox in the snow`. This works on every screen of a block,
so scripts can generate images too.

The prompt stays on the screen while the image is generated, then the image replaces it. A
failure replaces it with one line starting with `[ImageGen]`. The prompt is kept in the
block's Custom Data as `Screen[<index>].Prompt: <prompt>`, so it can be copied back later,
unless `WriteCustomData` is turned off.

On a server without the server plugin, a player with the client plugin can still generate:
the client generates the prompts that player writes in the screen's text editor or by chat.
Prompts written by scripts or by players without the plugin stay text there.

## From chat

Look at a screen within 10 m and send `/ig <prompt>` or `!ig <prompt>`. The plugin writes
`/ig <prompt>` on that screen, and from there it goes as above. It needs terminal access to the
block. On a block with several screens, `/ig #2 <prompt>` picks screen 2, counted from 0 as in
the terminal and the Custom Data entries. Without a number it is screen 0: the server can't
tell which screen of a cockpit is under the crosshair.

On a server without the server plugin, the client plugin handles the chat command itself:
the message stays on your client and only you see the replies. Right after joining it takes
the client up to 10 s to find out whether the server has the plugin; a command sent in that
time is answered with a request to try again.

The plugin answers in chat when it can't start: no screen in sight, no access to the block, no
such screen, the player already has an image in progress (`MaxPendingPerPlayer`), or the
server has as many images in progress as it allows (`MaxPendingTotal`).

### `/ig` or `!ig`

Both do the same. Use `/ig`. Use `!ig` when `/ig` doesn't reach the server: if a client plugin
or mod of yours registers its own `/ig` chat command, the game runs that one on your client
instead of sending the message. `!ig` also suits players used to the `!` commands of Torch and
Magnetar plugins.

Either way the message is posted in chat like any other, so other players see the prompt.

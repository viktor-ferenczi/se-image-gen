# Build and test

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

"""In-game test of LcdImageGen: "/ig <prompt>" written on LCD surfaces turns into fitted images.

    uv run --no-project --with httpx --with Pillow python tests/game/rig.py offline
    uv run --no-project --with httpx --with Pillow python tests/game/rig.py ds
    uv run --no-project --with httpx --with Pillow python tests/game/rig.py stop

offline: a headless client with the client plugin loads a world offline, so the local game is
the server. ds: an isolated Magnetar server with the server plugin and a headless client
without it that joins; the client must see the images through the game's own LCD sync.

Both paste a station with a programmable block (two screens), a square LCD, a wide LCD and a
cockpit seat (six screens). The block's script writes the prompts on the screens and reports
what they show. A stub sd-server (tests/stub_sd_server.py) answers with a test pattern after
STUB_SECONDS, or pass --sd-server URL to use a running sd-server with the real model.
--no-custom-data instead turns WriteCustomData off and checks that the image is made and the
block's own Custom Data stays as it was.
Prints PASS/FAIL per check.

Ports, the client id and folders come from the workspace's test slot claimed under TASK,
every game process starts under run-test.sh (RAM guard and test lock), and nothing of the
machine's own Pulsar, Magnetar or game folders is written. Adapted from the Octahedron rig.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
from xml.sax.saxutils import escape

HOME = Path.home()
REPO = Path(__file__).resolve().parents[2]
WORKSPACE = Path(os.environ.get("SE_WORKSPACE", HOME / "ws/se"))
PARALLEL = WORKSPACE / "testing" / "parallel-test-work"
REMOTE_REPO = WORKSPACE / "se1/plugins/remote"
DIRECT_TRANSPORT_REPO = WORKSPACE / "se1/direct-transport"
MAGNETAR = HOME / ".config/Magnetar"
PULSAR_TEMPLATE = HOME / ".config/Pulsar"
STEAM_APPS = HOME / ".steam/debian-installation/steamapps/common"
DS64 = STEAM_APPS / "SpaceEngineersDedicatedServer/DedicatedServer64"
BIN64 = STEAM_APPS / "SpaceEngineers/Bin64"
DS_CONFIG_TEMPLATE = HOME / ".config/SpaceEngineersDedicated/SpaceEngineers-Dedicated.cfg"
WORLD_ZIP = REMOTE_REPO / "Worlds/RemoteAPITestSpace.zip"

sys.path.insert(0, str(REMOTE_REPO / "skills" / "se-remote"))
from se_remote import RemoteAPI, SetOp  # noqa: E402

TASK = os.environ.get("LCD_TASK", "SE-1183")
TEST_ROOT = HOME / ".se-test"
CLIENT_PLUGIN_ID = "DD45EEE2-E0BC-420E-9787-35E4EE7E75A1"
SERVER_PLUGIN_ID = "A8BA1F4C-E820-4809-92E6-484ED67E87D3"
RENDER_GIB = 5.5
SERVER_GIB = 4.0
STUB_SECONDS = 4.0
# Seconds to wait for all images, longer with the real model (--sd-server)
IMAGES_TIMEOUT = 60 + 3 * STUB_SECONDS * 6


def _slot() -> dict[str, int]:
    slot = subprocess.run(
        [str(PARALLEL / "slot.sh"), "claim", TASK], check=True, capture_output=True, text=True
    ).stdout.strip()
    text = subprocess.run([str(PARALLEL / "slot.sh"), "env", slot], check=True, capture_output=True, text=True).stdout
    return {k: int(v) for k, v in re.findall(r"(\w+_BASE)=(\d+)", text)}


SLOT = _slot()
SERVER_ROOT = TEST_ROOT / f"{TASK}-ds0"
SERVER_CONFIG = SERVER_ROOT / "magnetar"
SERVER_DATA = SERVER_ROOT / "data"
SERVER_PID = SERVER_ROOT / "server.pid"
SERVER_LOG = SERVER_ROOT / "server.log"
SERVER_PORT = SLOT["DS_SERVER_PORT_BASE"]
PULSAR = TEST_ROOT / f"{TASK}-c0"
LAUNCHER = PULSAR / "Interim.bin"
APPDATA = TEST_ROOT / f"{TASK}-c0-data"
CLIENT_PORT = SLOT["REMOTE_PORT_BASE"]
CLIENT_ID = SLOT["CLIENT_ID_BASE"]
CLIENT_NAME = "LcdTester"
CLIENT_PID = APPDATA / "game.pid"
CLIENT_LOG = APPDATA / "launch.log"
STUB_PORT = SLOT["REMOTE_PORT_BASE"] + 9
STUB_PID = TEST_ROOT / f"{TASK}-sd-server.pid"
STUB_LOG = TEST_ROOT / f"{TASK}-sd-server.log"
ARTIFACTS = TEST_ROOT / f"{TASK}-artifacts"

# --- processes -------------------------------------------------------------------


def _alive(pid_file: Path, marker: str) -> int | None:
    try:
        pid = int(pid_file.read_text().strip())
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes()
        if not cmdline:  # Empty for a moment during an exec in run-test.sh's chain
            time.sleep(0.2)
            cmdline = Path(f"/proc/{pid}/cmdline").read_bytes()
    except (OSError, ValueError):
        return None
    return pid if marker.encode() in cmdline else None


def _start(command, need_gib, cwd: Path, log: Path, pid_file: Path, env=None, guarded=True):
    prefix = [str(PARALLEL / "run-test.sh"), "se1", str(need_gib), "--"] if guarded else []
    log.parent.mkdir(parents=True, exist_ok=True)
    with open(log, "w") as out:
        process = subprocess.Popen(
            [*prefix, *map(str, command)],
            cwd=cwd,
            env={**os.environ, **(env or {})},
            stdout=out,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
            start_new_session=True,
        )
    pid_file.write_text(str(process.pid))


def _stop(pid_file: Path, marker: str, timeout: float) -> None:
    pid = _alive(pid_file, marker)
    if pid is None:
        pid_file.unlink(missing_ok=True)
        return
    os.killpg(pid, signal.SIGTERM)
    deadline = time.monotonic() + timeout
    while True:
        try:
            os.waitpid(pid, os.WNOHANG)
        except ChildProcessError:
            pass
        try:
            os.killpg(pid, 0)
        except ProcessLookupError:
            break
        if time.monotonic() > deadline:
            os.killpg(pid, signal.SIGKILL)
            break
        time.sleep(1)
    pid_file.unlink(missing_ok=True)


def wait_until(predicate, timeout: float, description: str, interval: float = 1.0):
    deadline = time.monotonic() + timeout
    while not (last := predicate()):
        if time.monotonic() > deadline:
            raise TimeoutError(f"Timed out after {timeout:.0f} s waiting for {description}")
        time.sleep(interval)
    return last


# --- plugin sources and configs ----------------------------------------------------


def profile_xml(dev_folders) -> str:
    folders = "".join(
        f"<LocalFolderConfig><Id>{i}</Id><DebugBuild>true</DebugBuild></LocalFolderConfig>" for i in dev_folders
    )
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<Name>Current</Name><GitHub /><DevFolder>{folders}</DevFolder>"
        "<Local /><Mods /></Profile>\n"
    )


def register_sources(sources: Path, plugins) -> None:
    """Points the loader's dev folder sources at these (name, folder, manifest) only"""
    tree = ET.parse(sources)
    local = tree.getroot().find("LocalPluginSources")
    if local is None:
        local = ET.SubElement(tree.getroot(), "LocalPluginSources")
    for plugin in local.findall("LocalPlugin"):
        local.remove(plugin)
    for name, folder, file in plugins:
        plugin = ET.SubElement(local, "LocalPlugin")
        for tag, value in (("Name", name), ("Folder", str(folder)), ("File", file), ("Enabled", "true")):
            ET.SubElement(plugin, tag).text = value
    tree.write(sources, encoding="utf-8", xml_declaration=True)


# --no-custom-data runs the check of WriteCustomData false instead of the full set
WRITE_CUSTOM_DATA = True


def plugin_config(server_url: str) -> str:
    return f"""<?xml version="1.0" encoding="utf-8"?>
<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <ImageServerUrl>{server_url}</ImageServerUrl>
  <TimeoutSeconds>600</TimeoutSeconds>
  <ImagePixels>262144</ImagePixels>
  <Steps>8</Steps>
  <CfgScale>3</CfgScale>
  <MaxPendingPerPlayer>1</MaxPendingPerPlayer>
  <MaxCharacters>100000</MaxCharacters>
  <MinCellPixels>2.88</MinCellPixels>
  <Dither>true</Dither>
  <WriteCustomData>{str(WRITE_CUSTOM_DATA).lower()}</WriteCustomData>
  <Moderation>
    <Enabled>true</Enabled>
    <Prompt>Refuse anything not safe for work.</Prompt>
  </Moderation>
</PluginConfig>
"""


# --- world -------------------------------------------------------------------------


def prepare_world(saves: Path, online_mode: str) -> Path:
    world = saves / "LcdImageGenTest"
    shutil.rmtree(world, ignore_errors=True)
    shutil.rmtree(saves / "RemoteAPITestSpace", ignore_errors=True)
    saves.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(WORLD_ZIP) as z:
        z.extractall(saves)
    (saves / "RemoteAPITestSpace").rename(world)
    # The binary sector would win over the edited XML
    (world / "SANDBOX_0_0_0_.sbsB5").unlink()
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = world / name
        text = path.read_text(encoding="utf-8")
        for key, value in {
            "OnlineMode": online_mode,
            "TrashRemovalEnabled": "false",
            "EnableIngameScripts": "true",
            "ExperimentalMode": "true",
            "GameMode": "Creative",
            "AutoSaveInMinutes": "0",
        }.items():
            text, count = re.subn(rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text)
            if count == 0:
                text = text.replace("</Settings>", f"<{key}>{value}</{key}></Settings>", 1)
        path.write_text(text, encoding="utf-8")
    return world


def _block(xsi_type: str, subtype: str, pos, extra: str = "", name: str | None = None) -> str:
    custom_name = f"<CustomName>{escape(name)}</CustomName>" if name else ""
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="{xsi_type}">'
        f'<SubtypeName>{subtype}</SubtypeName><Min x="{pos[0]}" y="{pos[1]}" z="{pos[2]}" />'
        f'<BlockOrientation Forward="Forward" Up="Up" />{custom_name}{extra}'
        "</MyObjectBuilder_CubeBlock>"
    )


# Cells of the blocks the checks address
PB, LCD, WIDE, SEAT = (1, 0, 0), (2, 0, 0), (3, 0, 0), (5, 0, 0)


def blueprint_xml() -> str:
    program = escape((Path(__file__).parent / "TestProgram.cs").read_text(encoding="utf-8"))
    blocks = [
        _block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            (0, 0, 0),
            "<CurrentStoredPower>3</CurrentStoredPower><ProducerEnabled>true</ProducerEnabled>",
        ),
        _block(
            "MyObjectBuilder_MyProgrammableBlock", "LargeProgrammableBlock", PB, f"<Program>{program}</Program>", "PB"
        ),
        _block("MyObjectBuilder_TextPanel", "LargeLCDPanel", LCD, name="LCD"),
        _block("MyObjectBuilder_TextPanel", "LargeLCDPanelWide", WIDE, name="Wide LCD"),
        _block("MyObjectBuilder_Cockpit", "LargeBlockCockpitSeat", SEAT, name="Seat"),
    ]
    grid = (
        '<CubeGrid xsi:type="MyObjectBuilder_CubeGrid"><SubtypeName /><EntityId>731183000000001</EntityId>'
        "<PersistentFlags>CastShadows InScene</PersistentFlags>"
        '<PositionAndOrientation><Position x="0" y="0" z="0" /><Forward x="0" y="0" z="-1" />'
        '<Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{''.join(blocks)}</CubeBlocks>"
        "<IsStatic>true</IsStatic><DisplayName>LcdImageGen Test</DisplayName>"
        "<DestructibleBlocks>true</DestructibleBlocks><IsUnsupportedStation>true</IsUnsupportedStation></CubeGrid>"
    )
    return (
        '<?xml version="1.0"?>\n<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><ShipBlueprints>'
        '<ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="LcdImageGenTest" />'
        f"<DisplayName>LcdImageGenTest</DisplayName><CubeGrids>{grid}</CubeGrids>"
        "</ShipBlueprint></ShipBlueprints></Definitions>"
    )


# --- server and client ---------------------------------------------------------------


def prepare_server(server_url: str) -> None:
    if not (SERVER_CONFIG / "Sources").exists():
        SERVER_CONFIG.mkdir(parents=True, exist_ok=True)
        shutil.copytree(MAGNETAR / "Magnetar" / "Sources", SERVER_CONFIG / "Sources")
        shutil.copy(MAGNETAR / "Magnetar" / "config.xml", SERVER_CONFIG / "config.xml")
    register_sources(
        SERVER_CONFIG / "Sources" / "sources.xml",
        [
            ("direct-transport", DIRECT_TRANSPORT_REPO, "DirectTransportServer.xml"),
            ("se-image-gen", REPO, "ImageGenServer.xml"),
        ],
    )
    (SERVER_CONFIG / "Profiles").mkdir(exist_ok=True)
    (SERVER_CONFIG / "Profiles" / "Current.xml").write_text(
        profile_xml(["direct-transport", SERVER_PLUGIN_ID]), encoding="utf-8"
    )
    world = prepare_world(SERVER_DATA / "Saves", "PUBLIC")
    text = DS_CONFIG_TEMPLATE.read_text(encoding="utf-8")
    for tag, value in {
        "IP": "127.0.0.1",
        "ServerPort": SERVER_PORT,
        "SteamPort": SLOT["DS_STEAM_PORT_BASE"],
        "Administrators": f"<unsignedLong>{CLIENT_ID}</unsignedLong>",
        "ServerName": f"LcdImageGen Test {TASK}",
        "WorldName": "LcdImageGenTest",
        "PauseGameWhenEmpty": "false",
        "IgnoreLastSession": "true",
        "RemoteApiEnabled": "false",
        "LoadWorld": world,
    }.items():
        text, count = re.subn(rf"<{tag}>.*?</{tag}>|<{tag} />", f"<{tag}>{value}</{tag}>", text, flags=re.S)
        assert count == 1, tag
    (SERVER_DATA / "SpaceEngineers-Dedicated.cfg").write_text(text, encoding="utf-8")
    (SERVER_DATA / "LcdImageGen.cfg").write_text(plugin_config(server_url), encoding="utf-8")


def server_pid() -> int | None:
    return _alive(SERVER_PID, str(SERVER_CONFIG))


def start_server(timeout: float = 900.0) -> None:
    _start(
        [
            MAGNETAR / "MagnetarInterim.bin",
            "-multiInstance", "-stableLogs", "-noimplicitmod", "-consent", "deny",
            "-config", SERVER_CONFIG, "-ds64", DS64, "-path", SERVER_DATA,
        ],
        SERVER_GIB, MAGNETAR, SERVER_LOG, SERVER_PID, {"SE_DIRECT_TRANSPORT": "1"},
    )  # fmt: skip

    def ready():
        if "Game ready" in SERVER_LOG.read_text(errors="replace"):
            return True
        if server_pid() is None:
            time.sleep(1)
            if server_pid() is None:
                raise RuntimeError(f"The server exited, see {SERVER_LOG}")
        return False

    wait_until(ready, timeout, "the server to get ready", 2)


def server_game_log() -> str:
    logs = sorted(SERVER_DATA.glob("SpaceEngineersDedicated*.log"), key=lambda p: p.stat().st_mtime)
    return logs[-1].read_text(errors="replace") if logs else ""


def prepare_client(with_plugin: bool, server_url: str) -> None:
    if not LAUNCHER.exists():
        subprocess.run(
            [WORKSPACE / "testing/pulsar-dev-instances/new-pulsar-instance.sh", PULSAR, PULSAR_TEMPLATE],
            check=True,
            stdout=subprocess.DEVNULL,
        )
    legacy = PULSAR / "Legacy"
    plugins = [
        ("remote", REMOTE_REPO, "Remote.xml"),
        ("direct-transport", DIRECT_TRANSPORT_REPO, "DirectTransportClient.xml"),
    ]
    ids = ["remote", "direct-transport"]
    if with_plugin:
        plugins.append(("se-image-gen", REPO, "ImageGenClient.xml"))
        ids.append(CLIENT_PLUGIN_ID)
    register_sources(legacy / "Sources" / "sources.xml", plugins)
    (legacy / "Profiles" / "Current.xml").write_text(profile_xml(ids), encoding="utf-8")

    APPDATA.mkdir(parents=True, exist_ok=True)
    (APPDATA / "Remote.cfg").write_text(
        f"""<?xml version="1.0" encoding="utf-8"?>
<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <ListenIP>127.0.0.1</ListenIP>
  <ListenPort>{CLIENT_PORT}</ListenPort>
  <AdminPassword>SpaceEngineers</AdminPassword>
</PluginConfig>
""",
        encoding="utf-8",
    )
    game_cfg = APPDATA / "SpaceEngineers.cfg"
    if not game_cfg.exists():
        shutil.copy(HOME / ".config/SpaceEngineers/SpaceEngineers.cfg", game_cfg)
    text = re.sub(
        r"(<Key>ExperimentalMode</Key>\s*<Value>\s*<Value[^>]*>)\w+(</Value>)",
        r"\1True\2",
        game_cfg.read_text(encoding="utf-8"),
    )
    game_cfg.write_text(text, encoding="utf-8")
    config = APPDATA / "LcdImageGen.cfg"
    if with_plugin:
        config.write_text(plugin_config(server_url), encoding="utf-8")
    else:
        config.unlink(missing_ok=True)


def client_pid() -> int | None:
    return _alive(CLIENT_PID, str(LAUNCHER))


def api() -> RemoteAPI:
    return RemoteAPI(f"http://127.0.0.1:{CLIENT_PORT}", username="admin", password="SpaceEngineers", timeout=90)


def start_client(connect: bool, timeout: float = 900.0) -> RemoteAPI:
    _start(
        [
            LAUNCHER,
            "-multiInstance", "-lazySteam", "-noprompt", "-noupdate", "-nosplash", "-stablelogs",
            "-bin64", BIN64, "--no-steam", "--client-id", CLIENT_ID, "--client-name", CLIENT_NAME,
            "-appdata", APPDATA, "--quality", "minimal", "--no-audio", "--headless", "--resolution", "1280x720",
            *(["--connect", f"127.0.0.1:{SERVER_PORT}"] if connect else []),
        ],
        RENDER_GIB, PULSAR, CLIENT_LOG, CLIENT_PID,
    )  # fmt: skip
    remote = api()

    def listening():
        if not client_pid():
            time.sleep(1)
            if not client_pid():
                raise RuntimeError(f"The client exited, see {CLIENT_LOG}")
        try:
            remote.ping()
            return True
        except Exception:  # noqa: BLE001 -- not listening yet
            return False

    wait_until(listening, timeout, "the Remote API", 3)
    return remote


def wait_world(remote: RemoteAPI, timeout: float = 900.0) -> None:
    def ready():
        try:
            boxes = [s for s in remote.list_screens() if s.get("type") == "MyGuiScreenMessageBox"]
            for box in boxes:
                remote.control_click(text="OK", screen=box["index"])
            return not boxes and remote.get_state().get("ready")
        except Exception:  # noqa: BLE001 -- the API times out while the world loads
            return False

    wait_until(ready, timeout, "the world", 2)


def ensure_character(remote: RemoteAPI, timeout: float = 300.0) -> dict:
    def alive():
        try:
            character = remote.get_character()
            if character.get("state") not in (None, "dead"):
                return character
        except Exception:  # noqa: BLE001 -- 503 without a character
            pass
        try:
            screens = remote.list_screens()
            medical = next((i for i, s in enumerate(screens) if "Medical" in s.get("type", "")), None)
            if medical is not None:
                for button in ("Join", "Respawn"):
                    try:
                        remote.control_click(text=button, screen=medical)
                        break
                    except Exception:  # noqa: BLE001
                        continue
        except Exception:  # noqa: BLE001
            pass
        return None

    return wait_until(alive, timeout, "a live character", 3)


def start_stub(stub: float) -> str:
    _stop(STUB_PID, "stub_sd_server", 10)
    _start(
        [
            sys.executable, REPO / "tests/stub_sd_server.py", "--port", STUB_PORT,
            "--seconds", stub, "--keep", ARTIFACTS / "images",
        ],
        0, REPO, STUB_LOG, STUB_PID, guarded=False,
    )  # fmt: skip
    wait_until(lambda: "listening" in STUB_LOG.read_text(errors="replace"), 30, "the stub sd-server")
    return f"http://127.0.0.1:{STUB_PORT}"


def stop_all() -> None:
    _stop(CLIENT_PID, str(LAUNCHER), 60)
    _stop(SERVER_PID, str(SERVER_CONFIG), 120)
    _stop(STUB_PID, "stub_sd_server", 10)


# --- checks -------------------------------------------------------------------------


class Checks:
    def __init__(self):
        self.results: list[tuple[bool, str, str]] = []

    def check(self, ok, name: str, detail: str = "") -> bool:
        self.results.append((bool(ok), name, detail))
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + detail if detail else ''}", flush=True)
        return bool(ok)

    def summary(self) -> int:
        failed = [r for r in self.results if not r[0]]
        print(f"\n{len(self.results) - len(failed)} passed, {len(failed)} failed", flush=True)
        return 1 if failed or not self.results else 0


REPORTS = iter(range(1, 1_000_000))


def report(remote: RemoteAPI, grid: int) -> dict:
    """The script's report: {("LCD", 0): {...surface}, ("LCD", "data"): (length, tail)}"""
    tag = f"report {next(REPORTS)}"
    remote.batch(sets=[SetOp.run_pb(grid, PB, tag)])
    # On a joined client the script's output arrives a moment later
    info = wait_until(
        lambda: (i := remote.get_block(grid, PB).get("detailedInfo") or "").startswith(tag + "\n") and i,
        30,
        f"the script's {tag}",
        0.3,
    )
    state: dict = {}
    for line in info.splitlines():
        parts = line.split("|")
        if parts[0] == "S" and len(parts) >= 14:
            name, index = parts[1], int(parts[2])
            cols, rows = map(int, parts[11].split("x"))
            state[(name, index)] = {
                "content": parts[3], "font": parts[4], "fontSize": float(parts[5]), "padding": float(parts[6]),
                "alignment": parts[7], "texture": parts[8], "surface": parts[9], "length": int(parts[10]),
                "columns": cols, "rows": rows, "samples": parts[12], "head": "|".join(parts[13:]),
            }  # fmt: skip
        elif parts[0] == "C":
            state[(parts[1], "data")] = (int(parts[2]), "|".join(parts[3:]).replace("\\n", "\n"))
    return state


def write(remote: RemoteAPI, grid: int, block: str, index: int, text: str) -> None:
    remote.batch(sets=[SetOp.run_pb(grid, PB, f"{block}|{index}|{text}")])
    time.sleep(0.3)


def samples(text: str) -> str:
    """16 characters spread over the text as hex code points, like the script's report"""
    return "".join(f"{ord(text[len(text) * k // 16]):04x}" for k in range(16)) if text else ""


# The grids and font sizes the plugin must pick for each screen (Tests/ImageGenTests.cs has the math)
EXPECTED = {
    ("LCD", 0): (177, 177, 0.1),
    ("Wide LCD", 0): (355, 177, 0.1),
    ("PB", 0): (177, 111, 0.1),
    ("PB", 1): (177, 71, 0.2),
    ("Seat", 0): (88, 61, 0.2),
    ("Seat", 3): (88, 50, 0.2),
}


def check_image(c: Checks, state: dict, key) -> None:
    s = state.get(key)
    cols, rows, size = EXPECTED[key]
    if not c.check(
        s is not None and s["columns"] > 0, f"{key[0]} screen {key[1]} shows an image", repr(s and s["head"])
    ):
        return
    c.check(
        (s["columns"], s["rows"]) == (cols, rows) and abs(s["fontSize"] - size) < 1e-3,
        f"{key[0]} screen {key[1]} grid fills the {s['surface']} surface",
        f"{s['columns']}x{s['rows']} at font size {s['fontSize']}, expected {cols}x{rows} at {size}",
    )
    c.check(
        s["font"] == "Monospace"
        and s["padding"] == 0
        and s["alignment"] == "CENTER"
        and s["content"] == "TEXT_AND_IMAGE",
        f"{key[0]} screen {key[1]} font, padding and alignment",
        f"{s['font']}, {s['padding']}, {s['alignment']}, {s['content']}",
    )


def history_entry(state: dict, block: str, index: int) -> str | None:
    _, tail = state.get((block, "data"), (0, ""))
    match = re.search(rf"(?m)^Screen\[{index}\]\.Prompt: (.*)$", tail)
    return match[1] if match else None


def run_checks(c: Checks, remote: RemoteAPI, grid: int, game_log) -> None:
    # Prompts on a single LCD and on two screens of each multi-screen block, all at once:
    # the image server runs one job at a time, so they finish one after another
    prompts = {
        ("LCD", 0): "a red fox in the snow",
        ("Wide LCD", 0): "a panorama of a mountain lake",
        ("PB", 0): "a robot reading a book",
        ("PB", 1): "a keyboard made of candy",
        ("Seat", 0): "a starry night over a desert",
        ("Seat", 3): "a green circuit board",
    }
    frames_before = remote.get_state().get("frameCounter", 0)
    started = time.monotonic()
    for (block, index), prompt in prompts.items():
        write(remote, grid, block, index, f"/ig {prompt}")
    # The prompts on the screens, before the first image arrives
    time.sleep(1)
    screenshot(remote, "prompts")

    def all_done():
        state = report(remote, grid)
        return state if all(state.get(k, {}).get("columns") for k in prompts) else None

    try:
        state = wait_until(all_done, IMAGES_TIMEOUT, "all images", 2)
    except TimeoutError:
        state = report(remote, grid)
    elapsed = time.monotonic() - started
    frames = remote.get_state().get("frameCounter", 0) - frames_before
    for key, prompt in prompts.items():
        check_image(c, state, key)
        c.check(
            history_entry(state, *key) == prompt,
            f"{key[0]} CustomData has Screen[{key[1]}]'s prompt",
            repr(state.get((key[0], "data"))),
        )
    c.check(
        elapsed >= len(prompts) * STUB_SECONDS * 0.9,
        "the image server ran the jobs one at a time",
        f"{len(prompts)} images in {elapsed:.1f} s with {STUB_SECONDS:g} s each",
    )

    def updates_logged():
        found = [float(m) for m in re.findall(r"longest plugin update ([\d.]+) ms", game_log())]
        return found if len(found) >= len(prompts) else None

    try:
        updates = wait_until(updates_logged, 60, "the plugin's log lines (the game flushes its log late)")
    except TimeoutError:
        updates = [float(m) for m in re.findall(r"longest plugin update ([\d.]+) ms", game_log())]
    log = game_log()
    c.check(
        len(updates) >= len(prompts) and max(updates) < 20,
        "the game thread never waited for generation",
        f"longest plugin update per image {updates}; client drew {frames} frames in {elapsed:.0f} s",
    )
    c.check(
        "Content moderation is enabled in the configuration, but it is not implemented yet" in log,
        "moderation config is read and reported as not implemented",
    )

    stand_before(remote, "LargeLCDPanelWide", 9)
    screenshot(remote, "screens")

    # A new prompt on a screen replaces its CustomData entry
    write(remote, grid, "LCD", 0, "/ig a blue owl at night")

    def second():
        s = report(remote, grid)
        done = s.get(("LCD", 0), {}).get("columns") and history_entry(s, "LCD", 0) == "a blue owl at night"
        return s if done and "Done: " in game_log().split("a blue owl at night", 1)[-1] else None

    state = wait_until(second, IMAGES_TIMEOUT, "the second LCD image")
    _, data = state[("LCD", "data")]
    c.check(
        data == "Screen[0].Prompt: a blue owl at night\n\n",
        "a second prompt replaces the screen's CustomData entry",
        repr(data),
    )

    # An entry that does not fit into the CustomData discards the prompt
    filler = "x" * 63990
    remote.batch(sets=[SetOp.custom_data(grid, WIDE, filler)])
    wait_until(lambda: report(remote, grid)[("Wide LCD", "data")][0] == len(filler), 30, "the filler CustomData")
    requests_before = STUB_LOG.read_text(errors="replace").count(" generated ")
    write(remote, grid, "Wide LCD", 0, "/ig a cat that does not fit")
    state = wait_until(
        lambda: "discarded" in (s := report(remote, grid)).get(("Wide LCD", 0), {}).get("head", "") and s,
        30,
        "the discard message",
    )
    time.sleep(STUB_SECONDS + 1)
    c.check(
        state[("Wide LCD", "data")][0] == len(filler)
        and STUB_LOG.read_text(errors="replace").count(" generated ") == requests_before,
        "an entry that does not fit is discarded and nothing is generated",
        f"screen: {state[('Wide LCD', 0)]['head']!r}, CustomData length {state[('Wide LCD', 'data')][0]}",
    )

    # Text that isn't a prompt stays as it is
    write(remote, grid, "PB", 1, "Hello /ig world")
    time.sleep(2)
    state = report(remote, grid)
    c.check(state[("PB", 1)]["head"] == "Hello /ig world", "plain text is left alone", state[("PB", 1)]["head"])

    check_chat(c, remote, grid, game_log)


# Unit vector from the grid toward the side its screens face, taken from where the character
# stood when the grid was pasted in front of it
FRONT: list[float] | None = None


def stand_before(remote: RemoteAPI, subtype: str, distance: float) -> list[float]:
    """Teleports the character to the screen side of a block and turns it to the block's center.
    The character starts on the screen side, since the grid is pasted in front of it facing it.
    A negative distance stands behind the block"""
    global FRONT
    block = remote.find_nearby_blocks(subtype=subtype, max_distance=200, limit=1)[0]
    target = block["worldPosition"]
    if FRONT is None:
        here = remote.get_character()["position"]
        away = [h - t for h, t in zip(here, target)]
        length = sum(a * a for a in away) ** 0.5
        FRONT = [a / length for a in away]
    spot = [t + f * distance for t, f in zip(target, FRONT)]
    remote.character_teleport(*spot)
    wait_until(
        lambda: sum((p - q) ** 2 for p, q in zip(remote.get_character()["position"], spot)) < 4,
        20,
        f"the character to arrive at the {subtype}",
        0.5,
    )
    remote.character_look_at(*target, tolerance=0.5)
    time.sleep(1)
    return target


def chat_replies(remote: RemoteAPI, before: dict) -> list[str]:
    seen = {(h.get("sender"), h.get("text"), h.get("timestamp")) for h in before.get("hud", [])}
    after = remote.get_chat_messages()
    return [
        h.get("text", "")
        for h in after.get("hud", [])
        if h.get("sender") == "LcdImageGen" and (h.get("sender"), h.get("text"), h.get("timestamp")) not in seen
    ]


def chat(remote: RemoteAPI, text: str, expect: str) -> list[str]:
    before = remote.get_chat_messages()
    remote.send_chat(text)
    try:
        return wait_until(
            lambda: (r := chat_replies(remote, before)) and any(expect in line for line in r) and r,
            15,
            f"a reply to {text!r}",
            0.5,
        )
    except TimeoutError:
        return chat_replies(remote, before)


def check_chat(c: Checks, remote: RemoteAPI, grid: int, game_log) -> None:
    # /ig while looking at the LCD
    stand_before(remote, "LargeLCDPanel", 4)
    replies = chat(remote, "/ig a sunset over the sea", "Generating")
    c.check(
        any("Generating an image on LCD screen #0" in r for r in replies),
        "chat /ig aimed at an LCD is accepted",
        repr(replies),
    )
    state = wait_until(
        lambda: history_entry(s := report(remote, grid), "LCD", 0) == "a sunset over the sea"
        and s[("LCD", 0)]["columns"]
        and "Done: " in game_log().split("Chat command of player", 1)[-1]
        and s,
        IMAGES_TIMEOUT,
        "the chat command's image",
    )
    check_image(c, state, ("LCD", 0))
    screenshot(remote, "lcd-chat")

    # !ig #1 at the programmable block's keyboard, then a second command while it runs
    stand_before(remote, "LargeProgrammableBlock", 4)
    replies = chat(remote, "!ig #1 a tiny robot", "Generating")
    c.check(
        any("Generating an image on PB screen #1" in r for r in replies),
        "chat !ig #1 aimed at a block with two screens picks screen 1",
        repr(replies),
    )
    replies = chat(remote, "/ig #0 a second robot", "in progress")
    c.check(
        any("in progress already" in r for r in replies),
        "a second chat prompt while one is pending is refused (one per player)",
        repr(replies),
    )
    state = wait_until(
        lambda: history_entry(s := report(remote, grid), "PB", 1) == "a tiny robot" and s[("PB", 1)]["columns"] and s,
        IMAGES_TIMEOUT,
        "the !ig image",
    )
    check_image(c, state, ("PB", 1))
    c.check(history_entry(state, "PB", 0) == "a robot reading a book", "the refused prompt left screen 0 alone")

    # Looking at nothing
    here = remote.get_character()["position"]
    remote.character_look_at(here[0], here[1] + 100, here[2], tolerance=2)
    replies = chat(remote, "/ig nothing to see", "Look at a screen")
    c.check(
        any("Look at a screen" in r for r in replies), "chat /ig without a screen in sight gets a reply", repr(replies)
    )


def screenshot(remote: RemoteAPI, name: str) -> None:
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    try:
        (ARTIFACTS / f"{name}.png").write_bytes(remote.screenshot_bytes())
        print(f"screenshot {ARTIFACTS / name}.png")
    except Exception as e:  # noqa: BLE001
        print(f"screenshot failed: {e}")


def paste_grid(remote: RemoteAPI) -> int:
    """Pastes the test grid in front of the character and returns its entity id. On a joined
    client the paste answers with another grid's id, so the grid is looked up by name"""
    remote.paste_blueprint(xml=blueprint_xml())
    grid = wait_until(
        lambda: next((g["entityId"] for g in remote.list_grids() if g["name"] == "LcdImageGen Test"), None),
        60,
        "the pasted grid",
    )
    # The server ignores script runs from farther than 15 m
    stand_before(remote, "LargeLCDPanelWide", 9)
    # A dedicated server refuses to run a pasted block's script, its owner changed, until it is
    # recompiled (the terminal's Recompile button)
    pb = wait_until(lambda: remote.get_block(grid, PB).get("entityId"), 60, "the programmable block")
    remote.recompile_pb(pb)
    wait_until(lambda: report(remote, grid).get(("LCD", "data")) is not None, 90, "the test script")
    return grid


def run_custom_data_off(c: Checks, remote: RemoteAPI, grid: int) -> None:
    other = "Other script's data\n"
    remote.batch(sets=[SetOp.custom_data(grid, LCD, other)])
    wait_until(lambda: report(remote, grid)[("LCD", "data")][1] == other, 30, "the LCD's own CustomData")
    write(remote, grid, "LCD", 0, "/ig a lighthouse in a storm")
    state = wait_until(
        lambda: (s := report(remote, grid)).get(("LCD", 0), {}).get("columns") and s, IMAGES_TIMEOUT, "the LCD image"
    )
    check_image(c, state, ("LCD", 0))
    c.check(
        state[("LCD", "data")][1] == other,
        "with WriteCustomData false the block's CustomData is left alone",
        repr(state[("LCD", "data")]),
    )


def run(mode: str, sd_server: str | None) -> int:
    c = Checks()
    stop_all()
    global IMAGES_TIMEOUT
    if sd_server:
        IMAGES_TIMEOUT = 900
    url = sd_server or start_stub(STUB_SECONDS)
    try:
        if mode == "offline":
            prepare_client(True, url)
            remote = start_client(connect=False)
            world = prepare_world(APPDATA / "Saves" / str(CLIENT_ID), "OFFLINE")
            remote.load(str(world))
            wait_world(remote)
            game_log = lambda: (APPDATA / "SpaceEngineers.log").read_text(errors="replace")  # noqa: E731
        else:
            prepare_server(url)
            start_server()
            prepare_client(False, url)
            remote = start_client(connect=True)
            wait_world(remote)
            game_log = server_game_log
        ensure_character(remote)
        grid = paste_grid(remote)
        print(f"pasted grid {grid}", flush=True)
        if not WRITE_CUSTOM_DATA:
            run_custom_data_off(c, remote, grid)
            return c.summary()
        run_checks(c, remote, grid, game_log)
        if mode == "ds":
            # The joined client has no plugin: the images came by the game's LCD sync
            state = report(remote, grid)
            for key, cell in ((("LCD", 0), LCD), (("PB", 0), PB)):
                surface = remote.get_surface(grid, cell, key[1])
                texts = [s.get("data") or "" for s in surface.get("sprites", []) if s.get("type") == "TEXT"]
                c.check(
                    any(len(t) == state[key]["length"] and samples(t) == state[key]["samples"] for t in texts),
                    f"the joined client draws the server's image on {key[0]} screen {key[1]}",
                    f"{len(texts)} text sprites, lengths {[len(t) for t in texts]}, server {state[key]['length']}",
                )
        screenshot(remote, f"{mode}-screens")
    finally:
        if not os.environ.get("LCD_KEEP"):
            stop_all()
    return c.summary()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mode", choices=["offline", "ds", "stop"])
    parser.add_argument("--sd-server", help="URL of a running sd-server instead of the stub")
    parser.add_argument("--no-custom-data", action="store_true", help="check WriteCustomData false only")
    args = parser.parse_args()
    WRITE_CUSTOM_DATA = not args.no_custom_data
    if args.mode == "stop":
        stop_all()
    else:
        sys.exit(run(args.mode, args.sd_server))

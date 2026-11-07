// Programmable block script of the in-game rig, pasted with the test grid (rig.py).
// Argument "<block name>|<surface index>|<text>" writes the text on that surface, like a
// player would. "report <n>" echoes the state of every test surface and the blocks' CustomData.

readonly string[] names = { "PB", "LCD", "Wide LCD", "Seat" };

public void Main(string argument)
{
    if (argument.StartsWith("report"))
    {
        Report(argument);
        return;
    }

    var parts = argument.Split(new[] { '|' }, 3);
    var provider = (IMyTextSurfaceProvider)Block(parts[0]);
    provider.GetSurface(int.Parse(parts[1])).WriteText(parts[2]);
    Echo("written");
}

IMyTerminalBlock Block(string name) => name == "PB" ? Me : GridTerminalSystem.GetBlockWithName(name);

// Every character of the art and the newline, to tell art from text with one Trim call:
// a loop over 30000 characters would exceed the script's instruction limit
readonly char[] artChars = MakeArtChars();

static char[] MakeArtChars()
{
    var chars = new char[513];
    for (var i = 0; i < 512; i++)
        chars[i] = (char)(0xE100 + i);
    chars[512] = '\n';
    return chars;
}

void Report(string tag)
{
    var sb = new StringBuilder(tag).Append('\n');
    var buffer = new StringBuilder();
    foreach (var name in names)
    {
        var block = Block(name);
        var provider = (IMyTextSurfaceProvider)block;
        for (var i = 0; i < provider.SurfaceCount; i++)
        {
            var s = provider.GetSurface(i);
            s.ReadText(buffer);
            var text = buffer.ToString();
            var lines = text.Split('\n');
            var art = text.Length > 0 && text.Trim(artChars).Length == 0;
            foreach (var line in lines)
                art &= line.Length == lines[0].Length;
            // 16 characters spread over the text, as hex code points, to compare with a client
            var samples = new StringBuilder();
            for (var k = 0; k < 16 && text.Length > 0; k++)
                samples.Append(((int)text[(int)((long)text.Length * k / 16)]).ToString("x4"));
            var head = art ? "" : text.Substring(0, Math.Min(80, text.Length)).Replace("\n", "\\n").Replace("|", "/");
            sb.Append($"S|{name}|{i}|{s.ContentType}|{s.Font}|{s.FontSize:0.000}|{s.TextPadding:0.000}|{s.Alignment}");
            sb.Append($"|{s.TextureSize.X:0}x{s.TextureSize.Y:0}|{s.SurfaceSize.X:0.0}x{s.SurfaceSize.Y:0.0}");
            sb.Append($"|{text.Length}|{(art ? lines[0].Length : 0)}x{(art ? lines.Length : 0)}|{samples}|{head}\n");
        }
        var data = block.CustomData;
        var tail = data.Substring(Math.Max(0, data.Length - 200)).Replace("\n", "\\n").Replace("|", "/");
        sb.Append($"C|{name}|{data.Length}|{tail}\n");
    }
    Echo(sb.ToString());
}

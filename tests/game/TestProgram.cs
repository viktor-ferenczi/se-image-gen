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

void Report(string tag)
{
    var sb = new StringBuilder(tag).Append('\n');
    var text = new StringBuilder();
    foreach (var name in names)
    {
        var block = Block(name);
        var provider = (IMyTextSurfaceProvider)block;
        for (var i = 0; i < provider.SurfaceCount; i++)
        {
            var s = provider.GetSurface(i);
            s.ReadText(text);
            var lines = text.ToString().Split('\n');
            var art = text.Length > 0;
            var uniform = true;
            var hash = 0;
            for (var j = 0; j < text.Length; j++)
            {
                var c = text[j];
                hash = unchecked(hash * 31 + c);
                if (c != '\n' && (c < '' || c > ''))
                    art = false;
            }
            foreach (var line in lines)
                uniform &= line.Length == lines[0].Length;
            var head = art ? "" : text.ToString(0, Math.Min(80, text.Length)).Replace("\n", "\\n").Replace("|", "/");
            sb.Append($"S|{name}|{i}|{s.ContentType}|{s.Font}|{s.FontSize:0.000}|{s.TextPadding:0.000}|{s.Alignment}");
            sb.Append($"|{s.TextureSize.X:0}x{s.TextureSize.Y:0}|{s.SurfaceSize.X:0.0}x{s.SurfaceSize.Y:0.0}");
            sb.Append($"|{text.Length}|{(art && uniform ? lines[0].Length : 0)}x{(art && uniform ? lines.Length : 0)}|{hash}|{head}\n");
        }
        var data = block.CustomData;
        var tail = data.Substring(Math.Max(0, data.Length - 200)).Replace("\n", "\\n").Replace("|", "/");
        sb.Append($"C|{name}|{data.Length}|{tail}\n");
    }
    Echo(sb.ToString());
}

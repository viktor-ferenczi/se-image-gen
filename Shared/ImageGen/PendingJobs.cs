using System.Collections.Generic;

namespace Shared.ImageGen;

// Screens with an image in progress, counted per player and in total. Sender 0 is a prompt written
// by a script or present at world load, which counts only in the total.
public class PendingJobs
{
    private readonly HashSet<(long, int)> screens = new HashSet<(long, int)>();
    private readonly Dictionary<ulong, int> perPlayer = new Dictionary<ulong, int>();

    public int Count => screens.Count;

    public bool Contains((long EntityId, int Index) screen) => screens.Contains(screen);

    public int CountOf(ulong sender) =>
        sender != 0 && perPlayer.TryGetValue(sender, out var count) ? count : 0;

    // Why a new job can't start, null if it can. Limits of 0 mean no limit.
    public string CheckLimits(ulong sender, int extraOfSender, int maxPerPlayer, int maxTotal)
    {
        var count = CountOf(sender) + extraOfSender;
        if (sender != 0 && maxPerPlayer > 0 && count >= maxPerPlayer)
            return $"You have {count} image(s) in progress already, try again when done.";

        if (maxTotal > 0 && screens.Count >= maxTotal)
            return $"The image server is busy with {screens.Count} image(s), try again later.";

        return null;
    }

    public void Add((long EntityId, int Index) screen, ulong sender)
    {
        if (!screens.Add(screen) || sender == 0)
            return;

        perPlayer[sender] = CountOf(sender) + 1;
    }

    public void Remove((long EntityId, int Index) screen, ulong sender)
    {
        if (!screens.Remove(screen) || sender == 0)
            return;

        var count = CountOf(sender);
        if (count <= 1)
            perPlayer.Remove(sender);
        else
            perPlayer[sender] = count - 1;
    }

    public void Clear()
    {
        screens.Clear();
        perPlayer.Clear();
    }
}

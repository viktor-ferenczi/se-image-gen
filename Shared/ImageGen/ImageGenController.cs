using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Shared.Config;
using Shared.Logging;
using Shared.Plugin;
using VRage.Game.GUI.TextPanel;
using VRage.Network;
using VRageMath;
using IngameSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IngameSurfaceProvider = Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;

namespace Shared.ImageGen;

// Turns "/ig <prompt>" written on an LCD surface into an image on that surface. Runs only where the
// local game is the server: on the dedicated server, in offline games and on the host of a friends game.
// Everything here runs on the game thread, except the HTTP call and the conversion to characters.
public static class ImageGenController
{
    private const string ErrorPrefix = "[LcdImageGen] ";

    private static IPluginConfig Config => Common.Config;
    private static IPluginLogger Log => Common.Logger;

    private static readonly AccessTools.FieldRef<MyTextPanelComponent, MyTerminalBlock> BlockRef =
        AccessTools.FieldRefAccess<MyTextPanelComponent, MyTerminalBlock>("m_block");

    private class Trigger
    {
        public MyTextPanelComponent Surface;
        public string Text;
        public ulong Sender;
    }

    private class Job
    {
        public MySession Session;
        public long EntityId;
        public int Index;
        public string Text;
        public string Prompt;
        public ScreenFit Fit;
        public ulong Sender;
        public Stopwatch Timer;
        public string Art;
        public string Error;
    }

    private static readonly ConcurrentQueue<Trigger> Triggers = new ConcurrentQueue<Trigger>();
    private static readonly ConcurrentQueue<Job> Finished = new ConcurrentQueue<Job>();
    private static readonly HashSet<(long, int)> Pending = new HashSet<(long, int)>();
    private static readonly Dictionary<ulong, int> PendingPerPlayer = new Dictionary<ulong, int>();
    private static MySession session;
    private static bool moderationWarned;

    // Longest game thread time spent here while jobs were pending, proves that generation never blocks
    private static double maxUpdateMs;

    // Harmony postfix of both MyTextPanelComponent.UpdateText overloads, keep it cheap
    public static void OnTextUpdated(MyTextPanelComponent surface)
    {
        var text = surface.Text;
        if (!Config.Enabled || !text.StartsWith(PromptHistory.Prefix, StringComparison.OrdinalIgnoreCase) || !Sync.IsServer)
            return;

        var context = MyEventContext.Current;
        var sender = context.IsValid && !context.IsLocallyInvoked ? context.Sender.Value : 0ul;
        Triggers.Enqueue(new Trigger { Surface = surface, Text = text, Sender = sender });
    }

    public static void Update()
    {
        if (MySession.Static != session)
        {
            session = MySession.Static;
            Pending.Clear();
            PendingPerPlayer.Clear();
            while (Triggers.TryDequeue(out _)) { }
        }

        if (session == null || !Sync.IsServer)
            return;

        if (Triggers.IsEmpty && Finished.IsEmpty && Pending.Count == 0)
            return;

        var timer = Stopwatch.StartNew();

        while (Triggers.TryDequeue(out var trigger))
        {
            try
            {
                Start(trigger);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to start image generation");
            }
        }

        while (Finished.TryDequeue(out var job))
        {
            try
            {
                Complete(job);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to show the generated image");
            }
        }

        maxUpdateMs = Math.Max(maxUpdateMs, timer.Elapsed.TotalMilliseconds);
    }

    private static void Start(Trigger trigger)
    {
        var surface = trigger.Surface;
        var block = BlockRef(surface);
        if (block == null || block.Closed || block.MarkedForClose || surface.Text != trigger.Text)
            return;

        var index = FindSurfaceIndex(block, surface);
        if (index < 0)
            return;

        var key = (block.EntityId, index);
        if (Pending.Contains(key))
            return;

        var prompt = PromptHistory.ParsePrompt(trigger.Text);
        if (prompt == null)
            return;

        var moderation = Config.Moderation;
        if (moderation != null && moderation.Enabled && !moderationWarned)
        {
            moderationWarned = true;
            Log.Warning("Content moderation is enabled in the configuration, but it is not implemented yet");
        }

        var limit = Config.MaxPendingPerPlayer;
        if (trigger.Sender != 0 && limit > 0 && PendingPerPlayer.TryGetValue(trigger.Sender, out var count) && count >= limit)
        {
            Log.Info($"Rejected: player {trigger.Sender} has {count} images pending; block {block.EntityId} screen {index}");
            ShowError(surface, $"You have {count} image(s) in progress already, try again when done.");
            return;
        }

        if (!PromptHistory.TryUpdate(block.CustomData, index, prompt, PromptHistory.MaxCustomDataLength, out var customData))
        {
            Log.Info($"Discarded: prompt does not fit into CustomData; block {block.EntityId} screen {index}");
            ShowError(surface, $"Prompt discarded: it does not fit into the block's Custom Data ({PromptHistory.MaxCustomDataLength} characters).");
            return;
        }

        if (customData != block.CustomData)
            block.CustomData = customData;

        var fit = ScreenFit.Calculate(
            surface.TextureSize.X,
            surface.TextureSize.Y,
            surface.SurfaceSize.X,
            surface.SurfaceSize.Y,
            Math.Min(Config.MaxCharacters, MyTextPanelComponent.MAX_NUMBER_CHARACTERS),
            Config.MinCellPixels
        );

        var job = new Job
        {
            Session = session,
            EntityId = block.EntityId,
            Index = index,
            Text = trigger.Text,
            Prompt = prompt,
            Fit = fit,
            Sender = trigger.Sender,
            Timer = Stopwatch.StartNew(),
        };

        Pending.Add(key);
        PendingPerPlayer.TryGetValue(trigger.Sender, out count);
        PendingPerPlayer[trigger.Sender] = count + 1;

        Log.Info(
            $"Generating: block {block.EntityId} \"{block.DisplayNameText}\" screen {index}, texture {surface.TextureSize}, surface {surface.SurfaceSize}, {fit}; prompt: {prompt}"
        );

        var url = Config.ServiceUrl;
        var timeout = Config.TimeoutSeconds;
        var dither = Config.Dither;
        Task.Run(() =>
        {
            try
            {
                var rgb = ImageGenClient.Generate(url, prompt, fit.Columns, fit.Rows, timeout);
                job.Art = LcdArt.FromRgb(rgb, fit.Columns, fit.Rows, dither);
            }
            catch (ImageGenException e)
            {
                job.Error = e.Message;
            }
            catch (Exception e)
            {
                job.Error = e.Message;
                Log.Error(e, "Image generation failed");
            }

            Finished.Enqueue(job);
        });
    }

    private static void Complete(Job job)
    {
        if (job.Session != session)
            return;

        Pending.Remove((job.EntityId, job.Index));
        if (PendingPerPlayer.TryGetValue(job.Sender, out var count))
        {
            if (count <= 1)
                PendingPerPlayer.Remove(job.Sender);
            else
                PendingPerPlayer[job.Sender] = count - 1;
        }

        var elapsed = $"{job.Timer.Elapsed.TotalSeconds:0.0} s, longest plugin update {maxUpdateMs:0.00} ms";
        maxUpdateMs = 0;

        if (!MyEntities.TryGetEntityById(job.EntityId, out var entity) || !(entity is MyTerminalBlock block) || block.Closed)
        {
            Log.Info($"Dropped: block {job.EntityId} is gone ({elapsed})");
            return;
        }

        var provider = block as IngameSurfaceProvider;
        if (provider == null || job.Index >= provider.SurfaceCount || !(provider.GetSurface(job.Index) is MyTextPanelComponent surface))
            return;

        if (surface.Text != job.Text)
        {
            Log.Info($"Dropped: block {job.EntityId} screen {job.Index} text changed meanwhile ({elapsed})");
            return;
        }

        if (job.Error != null)
        {
            Log.Info($"Failed: block {job.EntityId} screen {job.Index}: {job.Error} ({elapsed})");
            ShowError(surface, $"Image generation failed: {job.Error}");
            return;
        }

        var ingame = (IngameSurface)surface;
        surface.ContentType = ContentType.TEXT_AND_IMAGE;
        ingame.Font = "Monospace";
        surface.FontSize = job.Fit.FontSize;
        surface.FontColor = Color.White;
        surface.TextPadding = 0f;
        surface.Alignment = TextAlignment.CENTER;
        ingame.WriteText(job.Art);

        Log.Info($"Done: block {job.EntityId} screen {job.Index}, {job.Fit}, {job.Art.Length} characters ({elapsed})");
    }

    private static int FindSurfaceIndex(MyTerminalBlock block, MyTextPanelComponent surface)
    {
        if (!(block is IngameSurfaceProvider provider))
            return -1;

        for (var i = 0; i < provider.SurfaceCount; i++)
        {
            if (ReferenceEquals(provider.GetSurface(i), surface))
                return i;
        }

        return -1;
    }

    private static void ShowError(MyTextPanelComponent surface, string message)
    {
        ((IngameSurface)surface).WriteText(ErrorPrefix + message);
    }
}

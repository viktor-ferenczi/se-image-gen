using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Config;
using Shared.Logging;
using Shared.Plugin;
using VRage.Game.Entity;
using VRage.Game.GUI.TextPanel;
using VRage.GameServices;
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
    private const string ChatAuthor = "LcdImageGen";

    // How far a chat command reaches from the player's eyes
    private const double ChatReach = 10.0;

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

    private class ChatCommand
    {
        public ulong SteamId;
        public string Message;
    }

    private static readonly ConcurrentQueue<Trigger> Triggers = new ConcurrentQueue<Trigger>();
    private static readonly ConcurrentQueue<ChatCommand> ChatCommands =
        new ConcurrentQueue<ChatCommand>();

    // Prompts written on a screen by a chat command, so the job counts against that player
    private static readonly Dictionary<
        MyTextPanelComponent,
        (string Text, ulong SteamId)
    > ChatWrites = new Dictionary<MyTextPanelComponent, (string, ulong)>();
    private static MyMultiplayerBase chatMultiplayer;
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
        if (
            !Config.Enabled
            || !text.StartsWith(PromptHistory.Prefix, StringComparison.OrdinalIgnoreCase)
            || !Sync.IsServer
        )
            return;

        var context = MyEventContext.Current;
        var sender = context.IsValid && !context.IsLocallyInvoked ? context.Sender.Value : 0ul;
        Triggers.Enqueue(
            new Trigger
            {
                Surface = surface,
                Text = text,
                Sender = sender,
            }
        );
    }

    public static void Update()
    {
        if (MySession.Static != session)
        {
            UnhookChat();
            session = MySession.Static;
            Pending.Clear();
            PendingPerPlayer.Clear();
            ChatWrites.Clear();
            while (Triggers.TryDequeue(out _)) { }
            while (ChatCommands.TryDequeue(out _)) { }
            if (session != null && Sync.IsServer)
                HookChat();
        }

        if (session == null || !Sync.IsServer)
            return;

        if (Triggers.IsEmpty && Finished.IsEmpty && ChatCommands.IsEmpty && Pending.Count == 0)
            return;

        var timer = Stopwatch.StartNew();

        while (ChatCommands.TryDequeue(out var command))
        {
            try
            {
                HandleChatCommand(command);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to handle a chat command");
            }
        }

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

        if (ChatWrites.TryGetValue(surface, out var chatWrite))
        {
            ChatWrites.Remove(surface);
            if (trigger.Sender == 0 && chatWrite.Text == trigger.Text)
                trigger.Sender = chatWrite.SteamId;
        }

        var prompt = PromptHistory.ParsePrompt(trigger.Text);
        if (prompt == null)
            return;

        var moderation = Config.Moderation;
        if (moderation != null && moderation.Enabled && !moderationWarned)
        {
            moderationWarned = true;
            Log.Warning(
                "Content moderation is enabled in the configuration, but it is not implemented yet"
            );
        }

        var limit = Config.MaxPendingPerPlayer;
        if (
            trigger.Sender != 0
            && limit > 0
            && PendingPerPlayer.TryGetValue(trigger.Sender, out var count)
            && count >= limit
        )
        {
            Log.Info(
                $"Rejected: player {trigger.Sender} has {count} images pending; block {block.EntityId} screen {index}"
            );
            ShowError(
                surface,
                $"You have {count} image(s) in progress already, try again when done."
            );
            return;
        }

        if (
            !PromptHistory.TryUpdate(
                block.CustomData,
                index,
                prompt,
                PromptHistory.MaxCustomDataLength,
                out var customData
            )
        )
        {
            Log.Info(
                $"Discarded: prompt does not fit into CustomData; block {block.EntityId} screen {index}"
            );
            ShowError(
                surface,
                $"Prompt discarded: it does not fit into the block's Custom Data ({PromptHistory.MaxCustomDataLength} characters)."
            );
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
            $"Generating: block {block.EntityId} \"{block.DisplayNameText}\" screen {index}, texture {surface.TextureSize.X:0}x{surface.TextureSize.Y:0}, surface {surface.SurfaceSize.X:0.#}x{surface.SurfaceSize.Y:0.#}, {fit}; prompt: {prompt}"
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

        var elapsed =
            $"{job.Timer.Elapsed.TotalSeconds:0.0} s, longest plugin update {maxUpdateMs:0.00} ms";
        maxUpdateMs = 0;

        if (
            !MyEntities.TryGetEntityById(job.EntityId, out var entity)
            || !(entity is MyTerminalBlock block)
            || block.Closed
        )
        {
            Log.Info($"Dropped: block {job.EntityId} is gone ({elapsed})");
            return;
        }

        var provider = block as IngameSurfaceProvider;
        if (
            provider == null
            || job.Index >= provider.SurfaceCount
            || !(provider.GetSurface(job.Index) is MyTextPanelComponent surface)
        )
            return;

        if (surface.Text != job.Text)
        {
            Log.Info(
                $"Dropped: block {job.EntityId} screen {job.Index} text changed meanwhile ({elapsed})"
            );
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

        Log.Info(
            $"Done: block {job.EntityId} screen {job.Index}, {job.Fit}, {job.Art.Length} characters ({elapsed})"
        );
    }

    private static void HookChat()
    {
        // Messages typed on this machine: offline and by the host of a friends game
        MyAPIGateway.Utilities.MessageEnteredSender += OnLocalChat;

        // Messages of other players: on a dedicated server and on the host
        chatMultiplayer = MyMultiplayer.Static;
        if (chatMultiplayer != null)
            chatMultiplayer.ChatMessageReceived += OnRemoteChat;
    }

    private static void UnhookChat()
    {
        if (MyAPIGateway.Utilities != null)
            MyAPIGateway.Utilities.MessageEnteredSender -= OnLocalChat;

        if (chatMultiplayer != null)
            chatMultiplayer.ChatMessageReceived -= OnRemoteChat;
        chatMultiplayer = null;
    }

    private static void OnLocalChat(ulong sender, string message, ref bool sendToOthers)
    {
        if (Config.Enabled && PromptHistory.TryParseChatCommand(message, out _, out _))
            ChatCommands.Enqueue(new ChatCommand { SteamId = sender, Message = message });
    }

    private static void OnRemoteChat(
        ulong steamId,
        string message,
        ChatChannel channel,
        long targetId,
        ChatMessageCustomData? customData
    )
    {
        if (
            steamId != Sync.MyId
            && Config.Enabled
            && PromptHistory.TryParseChatCommand(message, out _, out _)
        )
            ChatCommands.Enqueue(new ChatCommand { SteamId = steamId, Message = message });
    }

    // Writes the prompt on the screen the player looks at, the rest goes the way of a typed prompt
    private static void HandleChatCommand(ChatCommand command)
    {
        if (!PromptHistory.TryParseChatCommand(command.Message, out var screen, out var prompt))
            return;

        if (!MySession.Static.Players.TryGetPlayerBySteamId(command.SteamId, out var player))
            return;

        var identityId = player.Identity.IdentityId;
        void Reply(string text) =>
            MyVisualScriptLogicProvider.SendChatMessage(text, ChatAuthor, identityId, "Red");

        var block = FindTargetedBlock(player.Character);
        if (!(block is IngameSurfaceProvider provider) || provider.SurfaceCount == 0)
        {
            Reply($"Look at a screen within {ChatReach:0} m to generate an image on it.");
            return;
        }

        if (!block.HasPlayerAccess(identityId))
        {
            Reply($"You have no access to {block.DisplayNameText}.");
            return;
        }

        var index = screen ?? 0;
        if (index >= provider.SurfaceCount)
        {
            Reply($"{block.DisplayNameText} has screens #0 to #{provider.SurfaceCount - 1}.");
            return;
        }

        // Prompts written by chat but not picked up yet count too
        var limit = Config.MaxPendingPerPlayer;
        PendingPerPlayer.TryGetValue(command.SteamId, out var count);
        foreach (var write in ChatWrites.Values)
            count += write.SteamId == command.SteamId ? 1 : 0;
        if (limit > 0 && count >= limit)
        {
            Reply($"You have {count} image(s) in progress already, try again when done.");
            return;
        }

        var surface = (MyTextPanelComponent)provider.GetSurface(index);
        var text = PromptHistory.Prefix + prompt;
        ChatWrites[surface] = (text, command.SteamId);
        if (surface.Text == text)
            Triggers.Enqueue(
                new Trigger
                {
                    Surface = surface,
                    Text = text,
                    Sender = command.SteamId,
                }
            );
        else
            ((IngameSurface)surface).WriteText(text);

        Log.Info(
            $"Chat command of player {command.SteamId}: block {block.EntityId} screen {index}"
        );
        Reply($"Generating an image on {block.DisplayNameText} screen #{index}.");
    }

    // The terminal block under the character's crosshair, if within reach
    private static MyTerminalBlock FindTargetedBlock(
        Sandbox.Game.Entities.Character.MyCharacter character
    )
    {
        if (character == null || character.IsDead)
            return null;

        // From the head bone also in third person, where the default is the camera's bone
        var head = character.GetHeadMatrix(false, true, false, true);
        var from = head.Translation;
        var direction = head.Forward;
        var hits = new List<MyPhysics.HitInfo>();
        MyPhysics.CastRay(from, from + direction * ChatReach, hits);

        var nearest = double.MaxValue;
        MyTerminalBlock target = null;
        foreach (var hit in hits)
        {
            if (
                !((hit.HkHitInfo.GetHitEntity() as MyEntity)?.GetTopMostParent() is MyCubeGrid grid)
            )
                continue;

            var distance = Vector3D.Distance(from, hit.Position);
            if (distance >= nearest)
                continue;

            // Just past the hit point, inside the block that was hit
            var cell = grid.WorldToGridInteger(hit.Position + direction * 0.05);
            nearest = distance;
            target = grid.GetCubeBlock(cell)?.FatBlock as MyTerminalBlock;
        }

        return target;
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

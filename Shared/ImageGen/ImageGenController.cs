using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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

// Turns "/ig <prompt>" written on an LCD surface into an image on that surface. Runs where the local
// game is the server: on the dedicated server, in offline games and on the host of a friends game.
// A client whose server doesn't run Image Generation does it for its own player: it generates the
// prompts that player writes and writes the image the way a player's edit does, so the game syncs it.
// Everything here runs on the game thread, except the HTTP call and the conversion to characters.
public static class ImageGenController
{
    private const string ErrorPrefix = "[ImageGen] ";
    private const string ChatAuthor = "ImageGen";

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

    // Prompts written on a screen by a chat command, so the job counts against that player. On a
    // client also the local player's edits: a client acts only on these when the server sends them back
    private static readonly Dictionary<
        MyTextPanelComponent,
        (string Text, ulong SteamId)
    > ExpectedWrites = new Dictionary<MyTextPanelComponent, (string, ulong)>();
    private static MyMultiplayerBase chatMultiplayer;
    private static readonly ConcurrentQueue<Job> Finished = new ConcurrentQueue<Job>();
    private static readonly PendingJobs Pending = new PendingJobs();
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
        )
            return;

        ulong sender;
        if (Sync.IsServer)
        {
            var context = MyEventContext.Current;
            sender = context.IsValid && !context.IsLocallyInvoked ? context.Sender.Value : 0ul;
        }
        else if (ExpectedWrites.TryGetValue(surface, out var write) && write.Text == text)
            sender = write.SteamId;
        else
            return;
        Triggers.Enqueue(
            new Trigger
            {
                Surface = surface,
                Text = text,
                Sender = sender,
            }
        );
    }

    // Text the local player wrote on a screen, on a client before it goes to the server
    public static void OnLocalEdit(MyTextPanelComponent surface, StringBuilder description)
    {
        if (
            surface == null
            || Sync.IsServer
            || ServerProbe.ServerHasPlugin == true
            || !Config.Enabled
        )
            return;

        // An unchanged text is not sent, nor generated again
        var text = description.ToString();
        if (
            text != surface.Text
            && text.StartsWith(PromptHistory.Prefix, StringComparison.OrdinalIgnoreCase)
        )
            ExpectedWrites[surface] = (text, Sync.MyId);
    }

    public static void Update()
    {
        if (MySession.Static != session)
        {
            UnhookChat();
            ServerProbe.Stop();
            session = MySession.Static;
            Pending.Clear();
            ExpectedWrites.Clear();
            while (Triggers.TryDequeue(out _)) { }
            while (ChatCommands.TryDequeue(out _)) { }
            if (session != null)
            {
                ServerProbe.Start();
                HookChat();
            }
        }

        if (session == null)
            return;

        if (!Sync.IsServer)
        {
            ServerProbe.Update();

            // The prompts wait for the server's answer
            if (ServerProbe.ServerHasPlugin == null)
                return;

            // The server does the work
            if (ServerProbe.ServerHasPlugin == true)
            {
                ExpectedWrites.Clear();
                while (Triggers.TryDequeue(out _)) { }
                while (ChatCommands.TryDequeue(out _)) { }
                return;
            }
        }

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

        if (ExpectedWrites.TryGetValue(surface, out var expected))
        {
            ExpectedWrites.Remove(surface);
            if (trigger.Sender == 0 && expected.Text == trigger.Text)
                trigger.Sender = expected.SteamId;
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

        // Over a limit the screen shows why instead of the job waiting in the image server
        var rejection = Pending.CheckLimits(
            trigger.Sender,
            0,
            Config.MaxPendingPerPlayer,
            Config.MaxPendingTotal
        );
        if (rejection != null)
        {
            Log.Info(
                $"Rejected: {rejection} Player {trigger.Sender}, {Pending.Count} images pending; block {block.EntityId} screen {index}"
            );
            ShowError(surface, rejection);
            return;
        }

        if (Config.WriteCustomData && !TryWritePrompt(block, surface, index, prompt))
            return;

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

        Pending.Add(key, trigger.Sender);

        Log.Info(
            $"Generating: block {block.EntityId} \"{block.DisplayNameText}\" screen {index}, texture {surface.TextureSize.X:0}x{surface.TextureSize.Y:0}, surface {surface.SurfaceSize.X:0.#}x{surface.SurfaceSize.Y:0.#}, {fit}; prompt: {prompt}"
        );

        var url = Config.ImageServerUrl;
        var timeout = Config.TimeoutSeconds;
        var pixels = Config.ImagePixels;
        var steps = Config.Steps;
        var cfgScale = Config.CfgScale;
        var dither = Config.Dither;
        Task.Run(() =>
        {
            try
            {
                var rgb = ImageGenClient.Generate(
                    url,
                    prompt,
                    fit.Columns,
                    fit.Rows,
                    pixels,
                    steps,
                    cfgScale,
                    timeout
                );
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

    // Keeps the prompt in the block's Custom Data, false if it doesn't fit
    private static bool TryWritePrompt(
        MyTerminalBlock block,
        MyTextPanelComponent surface,
        int index,
        string prompt
    )
    {
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
            return false;
        }

        if (customData != block.CustomData)
            block.CustomData = customData;
        return true;
    }

    private static void Complete(Job job)
    {
        if (job.Session != session)
            return;

        Pending.Remove((job.EntityId, job.Index), job.Sender);

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
        if (!Sync.IsServer)
            return;
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
        if (!Config.Enabled || !PromptHistory.TryParseChatCommand(message, out _, out _))
            return;

        if (!Sync.IsServer)
        {
            // The server's plugin takes it from the chat
            if (ServerProbe.ServerHasPlugin == true)
                return;

            // Never shown to the other players
            sendToOthers = false;
            if (ServerProbe.ServerHasPlugin == null)
            {
                ShowLocalChat(
                    "Checking whether the server runs Image Generation, try again in a few seconds."
                );
                return;
            }
        }

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
        void Reply(string text)
        {
            if (Sync.IsServer)
                MyVisualScriptLogicProvider.SendChatMessage(text, ChatAuthor, identityId, "Red");
            else
                ShowLocalChat(text);
        }

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
        var written = 0;
        foreach (var write in ExpectedWrites.Values)
            written += write.SteamId == command.SteamId ? 1 : 0;
        var rejection = Pending.CheckLimits(
            command.SteamId,
            written,
            Config.MaxPendingPerPlayer,
            Config.MaxPendingTotal
        );
        if (rejection != null)
        {
            Reply(rejection);
            return;
        }

        var surface = (MyTextPanelComponent)provider.GetSurface(index);
        var text = PromptHistory.Prefix + prompt;
        ExpectedWrites[surface] = (text, command.SteamId);
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

    // A reply only the local player sees
    private static void ShowLocalChat(string text) =>
        MyHud.Chat.multiplayer_ScriptedChatMessageReceived(text, ChatAuthor, "Red", Color.White);

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

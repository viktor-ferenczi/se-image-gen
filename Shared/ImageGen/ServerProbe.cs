using System;
using System.Diagnostics;
using System.Text;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Logging;
using Shared.Plugin;

namespace Shared.ImageGen;

// Tells a client whether its server runs Image Generation. The client sends a probe message, the
// server plugin answers it while enabled and a server without the plugin drops it. Game thread only.
public static class ServerProbe
{
    // Arbitrary, unlikely to be used by a mod
    private const ushort MessageId = 48862;
    private const double ResendSeconds = 2;
    private const double TimeoutSeconds = 10;

    private static IPluginLogger Log => Common.Logger;

    // Null while the client waits for the answer
    public static bool? ServerHasPlugin { get; private set; }

    private static bool registered;
    private static bool isServer;
    private static Stopwatch timer;
    private static double lastSent;

    public static void Start()
    {
        isServer = Sync.IsServer;
        ServerHasPlugin = isServer ? true : null;
        timer = null;
        MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(MessageId, OnMessage);
        registered = true;
    }

    public static void Stop()
    {
        // The game keeps the handlers of past sessions
        if (registered)
            MyAPIGateway.Multiplayer?.UnregisterSecureMessageHandler(MessageId, OnMessage);
        registered = false;
        ServerHasPlugin = null;
    }

    public static void Update()
    {
        if (ServerHasPlugin != null || !MySession.Static.Ready)
            return;

        if (timer == null)
        {
            timer = Stopwatch.StartNew();
            lastSent = -ResendSeconds;
        }

        var elapsed = timer.Elapsed.TotalSeconds;
        if (elapsed >= TimeoutSeconds)
        {
            ServerHasPlugin = false;
            Log.Info(
                "The server does not run Image Generation, this client generates its player's images"
            );
            return;
        }

        if (elapsed - lastSent >= ResendSeconds)
        {
            lastSent = elapsed;
            MyAPIGateway.Multiplayer.SendMessageToServer(MessageId, Array.Empty<byte>(), true);
        }
    }

    private static void OnMessage(ushort id, byte[] message, ulong sender, bool fromServer)
    {
        try
        {
            if (isServer)
            {
                if (!fromServer && Common.Config.Enabled)
                {
                    var version = typeof(ServerProbe).Assembly.GetName().Version.ToString(3);
                    MyAPIGateway.Multiplayer.SendMessageTo(
                        MessageId,
                        Encoding.UTF8.GetBytes(version),
                        sender,
                        true
                    );
                }
            }
            else if (fromServer && ServerHasPlugin != true)
            {
                ServerHasPlugin = true;
                Log.Info(
                    $"The server runs Image Generation {Encoding.UTF8.GetString(message)}, it generates the images"
                );
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to handle a probe message");
        }
    }
}

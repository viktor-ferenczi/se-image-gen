using System.ComponentModel;

namespace Shared.Config;

public interface IPluginConfig : INotifyPropertyChanged
{
    // Enables the plugin
    bool Enabled { get; set; }

    // Base URL of the image generation service, see service/README.md
    string ServiceUrl { get; set; }

    // Seconds to wait for an image, including the time queued in the service
    int TimeoutSeconds { get; set; }

    // Prompts a player may have waiting at the same time, 0 for no limit
    int MaxPendingPerPlayer { get; set; }

    // Upper limit of the art's length (the game's limit is 100000 characters)
    int MaxCharacters { get; set; }

    // Smallest character cell in texture pixels, 2.88 is font size 0.1 on a 512 pixel screen
    float MinCellPixels { get; set; }

    // Floyd-Steinberg dithering for smoother gradients with 512 colors
    bool Dither { get; set; }

    // Optional content moderation of the prompts, not implemented yet
    ModerationConfig Moderation { get; set; }
}

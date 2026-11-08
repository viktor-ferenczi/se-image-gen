namespace Shared.Config;

// Content moderation of the prompts. Not implemented yet: it does nothing, even if enabled.
public class ModerationConfig
{
    public bool Enabled { get; set; }

    // Instructions for the moderating model, required if enabled
    public string Prompt { get; set; } = "";
}

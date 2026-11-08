using System;
using System.Text.RegularExpressions;

namespace Shared.ImageGen;

public static class PromptHistory
{
    public const string Prefix = "/ig ";

    // Chat commands, aimed at the screen the player looks at
    public static readonly string[] ChatPrefixes = { "/ig ", "!ig " };

    // MyTerminalBlock truncates CustomData at this length
    public const int MaxCustomDataLength = 64000;

    // The prompt in an LCD text, or null if the text is not an image generation request
    public static string ParsePrompt(string text)
    {
        if (text == null || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var prompt = text.Substring(Prefix.Length).Trim();
        return prompt.Length == 0 ? null : prompt;
    }

    // A chat command: "/ig [#<screen>] <prompt>" or the same with "!ig". Returns false if the message
    // is not one; screen is null when not given.
    public static bool TryParseChatCommand(string message, out int? screen, out string prompt)
    {
        screen = null;
        prompt = null;
        if (message == null)
            return false;

        var prefix = Array.Find(
            ChatPrefixes,
            p => message.StartsWith(p, StringComparison.OrdinalIgnoreCase)
        );
        if (prefix == null)
            return false;

        var match = Regex.Match(
            message.Substring(prefix.Length),
            @"^\s*(?:#(\d{1,2})(?:\s+|$))?(.*)$",
            RegexOptions.Singleline
        );
        prompt = match.Groups[2].Value.Trim();
        if (match.Groups[1].Success)
            screen = int.Parse(match.Groups[1].Value);
        return prompt.Length != 0;
    }

    // Writes "Screen[<index>].Prompt: <prompt>" and a blank line into the CustomData, in place of the
    // screen's earlier entry or at the end. CustomData uses "\n" line endings, so line breaks inside
    // the prompt become spaces. Returns false if the result would not fit, leaving the CustomData as is.
    public static bool TryUpdate(
        string customData,
        int screenIndex,
        string prompt,
        int maxLength,
        out string updated
    )
    {
        customData = customData ?? "";
        var entry =
            $"Screen[{screenIndex}].Prompt: {Regex.Replace(prompt, @"\s*[\r\n]+\s*", " ")}\n\n";

        var existing = Regex.Match(
            customData,
            $@"(?m)^Screen\[{screenIndex}\]\.Prompt: [^\n]*(\n\n|\n|$)"
        );

        if (existing.Success)
        {
            updated =
                customData.Substring(0, existing.Index)
                + entry
                + customData.Substring(existing.Index + existing.Length);
        }
        else
        {
            var separator =
                customData.Length == 0 || customData.EndsWith("\n\n") ? ""
                : customData.EndsWith("\n") ? "\n"
                : "\n\n";
            updated = customData + separator + entry;
        }

        if (updated.Length <= maxLength)
            return true;

        updated = customData;
        return false;
    }
}

using System;
using System.Text.RegularExpressions;

namespace Shared.ImageGen;

public static class PromptHistory
{
    public const string Prefix = "/ig ";

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

    // Writes "Screen[<index>].Prompt: <prompt>" and a blank line into the CustomData, in place of the
    // screen's earlier entry or at the end. CustomData uses "\n" line endings, so line breaks inside
    // the prompt become spaces. Returns false if the result would not fit, leaving the CustomData as is.
    public static bool TryUpdate(string customData, int screenIndex, string prompt, int maxLength, out string updated)
    {
        customData = customData ?? "";
        var entry = $"Screen[{screenIndex}].Prompt: {Regex.Replace(prompt, @"\s*[\r\n]+\s*", " ")}\n\n";

        var existing = Regex.Match(
            customData,
            $@"(?m)^Screen\[{screenIndex}\]\.Prompt: [^\n]*(\n\n|\n|$)"
        );

        if (existing.Success)
        {
            updated = customData.Substring(0, existing.Index) + entry + customData.Substring(existing.Index + existing.Length);
        }
        else
        {
            var separator = customData.Length == 0 || customData.EndsWith("\n\n") ? "" : customData.EndsWith("\n") ? "\n" : "\n\n";
            updated = customData + separator + entry;
        }

        if (updated.Length <= maxLength)
            return true;

        updated = customData;
        return false;
    }
}

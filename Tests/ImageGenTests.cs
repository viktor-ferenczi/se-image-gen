using System;
using System.Linq;
using Shared.ImageGen;
using Xunit;

public class ScreenFitTests
{
    [Theory]
    // Square LCD: 512 x 512 texture, font size 0.1 is the usual color-character art
    [InlineData(512, 512, 512, 512, 177, 177, 0.1f)]
    // Wide LCD: 1024 x 512 texture, cells are scaled by the shorter side
    [InlineData(1024, 512, 1024, 512, 355, 177, 0.1f)]
    // Billboard style 3:5 face: 512 x 512 texture, 307 x 512 visible
    [InlineData(512, 512, 307.2f, 512, 106, 177, 0.1f)]
    // Small 256 x 128 screen: text is drawn at quarter scale, so the same cell is font size 0.4
    [InlineData(256, 128, 256, 128, 88, 44, 0.4f)]
    public void FitsUsualScreensAtTheMinimumCell(
        float tx,
        float ty,
        float sx,
        float sy,
        int columns,
        int rows,
        float fontSize
    )
    {
        var fit = ScreenFit.Calculate(tx, ty, sx, sy, 100000, 2.88f);
        Assert.Equal(columns, fit.Columns);
        Assert.Equal(rows, fit.Rows);
        Assert.Equal(fontSize, fit.FontSize, 3);
    }

    [Theory]
    [InlineData(512, 512, 512, 512, 2000)]
    [InlineData(1024, 512, 1024, 512, 10000)]
    [InlineData(512, 512, 307.2f, 512, 31)]
    [InlineData(256, 128, 256, 128, 100000)]
    public void StaysWithinTheCharacterLimitAndTheSurface(
        float tx,
        float ty,
        float sx,
        float sy,
        int maxCharacters
    )
    {
        var fit = ScreenFit.Calculate(tx, ty, sx, sy, maxCharacters, 0.5f);
        var cell = fit.FontSize * ScreenFit.CellPixelsPerFontSize * Math.Min(tx, ty) / 512f;
        Assert.True(fit.Length <= maxCharacters, $"{fit} is {fit.Length} characters");
        Assert.True(fit.Columns * cell <= sx + 1e-3);
        Assert.True(fit.Rows * cell <= sy + 1e-3);
        // Filled: one more column or row would not fit
        Assert.True(
            (fit.Columns + 1) * cell > sx
                || (fit.Rows + 1) * cell > sy
                || fit.Length > maxCharacters / 2
        );
        Assert.Equal(fit.FontSize, (float)Math.Round(fit.FontSize, 3));
    }
}

public class LcdArtTests
{
    [Fact]
    public void MapsColorsTo512ColorCharacters()
    {
        var rgb = new byte[]
        {
            0,
            0,
            0,
            255,
            255,
            255,
            255,
            0,
            0,
            0,
            255,
            0,
            0,
            0,
            255,
            128,
            128,
            128,
        };
        var art = LcdArt.FromRgb(rgb, 3, 2, dither: false);
        Assert.Equal("\n", art);
    }

    [Fact]
    public void DitheringKeepsTheAverageBrightness()
    {
        const int size = 64;
        var rgb = Enumerable.Repeat((byte)100, size * size * 3).ToArray();
        var art = LcdArt.FromRgb(rgb, size, size, dither: true);
        var levels = art.Where(c => c != '\n').Select(c => (c - 0xE100) >> 6).ToArray();
        Assert.Equal(size * size, levels.Length);
        Assert.InRange(levels.Average() * 255 / 7, 97, 103);
        Assert.True(levels.Distinct().Count() > 1);
    }

    [Fact]
    public void RejectsWrongLength() =>
        Assert.Throws<ArgumentException>(() => LcdArt.FromRgb(new byte[5], 1, 2, false));
}

public class PromptHistoryTests
{
    [Theory]
    [InlineData("/ig a red fox", "a red fox")]
    [InlineData("/IG  a red fox \n", "a red fox")]
    [InlineData("/ig two\nlines", "two\nlines")]
    [InlineData("/ig ", null)]
    [InlineData("/ig", null)]
    [InlineData(" /ig a fox", null)]
    [InlineData("/igloo", null)]
    [InlineData("Hello", null)]
    [InlineData(null, null)]
    public void ParsesPrompts(string text, string expected) =>
        Assert.Equal(expected, PromptHistory.ParsePrompt(text));

    [Theory]
    [InlineData("/ig a red fox", null, "a red fox")]
    [InlineData("!ig a red fox", null, "a red fox")]
    [InlineData("!IG  #2 a red fox ", 2, "a red fox")]
    [InlineData("/ig #0 two\nlines", 0, "two\nlines")]
    [InlineData("/ig #a fox", null, "#a fox")]
    [InlineData("/ig 2 foxes", null, "2 foxes")]
    public void ParsesChatCommands(string message, int? screen, string prompt)
    {
        Assert.True(
            PromptHistory.TryParseChatCommand(message, out var parsedScreen, out var parsedPrompt)
        );
        Assert.Equal(screen, parsedScreen);
        Assert.Equal(prompt, parsedPrompt);
    }

    [Theory]
    [InlineData("/ig ")]
    [InlineData("/ig #3")]
    [InlineData("/ig #3 ")]
    [InlineData("ig a fox")]
    [InlineData("/igloo")]
    [InlineData("hello /ig a fox")]
    [InlineData(null)]
    public void IgnoresOtherChat(string message) =>
        Assert.False(PromptHistory.TryParseChatCommand(message, out _, out _));

    [Fact]
    public void AppendsToEmptyCustomData()
    {
        Assert.True(PromptHistory.TryUpdate("", 2, "a fox", 64000, out var data));
        Assert.Equal("Screen[2].Prompt: a fox\n\n", data);
    }

    [Theory]
    [InlineData("config=1", "config=1\n\nScreen[0].Prompt: a fox\n\n")]
    [InlineData("config=1\n", "config=1\n\nScreen[0].Prompt: a fox\n\n")]
    [InlineData("config=1\n\n", "config=1\n\nScreen[0].Prompt: a fox\n\n")]
    public void KeepsOtherContentApart(string before, string expected)
    {
        Assert.True(PromptHistory.TryUpdate(before, 0, "a fox", 64000, out var data));
        Assert.Equal(expected, data);
    }

    [Fact]
    public void ReplacesTheScreensEntryInPlace()
    {
        var before =
            "Screen[0].Prompt: a cat\n\nScreen[1].Prompt: a dog\n\nScreen[10].Prompt: a cow\n\n";
        Assert.True(PromptHistory.TryUpdate(before, 1, "a fox", 64000, out var data));
        Assert.Equal(
            "Screen[0].Prompt: a cat\n\nScreen[1].Prompt: a fox\n\nScreen[10].Prompt: a cow\n\n",
            data
        );
        Assert.True(PromptHistory.TryUpdate(data, 0, "an owl", 64000, out data));
        Assert.Equal(
            "Screen[0].Prompt: an owl\n\nScreen[1].Prompt: a fox\n\nScreen[10].Prompt: a cow\n\n",
            data
        );
    }

    [Fact]
    public void LineBreaksInThePromptBecomeSpaces()
    {
        Assert.True(PromptHistory.TryUpdate("", 0, "two\r\n  lines\nhere", 64000, out var data));
        Assert.Equal("Screen[0].Prompt: two lines here\n\n", data);
    }

    [Fact]
    public void DiscardsAnEntryThatDoesNotFit()
    {
        var before = new string('x', 63990);
        Assert.False(PromptHistory.TryUpdate(before, 0, "a red fox", 64000, out var data));
        Assert.Same(before, data);
        Assert.True(
            PromptHistory.TryUpdate(new string('x', 63960), 0, "a red fox", 64000, out data)
        );
        Assert.Equal(63960 + 2 + 29, data.Length);
    }

    [Fact]
    public void ReplacingAShorterEntryFitsWhereAppendingWouldNot()
    {
        var before =
            new string('x', 63900) + "\n\nScreen[0].Prompt: " + new string('y', 70) + "\n\n";
        Assert.True(PromptHistory.TryUpdate(before, 0, "a fox", 64000, out var data));
        Assert.EndsWith("Screen[0].Prompt: a fox\n\n", data);
    }
}

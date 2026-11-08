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

public class RgbImageTests
{
    // 13 x 11 PNG files written row by row with filters 0 to 4 in turn (None, Sub, Up, Average,
    // Paeth), checked against Pillow. Red is (x * 37 + y * 11) % 256 in all of them.
    private const string Rgb =
        "iVBORw0KGgoAAAANSUhEUgAAAA0AAAALCAIAAAAr0JA2AAABcklEQVR42o3PsUsbcRjG8Yvny8v5colJOPMGf9GYy9vIWyqc8NogxZMKgtoMFqdS6BAnoYoOheJkKS5dKorGVVDI3qGlXToLrk6K/0EFt06Nri7dH74PH8/zQDKw2A8bARwMwo8yXNfBT2B8FlrLsLUKRx/h11fIkPV2KD4KoCBKgEIoIUoOJY9SRIlQSthH5pEhWZYsInNkMZmSJWRNspRsnqxFtuIPrfk8nGVX4ErEI2UedVytdsZirjU4Vq5P8GnCZ9Z/38sg+VmCiNBR4IichI5yjvLu/iFyVFrxngfwdmB8J1w6K6yfl/dua9+iZ5fT03/fLVQ+v5ntvm9ffNq9O+5mXs70HCq+CqigSqBCKqFKTiWvUlSJVEr6347JjZ6jyi7mSoNHtDM6wdWEx4xrTY5fcD3l07nOk/nHjpRIKUwo16T8Qy9KqZR66wXYLy59H9q7Gr7sq1UaT9uvprqbc38OX9vP9vbNh9/wBfWk9Q8OBU1gjMTHKAAAAABJRU5ErkJggg==";
    private const string Rgba =
        "iVBORw0KGgoAAAANSUhEUgAAAA0AAAALCAYAAACksgdhAAABmklEQVR42p3QvUtbURjH8ed6fbjcc0jSvFxzQm7ekxquKES4bQhigkLAphkUJyl0yJkEI+1QWjpVpEsXpUXjWqiQvUNFF7cDha5Oiv+BgpuTP7q7OHx4ONN5ni8RMdUspleTTNsu049nTCcZpqsqk91gqneY+qtM7zXT4Semsz0mS4acqlkO1WxgcMAFCRGIQRyS4EHaoQkZUkqGDkTBAx8qEEADmtCGLvRhPWVPbdp1lY0K5SeEynlC5TNCFXyhikUxKlWEKk8LVQmEqs4J9ash1HEoJv//hPWkHSXJHknHJ+mC9LEeZgziEIIH6XWily7rN6Kuv0R6+jgx1H8z+/q2/Ft7sxe61brXb1dyenejo8dbA/1v56u+Oxpra2mRP9asACGAwQEXJEQgBnFIggfp4Ikh5rftDZUtIgSOzuHofCBGBRxdxNGlECGaCLGAEG2EWBaj593HQrQRIiAZaSBEEyHwDrsIgZluEw0TbL4ne+bP1L65zF6YiXLOTM8MzOsXY/Nu+cYcrIXmdPDZXH84N/zNMcHPvnkA2tdgyhy0OFYAAAAASUVORK5CYII=";
    private const string Gray =
        "iVBORw0KGgoAAAANSUhEUgAAAA0AAAALCAAAAACB2Vi9AAAAVUlEQVR42mNgUPXKn7LzHrOGb9H0PYzcqkiAiRsZMItJgMEMMMkCFVUFkwzmMY3LTn8QtYpvWXnuM6MTHlOMEGZIzGBBkWPIm7zjLpO6T+G03Q9YtQBhbhxrSHUDiAAAAABJRU5ErkJggg==";
    private const string GrayAlpha =
        "iVBORw0KGgoAAAANSUhEUgAAAA0AAAALCAQAAAAOu8/qAAAAg0lEQVR42mNgYFDl9BLKl56islP3nhmzvYaHb2BR1PTkPTmM3EAp7JCJmwEXZBZjkGBFwBlIbBZUlapIbAZzhhjORqFl0qdVPuiKmlnZx3u0BK6MOpf8OYfRiSxnGDFgcwKIx4JbF0Mew2TOHUJ3pZlU1HV9zArtp3nsDnwQxZqslQMAr0ol9QdhXIwAAAAASUVORK5CYII=";
    private const string Bits16 = "iVBORw0KGgoAAAANSUhEUgAAAA0AAAALEAIAAAB7QEx1AAAAAElFTkSuQmCC";

    private static (int R, int G, int B) Expected(int x, int y) =>
        ((x * 37 + y * 11) % 256, (x * x + y * 53) % 256, (x * y * 7 + 5) % 256);

    [Theory]
    [InlineData(Rgb)]
    [InlineData(Rgba)]
    public void DecodesColorPngWithEveryFilter(string png)
    {
        var image = RgbImage.FromPng(Convert.FromBase64String(png));
        Assert.Equal(13, image.Width);
        Assert.Equal(11, image.Height);
        for (var y = 0; y < 11; y++)
        for (var x = 0; x < 13; x++)
        {
            var i = (y * 13 + x) * 3;
            Assert.Equal(
                Expected(x, y),
                (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2])
            );
        }
    }

    [Theory]
    [InlineData(Gray)]
    [InlineData(GrayAlpha)]
    public void DecodesGrayPngAsGrayRgb(string png)
    {
        var image = RgbImage.FromPng(Convert.FromBase64String(png));
        for (var y = 0; y < 11; y++)
        for (var x = 0; x < 13; x++)
        {
            var i = (y * 13 + x) * 3;
            var r = Expected(x, y).R;
            Assert.Equal((r, r, r), (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]));
        }
    }

    [Fact]
    public void RefusesWhatItCannotDecode()
    {
        Assert.Throws<System.IO.InvalidDataException>(() =>
            RgbImage.FromPng(Convert.FromBase64String(Bits16))
        );
        Assert.Throws<System.IO.InvalidDataException>(() =>
            RgbImage.FromPng(new byte[] { 1, 2, 3 })
        );
        var truncated = Convert.FromBase64String(Rgb).Take(60).ToArray();
        Assert.Throws<System.IO.InvalidDataException>(() => RgbImage.FromPng(truncated));
    }

    [Fact]
    public void ResizeAveragesTheCoveredPixels()
    {
        // 4 x 2: left half black and white, right half all 90
        var pixels = new byte[]
        {
            0,
            0,
            0,
            255,
            255,
            255,
            90,
            90,
            90,
            90,
            90,
            90,
            255,
            255,
            255,
            0,
            0,
            0,
            90,
            90,
            90,
            90,
            90,
            90,
        };
        var small = new RgbImage(4, 2, pixels).Resize(2, 1);
        Assert.Equal(new byte[] { 128, 128, 128, 90, 90, 90 }, small.Pixels);
    }

    [Fact]
    public void ResizeKeepsTheImageWhenTheSizeIsTheSame()
    {
        var image = RgbImage.FromPng(Convert.FromBase64String(Rgb));
        Assert.Equal(image.Pixels, image.Resize(13, 11).Pixels);
    }
}

public class ImageSizeTests
{
    [Theory]
    [InlineData(178, 178, 512, 512)]
    [InlineData(355, 177, 720, 368)]
    [InlineData(100, 400, 256, 1024)]
    [InlineData(1, 1, 512, 512)]
    [InlineData(1000, 1, 16192, 64)]
    public void KeepsTheAspectAndTheArea(int columns, int rows, int width, int height)
    {
        Assert.Equal((width, height), ImageGenClient.ImageSize(columns, rows, 512 * 512));
    }
}

public class PendingJobsTests
{
    [Fact]
    public void ScriptPromptsStopAtTheTotalLimit()
    {
        var pending = new PendingJobs();
        var started = 0;
        // A script writes a prompt on 20 screens, none of them by a player
        for (var screen = 0; screen < 20; screen++)
        {
            if (pending.CheckLimits(0, 0, 1, 3) != null)
                continue;
            pending.Add((screen, 0), 0);
            started++;
        }

        Assert.Equal(3, started);
        Assert.Equal(3, pending.Count);
        Assert.StartsWith("The image server is busy", pending.CheckLimits(0, 0, 1, 3));
        Assert.StartsWith("The image server is busy", pending.CheckLimits(42, 0, 1, 3));

        pending.Remove((0, 0), 0);
        Assert.Null(pending.CheckLimits(42, 0, 1, 3));
    }

    [Fact]
    public void PlayersCountInTheTotalAndTheirOwnLimit()
    {
        var pending = new PendingJobs();
        pending.Add((1, 0), 42);
        Assert.StartsWith("You have 1 image(s)", pending.CheckLimits(42, 0, 1, 3));
        Assert.Null(pending.CheckLimits(7, 0, 1, 3));
        // A prompt written by chat but not picked up yet counts against the player
        Assert.StartsWith("You have 1 image(s)", pending.CheckLimits(7, 1, 1, 3));

        pending.Add((2, 0), 7);
        pending.Add((3, 1), 0);
        Assert.StartsWith("The image server is busy with 3", pending.CheckLimits(8, 0, 1, 3));

        pending.Remove((1, 0), 42);
        Assert.Equal(0, pending.CountOf(42));
        Assert.Equal(1, pending.CountOf(7));
        Assert.Null(pending.CheckLimits(42, 0, 1, 3));
    }

    [Fact]
    public void ZeroMeansNoLimit()
    {
        var pending = new PendingJobs();
        for (var screen = 0; screen < 50; screen++)
            pending.Add((screen, 0), 42);
        Assert.Null(pending.CheckLimits(42, 0, 0, 0));
    }

    [Fact]
    public void AddingAScreenTwiceCountsOnce()
    {
        var pending = new PendingJobs();
        pending.Add((1, 0), 42);
        pending.Add((1, 0), 42);
        Assert.Equal(1, pending.Count);
        Assert.Equal(1, pending.CountOf(42));
        pending.Remove((1, 0), 42);
        pending.Remove((1, 0), 42);
        Assert.Equal(0, pending.Count);
    }
}

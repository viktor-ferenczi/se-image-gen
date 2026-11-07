using System;
using System.Text;

namespace Shared.ImageGen;

// Turns an RGB image into the Monospace font's color characters: U+E100 + r * 64 + g * 8 + b,
// with 3 bits per channel.
public static class LcdArt
{
    public static string FromRgb(byte[] rgb, int columns, int rows, bool dither)
    {
        if (rgb.Length != columns * rows * 3)
            throw new ArgumentException(
                $"Expected {columns * rows * 3} bytes of RGB for {columns}x{rows}, got {rgb.Length}"
            );

        // Floyd-Steinberg error diffusion over the current and the next row
        var current = new float[(columns + 2) * 3];
        var next = new float[(columns + 2) * 3];

        var sb = new StringBuilder(columns * rows + rows);
        for (var y = 0; y < rows; y++)
        {
            if (y > 0)
                sb.Append('\n');

            for (var x = 0; x < columns; x++)
            {
                var levels = new int[3];
                for (var c = 0; c < 3; c++)
                {
                    var i = (x + 1) * 3 + c;
                    var value = rgb[(y * columns + x) * 3 + c] + (dither ? current[i] : 0f);
                    var level = Math.Max(0, Math.Min(7, (int)Math.Round(value * 7f / 255f)));
                    levels[c] = level;
                    if (!dither)
                        continue;

                    var error = value - level * 255f / 7f;
                    current[i + 3] += error * 7f / 16f;
                    next[i - 3] += error * 3f / 16f;
                    next[i] += error * 5f / 16f;
                    next[i + 3] += error * 1f / 16f;
                }

                sb.Append((char)(0xE100 + (levels[0] << 6) + (levels[1] << 3) + levels[2]));
            }

            (current, next) = (next, current);
            Array.Clear(next, 0, next.Length);
        }

        return sb.ToString();
    }
}

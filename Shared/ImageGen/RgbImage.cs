using System;
using System.IO;
using System.IO.Compression;

namespace Shared.ImageGen;

// An 8 bit RGB image, decoded from the PNG files the image server answers with.
// The game's runtimes have no common image library, so the PNG decoder is here.
public class RgbImage
{
    public readonly int Width;
    public readonly int Height;

    // Row by row, 3 bytes per pixel
    public readonly byte[] Pixels;

    public RgbImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 3)
            throw new ArgumentException(
                $"Expected {width * height * 3} bytes for {width}x{height}"
            );

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    // Supports non-interlaced PNG with 8 bits per channel: gray, gray with alpha, RGB and RGBA.
    // Alpha is dropped.
    public static RgbImage FromPng(byte[] png)
    {
        var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (png.Length < 8)
            throw new InvalidDataException("Not a PNG image");
        for (var i = 0; i < 8; i++)
            if (png[i] != signature[i])
                throw new InvalidDataException("Not a PNG image");

        int width = 0,
            height = 0,
            channels = 0;
        var data = new MemoryStream();
        var position = 8;
        while (position + 8 <= png.Length)
        {
            var length = ReadInt(png, position);
            var type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            var start = position + 8;
            if (length < 0 || start + length > png.Length)
                throw new InvalidDataException("Truncated PNG image");

            if (type == "IHDR")
            {
                width = ReadInt(png, start);
                height = ReadInt(png, start + 4);
                var bitDepth = png[start + 8];
                var colorType = png[start + 9];
                var interlace = png[start + 12];
                channels = colorType switch
                {
                    0 => 1,
                    2 => 3,
                    4 => 2,
                    6 => 4,
                    _ => 0,
                };
                if (bitDepth != 8 || channels == 0 || interlace != 0)
                    throw new InvalidDataException(
                        $"Unsupported PNG: bit depth {bitDepth}, color type {colorType}, interlace {interlace}"
                    );
            }
            else if (type == "IDAT")
                data.Write(png, start, length);
            else if (type == "IEND")
                break;

            position = start + length + 4;
        }

        if (channels == 0 || width <= 0 || height <= 0)
            throw new InvalidDataException("PNG image without a header");

        // zlib stream: a 2 byte header, then deflate
        var raw = new byte[(width * channels + 1) * height];
        data.Position = 2;
        using (var inflater = new DeflateStream(data, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var n = inflater.Read(raw, read, raw.Length - read);
                if (n == 0)
                    throw new InvalidDataException("Truncated PNG image data");
                read += n;
            }
        }

        var stride = width * channels;
        var previous = new byte[stride];
        var row = new byte[stride];
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var offset = y * (stride + 1);
            var filter = raw[offset];
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? row[i - channels] : 0;
                int b = previous[i];
                int c = i >= channels ? previous[i - channels] : 0;
                int x = raw[offset + 1 + i];
                row[i] = (byte)(
                    filter switch
                    {
                        0 => x,
                        1 => x + a,
                        2 => x + b,
                        3 => x + (a + b) / 2,
                        4 => x + Paeth(a, b, c),
                        _ => throw new InvalidDataException($"Unknown PNG filter {filter}"),
                    }
                );
            }

            for (var px = 0; px < width; px++)
            {
                var source = px * channels;
                var target = (y * width + px) * 3;
                if (channels < 3)
                {
                    pixels[target] = pixels[target + 1] = pixels[target + 2] = row[source];
                }
                else
                {
                    pixels[target] = row[source];
                    pixels[target + 1] = row[source + 1];
                    pixels[target + 2] = row[source + 2];
                }
            }

            var swap = previous;
            previous = row;
            row = swap;
        }

        return new RgbImage(width, height, pixels);
    }

    // Scales down by averaging the source pixels that fall into each target pixel
    public RgbImage Resize(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        var sums = new long[3];
        for (var y = 0; y < height; y++)
        {
            var y0 = y * Height / height;
            var y1 = Math.Max(y0 + 1, (y + 1) * Height / height);
            for (var x = 0; x < width; x++)
            {
                var x0 = x * Width / width;
                var x1 = Math.Max(x0 + 1, (x + 1) * Width / width);
                sums[0] = sums[1] = sums[2] = 0;
                for (var sy = y0; sy < y1; sy++)
                for (var sx = x0; sx < x1; sx++)
                {
                    var i = (sy * Width + sx) * 3;
                    sums[0] += Pixels[i];
                    sums[1] += Pixels[i + 1];
                    sums[2] += Pixels[i + 2];
                }

                var count = (y1 - y0) * (x1 - x0);
                var target = (y * width + x) * 3;
                for (var c = 0; c < 3; c++)
                    pixels[target + c] = (byte)((sums[c] + count / 2) / count);
            }
        }

        return new RgbImage(width, height, pixels);
    }

    private static int ReadInt(byte[] bytes, int offset) =>
        (bytes[offset] << 24)
        | (bytes[offset + 1] << 16)
        | (bytes[offset + 2] << 8)
        | bytes[offset + 3];

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
            return a;
        return pb <= pc ? b : c;
    }
}

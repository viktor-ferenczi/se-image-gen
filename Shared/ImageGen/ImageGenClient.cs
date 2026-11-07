using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Shared.ImageGen;

// Calls the txt2img API of a stable-diffusion.cpp sd-server that runs the image model,
// see the README. Blocking, so run it on a worker thread.
public static class ImageGenClient
{
    // Returns the image as columns x rows RGB bytes, scaled down to the character grid
    public static byte[] Generate(
        string serverUrl,
        string prompt,
        int columns,
        int rows,
        int pixels,
        int steps,
        float cfgScale,
        int timeoutSeconds
    )
    {
        var (width, height) = ImageSize(columns, rows, pixels);
        var body = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{{\"prompt\":{0},\"negative_prompt\":\"\",\"width\":{1},\"height\":{2},\"steps\":{3},\"cfg_scale\":{4},\"seed\":-1}}",
            JsonString(prompt),
            width,
            height,
            steps,
            cfgScale
        );

        string answer;
#pragma warning disable SYSLIB0014 // HttpWebRequest needs no extra reference on .NET Framework
        var request = (HttpWebRequest)
            WebRequest.Create(serverUrl.TrimEnd('/') + "/sdapi/v1/txt2img");
#pragma warning restore SYSLIB0014
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Timeout = timeoutSeconds * 1000;
        request.ReadWriteTimeout = timeoutSeconds * 1000;

        var bytes = Encoding.UTF8.GetBytes(body);
        try
        {
            using (var stream = request.GetRequestStream())
                stream.Write(bytes, 0, bytes.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                answer = reader.ReadToEnd();
        }
        catch (WebException e) when (e.Response is HttpWebResponse response)
        {
            using (response)
            using (var reader = new StreamReader(response.GetResponseStream()))
                throw new ImageGenException(
                    ErrorMessage(reader.ReadToEnd(), (int)response.StatusCode)
                );
        }
        catch (WebException e)
        {
            throw new ImageGenException($"Image server unavailable: {e.Message}");
        }

        var match = Regex.Match(answer, "\"images\"\\s*:\\s*\\[\\s*\"([A-Za-z0-9+/=]+)\"");
        if (!match.Success)
            throw new ImageGenException("The image server answered without an image");

        RgbImage image;
        try
        {
            image = RgbImage.FromPng(Convert.FromBase64String(match.Groups[1].Value));
        }
        catch (Exception e) when (e is InvalidDataException || e is FormatException)
        {
            throw new ImageGenException($"The image server sent a bad image: {e.Message}");
        }

        return image.Resize(columns, rows).Pixels;
    }

    // Width and height in multiples of 16 with about `pixels` pixels and the grid's aspect ratio
    public static (int Width, int Height) ImageSize(int columns, int rows, int pixels)
    {
        var width = Math.Sqrt((double)pixels * columns / rows);
        var height = pixels / width;
        return (
            Math.Max(64, (int)Math.Round(width / 16) * 16),
            Math.Max(64, (int)Math.Round(height / 16) * 16)
        );
    }

    private static string ErrorMessage(string body, int status)
    {
        var match = Regex.Match(body, "\"error\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return match.Success
            ? Regex.Unescape(match.Groups[1].Value)
            : $"Image server error {status}";
    }

    private static string JsonString(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            if (c == '"' || c == '\\')
                sb.Append('\\').Append(c);
            else if (c < ' ')
                sb.AppendFormat("\\u{0:x4}", (int)c);
            else
                sb.Append(c);
        }

        return sb.Append('"').ToString();
    }
}

public class ImageGenException : Exception
{
    public ImageGenException(string message)
        : base(message) { }
}

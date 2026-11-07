using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Shared.ImageGen;

// Calls the image generation service. Blocking, so run it on a worker thread.
public static class ImageGenClient
{
    // Returns the image as columns x rows RGB bytes, already scaled to the character grid
    public static byte[] Generate(string serviceUrl, string prompt, int columns, int rows, int timeoutSeconds)
    {
        var body = $"{{\"prompt\":{JsonString(prompt)},\"columns\":{columns},\"rows\":{rows}}}";

#pragma warning disable SYSLIB0014 // HttpWebRequest needs no extra reference on .NET Framework
        var request = (HttpWebRequest)WebRequest.Create(serviceUrl.TrimEnd('/') + "/generate");
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
            using (var stream = response.GetResponseStream())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
        catch (WebException e) when (e.Response is HttpWebResponse response)
        {
            using (response)
            using (var reader = new StreamReader(response.GetResponseStream()))
                throw new ImageGenException(ErrorMessage(reader.ReadToEnd(), (int)response.StatusCode));
        }
        catch (WebException e)
        {
            throw new ImageGenException($"Image service unavailable: {e.Message}");
        }
    }

    private static string ErrorMessage(string body, int status)
    {
        var match = Regex.Match(body, "\"error\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return match.Success ? Regex.Unescape(match.Groups[1].Value) : $"Image service error {status}";
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

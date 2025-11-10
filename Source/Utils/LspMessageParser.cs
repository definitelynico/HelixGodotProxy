using System.Text;
using System.Text.Json;

namespace HelixGodotProxy.Utils;

/// <summary>
/// Handles LSP message parsing and formatting
/// </summary>
public static class LspMessageParser
{
    private const string ContentLengthHeader = "Content-Length: ";
    private const string ContentTypeHeader = "Content-Type: ";
    private const string HeaderSeparator = "\r\n\r\n";

    /// <summary>
    /// Reads a complete LSP message from a stream
    /// </summary>
    /// <param name="stream">The stream to read from</param>
    /// <returns>The complete LSP message or null if stream ended</returns>
    public static async Task<string?> ReadMessageAsync(Stream stream)
    {
        try
        {
            // Read headers
            var headerBuilder = new StringBuilder();
            var headerBytes = new List<byte>();
            int b;

            while ((b = stream.ReadByte()) != -1)
            {
                headerBytes.Add((byte)b);

                // Check for header separator
                if (headerBytes.Count >= 4 &&
                    headerBytes[^4] == '\r' && headerBytes[^3] == '\n' &&
                    headerBytes[^2] == '\r' && headerBytes[^1] == '\n')
                {
                    break;
                }
            }

            if (b == -1) return null; // Stream ended

            var headers = Encoding.UTF8.GetString(headerBytes.ToArray());

            // Parse Content-Length
            var contentLength = ParseContentLength(headers);
            if (contentLength <= 0) return null;

            // Read content
            var contentBuffer = new byte[contentLength];
            var bytesRead = 0;

            while (bytesRead < contentLength)
            {
                var read = await stream.ReadAsync(contentBuffer, bytesRead, contentLength - bytesRead);
                if (read == 0) return null; // Stream ended unexpectedly
                bytesRead += read;
            }

            var content = Encoding.UTF8.GetString(contentBuffer);
            return headers + content;
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(LogLevel.ERROR, $"Error reading LSP message: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes an LSP message to a stream
    /// </summary>
    /// <param name="stream">The stream to write to</param>
    /// <param name="message">The message to write</param>
    public static async Task WriteMessageAsync(Stream stream, string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(LogLevel.ERROR, $"Error writing LSP message: {ex.Message}");
        }
    }

    /// <summary>
    /// Formats content as a proper LSP message with headers
    /// </summary>
    /// <param name="jsonContent">The JSON content to format</param>
    /// <returns>The formatted LSP message</returns>
    public static string FormatMessage(string jsonContent)
    {
        var contentBytes = Encoding.UTF8.GetBytes(jsonContent);
        return $"Content-Length: {contentBytes.Length}\r\n\r\n{jsonContent}";
    }

    /// <summary>
    /// Extracts just the JSON content from an LSP message
    /// </summary>
    /// <param name="lspMessage">The complete LSP message</param>
    /// <returns>The JSON content or null if parsing failed</returns>
    public static string? ExtractJsonContent(string lspMessage)
    {
        var separatorIndex = lspMessage.IndexOf(HeaderSeparator);
        if (separatorIndex == -1) return null;

        return lspMessage.Substring(separatorIndex + HeaderSeparator.Length);
    }

    private static int ParseContentLength(string headers)
    {
        var lines = headers.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith(ContentLengthHeader))
            {
                var lengthStr = line.Substring(ContentLengthHeader.Length).Trim();
                if (int.TryParse(lengthStr, out var length))
                {
                    return length;
                }
            }
        }
        return -1;
    }
}

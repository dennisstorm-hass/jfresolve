using System;

namespace Jfresolve.Services;

public static class StreamContainerGuesser
{
    public static string? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return FromFilename(url);

        if (uri.Query.Contains("filename=", StringComparison.OrdinalIgnoreCase))
        {
            var filename = Uri.UnescapeDataString(
                uri.Query.Split("filename=", StringSplitOptions.None)[1].Split('&')[0]);
            return FromFilename(filename);
        }

        return FromFilename(uri.AbsolutePath);
    }

    public static string? FromFilename(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;

        if (filename.Contains(".mkv", StringComparison.OrdinalIgnoreCase))
            return "mkv";
        if (filename.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
            return "mp4";
        if (filename.Contains(".webm", StringComparison.OrdinalIgnoreCase))
            return "webm";
        if (filename.Contains(".avi", StringComparison.OrdinalIgnoreCase))
            return "avi";

        return null;
    }
}

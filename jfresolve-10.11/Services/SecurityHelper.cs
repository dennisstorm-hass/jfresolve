using System;
using System.Text.RegularExpressions;

namespace Jfresolve.Services;

/// <summary>
/// Security helper methods for handling sensitive data
/// </summary>
public static class SecurityHelper
{
    /// <summary>
    /// Sanitizes a URL by removing API keys and other sensitive information
    /// Used for logging to prevent exposing sensitive data
    /// </summary>
    public static string SanitizeUrlForLogging(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        // Remove API keys from query string (api_key=...)
        var sanitized = Regex.Replace(url, @"[?&]api_key=[^&]*", "?api_key=***", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"&api_key=[^&]*", "&api_key=***", RegexOptions.IgnoreCase);
        
        // Remove other common sensitive parameters
        sanitized = Regex.Replace(sanitized, @"[?&]apikey=[^&]*", "?apikey=***", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"&apikey=[^&]*", "&apikey=***", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"[?&]token=[^&]*", "?token=***", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"&token=[^&]*", "&token=***", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"[?&]password=[^&]*", "?password=***", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"&password=[^&]*", "&password=***", RegexOptions.IgnoreCase);
        
        return sanitized;
    }

    /// <summary>
    /// Masks an API key for logging (shows only first 4 and last 4 characters)
    /// </summary>
    public static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return "***";

        if (apiKey.Length <= 8)
            return "***"; // Too short to mask meaningfully

        // Show first 4 and last 4 characters, mask the rest
        return $"{apiKey.Substring(0, 4)}...{apiKey.Substring(apiKey.Length - 4)}";
    }

    /// <summary>
    /// Checks if a string might contain sensitive information (API keys, tokens, etc.)
    /// </summary>
    public static bool ContainsSensitiveData(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Check for common patterns that indicate sensitive data
        var patterns = new[]
        {
            @"api[_-]?key\s*[:=]\s*\w+",
            @"apikey\s*[:=]\s*\w+",
            @"token\s*[:=]\s*\w+",
            @"password\s*[:=]\s*\w+",
            @"secret\s*[:=]\s*\w+"
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }
}

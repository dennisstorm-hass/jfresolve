using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Jfresolve.Services;

public static class StreamUrlValidation
{
    public static string SanitizeInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var sanitized = new string(input.Where(c => !char.IsControl(c)).ToArray()).Trim();
        var allowedChars = new HashSet<char>("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_:.");
        sanitized = new string(sanitized.Where(allowedChars.Contains).ToArray());

        const int maxLength = 100;
        if (sanitized.Length > maxLength)
            sanitized = sanitized[..maxLength];

        return sanitized;
    }

    public static bool IsValidStreamUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != "http" && uri.Scheme != "https")
            return false;

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0" or "[::1]")
            return false;

        if (host.StartsWith("10.", StringComparison.Ordinal)
            || host.StartsWith("192.168.", StringComparison.Ordinal)
            || host.StartsWith("169.254.", StringComparison.Ordinal))
        {
            return false;
        }

        if (host.StartsWith("172.", StringComparison.Ordinal) && IsPrivateIpRange(host))
            return false;

        if (host.StartsWith("224.", StringComparison.Ordinal)
            || host.StartsWith("0.", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            foreach (var ip in Dns.GetHostEntry(host).AddressList)
            {
                if (IsPrivateIpAddress(ip))
                    return false;
            }
        }
        catch
        {
            // Allow if hostname checks passed.
        }

        return true;
    }

    private static bool IsPrivateIpAddress(IPAddress ip)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 10)
                return true;
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var text = ip.ToString();
            if (text == "::1" || text.StartsWith("[::1]", StringComparison.Ordinal))
                return true;

            var bytes = ip.GetAddressBytes();
            if (bytes.Length >= 2 && bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                return true;
        }

        return false;
    }

    private static bool IsPrivateIpRange(string host)
    {
        if (!host.StartsWith("172.", StringComparison.Ordinal))
            return false;

        var parts = host.Split('.');
        return parts.Length >= 2
               && int.TryParse(parts[1], out var secondOctet)
               && secondOctet is >= 16 and <= 31;
    }
}

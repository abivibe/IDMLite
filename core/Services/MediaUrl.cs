namespace IDMLite.Core.Services;

public static class MediaUrl
{
    public static bool HasHost(string? url, params string[] domains) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        domains.Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                         uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    public static bool IsSocialPage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string path = uri.AbsolutePath;
        if (HasHost(url, "youtu.be")) return path.Length > 1;
        if (HasHost(url, "youtube.com")) return path == "/watch" || path.StartsWith("/shorts/") || path.StartsWith("/embed/") || path.StartsWith("/live/");
        if (HasHost(url, "instagram.com")) return System.Text.RegularExpressions.Regex.IsMatch(path, @"/(p|reel|reels|tv)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (HasHost(url, "x.com", "twitter.com")) return path.Contains("/status/", StringComparison.OrdinalIgnoreCase);
        return HasHost(url, "tiktok.com");
    }
}

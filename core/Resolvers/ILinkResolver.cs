namespace IDMLite.Core.Resolvers;

public class MediaStreamInfo
{
    public string StreamType { get; set; } = "video"; // "video" or "audio"
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Format { get; set; } = "mp4"; // "mp4", "mp3", "webm", "m4a", "m3u8"
    public string Quality { get; set; } = "HD"; // "1080p", "720p", "Audio", "320kbps"
    public long? FileSize { get; set; }
    public bool FileSizeIsEstimated { get; set; }
    public bool IsAudioOnly { get; set; } = false;
    public string? SuggestedFileName { get; set; }

    public string DisplayText => IsAudioOnly 
        ? $"🎵 Ses ({Format.ToUpperInvariant()}) - {Quality}" 
        : $"🎬 Video ({Format.ToUpperInvariant()}) - {Quality}";
}

public class ResolvedLinkResult
{
    public string OriginalUrl { get; set; } = string.Empty;
    public string DirectUrl { get; set; } = string.Empty;
    public string? SuggestedFileName { get; set; }
    public long? FileSize { get; set; }
    public string? Cookies { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public bool IsSuccess { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public List<MediaStreamInfo> Streams { get; set; } = new();
}

public interface ILinkResolver
{
    string ServiceName { get; }
    bool CanResolve(string url);
    Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default);
}

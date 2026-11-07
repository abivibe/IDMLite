using System.Text.Json;
using System.Text.RegularExpressions;

namespace IDMLite.Core.Resolvers;

public class TikTokResolver : ILinkResolver
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public string ServiceName => "TikTok Video & Audio Resolver";

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("tiktok.com/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        string title = "TikTok_Video_" + DateTime.Now.Ticks;
        string directVideoUrl = url;
        string directAudioUrl = url;
        long? videoSize = null;

        try
        {
            string apiUrl = $"https://www.tikwm.com/api/?url={Uri.EscapeDataString(url)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMLite/1.0");

            using var response = await HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("title", out var titleProp) && !string.IsNullOrWhiteSpace(titleProp.GetString()))
                    {
                        string raw = titleProp.GetString()!;
                        title = CleanFileName(raw.Length > 60 ? raw.Substring(0, 60) : raw);
                    }

                    if (data.TryGetProperty("author", out var author) && author.TryGetProperty("nickname", out var nick))
                    {
                        string authorName = nick.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(authorName))
                        {
                            title = $"{CleanFileName(authorName)} - {title}";
                        }
                    }

                    if (data.TryGetProperty("play", out var playProp) && !string.IsNullOrWhiteSpace(playProp.GetString()))
                    {
                        directVideoUrl = playProp.GetString()!;
                        if (directVideoUrl.StartsWith("/"))
                        {
                            directVideoUrl = "https://www.tikwm.com" + directVideoUrl;
                        }
                    }

                    if (data.TryGetProperty("music", out var musicProp) && !string.IsNullOrWhiteSpace(musicProp.GetString()))
                    {
                        directAudioUrl = musicProp.GetString()!;
                        if (directAudioUrl.StartsWith("/"))
                        {
                            directAudioUrl = "https://www.tikwm.com" + directAudioUrl;
                        }
                    }

                    if (data.TryGetProperty("size", out var sizeProp))
                    {
                        videoSize = sizeProp.GetInt64();
                    }
                }
            }
        }
        catch { }

        if ((!videoSize.HasValue || videoSize.Value <= 0) && !string.IsNullOrWhiteSpace(directVideoUrl) && directVideoUrl != url)
        {
            try
            {
                videoSize = await CobaltResolver.ProbeUrlSizeAsync(directVideoUrl, cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(title)) title = "TikTok_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var result = new ResolvedLinkResult
        {
            OriginalUrl = url,
            DirectUrl = directVideoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = videoSize,
            IsSuccess = true
        };

        // 1. Watermark-free Video Stream
        result.Streams.Add(new MediaStreamInfo
        {
            StreamType = "video",
            Title = $"{title} (Filigransız HD Video)",
            Format = "mp4",
            Quality = "HD (Filigransız)",
            Url = directVideoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = videoSize,
            IsAudioOnly = false
        });

        // 2. Audio MP3 Stream
        result.Streams.Add(new MediaStreamInfo
        {
            StreamType = "audio",
            Title = $"{title} (Orijinal Ses)",
            Format = "mp3",
            Quality = "Orijinal Ses (MP3)",
            Url = directAudioUrl,
            SuggestedFileName = $"{title} (Ses).mp3",
            IsAudioOnly = true
        });

        return result;
    }

    private static string CleanFileName(string name)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string clean = new string(name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        return clean.Trim(' ', '.');
    }
}

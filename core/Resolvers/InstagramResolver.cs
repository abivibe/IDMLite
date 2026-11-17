using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using IDMLite.Core.Services;

namespace IDMLite.Core.Resolvers;

public class InstagramResolver : ILinkResolver
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private static readonly ConcurrentDictionary<string, (ResolvedLinkResult Result, DateTime Timestamp)> ResolvedCache = new(StringComparer.OrdinalIgnoreCase);

    public string ServiceName => "Instagram Reels & Post Resolver";

    public bool CanResolve(string url)
    {
        return MediaUrl.HasHost(url, "cdninstagram.com", "fbcdn.net") ||
               (MediaUrl.HasHost(url, "instagram.com") &&
                (MediaUrl.IsSocialPage(url) || new Uri(url).AbsolutePath.StartsWith("/share/", StringComparison.OrdinalIgnoreCase)));
    }

    public static string NormalizeInstagramPostUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (url.Contains("cdninstagram.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        var m = Regex.Match(url, @"(?:(p)|reel|reels|tv|share\/reel|share\/p|reels\/videos)\/([a-zA-Z0-9_-]{7,25})", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            string kind = m.Groups[1].Success ? "p" : "reel";
            string code = m.Groups[2].Value;
            return $"https://www.instagram.com/{kind}/{code}/";
        }
        var m2 = Regex.Match(url, @"\/(?:reel|p)\/([a-zA-Z0-9_-]{7,25})", RegexOptions.IgnoreCase);
        if (m2.Success)
        {
            return $"https://www.instagram.com/reel/{m2.Groups[1].Value}/";
        }
        return url;
    }

    public static ResolvedLinkResult? TryGetCachedResult(string url)
    {
        string normalized = NormalizeInstagramPostUrl(url);
        if (ResolvedCache.TryGetValue(normalized, out var entry) &&
            (DateTime.UtcNow - entry.Timestamp).TotalMinutes < 5 &&
            entry.Result.IsSuccess &&
            !string.IsNullOrWhiteSpace(entry.Result.DirectUrl))
        {
            return entry.Result;
        }
        return null;
    }

    public Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        return ResolveWithCookiesAsync(url, null, cancellationToken);
    }

    public async Task<ResolvedLinkResult> ResolveWithCookiesAsync(string url, string? cookies = null, CancellationToken cancellationToken = default)
    {
        if (url.Contains("cdninstagram.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase))
        {
            long? fastSize = null;
            try
            {
                using var sizeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sizeCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                fastSize = await MediaSizeProbe.ProbeUrlSizeAsync(url, sizeCts.Token).ConfigureAwait(false);
            }
            catch { }
            string directTitle = "Instagram_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return BuildResult(url, url, url, directTitle, "HD Video", fastSize);
        }

        string normalizedUrl = NormalizeInstagramPostUrl(url);

        var cached = TryGetCachedResult(normalizedUrl);
        if (cached != null)
        {
            return cached;
        }

        var matchCode = Regex.Match(normalizedUrl, @"(?:p|reel|reels|tv|share\/reel|share\/p|reels\/videos)\/([a-zA-Z0-9_-]{7,25})", RegexOptions.IgnoreCase);
        if (!matchCode.Success)
        {
            matchCode = Regex.Match(normalizedUrl, @"\/(?:reel|p)\/([a-zA-Z0-9_-]{7,25})", RegexOptions.IgnoreCase);
        }
        string shortcode = matchCode.Success ? matchCode.Groups[1].Value : "";

        // Start fast Embed HTTP fetch (~250ms) and yt-dlp metadata extraction (with cookies) IN PARALLEL!
        var embedTask = FetchEmbedCaptionedAsync(normalizedUrl, shortcode, cancellationToken);
        Task<YtDlpMediaInfo?>? ytdlpTask = YtDlpHelper.IsAvailable
            ? YtDlpHelper.GetMediaInfoAsync(normalizedUrl, cancellationToken, cookies)
            : null;

        var (embedVideoUrl, embedTitle) = await embedTask.ConfigureAwait(false);

        // Fast Path: If Embed Captioned (~250ms) already found both direct MP4 stream URL and creator/caption title, return immediately!
        if (!string.IsNullOrWhiteSpace(embedVideoUrl) && embedVideoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            string fastTitle = !string.IsNullOrWhiteSpace(embedTitle)
                ? embedTitle
                : (!string.IsNullOrEmpty(shortcode) ? $"Instagram_Video_{shortcode}" : "Instagram_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            long? fastSize = null;
            try
            {
                using var sizeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sizeCts.CancelAfter(TimeSpan.FromMilliseconds(700));
                fastSize = await MediaSizeProbe.ProbeUrlSizeAsync(embedVideoUrl, sizeCts.Token).ConfigureAwait(false);
            }
            catch { }

            var fastResult = BuildResult(url, embedVideoUrl, embedVideoUrl, fastTitle, "HD Video", fastSize);
            ResolvedCache[normalizedUrl] = (fastResult, DateTime.UtcNow);
            return fastResult;
        }

        // Parallel Path 2: Await yt-dlp (which has already been running in parallel since 0ms!)
        if (ytdlpTask != null)
        {
            try
            {
                var mediaInfo = await ytdlpTask.ConfigureAwait(false);
                if (mediaInfo != null)
                {
                    string bestRawTitle = !string.IsNullOrWhiteSpace(mediaInfo.Title) &&
                                          !mediaInfo.Title.StartsWith("Instagram_Video", StringComparison.OrdinalIgnoreCase) &&
                                          !mediaInfo.Title.Equals("Media", StringComparison.OrdinalIgnoreCase) &&
                                          !mediaInfo.Title.Contains("Meta AI", StringComparison.OrdinalIgnoreCase)
                        ? mediaInfo.Title
                        : (!string.IsNullOrWhiteSpace(embedTitle) && !embedTitle.Contains("Meta AI", StringComparison.OrdinalIgnoreCase) ? embedTitle : mediaInfo.Title);

                    string safeTitle = CleanFileName(bestRawTitle);
                    if (string.IsNullOrWhiteSpace(safeTitle) || safeTitle.Contains("Meta AI", StringComparison.OrdinalIgnoreCase))
                    {
                        safeTitle = !string.IsNullOrEmpty(shortcode) ? $"Instagram_Video_{shortcode}" : "Instagram_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    }

                    string? directUrl = mediaInfo.Formats.LastOrDefault(f => !f.IsVideoOnly && !f.IsAudioOnly && f.Extension == "mp4" && !string.IsNullOrEmpty(f.Url))?.Url ??
                                        mediaInfo.DirectVideoUrl ??
                                        mediaInfo.Formats.LastOrDefault(f => f.Extension == "mp4" && !string.IsNullOrEmpty(f.Url))?.Url ??
                                        mediaInfo.Formats.LastOrDefault(f => !string.IsNullOrEmpty(f.Url))?.Url;

                    if (!string.IsNullOrEmpty(directUrl))
                    {
                        var igResult = BuildResult(url, directUrl, directUrl, safeTitle, mediaInfo.VideoQuality, mediaInfo.VideoSize);
                        ResolvedCache[normalizedUrl] = (igResult, DateTime.UtcNow);
                        return igResult;
                    }
                }
            }
            catch { }
        }

        string title = !string.IsNullOrWhiteSpace(embedTitle)
            ? embedTitle
            : (!string.IsNullOrEmpty(shortcode) ? $"Instagram_Video_{shortcode}" : "Instagram_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        string directVideoUrl = !string.IsNullOrWhiteSpace(embedVideoUrl) ? embedVideoUrl : normalizedUrl;

        if (string.IsNullOrWhiteSpace(directVideoUrl) || directVideoUrl == normalizedUrl || directVideoUrl == url)
        {
            return new ResolvedLinkResult
            {
                OriginalUrl = url,
                DirectUrl = url,
                SuggestedFileName = $"{title}.mp4",
                IsSuccess = false,
                ErrorMessage = "Instagram video bağlantısı doğrudan çözülemedi. Lütfen videoyu tarayıcıda izlerken IDMLite eklentisindeki 'Bu Videoyu İndir' butonunu kullanarak indirin."
            };
        }

        long? videoSize = null;
        try
        {
            using var sizeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sizeCts.CancelAfter(TimeSpan.FromMilliseconds(700));
            videoSize = await MediaSizeProbe.ProbeUrlSizeAsync(directVideoUrl, sizeCts.Token).ConfigureAwait(false);
        }
        catch { }

        var result = BuildResult(url, directVideoUrl, directVideoUrl, title, "HD Video", videoSize);
        ResolvedCache[normalizedUrl] = (result, DateTime.UtcNow);
        return result;
    }

    private static ResolvedLinkResult BuildResult(string originalUrl, string directVideoUrl, string directAudioUrl, string title, string quality, long? videoSize)
    {
        var result = new ResolvedLinkResult
        {
            OriginalUrl = originalUrl,
            DirectUrl = directVideoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = videoSize,
            IsSuccess = true
        };

        result.Streams.Add(new MediaStreamInfo
        {
            StreamType = "video",
            Title = $"{title} (Reels Video MP4)",
            Format = "mp4",
            Quality = string.IsNullOrWhiteSpace(quality) ? "HD Video" : quality,
            Url = directVideoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = videoSize,
            IsAudioOnly = false
        });

        result.Streams.Add(new MediaStreamInfo
        {
            StreamType = "audio",
            Title = $"{title} (Ses MP3)",
            Format = "mp3",
            Quality = "Audio (MP3)",
            Url = directAudioUrl,
            SuggestedFileName = $"{title} (Ses).mp3",
            IsAudioOnly = true
        });

        return result;
    }

    private static async Task<(string? VideoUrl, string? Title)> FetchEmbedCaptionedAsync(string url, string shortcode, CancellationToken cancellationToken)
    {
        try
        {
            string embedUrl = !string.IsNullOrEmpty(shortcode)
                ? $"https://www.instagram.com/p/{shortcode}/embed/captioned/"
                : url;

            using var embedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            embedCts.CancelAfter(TimeSpan.FromMilliseconds(1600));

            using var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            using var response = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, embedCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, null);

            string html = await response.Content.ReadAsStringAsync(embedCts.Token).ConfigureAwait(false);

            string? extractedVideoUrl = null;
            var videoMatch = Regex.Match(html, @"(?:video_url|videoUrl|""contentUrl"")\s*[:=]\s*""([^""]+)""");
            if (!videoMatch.Success)
            {
                videoMatch = Regex.Match(html, @"<video[^>]+src=[""']([^""']+)[""']");
            }
            if (!videoMatch.Success)
            {
                videoMatch = Regex.Match(html, @"property=[""']og:video[""'][^>]*content=[""']([^""']+)[""']");
            }

            if (videoMatch.Success)
            {
                string extracted = WebUtility.HtmlDecode(videoMatch.Groups[1].Value).Replace("\\u0026", "&").Replace("\\/", "/");
                if (extracted.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    string extLower = extracted.ToLowerInvariant();
                    if (!extLower.Contains(".jpg") && !extLower.Contains(".jpeg") && !extLower.Contains(".png") && !extLower.Contains(".webp") && !extLower.Contains("/v/t51."))
                    {
                        extractedVideoUrl = extracted;
                    }
                }
            }

            string author = "";
            var authorMatch = Regex.Match(html, @"class=[""']CaptionUsername[""'][^>]*><a[^>]*>([^<]+)<\/a>");
            if (!authorMatch.Success)
            {
                authorMatch = Regex.Match(html, @"class=[""']UsernameText[""'][^>]*>([^<]+)<\/");
            }
            if (!authorMatch.Success)
            {
                authorMatch = Regex.Match(html, @"""owner""\s*:\s*\{[^}]*""username""\s*:\s*""([^""]+)""");
            }
            if (authorMatch.Success) author = WebUtility.HtmlDecode(authorMatch.Groups[1].Value).Trim();
            if (author.Equals("Meta AI", StringComparison.OrdinalIgnoreCase)) author = "";

            string caption = "";
            var captionMatch = Regex.Match(html, @"class=[""']Caption[""'][^>]*>([\s\S]*?)<\/div>");
            if (captionMatch.Success)
            {
                caption = WebUtility.HtmlDecode(Regex.Replace(captionMatch.Groups[1].Value, @"<[^>]+>", " ")).Trim();
                if (!string.IsNullOrEmpty(author) && caption.StartsWith(author, StringComparison.OrdinalIgnoreCase))
                {
                    caption = caption.Substring(author.Length).Trim();
                }
                caption = Regex.Replace(caption, @"View all \d+ comments.*$", "", RegexOptions.IgnoreCase).Trim();
            }

            if (string.IsNullOrWhiteSpace(caption))
            {
                var textMatch = Regex.Match(html, @"""text""\s*:\s*""([^""]+)""");
                if (textMatch.Success)
                {
                    try { caption = Regex.Unescape(textMatch.Groups[1].Value).Trim(); } catch { }
                }
            }

            if (string.IsNullOrWhiteSpace(caption))
            {
                var titleMatch = Regex.Match(html, @"<title>([^<]+)<\/title>");
                if (titleMatch.Success && !titleMatch.Groups[1].Value.Trim().Equals("Instagram", StringComparison.OrdinalIgnoreCase))
                {
                    caption = WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Replace("Instagram:", "").Replace("• Instagram photos and videos", "").Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(caption))
            {
                if (caption.Contains("Meta AI", StringComparison.OrdinalIgnoreCase) ||
                    caption.Contains("agree to the AI terms", StringComparison.OrdinalIgnoreCase) ||
                    caption.Contains("interactions with AI", StringComparison.OrdinalIgnoreCase) ||
                    caption.Contains("Learn more about how Meta", StringComparison.OrdinalIgnoreCase))
                {
                    caption = "";
                }
            }

            string? resolvedTitle = null;
            if (!string.IsNullOrWhiteSpace(caption))
            {
                if (caption.Length > 70) caption = caption.Substring(0, 70).Trim();
                resolvedTitle = !string.IsNullOrWhiteSpace(author) && !caption.Contains(author, StringComparison.OrdinalIgnoreCase)
                    ? $"{CleanFileName(author)} - {CleanFileName(caption)}"
                    : CleanFileName(caption);
            }
            else if (!string.IsNullOrWhiteSpace(author))
            {
                resolvedTitle = !string.IsNullOrEmpty(shortcode)
                    ? $"{CleanFileName(author)} - {shortcode}"
                    : CleanFileName(author);
            }

            return (extractedVideoUrl, resolvedTitle);
        }
        catch
        {
            return (null, null);
        }
    }
    private static string CleanFileName(string name) => SegmentedDownloader.CleanFileName(name);
}

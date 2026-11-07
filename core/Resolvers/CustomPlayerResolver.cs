using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace IDMLite.Core.Resolvers;

public class CustomPlayerResolver : ILinkResolver
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public string ServiceName => "Custom & Generic Web Player Resolver";

    public static bool IsGenericVideoHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("vimeo.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("dailymotion.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("dai.ly/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("twitch.tv/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("kick.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("streamable.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("rumble.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("bilibili.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("ok.ru/video", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("vk.com/video", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Never intercept YouTube, Instagram, TikTok, or X/Twitter here (they have dedicated engines)
        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("twitter.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("x.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Matches direct media, streaming protocols, or generic video hosting sites
        return url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".mkv", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".flv", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".mov", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/stream/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("player", StringComparison.OrdinalIgnoreCase) ||
               IsGenericVideoHost(url);
    }

    public async Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        string title = "Web_Media_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string videoUrl = url;

        // 1. Check if it's an HLS Master Playlist
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

                using var response = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string playlistContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var baseUri = new Uri(url);

                    // Pick highest bandwidth video stream in master playlist
                    string[] lines = playlistContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    long maxBw = -1;
                    string? bestCandidate = null;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                        {
                            long bw = 0;
                            var bwMatch = Regex.Match(lines[i], @"BANDWIDTH=(\d+)", RegexOptions.IgnoreCase);
                            if (bwMatch.Success) long.TryParse(bwMatch.Groups[1].Value, out bw);
                            if (i + 1 < lines.Length && !lines[i + 1].StartsWith("#"))
                            {
                                if (bw >= maxBw)
                                {
                                    maxBw = bw;
                                    bestCandidate = lines[i + 1].Trim();
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(bestCandidate))
                    {
                        videoUrl = new Uri(baseUri, bestCandidate).ToString();
                    }
                }
            }
            catch { }

            long? hlsSize = null;
            try
            {
                hlsSize = await CobaltResolver.ProbeHlsSizeAsync(videoUrl, cancellationToken).ConfigureAwait(false);
            }
            catch { }

            string hlsName = SegmentedDownloader.ExtractFileNameFromUrl(url);
            string baseNoExt = Path.GetFileNameWithoutExtension(hlsName);
            if (hlsName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(Path.GetExtension(hlsName)) ||
                IsGenericPlaylistFileName(baseNoExt))
            {
                hlsName = "HLS_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4";
            }
            else
            {
                hlsName = Path.ChangeExtension(hlsName, ".mp4");
            }

            var hlsResult = new ResolvedLinkResult
            {
                OriginalUrl = url,
                DirectUrl = videoUrl,
                SuggestedFileName = hlsName,
                FileSize = hlsSize,
                IsSuccess = true
            };

            hlsResult.Streams.Add(new MediaStreamInfo
            {
                StreamType = "video",
                Title = $"{hlsName} (MP4 Video)",
                Format = "mp4",
                Quality = "HD Stream",
                Url = videoUrl,
                SuggestedFileName = hlsName,
                FileSize = hlsSize,
                FileSizeIsEstimated = true,
                IsAudioOnly = false
            });

            return hlsResult;
        }

        // 2. Generic Web Player / Direct Stream Scraper
        try
        {
            var uri = new Uri(url);
            string baseFileName = Path.GetFileNameWithoutExtension(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(baseFileName) && !IsGenericPlaylistFileName(baseFileName))
            {
                title = CleanFileName(WebUtility.UrlDecode(baseFileName));
            }
        }
        catch { }

        // If URL is a webpage/player URL (not a direct binary stream), try extracting page title and embedded video URL
        bool looksLikeDirectMedia = Regex.IsMatch(url, @"\.(mp4|webm|mkv|flv|mov|avi|ts|m4v)(\?.*)?$", RegexOptions.IgnoreCase);
        if (!looksLikeDirectMedia)
        {
            try
            {
                var (scrapedTitle, scrapedStreamUrl) = await TryScrapePageMetadataAsync(url, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(scrapedTitle))
                {
                    title = CleanFileName(scrapedTitle);
                }
                if (!string.IsNullOrWhiteSpace(scrapedStreamUrl))
                {
                    videoUrl = scrapedStreamUrl;
                }
            }
            catch { }
        }

        long? detectedSize = null;
        try
        {
            var uri = new Uri(videoUrl);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            string? clen = query["clen"] ?? query["content_length"] ?? query["size"];
            if (!string.IsNullOrEmpty(clen) && long.TryParse(clen, out long s) && s > 0)
            {
                detectedSize = s;
            }
        }
        catch { }

        if (detectedSize == null && videoUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                detectedSize = await CobaltResolver.ProbeHlsSizeAsync(videoUrl, cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }
        else if (detectedSize == null && Regex.IsMatch(videoUrl, @"\.(mp4|webm|mkv|flv|mov)(\?.*)?$", RegexOptions.IgnoreCase))
        {
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, videoUrl);
                headReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                using var headRes = await HttpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (headRes.Content.Headers.ContentLength.HasValue && headRes.Content.Headers.ContentLength.Value > 0)
                {
                    detectedSize = headRes.Content.Headers.ContentLength.Value;
                }
            }
            catch { }
        }

        var result = new ResolvedLinkResult
        {
            OriginalUrl = url,
            DirectUrl = videoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = detectedSize,
            IsSuccess = true
        };

        result.Streams.Add(new MediaStreamInfo
        {
            StreamType = "video",
            Title = $"{title} (Video MP4)",
            Format = "mp4",
            Quality = "HD MP4",
            Url = videoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = detectedSize,
            FileSizeIsEstimated = videoUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase),
            IsAudioOnly = false
        });

        return result;
    }

    public static bool IsGenericPlaylistFileName(string? nameWithoutExt)
    {
        if (string.IsNullOrWhiteSpace(nameWithoutExt)) return true;
        string n = nameWithoutExt.Trim().ToLowerInvariant();
        return n is "master" or "index" or "playlist" or "manifest" or "video" or "stream"
            or "playback" or "videoplayback" or "watch" or "view" or "download" or "media"
            or "player" or "embed" or "chunklist" or "hls";
    }

    public static async Task<(string? Title, string? StreamUrl)> TryScrapePageMetadataAsync(string pageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || !pageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            req.Headers.Range = new RangeHeaderValue(0, 262144); // Read up to first 256 KB if supported

            using var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, null);

            string mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(mediaType))
            {
                return (null, null);
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            char[] buffer = new char[131072];
            int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            string html = new string(buffer, 0, read);

            string? title = null;
            var ogTitle = Regex.Match(html, @"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!ogTitle.Success)
                ogTitle = Regex.Match(html, @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:title[""']", RegexOptions.IgnoreCase);

            if (ogTitle.Success)
            {
                title = WebUtility.HtmlDecode(ogTitle.Groups[1].Value).Trim();
            }
            else
            {
                var titleTag = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
                if (titleTag.Success)
                {
                    title = WebUtility.HtmlDecode(titleTag.Groups[1].Value).Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                title = Regex.Replace(title, @"\s*[-|–—]\s*(Vimeo|Dailymotion|Twitch|Kick|Streamable|Rumble|Bilibili|Watch|Video).*$", "", RegexOptions.IgnoreCase).Trim();
            }

            string? streamUrl = null;
            var ogVideo = Regex.Match(html, @"<meta[^>]+property=[""']og:video(?::secure_url|:url)?[""'][^>]+content=[""'](https?://[^""']+\.(?:mp4|m3u8)[^""']*)[""']", RegexOptions.IgnoreCase);
            if (ogVideo.Success)
            {
                streamUrl = WebUtility.HtmlDecode(ogVideo.Groups[1].Value);
            }
            else
            {
                var m3u8Match = Regex.Match(html, @"[""'](https?:\\?/\\?/[^""'\s\\]+?\.m3u8[^""'\s\\]*)[""']", RegexOptions.IgnoreCase);
                if (m3u8Match.Success)
                {
                    streamUrl = m3u8Match.Groups[1].Value.Replace("\\/", "/");
                }
                else
                {
                    var mp4Match = Regex.Match(html, @"[""'](https?:\\?/\\?/[^""'\s\\]+?\.mp4[^""'\s\\]*)[""']", RegexOptions.IgnoreCase);
                    if (mp4Match.Success)
                    {
                        streamUrl = mp4Match.Groups[1].Value.Replace("\\/", "/");
                    }
                }
            }

            return (title, streamUrl);
        }
        catch
        {
            return (null, null);
        }
    }

    public static string CleanFileName(string name)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string clean = new string(name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '.', '_');
        if (clean.Length > 110) clean = clean.Substring(0, 110).Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(clean) ? "Video" : clean;
    }
}

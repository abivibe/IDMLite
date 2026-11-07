using System.Text.Json;
using System.Text.RegularExpressions;
using IDMLite.Core.Services;

namespace IDMLite.Core.Resolvers;

public class TwitterResolver : ILinkResolver
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public string ServiceName => "X (Twitter) Video & Audio Resolver";

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return (url.Contains("twitter.com/", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("x.com/", StringComparison.OrdinalIgnoreCase)) &&
               url.Contains("/status/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        // 1. Try yt-dlp first if available
        if (YtDlpHelper.IsAvailable)
        {
            try
            {
                var mediaInfo = await YtDlpHelper.GetMediaInfoAsync(url, cancellationToken).ConfigureAwait(false);
                if (mediaInfo != null)
                {
                    string safeTitle = CleanFileName(mediaInfo.Title);
                    if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "X_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

                    string? directUrl = mediaInfo.DirectVideoUrl ??
                                       mediaInfo.Formats.LastOrDefault(f => f.Extension == "mp4" && !string.IsNullOrEmpty(f.Url))?.Url ??
                                       mediaInfo.Formats.LastOrDefault(f => !string.IsNullOrEmpty(f.Url))?.Url;

                    if (!string.IsNullOrEmpty(directUrl))
                    {
                        var twResult = new ResolvedLinkResult
                        {
                            OriginalUrl = url,
                            DirectUrl = directUrl,
                            SuggestedFileName = $"{safeTitle}.mp4",
                            FileSize = mediaInfo.VideoSize,
                            IsSuccess = true
                        };

                        twResult.Streams.Add(new MediaStreamInfo
                        {
                            StreamType = "video",
                            Title = $"{safeTitle} (MP4 Video)",
                            Format = "mp4",
                            Quality = mediaInfo.VideoQuality,
                            Url = directUrl,
                            SuggestedFileName = $"{safeTitle}.mp4",
                            FileSize = mediaInfo.VideoSize,
                            IsAudioOnly = false
                        });

                        return twResult;
                    }
                }
            }
            catch { }
        }

        // 2. KaraDelik2 Endpoints: FxTwitter & VxTwitter
        string title = "X_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string directVideoUrl = url;
        string directAudioUrl = url;

        try
        {
            var match = Regex.Match(url, @"(?:twitter\.com|x\.com)\/(?:([a-zA-Z0-9_]+)\/)?status\/(\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string? user = match.Groups[1].Success ? match.Groups[1].Value : null;
                string statusId = match.Groups[2].Value;

                var endpoints = new List<string>();
                if (!string.IsNullOrEmpty(user) && !user.Equals("i", StringComparison.OrdinalIgnoreCase))
                {
                    endpoints.Add($"https://api.fxtwitter.com/{user}/status/{statusId}");
                }
                endpoints.Add($"https://api.fxtwitter.com/i/status/{statusId}");
                if (!string.IsNullOrEmpty(user) && !user.Equals("i", StringComparison.OrdinalIgnoreCase))
                {
                    endpoints.Add($"https://api.vxtwitter.com/{user}/status/{statusId}");
                }
                endpoints.Add($"https://api.vxtwitter.com/i/status/{statusId}");

                foreach (var ep in endpoints)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, ep);
                        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
                        req.Headers.Accept.ParseAdd("application/json");

                        using var response = await HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                            var root = doc.RootElement;

                            if (root.TryGetProperty("tweet", out var tweet) && tweet.ValueKind == JsonValueKind.Object)
                            {
                                string text = tweet.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? "" : "";
                                string author = "";
                                if (tweet.TryGetProperty("author", out var authorProp) && authorProp.TryGetProperty("name", out var nameProp))
                                {
                                    author = nameProp.GetString() ?? "";
                                }

                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    string cleanText = CleanFileName(text.Length > 50 ? text.Substring(0, 50) : text);
                                    title = !string.IsNullOrWhiteSpace(author) ? $"{CleanFileName(author)} - {cleanText}" : cleanText;
                                }

                                // 1. media.videos listesi
                                if (tweet.TryGetProperty("media", out var media))
                                {
                                    if (media.TryGetProperty("videos", out var videos) && videos.GetArrayLength() > 0)
                                    {
                                        var firstVideo = videos[0];
                                        if (firstVideo.TryGetProperty("url", out var vUrl) && !string.IsNullOrWhiteSpace(vUrl.GetString()))
                                        {
                                            directVideoUrl = vUrl.GetString()!;
                                            directAudioUrl = directVideoUrl;
                                            break;
                                        }
                                    }

                                    // 2. media_extended listesi
                                    if (tweet.TryGetProperty("media_extended", out var mediaExt) && mediaExt.GetArrayLength() > 0)
                                    {
                                        foreach (var item in mediaExt.EnumerateArray())
                                        {
                                            if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "video" &&
                                                item.TryGetProperty("url", out var uProp) && !string.IsNullOrWhiteSpace(uProp.GetString()))
                                            {
                                                directVideoUrl = uProp.GetString()!;
                                                directAudioUrl = directVideoUrl;
                                                break;
                                            }
                                        }
                                        if (directVideoUrl != url) break;
                                    }

                                    // 3. media.all listesi
                                    if (media.TryGetProperty("all", out var allMedia) && allMedia.GetArrayLength() > 0)
                                    {
                                        foreach (var m in allMedia.EnumerateArray())
                                        {
                                            if (m.TryGetProperty("url", out var mUrl) && mUrl.GetString()?.EndsWith(".mp4") == true)
                                            {
                                                directVideoUrl = mUrl.GetString()!;
                                                directAudioUrl = directVideoUrl;
                                                break;
                                            }
                                        }
                                        if (directVideoUrl != url) break;
                                    }
                                }

                                // 4. video.url tekil alan
                                if (tweet.TryGetProperty("video", out var singleVideo) &&
                                    singleVideo.TryGetProperty("url", out var sUrl) && !string.IsNullOrWhiteSpace(sUrl.GetString()))
                                {
                                    directVideoUrl = sUrl.GetString()!;
                                    directAudioUrl = directVideoUrl;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 3. Cobalt Fallback
        if (string.IsNullOrWhiteSpace(directVideoUrl) || directVideoUrl == url)
        {
            try
            {
                var cobalt = new CobaltResolver();
                var cobRes = await cobalt.ResolveAsync(url, cancellationToken).ConfigureAwait(false);
                if (cobRes != null && cobRes.IsSuccess && !string.IsNullOrEmpty(cobRes.DirectUrl))
                {
                    return cobRes;
                }
            }
            catch { }
        }

        long? videoSize = null;
        if (!string.IsNullOrWhiteSpace(directVideoUrl) && directVideoUrl != url)
        {
            try
            {
                videoSize = await CobaltResolver.ProbeUrlSizeAsync(directVideoUrl, cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(directVideoUrl) || directVideoUrl == url)
        {
            return new ResolvedLinkResult
            {
                OriginalUrl = url,
                DirectUrl = url,
                IsSuccess = false,
                ErrorMessage = "X (Twitter) video bağlantısı çözülemedi. Lütfen videoyu tarayıcıda izlerken IDMLite eklentisindeki 'Bu Videoyu İndir' butonunu kullanarak indirin."
            };
        }

        var result = new ResolvedLinkResult
        {
            OriginalUrl = url,
            DirectUrl = directVideoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = videoSize,
            IsSuccess = true
        };

        // 1. Video option
        result.Streams.Add(new MediaStreamInfo
        {
            StreamType = "video",
            Title = $"{title} (Video MP4)",
            Format = "mp4",
            Quality = "HD Video",
            Url = directVideoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = videoSize,
            IsAudioOnly = false
        });

        // 2. Audio option
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

    private static string CleanFileName(string name)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string clean = new string(name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        return clean.Trim(' ', '.');
    }
}

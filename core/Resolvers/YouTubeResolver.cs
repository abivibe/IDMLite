using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using IDMLite.Core.Services;

namespace IDMLite.Core.Resolvers;

public class YouTubeResolver : ILinkResolver
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly string[] InvidiousInstances = new[]
    {
        "https://inv.nadeko.net",
        "https://invidious.nerdvpn.de",
        "https://invidious.privacydev.net",
        "https://yewtu.be",
        "https://invidious.jing.rocks",
        "https://invidious.f5.si"
    };

    private static readonly string[] PipedInstances = new[]
    {
        "https://api.piped.privacydev.net",
        "https://pipedapi.kavin.rocks",
        "https://pipedapi.tokhmi.xyz"
    };

    public string ServiceName => "YouTube Video & Audio Resolver";

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtube.com/shorts/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtube.com/embed/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("m.youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("googlevideo.com/videoplayback", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        // If already a direct googlevideo playback stream (e.g. sniffed from browser extension)
        if (url.Contains("googlevideo.com/videoplayback", StringComparison.OrdinalIgnoreCase))
        {
            string cleanStreamUrl = CleanGoogleVideoPlaybackUrl(url);
            bool isAudioStream = url.Contains("mime=audio", StringComparison.OrdinalIgnoreCase) ||
                                url.Contains("itag=140", StringComparison.OrdinalIgnoreCase) ||
                                url.Contains("itag=251", StringComparison.OrdinalIgnoreCase);

            string genericTitle = isAudioStream ? "YouTube_Audio" : "YouTube_Video";
            string ext = isAudioStream ? ".mp3" : ".mp4";
            long? clen = ExtractClenFromUrl(url);

            var directResult = new ResolvedLinkResult
            {
                OriginalUrl = url,
                DirectUrl = cleanStreamUrl,
                SuggestedFileName = $"{genericTitle}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}",
                FileSize = clen,
                IsSuccess = true
            };

            directResult.Streams.Add(new MediaStreamInfo
            {
                StreamType = isAudioStream ? "audio" : "video",
                Title = isAudioStream ? "Tek Parça Ses (HQ Audio)" : "Tek Parça Video (MP4 HD)",
                Format = isAudioStream ? "mp3" : "mp4",
                Quality = isAudioStream ? "HQ Audio (320 kbps)" : "1080p / 720p HD",
                Url = cleanStreamUrl,
                SuggestedFileName = directResult.SuggestedFileName,
                FileSize = clen,
                IsAudioOnly = isAudioStream
            });

            return directResult;
        }

        if (YtDlpHelper.IsAvailable)
        {
            try
            {
                var mediaInfo = await YtDlpHelper.GetMediaInfoAsync(url, cancellationToken).ConfigureAwait(false);
                if (mediaInfo != null)
                {
                    string safeTitle = CleanFileName(mediaInfo.Title);
                    if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "YouTube_Video";

                    var ytResult = new ResolvedLinkResult
                    {
                        OriginalUrl = url,
                        DirectUrl = url,
                        SuggestedFileName = $"{safeTitle}.mp4",
                        FileSize = mediaInfo.VideoSize,
                        IsSuccess = true
                    };

                    // 1. Primary Best Quality Video (Video + Audio merged via yt-dlp)
                    ytResult.Streams.Add(new MediaStreamInfo
                    {
                        StreamType = "video",
                        Title = $"{safeTitle} (En İyi Kalite MP4 - {mediaInfo.VideoQuality})",
                        Format = "mp4",
                        Quality = mediaInfo.VideoQuality,
                        Url = url,
                        SuggestedFileName = $"{safeTitle}.mp4",
                        FileSize = mediaInfo.VideoSize,
                        FileSizeIsEstimated = mediaInfo.VideoSizeIsEstimated,
                        IsAudioOnly = false
                    });

                    // 2. Primary Best Quality Audio
                    if (mediaInfo.AudioSize.HasValue && mediaInfo.AudioSize.Value > 0)
                    {
                        ytResult.Streams.Add(new MediaStreamInfo
                        {
                            StreamType = "audio",
                            Title = $"{safeTitle} (En Yüksek Kalite Ses)",
                            Format = "mp3",
                            Quality = mediaInfo.AudioQuality,
                            Url = mediaInfo.DirectAudioUrl ?? url,
                            SuggestedFileName = $"{safeTitle}.mp3",
                            FileSize = mediaInfo.AudioSize,
                            FileSizeIsEstimated = true,
                            IsAudioOnly = true
                        });
                    }

                    // 3. Other distinct video resolutions
                    var seenResolutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mediaInfo.VideoQuality };
                    foreach (var f in mediaInfo.Formats.Where(f => !f.IsAudioOnly && !string.IsNullOrEmpty(f.Resolution)))
                    {
                        if (seenResolutions.Add(f.Resolution))
                        {
                            ytResult.Streams.Add(new MediaStreamInfo
                            {
                                StreamType = "video",
                                Title = $"{safeTitle} ({f.Resolution} {f.Extension})",
                                Format = f.Extension,
                                Quality = f.Resolution,
                                Url = f.Url ?? url,
                                SuggestedFileName = $"{safeTitle}_{f.Resolution}.{f.Extension}",
                                FileSize = f.FileSize,
                                FileSizeIsEstimated = f.FileSizeIsEstimated,
                                IsAudioOnly = false
                            });
                        }
                    }

                    return ytResult;
                }
            }
            catch { }
        }

        string title = "YouTube Video";
        string videoId = ExtractVideoId(url);

        string? directVideoUrl = null;
        string directVideoQuality = "720p / 1080p HD";
        long? directVideoSize = null;
        string? directAudioUrl = null;
        string directAudioQuality = "HQ Audio (320 kbps)";
        long? directAudioSize = null;

        // 1. Try Invidious & Piped API instances in parallel for real direct MP4 and Audio streams
        if (!string.IsNullOrEmpty(videoId))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5)); // Fast 3.5s total timeout across all instances

            var tasks = new List<Task<YouTubeParsedStreamInfo?>>();
            foreach (var inst in InvidiousInstances)
            {
                tasks.Add(QueryInvidiousAsync(inst, videoId, cts.Token));
            }
            foreach (var inst in PipedInstances)
            {
                tasks.Add(QueryPipedAsync(inst, videoId, cts.Token));
            }

            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(finished);

                try
                {
                    var parsed = await finished.ConfigureAwait(false);
                    if (parsed != null && (!string.IsNullOrEmpty(parsed.VideoUrl) || !string.IsNullOrEmpty(parsed.AudioUrl)))
                    {
                        if (!string.IsNullOrEmpty(parsed.Title)) title = parsed.Title;
                        if (!string.IsNullOrEmpty(parsed.VideoUrl))
                        {
                            directVideoUrl = parsed.VideoUrl;
                            directVideoQuality = parsed.VideoQuality;
                            directVideoSize = parsed.VideoSize;
                        }
                        if (!string.IsNullOrEmpty(parsed.AudioUrl))
                        {
                            directAudioUrl = parsed.AudioUrl;
                            directAudioQuality = parsed.AudioQuality;
                            directAudioSize = parsed.AudioSize;
                        }
                        try { cts.Cancel(); } catch { }
                        break;
                    }
                }
                catch { }
            }
        }

        // 2. Fallback for Title via oEmbed if API was unreachable
        if (title == "YouTube Video")
        {
            try
            {
                string oembedUrl = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(url)}&format=json";
                using var response = await HttpClient.GetAsync(oembedUrl, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (doc.RootElement.TryGetProperty("title", out var titleProp))
                    {
                        string rawTitle = titleProp.GetString() ?? "YouTube Video";
                        string clean = CleanFileName(rawTitle);
                        if (!string.IsNullOrWhiteSpace(clean)) title = clean;
                    }
                }
            }
            catch { }
        }

        // 3. Fallback direct URLs if API was unreachable
        directVideoUrl ??= url;
        directAudioUrl ??= url;

        if (directVideoSize == null) directVideoSize = ExtractClenFromUrl(directVideoUrl);
        if (directAudioSize == null) directAudioSize = ExtractClenFromUrl(directAudioUrl);

        var result = new ResolvedLinkResult
        {
            OriginalUrl = url,
            DirectUrl = directVideoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = directVideoSize,
            IsSuccess = true
        };

        // 1. Video stream option (MP4 - Highest quality single piece)
        result.Streams.Add(new MediaStreamInfo
        {
            StreamType = "video",
            Title = $"{title} (Video MP4)",
            Format = "mp4",
            Quality = directVideoQuality,
            Url = directVideoUrl,
            SuggestedFileName = $"{title}.mp4",
            FileSize = directVideoSize,
            IsAudioOnly = false
        });

        // 2. Audio stream option (HQ Audio)
        result.Streams.Add(new MediaStreamInfo
        {
            StreamType = "audio",
            Title = $"{title} (Ses MP3)",
            Format = "mp3",
            Quality = directAudioQuality,
            Url = directAudioUrl,
            SuggestedFileName = $"{title}.mp3",
            FileSize = directAudioSize,
            IsAudioOnly = true
        });

        return result;
    }

    public static string ExtractVideoId(string url)
    {
        var match = Regex.Match(url, @"(?:v=|\/|youtu\.be\/|shorts\/)([a-zA-Z0-9_-]{11})");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    public static string CleanGoogleVideoPlaybackUrl(string url)
    {
        try
        {
            // Remove range parameter so the whole single-piece stream is served
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            query.Remove("range");
            query.Remove("rn");
            query.Remove("rbuf");
            var builder = new UriBuilder(uri)
            {
                Query = query.ToString()
            };
            return builder.Uri.ToString();
        }
        catch
        {
            return Regex.Replace(url, @"([?&])range=\d+-\d+(&?)", "$1").TrimEnd('?', '&');
        }
    }

    public static long? ExtractClenFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var match = Regex.Match(url, @"[?&](?:clen|content_length|size)=(\d+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out long clen) && clen > 0)
            {
                return clen;
            }
        }
        catch { }
        return null;
    }

    private static string CleanFileName(string name)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string clean = new string(name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        return clean.Trim(' ', '.');
    }

    private record YouTubeParsedStreamInfo(
        string? Title,
        string? VideoUrl,
        string VideoQuality,
        long? VideoSize,
        string? AudioUrl,
        string AudioQuality,
        long? AudioSize);

    private static async Task<YouTubeParsedStreamInfo?> QueryInvidiousAsync(string instance, string videoId, CancellationToken ct)
    {
        try
        {
            string apiUrl = $"{instance}/api/v1/videos/{videoId}";
            using var res = await HttpClient.GetAsync(apiUrl, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            string? title = null;
            if (root.TryGetProperty("title", out var titleEl) && !string.IsNullOrWhiteSpace(titleEl.GetString()))
            {
                title = CleanFileName(titleEl.GetString()!);
            }

            string? videoUrl = null;
            string videoQuality = "720p / 1080p HD";
            long? videoSize = null;

            if (root.TryGetProperty("formatStreams", out var formatsEl) && formatsEl.ValueKind == JsonValueKind.Array)
            {
                var mp4List = new List<(string url, string quality, int height, long? size)>();
                foreach (var f in formatsEl.EnumerateArray())
                {
                    string? fUrl = f.TryGetProperty("url", out var u) ? u.GetString() : null;
                    string? quality = f.TryGetProperty("qualityLabel", out var q) ? q.GetString() : null;
                    long? fSize = null;
                    if (f.TryGetProperty("clen", out var clenProp))
                    {
                        if (clenProp.ValueKind == JsonValueKind.Number) fSize = clenProp.GetInt64();
                        else if (long.TryParse(clenProp.GetString(), out long cl)) fSize = cl;
                    }
                    else if (f.TryGetProperty("contentLength", out var lenProp))
                    {
                        if (lenProp.ValueKind == JsonValueKind.Number) fSize = lenProp.GetInt64();
                        else if (long.TryParse(lenProp.GetString(), out long cl)) fSize = cl;
                    }
                    if (fSize == null && fUrl != null) fSize = ExtractClenFromUrl(fUrl);

                    if (!string.IsNullOrWhiteSpace(fUrl))
                    {
                        int height = 360;
                        if (quality != null && int.TryParse(Regex.Match(quality, @"\d+").Value, out int h))
                        {
                            height = h;
                        }
                        mp4List.Add((CleanGoogleVideoPlaybackUrl(fUrl), quality ?? $"{height}p", height, fSize));
                    }
                }

                var best = mp4List.OrderByDescending(x => x.height).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(best.url))
                {
                    videoUrl = best.url;
                    videoQuality = $"{best.quality} MP4 (Tek Parça)";
                    videoSize = best.size;
                }
            }

            string? audioUrl = null;
            string audioQuality = "HQ Audio (320 kbps)";
            long? audioSize = null;

            if (root.TryGetProperty("adaptiveFormats", out var adaptiveEl) && adaptiveEl.ValueKind == JsonValueKind.Array)
            {
                var audioList = new List<(string url, int bitrate, long? size)>();
                var videoList = new List<(string url, string quality, int height, long? size)>();

                foreach (var a in adaptiveEl.EnumerateArray())
                {
                    string? aUrl = a.TryGetProperty("url", out var u) ? u.GetString() : null;
                    string? type = a.TryGetProperty("type", out var t) ? t.GetString() : "";
                    string? container = a.TryGetProperty("container", out var c) ? c.GetString() : "";
                    if (string.IsNullOrWhiteSpace(aUrl)) continue;

                    long? aSize = null;
                    if (a.TryGetProperty("clen", out var clenProp))
                    {
                        if (clenProp.ValueKind == JsonValueKind.Number) aSize = clenProp.GetInt64();
                        else if (long.TryParse(clenProp.GetString(), out long cl)) aSize = cl;
                    }
                    else if (a.TryGetProperty("contentLength", out var lenProp))
                    {
                        if (lenProp.ValueKind == JsonValueKind.Number) aSize = lenProp.GetInt64();
                        else if (long.TryParse(lenProp.GetString(), out long cl)) aSize = cl;
                    }
                    if (aSize == null) aSize = ExtractClenFromUrl(aUrl);

                    if (type?.Contains("audio") == true)
                    {
                        int bitrate = a.TryGetProperty("bitrate", out var b) ? b.GetInt32() : 128000;
                        audioList.Add((CleanGoogleVideoPlaybackUrl(aUrl), bitrate, aSize));
                    }
                    else if (type?.Contains("video") == true && (container?.Contains("mp4") == true || container == "mp4"))
                    {
                        string? quality = a.TryGetProperty("qualityLabel", out var q) ? q.GetString() : null;
                        int height = 720;
                        if (quality != null && int.TryParse(Regex.Match(quality, @"\d+").Value, out int h))
                        {
                            height = h;
                        }
                        videoList.Add((CleanGoogleVideoPlaybackUrl(aUrl), quality ?? $"{height}p", height, aSize));
                    }
                }

                var bestAudio = audioList.OrderByDescending(x => x.bitrate).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(bestAudio.url))
                {
                    audioUrl = bestAudio.url;
                    int kbps = bestAudio.bitrate > 1000 ? bestAudio.bitrate / 1000 : 320;
                    audioQuality = $"{kbps} kbps HQ Audio";
                    audioSize = bestAudio.size;
                }

                if (string.IsNullOrWhiteSpace(videoUrl) && videoList.Count > 0)
                {
                    var bestAdaptive = videoList.OrderByDescending(x => x.height).FirstOrDefault();
                    videoUrl = bestAdaptive.url;
                    videoQuality = $"{bestAdaptive.quality} MP4";
                    videoSize = bestAdaptive.size;
                }
            }

            if (!string.IsNullOrWhiteSpace(videoUrl) || !string.IsNullOrWhiteSpace(audioUrl))
            {
                return new YouTubeParsedStreamInfo(title, videoUrl, videoQuality, videoSize, audioUrl, audioQuality, audioSize);
            }
        }
        catch { }
        return null;
    }

    private static async Task<YouTubeParsedStreamInfo?> QueryPipedAsync(string instance, string videoId, CancellationToken ct)
    {
        try
        {
            string apiUrl = $"{instance}/streams/{videoId}";
            using var res = await HttpClient.GetAsync(apiUrl, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            string? title = null;
            if (root.TryGetProperty("title", out var titleEl) && !string.IsNullOrWhiteSpace(titleEl.GetString()))
            {
                title = CleanFileName(titleEl.GetString()!);
            }

            string? videoUrl = null;
            string videoQuality = "720p / 1080p HD";
            long? videoSize = null;

            if (root.TryGetProperty("videoStreams", out var vEl) && vEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<(string url, string quality, int height, long? size)>();
                foreach (var v in vEl.EnumerateArray())
                {
                    string? u = v.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                    string? q = v.TryGetProperty("quality", out var qEl) ? qEl.GetString() : null;
                    string? fmt = v.TryGetProperty("format", out var fEl) ? fEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(u) || fmt != "MPEG_4") continue;

                    long? size = null;
                    if (v.TryGetProperty("contentLength", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
                    {
                        size = cEl.GetInt64();
                    }
                    if (size == null) size = ExtractClenFromUrl(u);

                    int height = 720;
                    if (q != null && int.TryParse(Regex.Match(q, @"\d+").Value, out int h)) height = h;

                    list.Add((CleanGoogleVideoPlaybackUrl(u), q ?? $"{height}p", height, size));
                }

                var bestV = list.OrderByDescending(x => x.height).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(bestV.url))
                {
                    videoUrl = bestV.url;
                    videoQuality = $"{bestV.quality} MP4";
                    videoSize = bestV.size;
                }
            }

            string? audioUrl = null;
            string audioQuality = "HQ Audio (320 kbps)";
            long? audioSize = null;

            if (root.TryGetProperty("audioStreams", out var aEl) && aEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<(string url, int bitrate, long? size)>();
                foreach (var a in aEl.EnumerateArray())
                {
                    string? u = a.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                    int bitrate = a.TryGetProperty("bitrate", out var bEl) ? bEl.GetInt32() : 128000;
                    if (string.IsNullOrWhiteSpace(u)) continue;

                    long? size = null;
                    if (a.TryGetProperty("contentLength", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
                    {
                        size = cEl.GetInt64();
                    }
                    if (size == null) size = ExtractClenFromUrl(u);

                    list.Add((CleanGoogleVideoPlaybackUrl(u), bitrate, size));
                }

                var bestA = list.OrderByDescending(x => x.bitrate).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(bestA.url))
                {
                    audioUrl = bestA.url;
                    int kbps = bestA.bitrate > 1000 ? bestA.bitrate / 1000 : 320;
                    audioQuality = $"{kbps} kbps HQ Audio";
                    audioSize = bestA.size;
                }
            }

            if (!string.IsNullOrWhiteSpace(videoUrl) || !string.IsNullOrWhiteSpace(audioUrl))
            {
                return new YouTubeParsedStreamInfo(title, videoUrl, videoQuality, videoSize, audioUrl, audioQuality, audioSize);
            }
        }
        catch { }
        return null;
    }
}

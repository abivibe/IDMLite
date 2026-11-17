
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

    public string ServiceName => "YouTube Video & Audio Resolver";

    public bool CanResolve(string url) =>
        (MediaUrl.HasHost(url, "youtube.com", "youtu.be") && MediaUrl.IsSocialPage(url)) ||
        (MediaUrl.HasHost(url, "googlevideo.com") && new Uri(url).AbsolutePath == "/videoplayback");

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


        string? directVideoUrl = null;
        string directVideoQuality = "720p / 1080p HD";
        long? directVideoSize = null;
        string? directAudioUrl = null;
        string directAudioQuality = "HQ Audio (320 kbps)";
        long? directAudioSize = null;

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
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IDMLite.Core.Resolvers;

public class CobaltResolver : ILinkResolver
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly string[] CobaltInstances = new[]
    {
        "https://rue-cobalt.xenon.zone/",
        "https://cobaltapi.cjs.nz/",
        "https://cobaltapi.kittycat.boo/",
        "https://cobaltapi.squair.xyz/",
        "https://dog.kittycat.boo/",
        "https://api.cobalt.rpkiinval.id/",
        "https://melon.clxxped.lol/",
        "https://grapefruit.clxxped.lol/",
        "https://lime.clxxped.lol/"
    };

    public string ServiceName => "Cobalt Social Stream Resolver";

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string lower = url.ToLowerInvariant();
        return lower.Contains("instagram.com/") ||
               lower.Contains("twitter.com/") ||
               lower.Contains("x.com/") ||
               lower.Contains("tiktok.com/");
    }

    public static async Task<long?> ProbeHlsSizeAsync(string m3u8Url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, m3u8Url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMLite/1.0");
            using var resp = await HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string content = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var baseUri = new Uri(m3u8Url);

            // If master playlist, find variant with highest bandwidth
            if (content.Contains("#EXT-X-STREAM-INF"))
            {
                long maxBw = -1;
                string bestSubUrl = string.Empty;
                var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("#EXT-X-STREAM-INF"))
                    {
                        long bw = ExtractBandwidthFromLine(lines[i]);
                        if (i + 1 < lines.Length && !lines[i + 1].StartsWith("#"))
                        {
                            if (bw > maxBw || string.IsNullOrEmpty(bestSubUrl))
                            {
                                maxBw = bw;
                                bestSubUrl = lines[i + 1].Trim();
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(bestSubUrl))
                {
                    var subFullUri = new Uri(baseUri, bestSubUrl).ToString();
                    return await ProbeHlsSizeAsync(subFullUri, cancellationToken).ConfigureAwait(false);
                }
            }

            // Media playlist: extract segments and estimate size
            double totalDuration = 0;
            var subLines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int segCount = 0;
            string? firstSegUrl = null;

            foreach (var l in subLines)
            {
                if (l.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(l, @"#EXTINF:(\d+(?:\.\d+)?)");
                    if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d))
                    {
                        totalDuration += d;
                    }
                }
                else if (!l.StartsWith("#") && !string.IsNullOrWhiteSpace(l))
                {
                    segCount++;
                    if (firstSegUrl == null)
                    {
                        firstSegUrl = new Uri(baseUri, l.Trim()).ToString();
                    }
                }
            }

            // If we have a first segment, probe its exact size and multiply by segment count
            if (firstSegUrl != null && segCount > 0)
            {
                long? segSize = await ProbeUrlSizeAsync(firstSegUrl, cancellationToken).ConfigureAwait(false);
                if (segSize.HasValue && segSize.Value > 0)
                {
                    return segSize.Value * segCount;
                }
            }

            // Fallback: estimate from duration assuming standard 2.5 Mbps bitrate
            if (totalDuration > 0)
            {
                return (long)(totalDuration * (2_500_000 / 8));
            }
        }
        catch { }
        return null;
    }

    private static long ExtractBandwidthFromLine(string line)
    {
        try
        {
            int idx = line.IndexOf("BANDWIDTH=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int start = idx + "BANDWIDTH=".Length;
                int end = line.IndexOfAny(new[] { ',', ' ' }, start);
                string val = end > start ? line.Substring(start, end - start) : line.Substring(start);
                if (long.TryParse(val, out long bw)) return bw;
            }
        }
        catch { }
        return 0;
    }

    public static async Task<long?> ProbeUrlSizeAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl)) return null;

        if (streamUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || streamUrl.Contains(".m3u8?", StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeHlsSizeAsync(streamUrl, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, streamUrl);
            headReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMLite/1.0");
            using var headResp = await HttpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (headResp.IsSuccessStatusCode && headResp.Content.Headers.ContentLength.HasValue && headResp.Content.Headers.ContentLength.Value > 0)
            {
                return headResp.Content.Headers.ContentLength.Value;
            }

            // Fallback Range: bytes=0-0
            using var rangeReq = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            rangeReq.Headers.Range = new RangeHeaderValue(0, 0);
            rangeReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMLite/1.0");
            using var rangeResp = await HttpClient.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (rangeResp.Content.Headers.ContentRange?.Length.HasValue == true)
            {
                return rangeResp.Content.Headers.ContentRange.Length.Value;
            }
            if (rangeResp.Content.Headers.ContentLength.HasValue && rangeResp.Content.Headers.ContentLength.Value > 0)
            {
                return rangeResp.Content.Headers.ContentLength.Value;
            }
        }
        catch { }
        return null;
    }

    public async Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            url = url,
            videoQuality = "max",
            downloadMode = "auto"
        };

        foreach (var instance in CobaltInstances)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                using var req = new HttpRequestMessage(HttpMethod.Post, instance);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMLite/1.0");
                req.Content = JsonContent.Create(payload);

                using var resp = await HttpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string status = root.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "" : "";
                    string? directUrl = null;
                    string? directAudioUrl = null;
                    string title = root.TryGetProperty("filename", out var fnProp) ? fnProp.GetString() ?? "Social_Video" : "Social_Video";

                    string cobaltType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";

                    if (status == "tunnel" || status == "redirect" || status == "stream")
                    {
                        bool isVideoExpected = url.Contains("/reel", StringComparison.OrdinalIgnoreCase) ||
                                               url.Contains("/reels", StringComparison.OrdinalIgnoreCase) ||
                                               url.Contains("/video", StringComparison.OrdinalIgnoreCase);

                        if (cobaltType.Equals("photo", StringComparison.OrdinalIgnoreCase) && isVideoExpected)
                        {
                            // Cobalt returned a photo/thumbnail tunnel instead of the video stream - skip to avoid corrupt .mp4
                            continue;
                        }

                        if (root.TryGetProperty("url", out var urlProp))
                        {
                            directUrl = urlProp.GetString();
                        }
                    }
                    else if (status == "picker" && root.TryGetProperty("picker", out var pickerProp))
                    {
                        foreach (var item in pickerProp.EnumerateArray())
                        {
                            string type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                            string itemUrl = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                            if (type == "video" && directUrl == null) directUrl = itemUrl;
                            else if (type == "audio" && directAudioUrl == null) directAudioUrl = itemUrl;
                        }
                    }

                    if (!string.IsNullOrEmpty(directUrl))
                    {
                        // Clean file title and enforce appropriate extension (.mp4 for videos, .jpg for photos)
                        title = SegmentedDownloader.CleanFileName(title);
                        if (cobaltType.Equals("photo", StringComparison.OrdinalIgnoreCase) ||
                            title.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            title.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            title.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!title.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                                !title.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                                !title.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                            {
                                title = Path.GetFileNameWithoutExtension(title) + ".jpg";
                            }
                        }
                        else if (!title.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            title = Path.GetFileNameWithoutExtension(title) + ".mp4";
                        }

                        // Instant parallel probe for true byte size
                        long? exactSize = await ProbeUrlSizeAsync(directUrl, cancellationToken).ConfigureAwait(false);

                        var result = new ResolvedLinkResult
                        {
                            OriginalUrl = url,
                            DirectUrl = directUrl,
                            SuggestedFileName = title,
                            FileSize = exactSize,
                            IsSuccess = true
                        };

                        result.Streams.Add(new MediaStreamInfo
                        {
                            StreamType = "video",
                            Title = $"{title} (Direct MP4 HD)",
                            Format = "mp4",
                            Quality = "1080p HD",
                            Url = directUrl,
                            SuggestedFileName = title,
                            FileSize = exactSize,
                            IsAudioOnly = false
                        });

                        if (!string.IsNullOrEmpty(directAudioUrl))
                        {
                            long? audioSize = await ProbeUrlSizeAsync(directAudioUrl, cancellationToken).ConfigureAwait(false);
                            string audioName = Path.ChangeExtension(title, ".mp3");
                            result.Streams.Add(new MediaStreamInfo
                            {
                                StreamType = "audio",
                                Title = $"{audioName} (HQ Audio)",
                                Format = "mp3",
                                Quality = "HQ Audio",
                                Url = directAudioUrl,
                                SuggestedFileName = audioName,
                                FileSize = audioSize,
                                IsAudioOnly = true
                            });
                        }

                        return result;
                    }
                }
            }
            catch
            {
                continue;
            }
        }

        return new ResolvedLinkResult
        {
            OriginalUrl = url,
            DirectUrl = url,
            IsSuccess = false,
            ErrorMessage = "All Cobalt endpoints failed to resolve social media stream."
        };
    }
}

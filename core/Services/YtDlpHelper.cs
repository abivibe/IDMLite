using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IDMLite.Core.Services;

public class YtDlpMediaInfo
{
    public string Title { get; set; } = "Media";
    public string? Uploader { get; set; }
    public string? Thumbnail { get; set; }
    public double? Duration { get; set; }
    public string? DirectVideoUrl { get; set; }
    public string? DirectAudioUrl { get; set; }
    public long? VideoSize { get; set; }
    public long? AudioSize { get; set; }
    public bool VideoSizeIsEstimated { get; set; } = true;
    public bool AudioSizeIsEstimated { get; set; } = true;
    public string VideoQuality { get; set; } = "";
    public string AudioQuality { get; set; } = "HQ Audio";
    public List<YtDlpFormatInfo> Formats { get; set; } = new();
}

public class YtDlpFormatInfo
{
    public string FormatId { get; set; } = "";
    public string Extension { get; set; } = "mp4";
    public string Resolution { get; set; } = "";
    public long? FileSize { get; set; }
    public bool FileSizeIsEstimated { get; set; } = true;
    public string? Url { get; set; }
    public bool IsVideoOnly { get; set; }
    public bool IsAudioOnly { get; set; }
}

public static class YtDlpHelper
{
    private static string? FindBundledTool(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; directory != null && depth < 7; depth++, directory = directory.Parent)
            foreach (string candidate in new[] { Path.Combine(directory.FullName, name), Path.Combine(directory.FullName, "ui", name) })
                if (File.Exists(candidate)) return candidate;
        return null;
    }
    private static string? _cachedYtDlpPath;
    private static string? _cachedFfmpegPath;
    private static readonly object _lock = new();

    public static bool IsAvailable => !string.IsNullOrEmpty(FindYtDlpPath());

    public static string? FindYtDlpPath()
    {
        lock (_lock)
        {
            string? bundled = FindBundledTool("yt-dlp.exe");
            if (bundled != null) return bundled;
            if (!string.IsNullOrEmpty(_cachedYtDlpPath) && File.Exists(_cachedYtDlpPath))
                return _cachedYtDlpPath;

            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "ui", "yt-dlp.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "installer", "dist_app", "yt-dlp.exe"),



            };

            foreach (var path in candidates)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        _cachedYtDlpPath = Path.GetFullPath(path);
                        return _cachedYtDlpPath;
                    }
                }
                catch { }
            }

            // Fallback: check PATH
            try
            {
                var psi = new ProcessStartInfo("where.exe", "yt-dlp.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string? line = proc.StandardOutput.ReadLine()?.Trim();
                    proc.WaitForExit(1000);
                    if (!string.IsNullOrEmpty(line) && File.Exists(line))
                    {
                        _cachedYtDlpPath = line;
                        return _cachedYtDlpPath;
                    }
                }
            }
            catch { }

            return null;
        }
    }

    public static string? FindFfmpegPath()
    {
        lock (_lock)
        {
            string? bundled = FindBundledTool("ffmpeg.exe");
            if (bundled != null) return bundled;
            if (!string.IsNullOrEmpty(_cachedFfmpegPath) && (File.Exists(_cachedFfmpegPath) || Directory.Exists(_cachedFfmpegPath)))
                return _cachedFfmpegPath;

            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),






                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe")
            };

            foreach (var path in candidates)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        _cachedFfmpegPath = Path.GetFullPath(path);
                        return _cachedFfmpegPath;
                    }
                }
                catch { }
            }

            // Fallback: check PATH
            try
            {
                var psi = new ProcessStartInfo("where.exe", "ffmpeg.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string? line = proc.StandardOutput.ReadLine()?.Trim();
                    proc.WaitForExit(1000);
                    if (!string.IsNullOrEmpty(line) && File.Exists(line))
                    {
                        _cachedFfmpegPath = line;
                        return _cachedFfmpegPath;
                    }
                }
            }
            catch { }

            return null;
        }
    }

    public static async Task<YtDlpMediaInfo?> GetMediaInfoAsync(string url, CancellationToken cancellationToken = default, string? cookies = null)
    {
        string? exe = FindYtDlpPath();
        if (string.IsNullOrEmpty(exe)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var invocation = new YtDlpInvocation(psi, url, cookies);
        foreach (string arg in new[] { "--dump-single-json", "--no-warnings", "--skip-download", "-f", YtDlpInvocation.VideoSelector, "--", url })
            psi.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
            var output = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = proc.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, proc.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            string json = await output.ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                var failure = YtDlpFailure.Classify(await error.ConfigureAwait(false));
                Trace.TraceWarning("METADATA_FAILED category={0} exitCode={1}", failure, proc.ExitCode);
                return null;
            }
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var info = new YtDlpMediaInfo { VideoQuality = ExtractResolution(root) };
            if (root.TryGetProperty("title", out var titleProp))
            {
                info.Title = titleProp.GetString() ?? "Media";
            }
            if (root.TryGetProperty("uploader", out var uploaderProp))
            {
                info.Uploader = uploaderProp.GetString();
            }

            if (url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
            {
                string? igCreator = null;
                if (root.TryGetProperty("channel", out var chanProp) && !string.IsNullOrWhiteSpace(chanProp.GetString()))
                {
                    igCreator = chanProp.GetString()?.Trim();
                }
                else if (root.TryGetProperty("uploader_id", out var upIdProp) && !string.IsNullOrWhiteSpace(upIdProp.GetString()))
                {
                    igCreator = upIdProp.GetString()?.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(info.Uploader))
                {
                    igCreator = info.Uploader.Trim();
                }

                if (!string.IsNullOrWhiteSpace(info.Title) && info.Title.StartsWith("Video by ", StringComparison.OrdinalIgnoreCase))
                {
                    string fromTitle = info.Title.Substring("Video by ".Length).Trim();
                    if (string.IsNullOrWhiteSpace(igCreator))
                    {
                        igCreator = fromTitle;
                    }
                    info.Title = "";
                }

                string? igDesc = null;
                if (root.TryGetProperty("description", out var descProp) && !string.IsNullOrWhiteSpace(descProp.GetString()))
                {
                    igDesc = System.Text.RegularExpressions.Regex.Replace(descProp.GetString()!, @"[\r\n\t]+", " ").Trim();
                    if (igDesc.Length > 70) igDesc = igDesc.Substring(0, 70).Trim();
                }

                if (!string.IsNullOrWhiteSpace(igDesc) && !string.IsNullOrWhiteSpace(igCreator))
                {
                    info.Title = igDesc.Contains(igCreator, StringComparison.OrdinalIgnoreCase)
                        ? igDesc
                        : $"{igCreator} - {igDesc}";
                }
                else if (!string.IsNullOrWhiteSpace(igDesc))
                {
                    info.Title = igDesc;
                }
                else if (!string.IsNullOrWhiteSpace(igCreator))
                {
                    info.Title = igCreator;
                }
            }

            if (root.TryGetProperty("thumbnail", out var thumbProp))
            {
                info.Thumbnail = thumbProp.GetString();
            }
            if (root.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number && durProp.TryGetDouble(out double dur))
            {
                info.Duration = dur;
            }

            // Direct URL if single piece exists
            if (root.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
            {
                info.DirectVideoUrl = urlProp.GetString();
            }

            // Inspect formats and calculate accurate sizes
            long bestAudioSize = 0;
            double bestAudioBitrate = 0;
            string? bestAudioUrl = null;
            string bestAudioQuality = "HQ Audio";

            if (root.TryGetProperty("formats", out var formatsProp) && formatsProp.ValueKind == JsonValueKind.Array)
            {
                // First pass: identify best standalone audio format size and bitrate
                foreach (var fmt in formatsProp.EnumerateArray())
                {
                    string vcodec = fmt.TryGetProperty("vcodec", out var vc) && vc.ValueKind == JsonValueKind.String ? vc.GetString() ?? "none" : "none";
                    string acodec = fmt.TryGetProperty("acodec", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() ?? "none" : "none";
                    string videoExt = fmt.TryGetProperty("video_ext", out var ve) && ve.ValueKind == JsonValueKind.String ? ve.GetString() ?? "none" : "none";
                    string audioExt = fmt.TryGetProperty("audio_ext", out var ae) && ae.ValueKind == JsonValueKind.String ? ae.GetString() ?? "none" : "none";

                    bool isVideo = (vcodec != "none" && !string.IsNullOrWhiteSpace(vcodec)) ||
                                   (videoExt != "none" && !string.IsNullOrWhiteSpace(videoExt) && !videoExt.Equals("none", StringComparison.OrdinalIgnoreCase));
                    bool isAudio = (acodec != "none" && !string.IsNullOrWhiteSpace(acodec)) ||
                                   (audioExt != "none" && !string.IsNullOrWhiteSpace(audioExt) && !audioExt.Equals("none", StringComparison.OrdinalIgnoreCase));

                    if (!isVideo && isAudio)
                    {
                        long size = ExtractFormatSize(fmt, info.Duration ?? 0);
                        if (size > bestAudioSize)
                        {
                            bestAudioSize = size;
                            info.AudioSizeIsEstimated = !fmt.TryGetProperty("filesize", out var exactAudio) || exactAudio.ValueKind != JsonValueKind.Number || !exactAudio.TryGetInt64(out long audioBytes) || audioBytes <= 0;
                            bestAudioUrl = fmt.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                            if (fmt.TryGetProperty("format_note", out var fn) && fn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(fn.GetString()))
                                bestAudioQuality = fn.GetString()!;
                            if (fmt.TryGetProperty("abr", out var abr) && abr.ValueKind == JsonValueKind.Number && abr.TryGetDouble(out double a) && a > 0)
                                bestAudioBitrate = a;
                            else if (fmt.TryGetProperty("tbr", out var tbr) && tbr.ValueKind == JsonValueKind.Number && tbr.TryGetDouble(out double t) && t > 0)
                                bestAudioBitrate = t;
                        }
                    }
                }

                info.AudioSize = bestAudioSize > 0 ? bestAudioSize : null;
                info.DirectAudioUrl = bestAudioUrl;
                info.AudioQuality = bestAudioQuality;

                // Second pass: populate formats with accurate video + audio combined sizes
                long bestVideoSize = 0;
                string? bestVideoUrl = null;
                string? bestCombinedMuxedUrl = null;
                long bestCombinedMuxedSize = 0;
                string bestQuality = "";

                foreach (var fmt in formatsProp.EnumerateArray())
                {
                    string vcodec = fmt.TryGetProperty("vcodec", out var vc) && vc.ValueKind == JsonValueKind.String ? vc.GetString() ?? "none" : "none";
                    string acodec = fmt.TryGetProperty("acodec", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() ?? "none" : "none";
                    string fmtExt = fmt.TryGetProperty("ext", out var ex) && ex.ValueKind == JsonValueKind.String ? ex.GetString() ?? "mp4" : "mp4";
                    string fmtUrl = fmt.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                    string formatId = fmt.TryGetProperty("format_id", out var fid) && fid.ValueKind == JsonValueKind.String ? fid.GetString() ?? "" : "";
                    string resolution = ExtractResolution(fmt);
                    string videoExt = fmt.TryGetProperty("video_ext", out var ve) && ve.ValueKind == JsonValueKind.String ? ve.GetString() ?? "none" : "none";
                    string audioExt = fmt.TryGetProperty("audio_ext", out var ae) && ae.ValueKind == JsonValueKind.String ? ae.GetString() ?? "none" : "none";

                    bool isVideo = (vcodec != "none" && !string.IsNullOrWhiteSpace(vcodec)) ||
                                   (videoExt != "none" && !string.IsNullOrWhiteSpace(videoExt) && !videoExt.Equals("none", StringComparison.OrdinalIgnoreCase));
                    bool isAudio = (acodec != "none" && !string.IsNullOrWhiteSpace(acodec)) ||
                                   (audioExt != "none" && !string.IsNullOrWhiteSpace(audioExt) && !audioExt.Equals("none", StringComparison.OrdinalIgnoreCase));

                    long fmtBaseSize = ExtractFormatSize(fmt, info.Duration ?? 0);
                    // For video-only formats (modern YouTube 720p/1080p/4K DASH), yt-dlp automatically downloads & merges
                    // with best audio, so the true download size is video_size + best_audio_size.
                    long? effectiveSize = null;
                    if (isVideo && !isAudio)
                    {
                        effectiveSize = fmtBaseSize > 0 && bestAudioSize > 0 && fmtBaseSize <= long.MaxValue - bestAudioSize ? fmtBaseSize + bestAudioSize : null;
                    }
                    else if (fmtBaseSize > 0)
                    {
                        effectiveSize = fmtBaseSize;
                    }

                    if (!string.IsNullOrEmpty(fmtUrl))
                    {
                        info.Formats.Add(new YtDlpFormatInfo
                        {
                            FormatId = formatId,
                            Extension = fmtExt,
                            Resolution = resolution,
                            FileSize = effectiveSize,
                            FileSizeIsEstimated = !fmt.TryGetProperty("filesize", out var exactFormat) || exactFormat.ValueKind != JsonValueKind.Number ||
                                !exactFormat.TryGetInt64(out long exactBytes) || exactBytes <= 0 || (isVideo && !isAudio && (bestAudioSize <= 0 || info.AudioSizeIsEstimated)),
                            Url = fmtUrl,
                            IsVideoOnly = isVideo && !isAudio,
                            IsAudioOnly = !isVideo && isAudio
                        });

                        if (isVideo && isAudio && fmtExt == "mp4")
                        {
                            bestCombinedMuxedUrl = fmtUrl;
                            if ((effectiveSize ?? 0) > bestCombinedMuxedSize)
                            {
                                bestCombinedMuxedSize = effectiveSize ?? 0;
                            }
                        }

                        if (isVideo && (effectiveSize ?? 0) > bestVideoSize)
                        {
                            bestVideoSize = effectiveSize ?? 0;
                            bestVideoUrl = fmtUrl;
                            if (!string.IsNullOrEmpty(resolution)) bestQuality = resolution;
                        }
                        else if (isVideo && bestVideoUrl == null)
                        {
                            bestVideoUrl = fmtUrl;
                            if (!string.IsNullOrEmpty(resolution)) bestQuality = resolution;
                        }
                        else if (bestVideoUrl == null && !isAudio)
                        {
                            bestVideoUrl = fmtUrl;
                        }
                        else if (bestVideoUrl == null)
                        {
                            bestVideoUrl = fmtUrl;
                        }
                    }
                }

                // Check requested_formats for exact combined size if available
                long requestedSum = 0;
                bool requestedExact = true;
                bool requestedComplete = true;
                if (root.TryGetProperty("requested_formats", out var reqProp) && reqProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rf in reqProp.EnumerateArray())
                    {
                        long s = ExtractFormatSize(rf, info.Duration ?? 0);
                        requestedComplete &= s > 0;
                        requestedExact &= rf.TryGetProperty("filesize", out var exactPart) && exactPart.ValueKind == JsonValueKind.Number && exactPart.TryGetInt64(out long partBytes) && partBytes > 0;
                        if (s > 0) requestedSum += s;
                    }
                }

                // Root filesize or filesize_approx
                long rootSize = 0;
                if (root.TryGetProperty("filesize", out var fsProp) && fsProp.ValueKind == JsonValueKind.Number && fsProp.TryGetInt64(out long fs) && fs > 0)
                    rootSize = fs;
                else if (root.TryGetProperty("filesize_approx", out var fsaProp) && fsaProp.ValueKind == JsonValueKind.Number && fsaProp.TryGetInt64(out long fsa) && fsa > 0)
                    rootSize = fsa;

                // Mathematical bitrate estimation: (tbr * 1000 / 8) * duration
                long mathEstimatedSize = 0;
                if (info.Duration.HasValue && info.Duration.Value > 0)
                {
                    double totalBitrate = 0;
                    if (root.TryGetProperty("tbr", out var tbrProp) && tbrProp.ValueKind == JsonValueKind.Number && tbrProp.TryGetDouble(out double tb) && tb > 0)
                        totalBitrate = tb;
                    else if (root.TryGetProperty("vbr", out var vbrProp) && vbrProp.ValueKind == JsonValueKind.Number && vbrProp.TryGetDouble(out double vb) && vb > 0 && bestAudioBitrate > 0)
                        totalBitrate = vb + bestAudioBitrate;

                    mathEstimatedSize = EstimateBytes(totalBitrate, info.Duration.Value);
                }

                // Determine true video size through priority hierarchy
                if (requestedComplete && requestedSum > 0)
                {
                    info.VideoSize = requestedSum;
                    info.VideoSizeIsEstimated = !requestedExact;
                }
                else if (rootSize > 0)
                {
                    info.VideoSize = rootSize;
                    info.VideoSizeIsEstimated = !root.TryGetProperty("filesize", out var exactRoot) || exactRoot.ValueKind != JsonValueKind.Number || !exactRoot.TryGetInt64(out long rootBytes) || rootBytes <= 0;
                }
                else if (mathEstimatedSize > 0)
                    info.VideoSize = mathEstimatedSize;

                if (string.IsNullOrEmpty(info.DirectVideoUrl))
                {
                    info.DirectVideoUrl = bestCombinedMuxedUrl ?? bestVideoUrl;
                }
                if (string.IsNullOrEmpty(info.VideoQuality)) info.VideoQuality = bestQuality;
            }
            else
            {
                // Fallback if formats array was omitted
                if (root.TryGetProperty("filesize", out var fsProp) && fsProp.ValueKind == JsonValueKind.Number && fsProp.TryGetInt64(out long fs) && fs > 0)
                {
                    info.VideoSize = fs;
                    info.VideoSizeIsEstimated = false;
                }
                else if (root.TryGetProperty("filesize_approx", out var fsaProp) && fsaProp.ValueKind == JsonValueKind.Number && fsaProp.TryGetInt64(out long fsa) && fsa > 0)
                    info.VideoSize = fsa;
                else if (info.Duration.HasValue && info.Duration.Value > 0 &&
                         root.TryGetProperty("tbr", out var tbrProp) && tbrProp.ValueKind == JsonValueKind.Number && tbrProp.TryGetDouble(out double tb) && tb > 0)
                {
                    info.VideoSize = EstimateBytes(tb, info.Duration.Value) is > 0 and var bytes ? bytes : null;
                }
            }

            return info;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    public static string ExtractResolution(JsonElement format)
    {
        static int Dimension(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int dimension) && dimension > 0 ? dimension : 0;

        int width = Dimension(format, "width"), height = Dimension(format, "height");
        if (width > 0 && height > 0) return $"{width}x{height}";
        foreach (string key in new[] { "resolution", "format_note" })
        {
            if (!format.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var match = Regex.Match(value.GetString() ?? "", @"(?<![0-9])(?:[1-9][0-9]{1,4}x[1-9][0-9]{1,4}|[1-9][0-9]{1,4}p)(?![0-9])", RegexOptions.IgnoreCase);
            if (match.Success) return match.Value;
        }
        return height > 0 ? $"{height}p" : "";
    }

    private static long EstimateBytes(double kbps, double seconds)
    {
        double bytes = kbps * 125.0 * seconds;
        return bytes > 0 && bytes < long.MaxValue ? (long)bytes : 0;
    }

    public static long ExtractFormatSize(JsonElement fmt, double duration)
    {
        if (fmt.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number && fs.TryGetInt64(out long s) && s > 0)
            return s;

        if (fmt.TryGetProperty("filesize_approx", out var fsa) && fsa.ValueKind == JsonValueKind.Number && fsa.TryGetInt64(out long sa) && sa > 0)
            return sa;

        if (duration > 0)
        {
            double kbps = 0;
            if (fmt.TryGetProperty("tbr", out var tbr) && tbr.ValueKind == JsonValueKind.Number && tbr.TryGetDouble(out double t) && t > 0)
                kbps = t;
            else if (fmt.TryGetProperty("vbr", out var vbr) && vbr.ValueKind == JsonValueKind.Number && vbr.TryGetDouble(out double v) && v > 0)
            {
                kbps = v;
                if (fmt.TryGetProperty("abr", out var abr) && abr.ValueKind == JsonValueKind.Number && abr.TryGetDouble(out double a) && a > 0)
                    kbps += a;
            }
            else if (fmt.TryGetProperty("abr", out var abrOnly) && abrOnly.ValueKind == JsonValueKind.Number && abrOnly.TryGetDouble(out double ao) && ao > 0)
                kbps = ao;

            if (kbps > 0)
            {
                // kbps * 1000 bits/sec / 8 = bytes per second
                return EstimateBytes(kbps, duration);
            }
        }

        return 0;
    }
}

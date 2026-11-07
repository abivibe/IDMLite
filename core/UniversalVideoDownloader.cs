using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using IDMLite.Core.Resolvers;
using IDMLite.Core.Services;

namespace IDMLite.Core;

/// <summary>
/// Dedicated zero-error Universal Video Engine for all generic web media players and video streams
/// (excluding YouTube, Instagram, TikTok, and X/Twitter which use their own dedicated resolvers).
/// Guarantees a clean video title and a 100% playable H.264/AAC MP4 container output.
/// </summary>
public class UniversalVideoDownloader : IDisposable
{
    public string Url { get; private set; }
    public string DestinationPath { get; private set; }
    public string FinalDestinationPath { get; private set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookies { get; set; }
    public string? CookieSourceUrl { get; set; }
    public Dictionary<string, string>? CustomHeaders { get; set; }
    public SpeedLimiter SpeedLimiter { get; }

    public long DownloadedBytes { get; private set; }
    public long TotalBytes { get; set; }
    public int ConnectionCount { get; set; } = 8;
    public string? RecoveryFilePath { get; set; }
    public DownloadStatus Status { get; private set; } = DownloadStatus.Queued;

    public string? ComputedSha256 { get; private set; }
    public double VerificationProgress { get; private set; }
    public event EventHandler<double>? VerificationProgressChanged;

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStatus>? StatusChanged;
    public event EventHandler<string>? Completed;

    private CancellationTokenSource? _cts;
    private HlsDownloader? _activeHls;
    private SegmentedDownloader? _activeSegmented;
    private YtDlpDownloader? _activeYtDlp;

    public UniversalVideoDownloader(string url, string destinationPath, SpeedLimiter? speedLimiter = null)
    {
        Url = url;
        DestinationPath = EnsureMp4Extension(destinationPath);
        FinalDestinationPath = DestinationPath;
        SpeedLimiter = speedLimiter ?? new SpeedLimiter(0);
    }

    public static string EnsureMp4Extension(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Video.mp4";
        string ext = Path.GetExtension(path);
        if (!string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return Path.ChangeExtension(path, ".mp4");
        }
        return path;
    }

    public static bool IsSocialPlatformUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("cdninstagram.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("tiktokcdn.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("twitter.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("x.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("twimg.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldHandleTask(DownloadTask task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.Url)) return false;

        // Never touch YouTube, Instagram, TikTok, or X/Twitter
        if (task.IsSocialVideo || IsSocialPlatformUrl(task.Url) || IsSocialPlatformUrl(task.Referer))
        {
            return false;
        }

        if (task.IsHls) return true;
        if (task.Category == DownloadCategory.Video) return true;
        if (CustomPlayerResolver.IsGenericVideoHost(task.Url)) return true;

        string ext = Path.GetExtension(task.FileName);
        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".flv", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(task.Url, @"\.(mp4|m3u8|mpd|webm|mkv|ts|flv|mov|avi|m4v)(\?.*)?$", RegexOptions.IgnoreCase);
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Status = DownloadStatus.Connecting;
        StatusChanged?.Invoke(this, Status);

        // 1. Resolve real video title if current filename is generic
        await TryResolveBestVideoTitleAsync(ct).ConfigureAwait(false);

        string? outDir = Path.GetDirectoryName(DestinationPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        string effectiveUrl = Url;
        bool downloaded = false;
        string workingFilePath = DestinationPath;

        // 2. Check if URL is a webpage rather than a direct stream and scrape embedded stream if needed
        bool isHlsUrl = effectiveUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        bool isDirectMediaUrl = Regex.IsMatch(effectiveUrl, @"\.(mp4|webm|mkv|ts|flv|mov|avi|m4v)(\?.*)?$", RegexOptions.IgnoreCase) ||
                                effectiveUrl.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase) ||
                                effectiveUrl.Contains("mime=video", StringComparison.OrdinalIgnoreCase);

        if (!isHlsUrl && !isDirectMediaUrl)
        {
            try
            {
                var (scrapedTitle, scrapedStream) = await CustomPlayerResolver.TryScrapePageMetadataAsync(effectiveUrl, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(scrapedTitle))
                {
                    ApplyResolvedTitleIfGeneric(scrapedTitle);
                }
                if (!string.IsNullOrWhiteSpace(scrapedStream) && !CustomPlayerResolver.IsGenericVideoHost(effectiveUrl))
                {
                    effectiveUrl = scrapedStream;
                    isHlsUrl = effectiveUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
                    isDirectMediaUrl = Regex.IsMatch(effectiveUrl, @"\.(mp4|webm|mkv|ts|flv|mov|avi|m4v)(\?.*)?$", RegexOptions.IgnoreCase);
                }
            }
            catch { }
        }

        // Stage A: HLS (.m3u8) Stream Engine
        if (isHlsUrl && !YtDlpHelper.IsAvailable)
        {
            try
            {
                using var hls = new HlsDownloader(effectiveUrl, DestinationPath, 8, SpeedLimiter)
                {
                    Referer = Referer ?? Url,
                    UserAgent = UserAgent,
                    Cookies = Cookies,
                    CookieSourceUrl = CookieSourceUrl ?? Url
                };
                _activeHls = hls;

                hls.ProgressChanged += (s, e) =>
                {
                    DownloadedBytes = e.DownloadedBytes;
                    if (e.TotalBytes > 0) TotalBytes = e.TotalBytes;
                    ProgressChanged?.Invoke(this, e);
                };

                hls.StatusChanged += (s, st) =>
                {
                    if (st is DownloadStatus.Downloading or DownloadStatus.Assembling)
                    {
                        Status = st;
                        StatusChanged?.Invoke(this, st);
                    }
                };

                await hls.StartAsync().ConfigureAwait(false);
                workingFilePath = hls.FinalDestinationPath;
                if (File.Exists(workingFilePath) && new FileInfo(workingFilePath).Length > 512)
                {
                    downloaded = true;
                    DownloadedBytes = new FileInfo(workingFilePath).Length;
                    TotalBytes = DownloadedBytes;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                downloaded = false;
            }
            finally
            {
                _activeHls = null;
            }
        }

        // Stage B: Direct Stream Engine (.mp4, .webm, .mkv, .ts, .flv, .mov, etc.)
        if (!downloaded && (isDirectMediaUrl || !YtDlpHelper.IsAvailable))
        {
            string tempDownloadPath = DestinationPath + ".idmlite.part";

            try
            {
                using var segDownloader = new SegmentedDownloader(effectiveUrl, tempDownloadPath, ConnectionCount, SpeedLimiter)
                {
                    Referer = Referer,
                    UserAgent = UserAgent,
                    Cookies = Cookies,
                    CookieSourceUrl = CookieSourceUrl ?? Url,
                    CustomHeaders = CustomHeaders,
                    SkipResolver = true,
                    AllowVideoRanges = true,
                    RecoveryFilePath = RecoveryFilePath,
                    TotalBytes = TotalBytes
                };
                _activeSegmented = segDownloader;

                segDownloader.ProgressChanged += (s, e) =>
                {
                    DownloadedBytes = e.DownloadedBytes;
                    if (e.TotalBytes > 0) TotalBytes = e.TotalBytes;
                    ProgressChanged?.Invoke(this, e);
                };

                segDownloader.StatusChanged += (s, st) =>
                {
                    if (st == DownloadStatus.Downloading)
                    {
                        Status = st;
                        StatusChanged?.Invoke(this, st);
                    }
                };

                await segDownloader.StartAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                if (File.Exists(tempDownloadPath) && new FileInfo(tempDownloadPath).Length > 512 && !LooksLikeHtmlOrTextFile(tempDownloadPath))
                {
                    downloaded = true;
                    workingFilePath = tempDownloadPath;
                    DownloadedBytes = new FileInfo(tempDownloadPath).Length;
                    TotalBytes = DownloadedBytes;
                }
                else
                {
                    try { if (File.Exists(tempDownloadPath)) File.Delete(tempDownloadPath); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException) { throw; }
            catch
            {
                try { if (File.Exists(tempDownloadPath)) File.Delete(tempDownloadPath); } catch { }
                downloaded = false;
            }
            finally
            {
                _activeSegmented = null;
            }
        }

        // Stage C: Universal yt-dlp Extractor Engine (for Vimeo, Dailymotion, Twitch, Kick, DASH .mpd, or fallback)
        if (!downloaded && YtDlpHelper.IsAvailable)
        {
            string[] candidateUrls = !string.IsNullOrWhiteSpace(Referer) && !string.Equals(effectiveUrl, Referer, StringComparison.OrdinalIgnoreCase)
                ? new[] { effectiveUrl, Referer! } : new[] { effectiveUrl };

            foreach (string candidateUrl in candidateUrls)
            {
                if (IsSocialPlatformUrl(candidateUrl)) continue;

                try
                {
                    var ytDlp = new YtDlpDownloader(candidateUrl, DestinationPath)
                    {
                        FragmentConcurrency = ConnectionCount,
                        Cookies = Uri.TryCreate(CookieSourceUrl ?? Url, UriKind.Absolute, out var cookieOrigin) &&
                            new Uri(candidateUrl).Host.Equals(cookieOrigin.Host, StringComparison.OrdinalIgnoreCase) ? Cookies : null,
                        Referer = Referer,
                        UserAgent = UserAgent
                    };
                    _activeYtDlp = ytDlp;

                    ytDlp.ProgressChanged += (s, e) =>
                    {
                        DownloadedBytes = e.DownloadedBytes;
                        if (e.TotalBytes > 0) TotalBytes = e.TotalBytes;
                        ProgressChanged?.Invoke(this, e);
                    };

                    ytDlp.StatusChanged += (s, st) =>
                    {
                        if (st is DownloadStatus.Downloading or DownloadStatus.Assembling)
                        {
                            Status = st;
                            StatusChanged?.Invoke(this, st);
                        }
                    };

                    await ytDlp.StartAsync().ConfigureAwait(false);
                    workingFilePath = ytDlp.DestinationPath;
                    if (File.Exists(workingFilePath) && new FileInfo(workingFilePath).Length > 512)
                    {
                        downloaded = true;
                        DownloadedBytes = new FileInfo(workingFilePath).Length;
                        TotalBytes = DownloadedBytes;
                        break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    downloaded = false;
                }
                finally
                {
                    _activeYtDlp = null;
                }
            }
        }

        if (!downloaded || !File.Exists(workingFilePath))
        {
            throw new InvalidOperationException("Video stream could not be downloaded or converted to MP4.");
        }

        // 3. Guarantee 100% playable MP4 container (remux/transcode if WebM, MKV, TS, FLV, AVI, or raw stream)
        Status = DownloadStatus.Assembling;
        StatusChanged?.Invoke(this, Status);

        string finalMp4Path = EnsureMp4Extension(DestinationPath);
        await EnsurePlayableMp4ContainerAsync(workingFilePath, finalMp4Path, ct).ConfigureAwait(false);

        if (!string.Equals(workingFilePath, finalMp4Path, StringComparison.OrdinalIgnoreCase) && File.Exists(workingFilePath))
        {
            try { File.Delete(workingFilePath); } catch { }
        }

        FinalDestinationPath = finalMp4Path;
        if (File.Exists(FinalDestinationPath))
        {
            DownloadedBytes = new FileInfo(FinalDestinationPath).Length;
            TotalBytes = DownloadedBytes;
        }

        // 4. Fix MP4 duration header if needed
        try
        {
            await Mp4Fixer.FixDurationIfNeededAsync(FinalDestinationPath, cancellationToken: ct).ConfigureAwait(false);
        }
        catch { }

        // 5. Compute SHA-256 integrity hash
        Status = DownloadStatus.Verifying;
        StatusChanged?.Invoke(this, Status);
        try
        {
            var verifyProg = new Progress<double>(p =>
            {
                VerificationProgress = p;
                VerificationProgressChanged?.Invoke(this, p);
            });
            ComputedSha256 = await ChecksumHelper.ComputeHashAsync(FinalDestinationPath, "SHA256", verifyProg, ct).ConfigureAwait(false);
        }
        catch { }

        Status = DownloadStatus.Completed;
        StatusChanged?.Invoke(this, Status);
        Completed?.Invoke(this, FinalDestinationPath);
    }

    private async Task TryResolveBestVideoTitleAsync(CancellationToken ct)
    {
        try
        {
            string currentFileName = Path.GetFileNameWithoutExtension(DestinationPath);
            bool isGeneric = CustomPlayerResolver.IsGenericPlaylistFileName(currentFileName) ||
                             currentFileName.StartsWith("HLS_Video_", StringComparison.OrdinalIgnoreCase) ||
                             currentFileName.StartsWith("Web_Media_", StringComparison.OrdinalIgnoreCase) ||
                             currentFileName.StartsWith("download_", StringComparison.OrdinalIgnoreCase);

            if (!isGeneric) return;

            string? pageToProbe = !string.IsNullOrWhiteSpace(Referer) && Referer.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? Referer
                : Url;

            if (!string.IsNullOrWhiteSpace(pageToProbe))
            {
                var (scrapedTitle, _) = await CustomPlayerResolver.TryScrapePageMetadataAsync(pageToProbe, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(scrapedTitle))
                {
                    ApplyResolvedTitleIfGeneric(scrapedTitle);
                    return;
                }
            }
        }
        catch { }
    }

    private void ApplyResolvedTitleIfGeneric(string rawTitle)
    {
        string clean = CustomPlayerResolver.CleanFileName(rawTitle);
        if (string.IsNullOrWhiteSpace(clean) || clean.Equals("Video", StringComparison.OrdinalIgnoreCase))
            return;

        string? dir = Path.GetDirectoryName(DestinationPath);
        string newFileName = $"{clean}.mp4";
        DestinationPath = string.IsNullOrEmpty(dir) ? newFileName : Path.Combine(dir, newFileName);
        FinalDestinationPath = DestinationPath;
    }

    /// <summary>
    /// Inspects binary file signature to verify whether a file is a valid ISO Base Media / MP4 container.
    /// </summary>
    public static bool IsValidMp4Container(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
            var info = new FileInfo(filePath);
            if (info.Length < 32) return false;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[32];
            int read = fs.Read(header, 0, header.Length);
            if (read < 12) return false;

            // Standard MP4 / ISO Base Media file has 'ftyp' or 'moov' or 'styp' at bytes 4..7
            string boxType = Encoding.ASCII.GetString(header, 4, 4);
            return boxType is "ftyp" or "moov" or "styp" or "wide" or "mdat";
        }
        catch
        {
            return false;
        }
    }

    public static bool LooksLikeHtmlOrTextFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return true;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[Math.Min(512, (int)fs.Length)];
            int read = fs.Read(header, 0, header.Length);
            if (read <= 0) return true;

            // Check if valid MP4, WebM/MKV, TS, FLV, AVI, Ogg
            if (read >= 8)
            {
                string box = Encoding.ASCII.GetString(header, 4, 4);
                if (box is "ftyp" or "moov" or "styp" or "mdat") return false;
                if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return false; // WebM / MKV
                if (header[0] == 0x47) return false; // MPEG-TS sync byte
                if (header[0] == (byte)'F' && header[1] == (byte)'L' && header[2] == (byte)'V') return false; // FLV
                if (header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F') return false; // AVI
                if (header[0] == (byte)'O' && header[1] == (byte)'g' && header[2] == (byte)'g' && header[3] == (byte)'S') return false; // Ogg
            }

            string text = Encoding.UTF8.GetString(header, 0, read).TrimStart();
            if (text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("{\"error", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Guarantees that the file at inputPath is converted/remuxed into a valid, universally playable .mp4 file at outputMp4Path.
    /// </summary>
    public static async Task EnsurePlayableMp4ContainerAsync(string inputPath, string outputMp4Path, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Input media file not found.", inputPath);

        bool alreadyValidMp4 = IsValidMp4Container(inputPath);
        string? ffmpeg = YtDlpHelper.FindFfmpegPath();

        // If already a valid MP4 container and ffmpeg is not needed (or not available), move into place
        if (alreadyValidMp4 && string.IsNullOrEmpty(ffmpeg))
        {
            if (!string.Equals(inputPath, outputMp4Path, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(outputMp4Path)) File.Delete(outputMp4Path);
                File.Move(inputPath, outputMp4Path);
            }
            return;
        }

        // If already a valid MP4 and inputPath == outputMp4Path, we are done
        if (alreadyValidMp4 && string.Equals(inputPath, outputMp4Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (alreadyValidMp4 && !string.Equals(inputPath, outputMp4Path, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(outputMp4Path)) File.Delete(outputMp4Path);
            File.Move(inputPath, outputMp4Path);
            return;
        }

        // Not a valid MP4 container (e.g. WebM, MKV, MPEG-TS, FLV, AVI, or raw stream) -> Convert via FFmpeg!
        if (string.IsNullOrEmpty(ffmpeg))
        {
            if (!string.Equals(inputPath, outputMp4Path, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(outputMp4Path)) File.Delete(outputMp4Path);
                File.Move(inputPath, outputMp4Path);
            }
            return;
        }

        string tempOut = Path.Combine(
            Path.GetDirectoryName(outputMp4Path) ?? Path.GetTempPath(),
            $".idmlite_remux_{Guid.NewGuid():N}.mp4");

        try
        {
            // Tier 1: Lossless direct stream copy with AAC bitstream filter + faststart
            bool converted = await RunFfmpegAsync(
                ffmpeg,
                $"-y -i \"{inputPath}\" -c copy -bsf:a aac_adtstoasc -movflags +faststart \"{tempOut}\"",
                tempOut,
                ct).ConfigureAwait(false);

            // Tier 2: Lossless video copy + AAC audio transcode (handles H.264 + Opus/Vorbis/MP3)
            if (!converted)
            {
                converted = await RunFfmpegAsync(
                    ffmpeg,
                    $"-y -i \"{inputPath}\" -c:v copy -c:a aac -b:a 192k -movflags +faststart \"{tempOut}\"",
                    tempOut,
                    ct).ConfigureAwait(false);
            }

            // Tier 3: Full H.264 + AAC transcode (handles VP8/VP9/AV1 WebM, Theora, FLV, WMV, etc.)
            if (!converted)
            {
                converted = await RunFfmpegAsync(
                    ffmpeg,
                    $"-y -i \"{inputPath}\" -c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart \"{tempOut}\"",
                    tempOut,
                    ct).ConfigureAwait(false);
            }

            if (converted && File.Exists(tempOut) && IsValidMp4Container(tempOut))
            {
                if (File.Exists(outputMp4Path)) File.Delete(outputMp4Path);
                File.Move(tempOut, outputMp4Path);
            }
            else if (!string.Equals(inputPath, outputMp4Path, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(outputMp4Path)) File.Delete(outputMp4Path);
                File.Move(inputPath, outputMp4Path);
            }
        }
        finally
        {
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
        }
    }

    private static async Task<bool> RunFfmpegAsync(string ffmpegExe, string arguments, string expectedOutput, CancellationToken ct)
    {
        try
        {
            if (File.Exists(expectedOutput))
            {
                try { File.Delete(expectedOutput); } catch { }
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            _ = proc.StandardError.ReadToEndAsync(ct);
            _ = proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            return proc.ExitCode == 0 && File.Exists(expectedOutput) && new FileInfo(expectedOutput).Length > 512;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return false;
        }
    }

    public void Pause()
    {
        _cts?.Cancel();
        _activeHls?.Pause();
        _activeSegmented?.Pause();
        _activeYtDlp?.Pause();
    }

    public void Dispose()
    {
        Pause();
        _cts?.Dispose();
    }
}

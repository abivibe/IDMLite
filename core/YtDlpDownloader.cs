using System.Diagnostics;
using System.Text.RegularExpressions;
using IDMLite.Core.Services;

namespace IDMLite.Core;

public class YtDlpDownloader : IDisposable
{
    private static readonly Regex ProgressRegex = new(
        @"\[download\]\s+(\d+(?:\.\d+)?)%\s+of\s+(?:~\s*)?(\d+(?:\.\d+)?)\s*([A-Za-z]+)(?:\s+at\s+(\d+(?:\.\d+)?)\s*([A-Za-z]+)\/s)?(?:\s+ETA\s+(\d{2}:\d{2}(?::\d{2})?))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CompletedSizeRegex = new(
        @"\[download\]\s+100%\s+of\s+(\d+(?:\.\d+)?)\s*([A-Za-z]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Url { get; }
    public string DestinationPath { get; }
    public string ActiveEngine { get; private set; } = "";
    public int FragmentConcurrency { get; set; } = 8;
    public string? Cookies { get; set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public DownloadStatus Status { get; private set; } = DownloadStatus.Queued;

    public long TotalBytes { get; private set; }
    public long DownloadedBytes { get; private set; }
    public double SpeedBytesPerSec { get; private set; }
    public TimeSpan? TimeRemaining { get; private set; }
    public double ProgressPercentage { get; private set; }

    public string? ComputedSha256 { get; private set; }
    public double VerificationProgress { get; private set; }

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStatus>? StatusChanged;
    public event EventHandler<string>? Completed;
    public event EventHandler<Exception>? Error;
    public event EventHandler<double>? VerificationProgressChanged;

    private Process? _process;
    private CancellationTokenSource? _cts;
    private readonly object _stateLock = new();

    public YtDlpDownloader(string url, string destinationPath)
    {
        Url = url;
        DestinationPath = destinationPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(destinationPath, ".mp4")
            : destinationPath;
    }

    private void SetStatus(DownloadStatus status)
    {
        lock (_stateLock)
        {
            Status = status;
        }
        StatusChanged?.Invoke(this, status);
    }

    public async Task StartAsync()
    {
        if (Status == DownloadStatus.Downloading) return;

        string? ytdlpPath = YtDlpHelper.FindYtDlpPath();
        if (string.IsNullOrEmpty(ytdlpPath))
        {
            throw new FileNotFoundException("yt-dlp.exe bulunamadı. YouTube ve sosyal medya videolarını en yüksek kalitede indirmek için yt-dlp gereklidir.");
        }

        string? ffmpegPath = YtDlpHelper.FindFfmpegPath();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            SetStatus(DownloadStatus.Connecting);

            string? dir = Path.GetDirectoryName(DestinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bool isYouTube = MediaUrl.HasHost(Url, "youtube.com", "youtu.be");

            bool isAudioOnly = DestinationPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                               DestinationPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);

            bool downloaded = false;
            ActiveEngine = "yt-dlp";
            // Arguments for reliable high-speed download with auto video+audio merge and true MP4 container
            var argsList = new List<string>
            {
                "--newline",
                "--no-colors",
                "--no-playlist",
                "--continue",
                "--no-overwrites",
                "--retries", "3",
                "--fragment-retries", "3",
                "--abort-on-unavailable-fragments",
                "--retry-sleep", FormattableString.Invariant($"http:exp={1 + Random.Shared.NextDouble():0.00}:8"),
                "--retry-sleep", "fragment:exp=1:8",
                "--throttled-rate", "100K",
                "--buffer-size", "1024K"
            };
            argsList.Add("--concurrent-fragments");
            argsList.Add(Math.Clamp(FragmentConcurrency, 1, 16).ToString(System.Globalization.CultureInfo.InvariantCulture));



            if (string.IsNullOrEmpty(ffmpegPath))
                throw new InvalidOperationException(YtDlpFailure.Message("ffmpeg_missing"));

            if (isAudioOnly)
            {
                string audioFmt = DestinationPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? "m4a" : "mp3";
                argsList.Add("-f");
                argsList.Add("bestaudio[ext=m4a]/bestaudio/best");
                argsList.Add("-x");
                argsList.Add("--audio-format");
                argsList.Add(audioFmt);
                argsList.Add("--audio-quality");
                argsList.Add("0");
            }
            else
            {
                argsList.Add("-f");
                argsList.Add(YtDlpInvocation.VideoSelector);
                argsList.Add("--merge-output-format");
                argsList.Add("mp4");
                argsList.Add("--remux-video");
                argsList.Add("mp4");
            }

            if (!string.IsNullOrEmpty(ffmpegPath))
            {
                // If ffmpegPath is a file or folder, supply it
                string ffmpegArg = File.Exists(ffmpegPath) ? Path.GetDirectoryName(ffmpegPath)! : ffmpegPath;
                argsList.Add("--ffmpeg-location");
                argsList.Add(ffmpegArg);
            }

            if (!string.IsNullOrWhiteSpace(Referer)) { argsList.Add("--referer"); argsList.Add(Referer); }
            if (!string.IsNullOrWhiteSpace(UserAgent)) { argsList.Add("--user-agent"); argsList.Add(UserAgent); }
            argsList.Add("-o");
            // Escape yt-dlp template markers in user-selected filenames.
            argsList.Add(DestinationPath.Replace("%", "%%"));
            argsList.Add("--");
            argsList.Add(Url);

            var psi = new ProcessStartInfo
            {
                FileName = ytdlpPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using var invocation = new YtDlpInvocation(psi, Url, Cookies);
            foreach (string arg in argsList) psi.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process = process;


            SetStatus(DownloadStatus.Downloading);

            string? lastErrorDetails = null;

            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var idleLimit = TimeSpan.FromMinutes(5);
            idle.CancelAfter(idleLimit);
            _process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    if (e.Data.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                    {
                        lastErrorDetails = e.Data.Trim();
                    }
                    idle.CancelAfter(idleLimit);
                    ParseYtDlpOutput(e.Data);
                }
            };

            _process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    if (e.Data.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                    {
                        lastErrorDetails = e.Data.Trim();
                    }
                    idle.CancelAfter(idleLimit);
                    ParseYtDlpOutput(e.Data);
                }
            };

            ct.ThrowIfCancellationRequested();
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            try { await process.WaitForExitAsync(idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("yt-dlp beş dakika boyunca ilerleme bildirmedi.");
            }
            ct.ThrowIfCancellationRequested();

            if (_process.ExitCode != 0)
            {
                var category = YtDlpFailure.Classify(lastErrorDetails);
                Trace.TraceWarning("YTDLP_PROCESS_FAILED category={0} exitCode={1}", category, process.ExitCode);
                if (isYouTube && (category is "extractor" or "outdated") && DartEngineHelper.IsAvailable)
                {
                    ActiveEngine = "YouTubeExplode";
                    try { downloaded = await DownloadYouTubeWithDartAsync(ffmpegPath, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { Trace.TraceWarning("YOUTUBE_FALLBACK_FAILED"); }
                }
                if (!downloaded)
                {
                    throw new InvalidOperationException($"{YtDlpFailure.Message(category)} (yt-dlp: {process.ExitCode})");
                }
            }

            ct.ThrowIfCancellationRequested();

            // If destination was created as .mp4 or original path
            string finalPath = DestinationPath;
            if (!File.Exists(finalPath))
            {
                // Check if yt-dlp produced an .mp4 file or appended .mp4
                string altPath = Path.ChangeExtension(DestinationPath, ".mp4");
                if (File.Exists(altPath))
                {
                    finalPath = altPath;
                }
                else if (File.Exists(DestinationPath + ".mp4"))
                {
                    finalPath = DestinationPath + ".mp4";
                }
            }

            if (File.Exists(finalPath) && finalPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await Mp4Fixer.FixDurationIfNeededAsync(finalPath, cancellationToken: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            if (File.Exists(finalPath))
            {
                var fi = new FileInfo(finalPath);
                TotalBytes = fi.Length;
                DownloadedBytes = fi.Length;
            }

            if (!File.Exists(finalPath) || new FileInfo(finalPath).Length == 0)
                throw new IOException("İndirme motoru geçerli bir çıktı dosyası üretmedi.");

            // Verification phase
            SetStatus(DownloadStatus.Verifying);
            try
            {
                var verifyProg = new Progress<double>(p =>
                {
                    VerificationProgress = p;
                    VerificationProgressChanged?.Invoke(this, p);
                });
                ComputedSha256 = await ChecksumHelper.ComputeHashAsync(finalPath, "SHA256", verifyProg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }

            ct.ThrowIfCancellationRequested();
            SetStatus(DownloadStatus.Completed);
            Completed?.Invoke(this, finalPath);
        }
        catch (OperationCanceledException)
        {
            try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
            SetStatus(DownloadStatus.Paused);
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(DownloadStatus.Error);
            Error?.Invoke(this, ex);
            throw;
        }
    }

    private Task<bool> DownloadYouTubeWithDartAsync(string? ffmpeg, CancellationToken ct)
    {
        SetStatus(DownloadStatus.Downloading);
        var progress = new Progress<DartProgressDto>(p => {
            DownloadedBytes = p.DownloadedBytes; TotalBytes = p.TotalBytes;
            SpeedBytesPerSec = p.Speed; ProgressPercentage = Math.Clamp(p.Percentage, 0, 100);
            TimeRemaining = p.Speed > 0 && p.TotalBytes > p.DownloadedBytes
                ? TimeSpan.FromSeconds((p.TotalBytes - p.DownloadedBytes) / p.Speed) : null;
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs {
                DownloadedBytes = DownloadedBytes, TotalBytes = TotalBytes,
                SpeedBytesPerSec = SpeedBytesPerSec, ProgressPercentage = ProgressPercentage,
                TimeRemaining = TimeRemaining
            });
        });
        return DartEngineHelper.DownloadYouTubeAsync(Url, DestinationPath, ffmpeg, progress, ct);
    }

    private void ParseYtDlpOutput(string line)
    {
        try
        {
            if (line.Contains("[Merger]") || line.Contains("Merging formats"))
            {
                SetStatus(DownloadStatus.Assembling);
                return;
            }

            var match = ProgressRegex.Match(line);
            if (match.Success)
            {
                if (double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double percent))
                {
                    ProgressPercentage = Math.Clamp(percent, 0.0, 100.0);
                }

                if (double.TryParse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out double totalUnitVal))
                {
                    string unit = match.Groups[3].Value.ToUpperInvariant();
                    TotalBytes = ConvertToBytes(totalUnitVal, unit);
                    DownloadedBytes = (long)(TotalBytes * (ProgressPercentage / 100.0));
                }

                if (match.Groups[4].Success && double.TryParse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture, out double speedVal))
                {
                    string speedUnit = match.Groups[5].Value.ToUpperInvariant();
                    SpeedBytesPerSec = ConvertToBytes(speedVal, speedUnit);
                }

                if (match.Groups[6].Success)
                {
                    string etaStr = match.Groups[6].Value;
                    if (TimeSpan.TryParse("00:" + etaStr, out TimeSpan eta))
                    {
                        TimeRemaining = eta;
                    }
                    else if (TimeSpan.TryParse(etaStr, out TimeSpan etaFull))
                    {
                        TimeRemaining = etaFull;
                    }
                }

                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    DownloadedBytes = DownloadedBytes,
                    TotalBytes = TotalBytes,
                    ProgressPercentage = ProgressPercentage,
                    SpeedBytesPerSec = SpeedBytesPerSec,
                    TimeRemaining = TimeRemaining,
                    Segments = new List<DownloadSegment>()
                });
            }
            else
            {
                var compMatch = CompletedSizeRegex.Match(line);
                if (compMatch.Success && double.TryParse(compMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double compVal))
                {
                    string unit = compMatch.Groups[2].Value.ToUpperInvariant();
                    TotalBytes = ConvertToBytes(compVal, unit);
                    DownloadedBytes = TotalBytes;
                    ProgressPercentage = 100.0;
                }
            }
        }
        catch { }
    }

    private static long ConvertToBytes(double value, string unit)
    {
        return unit switch
        {
            "GIB" or "GB" => (long)(value * 1024 * 1024 * 1024),
            "MIB" or "MB" => (long)(value * 1024 * 1024),
            "KIB" or "KB" => (long)(value * 1024),
            _ => (long)value
        };
    }

    public void Pause()
    {
        try
        {
            _cts?.Cancel();
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            SetStatus(DownloadStatus.Paused);
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            _process?.Dispose();
        }
        catch { }
    }
}

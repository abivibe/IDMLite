using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using IDMLite.Core.Resolvers;
using IDMLite.Core.Services;

namespace IDMLite.Core;

public class HlsDownloader : IDisposable
{
    private SegmentedDownloader? _httpSession;

    public string PlaylistUrl { get; }
    public string DestinationPath { get; }
    public string FinalDestinationPath { get; private set; }
    public int MaxParallelSegments { get; set; } = 8;
    public SpeedLimiter SpeedLimiter { get; }
    public DownloadStatus Status { get; private set; } = DownloadStatus.Queued;

    public long TotalBytesDownloaded { get; private set; }
    public int TotalSegments { get; private set; }
    public int DownloadedSegments { get; private set; }

    public string? ComputedSha256 { get; private set; }
    public double VerificationProgress { get; private set; }
    public event EventHandler<double>? VerificationProgressChanged;

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStatus>? StatusChanged;
    public event EventHandler<string>? Completed;
    public event EventHandler<Exception>? Error;

    private CancellationTokenSource? _cts;

    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookies { get; set; }
    public string? CookieSourceUrl { get; set; }

    public HlsDownloader(string playlistUrl, string destinationPath, int maxParallelSegments = 8, SpeedLimiter? speedLimiter = null)
    {
        PlaylistUrl = playlistUrl;
        DestinationPath = destinationPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(destinationPath, ".mp4")
            : destinationPath;
        FinalDestinationPath = DestinationPath;
        MaxParallelSegments = Math.Clamp(maxParallelSegments, 1, 16);
        SpeedLimiter = speedLimiter ?? new SpeedLimiter(0);
    }

    private string BuildFfmpegHeaderArg()
    {
        string ua = !string.IsNullOrWhiteSpace(UserAgent)
            ? UserAgent
            : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        var sb = new System.Text.StringBuilder();
        sb.Append($"User-Agent: {ua.Replace("\r", "").Replace("\n", "")}\r\n");
        if (!string.IsNullOrWhiteSpace(Referer))
        {
            sb.Append($"Referer: {Referer.Replace("\r", "").Replace("\n", "")}\r\n");
        }
        return sb.ToString();
    }

    private ProcessStartInfo FfmpegStartInfo(string executable, string output, bool transcodeAudio, string? localInput = null)
    {
        var start = new ProcessStartInfo { FileName = executable, CreateNoWindow = true, UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = localInput == null, RedirectStandardOutput = localInput == null };
        start.ArgumentList.Add("-y");
        if (localInput == null)
            foreach (string arg in new[] { "-protocol_whitelist", "http,https,tcp,tls,crypto", "-tls_verify", "1", "-headers", BuildFfmpegHeaderArg() }) start.ArgumentList.Add(arg);
        if (localInput == null && !string.IsNullOrWhiteSpace(Cookies))
        {
            var origin = new Uri(CookieSourceUrl ?? PlaylistUrl);
            var scoped = Cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => part.IndexOf('=') > 0 && !part.Contains('\r') && !part.Contains('\n'))
                .Select(part => $"{part}; path=/; domain={origin.Host};{(origin.Scheme == "https" ? " secure;" : "")}");
            start.ArgumentList.Add("-cookies"); start.ArgumentList.Add(string.Join("\n", scoped));
        }
        foreach (string arg in new[] { "-i", localInput ?? PlaylistUrl }) start.ArgumentList.Add(arg);
        foreach (string arg in transcodeAudio ? new[] { "-c:v", "copy", "-c:a", "aac" } : new[] { "-c", "copy", "-bsf:a", "aac_adtstoasc" }) start.ArgumentList.Add(arg);
        foreach (string arg in new[] { "-movflags", "+faststart", output }) start.ArgumentList.Add(arg);
        return start;
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            Status = DownloadStatus.Connecting;
            StatusChanged?.Invoke(this, Status);

            string? ffmpegExe = YtDlpHelper.FindFfmpegPath();
            if (!string.IsNullOrEmpty(ffmpegExe))
            {
                string targetMp4 = DestinationPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                    ? Path.ChangeExtension(DestinationPath, ".mp4")
                    : DestinationPath;

                string? outDir = Path.GetDirectoryName(targetMp4);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }

                Status = DownloadStatus.Downloading;
                StatusChanged?.Invoke(this, Status);

                long estimatedTotal = (await CobaltResolver.ProbeHlsSizeAsync(PlaylistUrl, ct).ConfigureAwait(false)) ?? 0;
                long streamTicks = Stopwatch.GetTimestamp();
                long streamBytes = 0;
                bool ffmpegSuccess = false;

                try
                {
                    // 1. Direct stream remux with AAC bitstream filter and faststart
                    var psi = FfmpegStartInfo(ffmpegExe, targetMp4, false);

                    using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
                    {
                        proc.ErrorDataReceived += (s, e) =>
                        {
                            if (!string.IsNullOrWhiteSpace(e.Data))
                            {
                                var m = Regex.Match(e.Data, @"size=\s*(\d+)kB");
                                if (m.Success && long.TryParse(m.Groups[1].Value, out long kb))
                                {
                                    long bytes = kb * 1024;
                                    TotalBytesDownloaded = bytes;
                                    long now = Stopwatch.GetTimestamp();
                                    double elapsed = (double)(now - streamTicks) / Stopwatch.Frequency;
                                    double speed = elapsed >= 0.5 ? (bytes - streamBytes) / elapsed : 0;
                                    if (elapsed >= 0.5) { streamBytes = bytes; streamTicks = now; }

                                    long total = estimatedTotal > bytes ? estimatedTotal : bytes;
                                    double percent = total > 0 ? Math.Min(99.0, (double)bytes / total * 100.0) : 0;
                                    TimeSpan? eta = speed > 500 && total > bytes ? TimeSpan.FromSeconds((total - bytes) / speed) : null;

                                    ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                                    {
                                        DownloadedBytes = bytes,
                                        TotalBytes = total,
                                        ProgressPercentage = percent,
                                        SpeedBytesPerSec = speed,
                                        TimeRemaining = eta,
                                        Segments = new List<DownloadSegment>()
                                    });
                                }
                            }
                        };

                        proc.Start();
                        proc.BeginErrorReadLine();
                        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                        if (proc.ExitCode == 0 && File.Exists(targetMp4) && new FileInfo(targetMp4).Length > 1024)
                        {
                            ffmpegSuccess = true;
                        }
                    }

                    // 2. Secondary fallback: direct video copy with AAC audio transcoding
                    if (!ffmpegSuccess)
                    {
                        var psiAac = FfmpegStartInfo(ffmpegExe, targetMp4, true);

                        using var procAac = new Process { StartInfo = psiAac, EnableRaisingEvents = true };
                        procAac.Start();
                        await procAac.WaitForExitAsync(ct).ConfigureAwait(false);

                        if (procAac.ExitCode == 0 && File.Exists(targetMp4) && new FileInfo(targetMp4).Length > 1024)
                        {
                            ffmpegSuccess = true;
                        }
                    }

                    if (ffmpegSuccess)
                    {
                        FinalDestinationPath = targetMp4;
                        TotalBytesDownloaded = new FileInfo(targetMp4).Length;

                        if (!string.Equals(DestinationPath, targetMp4, StringComparison.OrdinalIgnoreCase) && File.Exists(DestinationPath))
                        {
                            try { File.Delete(DestinationPath); } catch { }
                        }

                        // Fix missing/zero duration in MP4 header if needed (e.g. CMAF HLS streams)
                        try
                        {
                            await Mp4Fixer.FixDurationIfNeededAsync(targetMp4, cancellationToken: ct).ConfigureAwait(false);
                        }
                        catch { }

                        // Automatic integrity verification
                        Status = DownloadStatus.Verifying;
                        StatusChanged?.Invoke(this, Status);
                        try
                        {
                            var verifyProg = new Progress<double>(p =>
                            {
                                VerificationProgress = p;
                                VerificationProgressChanged?.Invoke(this, p);
                            });
                            ComputedSha256 = await ChecksumHelper.ComputeHashAsync(targetMp4, "SHA256", verifyProg, ct).ConfigureAwait(false);
                        }
                        catch { }

                        Status = DownloadStatus.Completed;
                        StatusChanged?.Invoke(this, Status);
                        Completed?.Invoke(this, targetMp4);
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            string mediaPlaylistUrl = await ResolveMediaPlaylistUrlAsync(PlaylistUrl, ct).ConfigureAwait(false);
            var segmentUrls = await ParseSegmentUrlsAsync(mediaPlaylistUrl, ct).ConfigureAwait(false);

            if (segmentUrls.Count == 0)
            {
                throw new InvalidOperationException("No media segments found in HLS playlist.");
            }

            TotalSegments = segmentUrls.Count;
            Status = DownloadStatus.Downloading;
            StatusChanged?.Invoke(this, Status);

            string tempDir = Path.Combine(Path.GetTempPath(), "IDMLite_HLS_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            long lastTicks = Stopwatch.GetTimestamp();
            long lastBytes = 0;
            double currentSpeed = 0;

            try
            {
                // Multi-threaded parallel segment downloads (default 8 concurrent workers)
                using var semaphore = new SemaphoreSlim(MaxParallelSegments);
                var downloadTasks = new List<Task>();

                for (int i = 0; i < segmentUrls.Count; i++)
                {
                    int index = i;
                    string segUrl = segmentUrls[i];
                    string segFile = Path.Combine(tempDir, $"seg_{index:D6}.ts");

                    await semaphore.WaitAsync(ct).ConfigureAwait(false);

                    downloadTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadSingleSegmentAsync(segUrl, segFile, ct).ConfigureAwait(false);
                            lock (tempDir)
                            {
                                DownloadedSegments++;
                                if (File.Exists(segFile))
                                {
                                    TotalBytesDownloaded += new FileInfo(segFile).Length;
                                }

                                long now = Stopwatch.GetTimestamp();
                                double elapsed = (double)(now - lastTicks) / Stopwatch.Frequency;
                                if (elapsed >= 0.4 || DownloadedSegments == TotalSegments)
                                {
                                    currentSpeed = elapsed > 0 ? (TotalBytesDownloaded - lastBytes) / elapsed : 0;
                                    lastBytes = TotalBytesDownloaded;
                                    lastTicks = now;
                                }

                                double percent = (double)DownloadedSegments / TotalSegments * 100.0;
                                long estimatedTotalBytes = DownloadedSegments > 0
                                    ? (long)((double)TotalBytesDownloaded / DownloadedSegments * TotalSegments)
                                    : TotalBytesDownloaded;

                                TimeSpan? eta = null;
                                if (currentSpeed > 500 && DownloadedSegments < TotalSegments && estimatedTotalBytes > TotalBytesDownloaded)
                                {
                                    double remainingBytes = estimatedTotalBytes - TotalBytesDownloaded;
                                    eta = TimeSpan.FromSeconds(remainingBytes / currentSpeed);
                                }

                                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                                {
                                    DownloadedBytes = TotalBytesDownloaded,
                                    TotalBytes = estimatedTotalBytes,
                                    ProgressPercentage = percent,
                                    SpeedBytesPerSec = currentSpeed,
                                    TimeRemaining = eta,
                                    Segments = new List<DownloadSegment>()
                                });
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, ct));
                }

                await Task.WhenAll(downloadTasks).ConfigureAwait(false);

                // Assemble segments into raw TS file
                Status = DownloadStatus.Assembling;
                StatusChanged?.Invoke(this, Status);

                string rawTsPath = Path.Combine(tempDir, "temp_combined.ts");
                using (var outputStream = new FileStream(rawTsPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                {
                    for (int i = 0; i < segmentUrls.Count; i++)
                    {
                        string segFile = Path.Combine(tempDir, $"seg_{i:D6}.ts");
                        if (File.Exists(segFile))
                        {
                            using var segStream = File.OpenRead(segFile);
                            await segStream.CopyToAsync(outputStream, ct).ConfigureAwait(false);
                        }
                    }
                }

                // If destination ends in .ts, automatically target clean .mp4
                string targetMp4Path = DestinationPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                    ? Path.ChangeExtension(DestinationPath, ".mp4")
                    : DestinationPath;

                string? outDir = Path.GetDirectoryName(targetMp4Path);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }

                string finalFile = targetMp4Path;
                string? ffmpeg = YtDlpHelper.FindFfmpegPath();
                bool remuxed = false;

                if (!string.IsNullOrEmpty(ffmpeg))
                {
                    try
                    {
                        // 1. Fast direct stream copy remux into MP4 container
                        var psi = FfmpegStartInfo(ffmpeg, targetMp4Path, false, rawTsPath);

                        using (var proc = new Process { StartInfo = psi })
                        {
                            proc.Start();
                            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                            if (proc.ExitCode == 0 && File.Exists(targetMp4Path) && new FileInfo(targetMp4Path).Length > 0)
                            {
                                remuxed = true;
                                finalFile = targetMp4Path;
                            }
                        }

                        // 2. Fallback: If direct stream copy failed (e.g. non-AAC audio), remux with standard AAC audio
                        if (!remuxed)
                        {
                            var psiAac = FfmpegStartInfo(ffmpeg, targetMp4Path, true, rawTsPath);
                            using var procAac = new Process { StartInfo = psiAac };
                            procAac.Start();
                            await procAac.WaitForExitAsync(ct).ConfigureAwait(false);
                            if (procAac.ExitCode == 0 && File.Exists(targetMp4Path) && new FileInfo(targetMp4Path).Length > 0)
                            {
                                remuxed = true;
                                finalFile = targetMp4Path;
                            }
                        }

                        if (remuxed && !string.Equals(DestinationPath, targetMp4Path, StringComparison.OrdinalIgnoreCase) && File.Exists(DestinationPath))
                        {
                            try { File.Delete(DestinationPath); } catch { }
                        }

                        if (remuxed && File.Exists(targetMp4Path))
                        {
                            try { await Mp4Fixer.FixDurationIfNeededAsync(targetMp4Path, cancellationToken: ct).ConfigureAwait(false); } catch { }
                        }
                    }
                    catch { }
                }

                if (!remuxed)
                {
                    if (targetMp4Path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("HLS akışı geçerli bir MP4 konteynerine dönüştürülemedi. Lütfen FFmpeg aracının kurulu olduğunu doğrulayın.");
                    }
                    if (File.Exists(DestinationPath)) File.Delete(DestinationPath);
                    File.Copy(rawTsPath, DestinationPath, true);
                    finalFile = DestinationPath;
                }

                FinalDestinationPath = finalFile;

                // Automatic integrity verification
                Status = DownloadStatus.Verifying;
                StatusChanged?.Invoke(this, Status);
                try
                {
                    var verifyProg = new Progress<double>(p =>
                    {
                        VerificationProgress = p;
                        VerificationProgressChanged?.Invoke(this, p);
                    });
                    ComputedSha256 = await ChecksumHelper.ComputeHashAsync(finalFile, "SHA256", verifyProg, ct).ConfigureAwait(false);
                }
                catch { }

                Status = DownloadStatus.Completed;
                StatusChanged?.Invoke(this, Status);
                Completed?.Invoke(this, finalFile);
            }
            finally
            {
                // Cleanup temp segment directory
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch { }
            }
        }
        catch (OperationCanceledException)
        {
            Status = DownloadStatus.Paused;
            StatusChanged?.Invoke(this, Status);
        }
        catch (Exception ex)
        {
            Status = DownloadStatus.Error;
            StatusChanged?.Invoke(this, Status);
            Error?.Invoke(this, ex);
            throw;
        }
    }

    private Task<HttpResponseMessage> SendHttpAsync(string url, CancellationToken ct)
    {
        // One scoped session for playlists, redirects, keys and segments.
        lock (this)
            _httpSession ??= new SegmentedDownloader(PlaylistUrl, DestinationPath) {
                Cookies = Cookies, CookieSourceUrl = CookieSourceUrl ?? PlaylistUrl, Referer = Referer, UserAgent = UserAgent
            };
        return _httpSession.SendRequestAsync(url, ct);
    }
    private async Task DownloadSingleSegmentAsync(string url, string destPath, CancellationToken ct)
    {
        using var res = await SendHttpAsync(url, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 32 * 1024);

        byte[] buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await SpeedLimiter.ThrottleAsync(read, ct).ConfigureAwait(false);
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private async Task<string> ResolveMediaPlaylistUrlAsync(string url, CancellationToken ct)
    {
        using var res = await SendHttpAsync(url, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        string content = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!content.Contains("#EXT-X-STREAM-INF"))
        {
            return url; // It is already a media playlist
        }

        // Master playlist: find stream with highest bandwidth
        string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        long maxBandwidth = -1;
        string bestUri = string.Empty;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("#EXT-X-STREAM-INF"))
            {
                long bandwidth = ExtractBandwidth(lines[i]);
                if (i + 1 < lines.Length && !lines[i + 1].StartsWith("#"))
                {
                    string candidate = lines[i + 1].Trim();
                    if (bandwidth > maxBandwidth || string.IsNullOrEmpty(bestUri))
                    {
                        maxBandwidth = bandwidth;
                        bestUri = candidate;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(bestUri))
        {
            var baseUri = new Uri(url);
            return new Uri(baseUri, bestUri).ToString();
        }

        return url;
    }

    private async Task<List<string>> ParseSegmentUrlsAsync(string playlistUrl, CancellationToken ct)
    {
        using var res = await SendHttpAsync(playlistUrl, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        string content = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        var baseUri = new Uri(playlistUrl);
        string? initSegmentUrl = null;

        string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(trimmed, @"URI=""([^""]+)""");
                if (match.Success)
                {
                    initSegmentUrl = new Uri(baseUri, match.Groups[1].Value).ToString();
                }
            }
            else if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("#"))
            {
                var fullUri = new Uri(baseUri, trimmed);
                list.Add(fullUri.ToString());
            }
        }

        if (!string.IsNullOrEmpty(initSegmentUrl))
        {
            list.Insert(0, initSegmentUrl);
        }

        return list;
    }

    private long ExtractBandwidth(string line)
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

    public void Pause() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _httpSession?.Dispose();
    }
}

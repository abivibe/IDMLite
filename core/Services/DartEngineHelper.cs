using System.Diagnostics;
using System.IO;
using System.Text.Json;
using IDMLite.Core.Resolvers;

namespace IDMLite.Core.Services;

public class DartMediaStreamDto
{
    public string StreamType { get; set; } = "video";
    public string Title { get; set; } = "";
    public string Format { get; set; } = "mp4";
    public string Quality { get; set; } = "";
    public string Url { get; set; } = "";
    public string SuggestedFileName { get; set; } = "";
    public long? FileSize { get; set; }
    public bool IsAudioOnly { get; set; }
}

public class DartProbeResultDto
{
    public bool Success { get; set; }
    public string OriginalUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string DirectUrl { get; set; } = "";
    public string? DirectAudioUrl { get; set; }
    public string SuggestedFileName { get; set; } = "";
    public long? FileSize { get; set; }
    public string Quality { get; set; } = "";
    public string Format { get; set; } = "mp4";
    public string Provider { get; set; } = "";
    public List<DartMediaStreamDto> Streams { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public class DartProgressDto
{
    public string Type { get; set; } = "";
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double Speed { get; set; }
    public double Percentage { get; set; }
    public string Status { get; set; } = "";
}

public static class DartEngineHelper
{
    public static string? FindEnginePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; directory != null && depth < 7; depth++, directory = directory.Parent)
            foreach (string candidate in new[] { Path.Combine(directory.FullName, "idm_social_engine.exe"), Path.Combine(directory.FullName, "ui", "idm_social_engine.exe") })
                if (File.Exists(candidate)) return candidate;
        return null;
    }
    public static bool IsAvailable => FindEnginePath() != null;
    public static bool CanResolve(string url) => MediaUrl.IsSocialPage(url);
    private static ProcessStartInfo CreateProcess(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo {
            FileName = FindEnginePath() ?? throw new FileNotFoundException("Dart sosyal medya motoru bulunamadı."),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
    public static async Task<ResolvedLinkResult?> ProbeAsync(string url, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = new Process { StartInfo = CreateProcess(new[] { "probe", url }) };
        try
        {
            process.Start();
            using var registration = timeout.Token.Register(() => Kill(process));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<DartProbeResultDto>(await output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (process.ExitCode != 0 || dto?.Success != true) return null;
            var result = new ResolvedLinkResult { OriginalUrl = url, DirectUrl = dto.DirectUrl,
                SuggestedFileName = dto.SuggestedFileName, FileSize = dto.FileSize, IsSuccess = true };
            result.Streams.AddRange(dto.Streams.Select(s => new MediaStreamInfo { StreamType = s.StreamType,
                Title = s.Title, Format = s.Format, Quality = s.Quality, Url = s.Url,
                SuggestedFileName = s.SuggestedFileName, FileSize = s.FileSize, IsAudioOnly = s.IsAudioOnly }));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
        finally { Kill(process); }
    }
    public static Task<bool> DownloadWithTurboRangeAsync(string url, string destinationPath, long? totalBytes,
        IProgress<DartProgressDto>? progressCallback, CancellationToken ct = default)
    {
        var args = new List<string> { "download", url, destinationPath, "--threads", "8" };
        if (totalBytes > 0) { args.Add("--size"); args.Add(totalBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        return DownloadAsync(args, destinationPath, progressCallback, ct);
    }
    public static Task<bool> DownloadYouTubeAsync(string url, string destinationPath, string? ffmpegPath,
        IProgress<DartProgressDto>? progressCallback, CancellationToken ct = default)
    {
        var args = new List<string> { "yt-download", url, destinationPath };
        if (!string.IsNullOrEmpty(ffmpegPath)) { args.Add("--ffmpeg"); args.Add(ffmpegPath); }
        return DownloadAsync(args, destinationPath, progressCallback, ct, reportFailure: true);
    }
    private static async Task<bool> DownloadAsync(IEnumerable<string> args, string destinationPath,
        IProgress<DartProgressDto>? progressCallback, CancellationToken ct, bool reportFailure = false)
    {
        if (!IsAvailable) return false;
        using var process = new Process { StartInfo = CreateProcess(args) };
        try
        {
            process.Start();
            using var registration = ct.Register(() => Kill(process));
            var error = process.StandardError.ReadToEndAsync(ct);
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is string line)
            {
                if (!line.StartsWith('{')) continue;
                try {
                    var progress = JsonSerializer.Deserialize<DartProgressDto>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (progress != null && progress.Type == "progress") progressCallback?.Report(progress);
                } catch (JsonException) { }
            }
            await Task.WhenAll(error, process.WaitForExitAsync(ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            bool success = process.ExitCode == 0 && File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
            if (!success && reportFailure)
            {
                string detail = System.Text.RegularExpressions.Regex.Replace(await error, @"https?://\S+", "[URL]");
                if (detail.Length > 1500) detail = detail[^1500..];
                throw new InvalidOperationException($"YouTube motoru kodu {process.ExitCode}: {detail.Trim()}");
            }
            return success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch when (!reportFailure) { return false; }
        finally { Kill(process); }
    }
}

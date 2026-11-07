using System.Diagnostics;
using System.Text.Json;
using IDMLite.Core;
using IDMLite.Core.Resolvers;
using IDMLite.Core.Services;

string output = Path.GetFullPath("installer/verification");
Directory.CreateDirectory(output);
string runDirectory = Path.Combine(output, "live-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(runDirectory);
bool youtubeRegression = args.Contains("--youtube-regression");
var samples = new Dictionary<string, string> {
    ["youtube"] = "https://www.youtube.com/watch?v=jNQXAC9IVRw",
    ["instagram"] = "https://www.instagram.com/reels/Cop84x6u7CP/",
    ["tiktok"] = "https://www.tiktok.com/@scout2015/video/6718335390845095173",
    ["x"] = "https://twitter.com/BrooklynNets/status/1349794411333394432"
};
if (args.Contains("--public-youtube")) samples = new() { ["youtube"] = samples["youtube"] };
if (youtubeRegression) samples = new() { ["youtube"] = "https://www.youtube.com/watch?v=mJYxSed0D_I" };
var results = await Task.WhenAll(samples.Select(async pair => {
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    string path = Path.Combine(runDirectory, pair.Key + "-integration.mp4");
    try {
        var resolved = await LinkResolverManager.Instance.ResolveMediaPreviewAsync(pair.Value, deadline.Token);
        if (!resolved.IsSuccess) throw new Exception("Metadata resolution failed");
        using var downloader = new YtDlpDownloader(pair.Value, path);
        if (youtubeRegression)
        {
            string queueFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IDMLite", "downloads.json");
            var original = JsonSerializer.Deserialize<List<DownloadTask>>(await File.ReadAllTextAsync(queueFile))?.FirstOrDefault(t => t.Url == pair.Value);
            downloader.Cookies = original?.Cookies; downloader.Referer = original?.Referer; downloader.UserAgent = original?.UserAgent;
        }
        long lastReport = Environment.TickCount64;
        downloader.ProgressChanged += (_, p) => {
            if (Environment.TickCount64 - lastReport > 10000) {
                Console.WriteLine($"PROGRESS: {pair.Key} {p.ProgressPercentage:F1}% ({downloader.ActiveEngine})");
                lastReport = Environment.TickCount64;
            }
        };
        using var registration = deadline.Token.Register(downloader.Pause);
        await downloader.StartAsync();
        if (downloader.Status != DownloadStatus.Completed) throw new Exception("Download status: " + downloader.Status);
        var probe = new ProcessStartInfo {
            FileName = Path.Combine(Path.GetDirectoryName(YtDlpHelper.FindFfmpegPath())!, "ffprobe.exe"),
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name:format=duration,size", "-of", "json", path }) probe.ArgumentList.Add(arg);
        using var process = Process.Start(probe)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(deadline.Token);
        using var document = JsonDocument.Parse(stdout);
        bool video = document.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_type").GetString() == "video");
        if (pair.Key == "youtube" && !document.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_type").GetString() == "audio"))
            throw new Exception("YouTube output has no audio stream");
        if (process.ExitCode != 0 || !video) throw new Exception("Output video validation failed");
        Console.WriteLine("PASS: " + pair.Key + " metadata + download + ffprobe");
        return new { platform = pair.Key, success = true, bytes = new FileInfo(path).Length, engine = downloader.ActiveEngine, error = (string?)null };
    } catch (Exception error) {
        string detail = System.Text.RegularExpressions.Regex.Replace(error.Message, @"https?://\S+", "[URL]");
        Console.WriteLine("FAIL: " + pair.Key + " " + detail);
        return new { platform = pair.Key, success = false, bytes = 0L, engine = "", error = (string?)detail };
    }
}));
await File.WriteAllTextAsync(Path.Combine(output, youtubeRegression ? "youtube-regression-1.1.1.json" : args.Contains("--public-youtube") ? "stabilization-live-youtube-1.0.1.json" : "stabilization-live-platforms-1.0.1.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
if (results.Any(r => !r.success)) Environment.ExitCode = 1;

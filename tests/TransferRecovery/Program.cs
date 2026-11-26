using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using IDMLite.Core;
using IDMLite.Core.Resolvers;
using IDMLite.Core.Services;

if (args.FirstOrDefault() == "worker") {
    var childSettings = new SettingsManager(args[1]);
    using var childQueue = new DownloadQueueManager(childSettings);
    childQueue.Add(new DownloadTask { Url = args[2], SaveDirectory = args[3], FileName = "crash.bin", IsDirect = true, ConnectionCount = 4 }, true);
    await Task.Delay(Timeout.Infinite); return;
}

int passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); passed++; }
async Task Until(Func<bool> check, int milliseconds = 25000) { using var timeout = new CancellationTokenSource(milliseconds); while (!check()) await Task.Delay(30, timeout.Token); }
string root = Path.Combine(Path.GetTempPath(), "IDMLite-transfer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var server = new RangeFixture();
try {
    var archiveSettings = new SettingsManager(Path.Combine(root, "archives-state"));
    string compressed = Path.Combine(root, "Compressed");
    archiveSettings.Current.DefaultDownloadDirectory = root;
    archiveSettings.Current.CategoryDirectories[DownloadCategory.Compressed] = compressed;
    var ipcPort = new TcpListener(IPAddress.Loopback, 0);
    ipcPort.Start();
    archiveSettings.Current.IpcPort = ((IPEndPoint)ipcPort.LocalEndpoint).Port;
    ipcPort.Stop();
    using (var queue = new DownloadQueueManager(archiveSettings))
    using (var ipc = new IpcServer(queue, archiveSettings))
    using (var client = new HttpClient())
    {
        ipc.Start();
        client.DefaultRequestHeaders.Add("X-IDMLite-Token", archiveSettings.Current.IpcToken);
        Directory.CreateDirectory(compressed);
        string existing = Path.Combine(compressed, "server.tar.gz");
        File.WriteAllText(existing, "existing");
        foreach (bool direct in new[] { false, true })
        {
            string requestId = Guid.NewGuid().ToString();
            using var response = await client.PostAsJsonAsync($"http://127.0.0.1:{ipc.Port}/api/download", new DownloadRequest
            {
                RequestId = requestId, Url = server.Url + "archive", FileName = "download.bin", AutoStart = true, Direct = direct
            });
            response.EnsureSuccessStatusCode();
            var archive = queue.GetTasksSnapshot().Single(task => task.OriginRequestId == requestId);
            await Until(() => archive.Status is DownloadStatus.Completed or DownloadStatus.Error);
            Check(archive.Status == DownloadStatus.Completed && archive.Category == DownloadCategory.Compressed &&
                archive.SaveDirectory == compressed && File.ReadAllBytes(archive.FullPath).SequenceEqual(server.Data),
                $"Content-Disposition routes queued download to Compressed when direct={direct}");
        }
        Check(File.ReadAllText(existing) == "existing" && queue.GetTasksSnapshot().Select(task => task.FullPath).Distinct().Count() == 2,
            "Late archive names do not overwrite existing files or other queued downloads");
        foreach (var (path, category, extension) in new[]
        {
            ("document", DownloadCategory.Documents, ".pdf"),
            ("program", DownloadCategory.Programs, ".msix")
        })
        {
            string folder = Path.Combine(root, category.ToString());
            archiveSettings.Current.CategoryDirectories[category] = folder;
            using var reply = await client.PostAsJsonAsync($"http://127.0.0.1:{ipc.Port}/api/download",
                new DownloadRequest { Url = server.Url + path, FileName = "download.bin", AutoStart = true, Direct = true });
            reply.EnsureSuccessStatusCode();
            var task = queue.GetTasksSnapshot().Single(t => t.Url == server.Url + path);
            await Until(() => task.Status is DownloadStatus.Completed or DownloadStatus.Error);
            Check(task.Status == DownloadStatus.Completed && task.SaveDirectory == folder && Path.GetExtension(task.FileName) == extension,
                $"Late {extension} content disposition uses the {category} folder");
        }
    }

    async Task<double> Benchmark(int connections) {
        using var sample = new SegmentedDownloader(server.Url + "benchmark", Path.Combine(root, $"benchmark-{connections}.bin"), connections) { SkipResolver = true };
        var timer = Stopwatch.StartNew(); await sample.StartAsync();
        Check(sample.Status == DownloadStatus.Completed && File.ReadAllBytes(sample.DestinationPath).SequenceEqual(server.BenchmarkData), $"Bandwidth fixture {connections} connection(s) preserves bytes");
        return timer.Elapsed.TotalSeconds;
    }
    double serialTime = await Benchmark(1), parallelTime = await Benchmark(4);
    Check(parallelTime < serialTime * 0.8, "Parallel engine uses more of available bandwidth on a per-connection-limited server");
    Console.WriteLine($"MEASURED FIXTURE: single={4 / serialTime:F2} MiB/s, parallel={4 / parallelTime:F2} MiB/s; speedup={serialTime / parallelTime:F2}x");

    using (var redirected = new SegmentedDownloader(server.Url + "release", Path.Combine(root, "release.bin"), 4) { SkipResolver = true }) {
        await redirected.StartAsync();
        Check(redirected.Url == server.Url + "release", "Release download preserves its source after a HEAD redirect to an expired asset");
        Check(redirected.Segments.Count == 4 && File.ReadAllBytes(redirected.DestinationPath).SequenceEqual(server.Data), "Redirected short ranges finish within the logical part limit");
    }

    var planned = AdaptiveTransferEngine.Plan(1024L * 1024 * 1024, 8);
    Check(planned.Count == 8 && planned.Sum(p => p.TotalBytes) == 1024L * 1024 * 1024, "Plan covers all bytes within the configured part limit");
    Check(AdaptiveTransferEngine.Plan(long.MaxValue, 32).Count == 32, "Very large files stay within the 32-part limit without integer overflow");
    Check(planned.Zip(planned.Skip(1)).All(pair => pair.First.EndByte + 1 == pair.Second.StartByte), "Adaptive parts do not overlap or leave gaps");
    Check(AdaptiveTransferEngine.Plan(1, 32).Single().TotalBytes == 1, "One-byte file is represented correctly");
    string balanced = Path.Combine(root, "balanced.bin");
    using (var download = new SegmentedDownloader(server.Url + "balanced", balanced, 4) { SkipResolver = true, TotalBytes = server.Data.Length }) {
        await download.StartAsync();
        Check(download.Status == DownloadStatus.Completed && File.ReadAllBytes(balanced).SequenceEqual(server.Data), "Balanced multi-connection download has exact file bytes");
        Check(server.Peak > 1 && server.Peak <= 4, "Server observes real bounded parallel connections");
        Check(download.Segments.Count == 4, "Concurrent transfer keeps the configured number of logical parts");
    }
    using (var hiddenRange = new SegmentedDownloader(server.Url + "unadvertised", Path.Combine(root, "unadvertised.bin"), 4) { SkipResolver = true, TotalBytes = server.Data.Length }) {
        await hiddenRange.StartAsync();
        Check(hiddenRange.Status == DownloadStatus.Completed && hiddenRange.Segments.Count > 1 && File.ReadAllBytes(hiddenRange.DestinationPath).SequenceEqual(server.Data), "Range support is discovered even without Accept-Ranges advertising");
    }

    string legacyPath = Path.Combine(root, "legacy.bin");
    int eighth = server.Data.Length / 8;
    var oldParts = Enumerable.Range(0, 8).Select(i => new DownloadSegment(i, i * eighth, (i + 1) * eighth - 1)).ToList();
    using (var legacy = File.Create(legacyPath))
    {
        legacy.SetLength(server.Data.Length);
        foreach (var part in oldParts.Where(p => p.Index % 2 == 0))
        {
            legacy.Position = part.StartByte;
            legacy.Write(server.Data.AsSpan((int)part.StartByte, eighth));
            part.CurrentByte = part.EndByte + 1;
            part.IsCompleted = true;
        }
    }
    File.WriteAllText(legacyPath + ".idmlite.meta", JsonSerializer.Serialize(new DownloadMetadata
    {
        Url = server.Url + "legacy", DestinationPath = legacyPath, TotalBytes = server.Data.Length,
        ETag = "\"v1\"", SupportsRange = true, Segments = oldParts
    }));
    int legacyPhase = ++server.Phase;
    using (var legacy = new SegmentedDownloader(server.Url + "legacy", legacyPath, 4) { SkipResolver = true })
    {
        await legacy.StartAsync();
        Check(legacy.Segments.Count == 4 && File.ReadAllBytes(legacyPath).SequenceEqual(server.Data), "Legacy checkpoint resumes within the new logical part limit");
        var requests = server.Requests.Where(r => r.Phase == legacyPhase).ToArray();
        Check(requests.Length == 4 && requests.Sum(r => r.End - r.Start + 1) == server.Data.Length / 2, "Legacy checkpoint retains the four saved prefixes");
    }
    server.Version = 1;
    using (var changing = new SegmentedDownloader(server.Url + "mutating", Path.Combine(root, "mutating.bin"), 4) { SkipResolver = true }) {
        await changing.StartAsync();
        Check(changing.Status == DownloadStatus.Completed && File.ReadAllBytes(changing.DestinationPath).SequenceEqual(server.CurrentData), "If-Range protects against server changes during parallel transfer");
    }
    server.Version = 1;

    var settings = new SettingsManager(Path.Combine(root, "state"));
    settings.Current.MetadataDirectory = Path.Combine(root, "recovery");
    settings.Current.MaxConnectionsPerDownload = 4;
    settings.Current.MaxConcurrentDownloads = 1;
    settings.Save();
    using (var queue = new DownloadQueueManager(settings)) {
        var item = new DownloadTask { Url = server.Url + "slow", SaveDirectory = root, FileName = "move.bin", IsDirect = true, ConnectionCount = 4, Cookies = "test-secret=do-not-store-as-plain-text" };
        queue.Add(item, true);
        await Until(() => item.RecoveryFilePath != null && RecoveryStore.Load(item.RecoveryFilePath)?.Http?.Segments.Sum(s => s.DownloadedBytes) > 0);
        Check(File.Exists(item.WorkingPath) && item.DisplayName.StartsWith("IDMLDownload-") && !File.Exists(item.FullPath),
            "an unfinished HTTP download uses an isolated numbered file");
        queue.SetTaskSpeedLimit(item, 3 * 1024 * 1024);
        Check(item.TransferLimiter?.MaxBytesPerSecond == 3 * 1024 * 1024, "per-task HTTP cap updates a live limiter");
        queue.SetTaskSpeedLimit(item, 0);
        string oldPath = item.RecoveryFilePath!;
        string movedDirectory = Path.Combine(root, "moved");
        queue.ChangeMetadataDirectory(movedDirectory);
        await Task.Delay(1300);
        Check(!File.Exists(oldPath) && File.Exists(item.RecoveryFilePath), "Changing metadata folder moves active checkpoint without reviving the old file");
        Check(RecoveryStore.Load(item.RecoveryFilePath!)?.Http?.Segments.Sum(s => s.DownloadedBytes) > 0, "Transferred checkpoint retains completed bytes");
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(item.RecoveryFilePath!)).Contains("do-not-store-as-plain-text"), "Recovery data protects session cookies with Windows DPAPI");
        queue.SaveQueue();
        string queueIndex = Path.Combine(settings.StorageDirectory, "downloads.json");
        Check(new[] { queueIndex, queueIndex + ".bak" }.All(file => !File.ReadAllText(file).Contains("do-not-store-as-plain-text")),
            "Plain queue index and its backup do not contain session credentials");
        Check(RecoveryStore.Load(item.RecoveryFilePath!)?.Task.Cookies == item.Cookies,
            "Encrypted recovery retains credentials needed to continue the transfer");
        queue.Pause(item);
        await Until(() => item.Downloader == null);
        Check(RecoveryStore.Load(item.RecoveryFilePath!)?.Task.Status == DownloadStatus.Paused, "Explicit pause is persisted and is not auto-resumed");
        File.WriteAllBytes(item.RecoveryFilePath!, new byte[] { 1, 2, 3 });
        Check(RecoveryStore.Load(item.RecoveryFilePath!)?.Task.Id == item.Id, "Corrupt checkpoint falls back to the last valid atomic backup");
        queue.SaveQueue();
        server.Version = 2;
        queue.Start(item);
        await Until(() => item.Status is DownloadStatus.Completed or DownloadStatus.Error);
        Check(item.Status == DownloadStatus.Completed && File.ReadAllBytes(item.FullPath).SequenceEqual(server.CurrentData), "Changed ETag restarts safely without mixing file versions");
        Check(!File.Exists(item.WorkingPath) && item.DisplayName == "move.bin", "finished file replaces its staging name");
        queue.Remove(item);
        Check(!File.Exists(item.RecoveryFilePath!), "Removing a download removes its recovery record");
        server.Version = 1; server.FailuresRemaining = 4;
        var retrying = new DownloadTask { Url = server.Url + "flaky", SaveDirectory = root, FileName = "retry.bin", IsDirect = true, ConnectionCount = 4 };
        queue.Add(retrying, true);
        await Until(() => retrying.Status == DownloadStatus.Error);
        Check(retrying.ResumeOnRestart && retrying.RetryAfterUtc.HasValue, "Temporary server/network failures schedule a recovery retry");
        await Until(() => retrying.Status == DownloadStatus.Completed);
        Check(File.ReadAllBytes(retrying.FullPath).SequenceEqual(server.Data), "Download recovers automatically when connectivity returns");
    }

    // Abrupt process termination simulates loss of power: no Pause/Dispose runs.
    server.Version = 1;
    var crashSettings = new SettingsManager(Path.Combine(root, "crash-state"));
    crashSettings.Current.MetadataDirectory = Path.Combine(root, "crash-recovery");
    crashSettings.Current.MaxConcurrentDownloads = 1; crashSettings.Save();
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (string arg in new[] { "worker", crashSettings.StorageDirectory, server.Url + "slow", root }) start.ArgumentList.Add(arg);
    using (var child = Process.Start(start)!) {
        string? checkpoint = null;
        try {
            await Until(() => { checkpoint = Directory.Exists(crashSettings.Current.MetadataDirectory) ? Directory.EnumerateFiles(crashSettings.Current.MetadataDirectory, "*.idmlitemeta").FirstOrDefault() : null;
                return checkpoint != null && RecoveryStore.Load(checkpoint)?.Http?.Segments.Sum(s => s.DownloadedBytes) > 1024 * 1024; });
        } finally { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        var saved = RecoveryStore.Load(checkpoint!)!;
        long savedBytes = saved.Http!.Segments.Sum(s => s.DownloadedBytes);
        Check(savedBytes > 0 && savedBytes < server.Data.Length, "Abruptly killed process leaves a readable durable partial checkpoint");
        File.Delete(Path.Combine(crashSettings.StorageDirectory, "downloads.json"));
        File.Delete(Path.Combine(crashSettings.StorageDirectory, "downloads.json.bak"));
        int phase = ++server.Phase;
        using var recoveredQueue = new DownloadQueueManager(new SettingsManager(crashSettings.StorageDirectory));
        var recovered = recoveredQueue.GetTasksSnapshot().Single();
        await Until(() => recovered.Status is DownloadStatus.Completed or DownloadStatus.Error);
        Check(recovered.Status == DownloadStatus.Completed && File.ReadAllBytes(recovered.FullPath).SequenceEqual(server.Data), "After restart, .idmlitemeta alone recovers and completes the exact file");
        var newRequests = server.Requests.Where(r => r.Phase == phase).ToList();
        Check(newRequests.Any(r => saved.Http.Segments.Any(s => s.DownloadedBytes > 0 && !s.IsCompleted && s.CurrentByte == r.Start)), "Restart resumes from saved byte offsets rather than zero");
        Check(newRequests.Sum(r => r.End - r.Start + 1) < server.Data.Length, "Restart does not redownload already checkpointed bytes");
    }

    var cache = new MediaProbeCache { Budget = TimeSpan.FromMilliseconds(180) };
    int probes = 0;
    async Task<ResolvedLinkResult> Probe(CancellationToken ct) { Interlocked.Increment(ref probes); await Task.Delay(50, ct); return new() { OriginalUrl = "https://example.com/video", DirectUrl = "https://example.com/video", SuggestedFileName = "video.mp4" }; }
    await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => cache.GetAsync("https://example.com/video", null, Probe)));
    Check(probes == 1, "Simultaneous metadata requests share one resolver call");
    var watch = Stopwatch.StartNew(); await cache.GetAsync("https://example.com/video", null, Probe);
    Check(probes == 1 && watch.ElapsedMilliseconds < 80, "Warm metadata cache returns without another network probe");
    watch.Restart();
    var timedOut = await cache.GetAsync("https://example.com/slow", null, async ct => { await Task.Delay(5000, ct); return new(); });
    Check(!timedOut.IsSuccess && watch.ElapsedMilliseconds < 1000, "Determining has a strict time budget");
    await cache.GetAsync("https://example.com/video", "different-session", Probe);
    Check(probes == 2, "Metadata cache isolates authentication sessions");
    Console.WriteLine($"RESULT: {passed} passed");
}
finally { Directory.Delete(root, true); }

sealed class RangeFixture : IDisposable
{
    private readonly HttpListener listener = new();
    public byte[] Data { get; } = Enumerable.Range(0, 16 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
    public byte[] BenchmarkData { get; } = Enumerable.Range(0, 4 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
    private byte[]? changed;
    public byte[] CurrentData => Version == 1 ? Data : changed ??= Data.Select(b => (byte)(b ^ 255)).ToArray();
    public int Version = 1, Phase, Peak, FailuresRemaining;
    private int active;
    public record RangeRequest(int Phase, int Start, int End);
    public ConcurrentQueue<RangeRequest> Requests = new();
    public string Url { get; }
    public RangeFixture() { var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop(); Url = $"http://127.0.0.1:{port}/"; listener.Prefixes.Add(Url); listener.Start(); _ = Serve(); }
    private async Task Serve() { while (listener.IsListening) { try { var context = await listener.GetContextAsync(); _ = Reply(context); } catch { break; } } }
    private async Task Reply(HttpListenerContext context)
    {
        bool counted = false;
        try {
            var request = context.Request; var response = context.Response;
            if (request.Url!.AbsolutePath is "/archive" or "/document" or "/program")
            {
                if (request.HttpMethod == "HEAD") { response.StatusCode = 405; response.Close(); return; }
                response.ContentType = "application/gzip";
                string name = request.Url.AbsolutePath switch
                {
                    "/document" => "server.pdf",
                    "/program" => "server.msix",
                    _ => "server.tar.gz"
                };
                response.AddHeader("Content-Disposition", $"attachment; filename*=UTF-8''{name}");
                response.ContentLength64 = Data.Length;
                await response.OutputStream.WriteAsync(Data);
                response.Close(); return;
            }
            if (request.Url!.AbsolutePath == "/release") {
                response.Redirect(request.HttpMethod == "HEAD" ? "/expired-asset" : "/release-asset");
                response.Close(); return;
            }
            if (request.Url.AbsolutePath == "/expired-asset") { response.StatusCode = 403; response.Close(); return; }
            if (request.Url!.AbsolutePath == "/flaky" && Interlocked.Decrement(ref FailuresRemaining) >= 0) { response.StatusCode = 503; response.Close(); return; }
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/mutating") Version = 2;
            var data = request.Url.AbsolutePath == "/benchmark" ? BenchmarkData : CurrentData; string etag = $"\"v{Version}\"";
            response.ContentType = "application/octet-stream";
            if (request.Url.AbsolutePath != "/unadvertised") response.AddHeader("Accept-Ranges", "bytes");
            response.AddHeader("ETag", etag);
            int start = 0, end = data.Length - 1;
            if (request.Headers["Range"] is string range && (request.Headers["If-Range"] == null || request.Headers["If-Range"] == etag)) {
                var parts = range[6..].Split('-'); start = int.Parse(parts[0]); end = int.Parse(parts[1]); response.StatusCode = 206;
                if (request.Url.AbsolutePath == "/release-asset") end = Math.Min(end, start + 1024 * 1024 - 1);
                response.AddHeader("Content-Range", $"bytes {start}-{end}/{data.Length}");
            }
            response.ContentLength64 = end - start + 1;
            if (request.HttpMethod != "HEAD") {
                if (request.Url.AbsolutePath == "/benchmark") await Task.Delay(100);
                Requests.Enqueue(new(Phase, start, end));
                if (request.Url.AbsolutePath == "/balanced")
                {
                    counted = true;
                    int concurrent = Interlocked.Increment(ref active);
                    int observed;
                    do { observed = Peak; if (observed >= concurrent) break; } while (Interlocked.CompareExchange(ref Peak, concurrent, observed) != observed);
                    if (start == 0) await Task.Delay(500);
                }
                for (int pos = start; pos <= end; pos += 8192) {
                    await response.OutputStream.WriteAsync(data.AsMemory(pos, Math.Min(8192, end - pos + 1)));
                    if (request.Url!.AbsolutePath == "/slow") await Task.Delay(12);
                    if (request.Url.AbsolutePath == "/benchmark") await Task.Delay(4);
                }
            }
            response.Close();
        } catch { try { context.Response.Abort(); } catch { } }
        finally { if (counted) Interlocked.Decrement(ref active); }
    }
    public void Dispose() { listener.Stop(); listener.Close(); }
}

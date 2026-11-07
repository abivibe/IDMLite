using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IDMLite.Core;
using IDMLite.Core.Resolvers;
using IDMLite.Core.Services;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(15000); while (!condition()) await Task.Delay(20, timeout.Token); }
string folder = Path.Combine(Path.GetTempPath(), "IDMLite-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
Environment.SetEnvironmentVariable("IDMLITE_DATA_DIR", folder);
using var server = new FixtureServer();
try
{
    Check(MediaUrl.IsSocialPage("https://www.youtube.com/live/abc"), "YouTube live page");
    Check(MediaUrl.IsSocialPage("https://vm.tiktok.com/abc"), "TikTok short page");
    Check(!MediaUrl.IsSocialPage("https://example.com/?u=https://x.com/a/status/1"), "No social match in query");
    Check(!MediaUrl.IsSocialPage("https://youtube.com.evil.test/watch?v=1"), "No spoofed platform host");
    Check(!new MediaFireResolver().CanResolve("https://evil.test/mediafire.com/file/x"), "MediaFire host validation");
    Check(!new MediaFireResolver().CanResolve("https://download123.mediafire.com/a/b.zip"), "Direct MediaFire CDN skips resolver");
    Check(MediaFireResolver.ExtractDownloadUrl("<a href='https://download123.mediafire.com/a.zip?x=1&amp;y=2' class='x' id='downloadButton'>") == "https://download123.mediafire.com/a.zip?x=1&y=2", "MediaFire attribute order, quotes and entities");
    Check(MediaFireResolver.ExtractDownloadUrl("<a id='downloadButton' href='https://evil.test/x'>") == null, "MediaFire rejects foreign download host");
    var psi = new ProcessStartInfo(); string cookieFile;
    using (var invocation = new YtDlpInvocation(psi, "https://www.youtube.com/watch?v=x", "SID=abc; name=value=1"))
    {
        cookieFile = psi.ArgumentList[psi.ArgumentList.IndexOf("--cookies") + 1];
        Check(File.ReadAllText(cookieFile).Contains("www.youtube.com\tFALSE") && !psi.ArgumentList.Contains("--add-header"), "Scoped cookie jar instead of global Cookie header");
    }
    Check(!File.Exists(cookieFile), "Temporary credentials removed");
    Check(psi.ArgumentList.Contains("--force-ipv4"), "YouTube metadata/download invocation selects IPv4 consistently");
    var generic = new ProcessStartInfo();
    using (var invocation = new YtDlpInvocation(generic, "https://www.instagram.com/reel/sample/", null))
        Check(!generic.ArgumentList.Contains("--force-ipv4"), "YouTube route workaround does not change other platforms");

    foreach (string route in new[] { "range", "ignore", "wrong" })
    {
        string path = Path.Combine(folder, route + ".bin");
        using var dl = new SegmentedDownloader(server.Url + route, path, 4) { SkipResolver = true };
        await dl.ProbeAsync(); await dl.StartAsync();
        Check(dl.Status == DownloadStatus.Completed && File.ReadAllBytes(path).SequenceEqual(server.Data), route + " response downloads exact bytes");
    }
    using (var dl = new SegmentedDownloader(server.Url + "truncated", Path.Combine(folder, "truncated.txt"), 1) { SkipResolver = true })
    {
        try { await dl.StartAsync(); } catch (Exception) { }
        Check(dl.Status != DownloadStatus.Completed, "Truncated response cannot complete");
    }
    using (var formats = JsonDocument.Parse("""{"width":720,"height":1280,"filesize_approx":1000,"tbr":500}"""))
    {
        Check(YtDlpHelper.ExtractResolution(formats.RootElement) == "720x1280", "Portrait resolution uses both dimensions");
        Check(YtDlpHelper.ExtractFormatSize(formats.RootElement, 10) == 1000, "Declared estimate takes precedence over bitrate");
    }
    using (var formats = JsonDocument.Parse("""{"resolution":"unknown","tbr":800}"""))
    {
        Check(YtDlpHelper.ExtractResolution(formats.RootElement) == "", "Missing resolution does not become HD");
        Check(YtDlpHelper.ExtractFormatSize(formats.RootElement, 10) == 1000000, "Bitrate and duration convert to bytes");
    }
    Check(YtDlpFailure.Classify("HTTP Error 429") == "rate_limit" && YtDlpFailure.Classify("This video is private. Sign in.") == "private",
        "Temporary and permanent extractor failures are distinct");
    var settings = new SettingsManager(Path.Combine(folder, "state"));
    settings.Current.MetadataDirectory = Path.Combine(folder, "recovery");
    settings.Current.MaxConcurrentDownloads = 1;
    using var queue = new DownloadQueueManager(settings);
    var task = new DownloadTask { Url = server.Url + "slow", SaveDirectory = folder, FileName = "pause.bin", IsDirect = true };
    int completed = 0; queue.TaskCompleted += (_, _) => Interlocked.Increment(ref completed);
    queue.Add(task, true);
    await Until(() => task.Status == DownloadStatus.Downloading);
    queue.Pause(task);
    await Task.Delay(400);
    Check(task.Status == DownloadStatus.Paused && completed == 0, "Paused task stays paused and never reports completion");
    queue.Start(task);
    await Until(() => task.Status == DownloadStatus.Downloading);
    queue.Pause(task); queue.Start(task);
    await Until(() => Volatile.Read(ref completed) > 0 || task.Status == DownloadStatus.Error);
    Check(task.Status == DownloadStatus.Completed && completed == 1 && File.ReadAllBytes(task.FullPath).SequenceEqual(server.Data), "Rapid pause/resume completes once without corruption");
    Check(JsonDocument.Parse(File.ReadAllText(Path.Combine(settings.StorageDirectory, "downloads.json"))).RootElement.GetArrayLength() == 1, "Queue state is valid JSON");
    settings.Current.IpcPort = FixtureServer.FreePort();
    using var ipc = new IpcServer(queue, settings); int dialogs = 0;
    var windowVisible = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    ipc.OpenDownloadWindowAsync = async (_, ct) =>
    {
        Interlocked.Increment(ref dialogs);
        return await windowVisible.Task.WaitAsync(ct);
    };
    ipc.Start();
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("X-IDMLite-Token", settings.Current.IpcToken);
    string request = JsonSerializer.Serialize(new { url = server.Url + "range", requestId = "retry-test" });
    string endpoint = $"http://127.0.0.1:{settings.Current.IpcPort}/api/download";
    var first = http.PostAsync(endpoint, new StringContent(request));
    var retry = http.PostAsync(endpoint, new StringContent(request));
    await Until(() => dialogs > 0);
    Check(!first.IsCompleted && !retry.IsCompleted, "HTTP does not acknowledge a queued UI operation");
    windowVisible.SetResult(true);
    using var firstReply = await first;
    using var retryReply = await retry;
    Check(firstReply.IsSuccessStatusCode && retryReply.IsSuccessStatusCode &&
        await firstReply.Content.ReadAsStringAsync() == await retryReply.Content.ReadAsStringAsync() &&
        dialogs == 1, "Concurrent retries share one actual dialog acknowledgement");
    ipc.OpenDownloadWindowAsync = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return true; };
    using var stalled = await http.PostAsync(endpoint, new StringContent(request.Replace("retry-test", "stalled-window")));
    Check(stalled.StatusCode == HttpStatusCode.ServiceUnavailable, "Stalled UI does not acknowledge handoff");
    using var invalid = await http.PostAsync($"http://127.0.0.1:{settings.Current.IpcPort}/api/download", new StringContent("{\"url\":\"file:///C:/test\"}"));
    Check(invalid.StatusCode == HttpStatusCode.BadRequest, "IPC rejects unsupported URL scheme");
    using var foreignRequest = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{settings.Current.IpcPort}/api/download") { Content = new StringContent(request) };
    foreignRequest.Headers.Add("Origin", "https://example.com");
    using var foreignResponse = await http.SendAsync(foreignRequest);
    Check(foreignResponse.StatusCode == HttpStatusCode.Forbidden, "IPC rejects ordinary website origins");
    using var extensionRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{settings.Current.IpcPort}/api/status");
    extensionRequest.Headers.Add("Origin", "chrome-extension://gdcnkipjepgidojhonbcfpgagchjcdci");
    using var extensionResponse = await http.SendAsync(extensionRequest);
    Check(extensionResponse.IsSuccessStatusCode, "IPC accepts packaged extension origin");
    Console.WriteLine($"RESULT: {passed} passed");
}
finally { Directory.Delete(folder, true); }

sealed class FixtureServer : IDisposable
{
    private readonly HttpListener listener = new();
    public byte[] Data { get; } = Enumerable.Range(0, 262144).Select(i => (byte)(i % 251)).ToArray();
    public string Url { get; }
    public static int FreePort() { var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop(); return port; }
    public FixtureServer() { Url = $"http://127.0.0.1:{FreePort()}/"; listener.Prefixes.Add(Url); listener.Start(); _ = Serve(); }
    private async Task Serve() { while (listener.IsListening) { try { var context = await listener.GetContextAsync(); _ = Reply(context); } catch { break; } } }
    private async Task Reply(HttpListenerContext context)
    {
        try
        {
            var request = context.Request; var response = context.Response;
            string route = request.Url!.AbsolutePath;
            response.ContentType = "application/octet-stream"; response.AddHeader("Accept-Ranges", "bytes");
            int start = 0, end = Data.Length - 1;
            if (request.Headers["Range"] is string range && route != "/ignore")
            {
                string[] parts = range[6..].Split('-'); start = int.Parse(parts[0]); end = string.IsNullOrEmpty(parts[1]) ? end : int.Parse(parts[1]);
                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {(route == "/wrong" ? start + 1 : start)}-{end}/{Data.Length}");
            }
            response.ContentLength64 = end - start + 1;
            if (request.HttpMethod != "HEAD")
            {
                if (route == "/truncated") { await response.OutputStream.WriteAsync(Data.AsMemory(0, 1024)); response.Abort(); return; }
                for (int pos = start; pos <= end; pos += 8192)
                {
                    await response.OutputStream.WriteAsync(Data.AsMemory(pos, Math.Min(8192, end - pos + 1)));
                    if (route == "/slow") await Task.Delay(25);
                }
            }
            response.Close();
        }
        catch { try { context.Response.Abort(); } catch { } }
    }
    public void Dispose() { listener.Stop(); listener.Close(); }
}

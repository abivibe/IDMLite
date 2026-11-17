using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace IDMLite.Core;

public class DownloadRequest
{
    public string? RequestId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ResolvedStreamUrl { get; set; }
    public string? FileName { get; set; }
    public string? Category { get; set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookies { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public bool AutoStart { get; set; } = false;
    public long? FileSize { get; set; }
    public bool FileSizeIsEstimated { get; set; }
    public Resolvers.ResolvedLinkResult? Preview { get; set; }
    public DateTime PreviewCachedAtUtc { get; set; }
    public bool Direct { get; set; } = false;
}

public class IpcServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly DownloadQueueManager _queueManager;
    private readonly SettingsManager _settingsManager;
    private readonly Func<string, string?, CancellationToken, Task<Resolvers.ResolvedLinkResult>> _previewResolver;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private readonly object _handoffLock = new();
    private readonly Dictionary<string, (DateTime Created, Lazy<Task<HandoffReply>> Reply)> _handoffs = new();
    private sealed record HandoffReply(int Code, object Body);

    public Func<DownloadRequest, CancellationToken, Task<bool>>? OpenDownloadWindowAsync { get; set; }

    public int Port => _settingsManager.Current.IpcPort;
    public bool IsRunning => _isRunning;

    public IpcServer(DownloadQueueManager queueManager, SettingsManager settingsManager,
        Func<string, string?, CancellationToken, Task<Resolvers.ResolvedLinkResult>>? previewResolver = null)
    {
        _queueManager = queueManager;
        _settingsManager = settingsManager;
        _previewResolver = previewResolver ?? ((url, cookies, ct) => Resolvers.LinkResolverManager.Instance.ResolveMediaPreviewAsync(url, ct, cookies));
    }

    private Task<HandoffReply> GetHandoffAsync(DownloadRequest request)
    {
        if (string.IsNullOrEmpty(request.RequestId)) return HandleHandoffAsync(request);
        Lazy<Task<HandoffReply>> operation;
        lock (_handoffLock)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _handoffs.Where(p => now - p.Value.Created > TimeSpan.FromMinutes(10)
                         && p.Value.Reply.IsValueCreated && p.Value.Reply.Value.IsCompleted).ToArray())
                _handoffs.Remove(entry.Key);
            if (_handoffs.TryGetValue(request.RequestId, out var existing))
                operation = existing.Reply;
            else
            {
                if (_handoffs.Count >= 4096)
                    return Task.FromResult(new HandoffReply(503, new { success = false, error = "Download handoff is busy" }));
                operation = new(() => HandleHandoffAsync(request));
                _handoffs.Add(request.RequestId, (now, operation));
            }
        }
        return operation.Value;
    }

    private async Task<HandoffReply> HandleHandoffAsync(DownloadRequest downloadReq)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (downloadReq.RequestId is { Length: > 0 } key &&
                _queueManager.GetTasksSnapshot().FirstOrDefault(task => task.OriginRequestId == key) is { } previous)
                return new(200, new { success = true, action = "task_queued", requestId = key, id = previous.Id, fileName = previous.FileName });
            if (!downloadReq.AutoStart && _settingsManager.Current.ShowDownloadDialog)
            {
                var openWindow = OpenDownloadWindowAsync;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (openWindow != null && await openWindow(downloadReq, timeout.Token).ConfigureAwait(false))
                {
                    System.Diagnostics.Trace.TraceInformation("WINDOW_OPENED requestId={0} elapsedMs={1}", LogId(downloadReq), timer.ElapsedMilliseconds);
                    return new(200, new { success = true, action = "dialog_opened", requestId = downloadReq.RequestId });
                }
                return WindowUnavailable(downloadReq);
            }

            string fileName = string.IsNullOrWhiteSpace(downloadReq.FileName)
                ? SegmentedDownloader.ExtractFileNameFromUrl(downloadReq.Url)
                : SegmentedDownloader.CleanFileName(downloadReq.FileName);
            var detected = CategoryDetector.FromExtension(Path.GetExtension(fileName));
            var category = detected != DownloadCategory.Compressed &&
                Enum.TryParse<DownloadCategory>(downloadReq.Category, true, out var cat) && cat is not (DownloadCategory.All or DownloadCategory.Other)
                ? cat
                : detected;

            var task = new DownloadTask
            {
                OriginRequestId = downloadReq.RequestId,
                Url = downloadReq.Url,
                FallbackStreamUrl = downloadReq.ResolvedStreamUrl,
                FileName = fileName,
                SaveDirectory = _settingsManager.GetDirectoryForCategory(category),
                Category = category,
                ResolveArchiveDestination = category == DownloadCategory.Other,
                Referer = downloadReq.Referer,
                UserAgent = downloadReq.UserAgent,
                Cookies = downloadReq.Cookies,
                CustomHeaders = downloadReq.Headers,
                TotalSize = downloadReq.FileSize ?? 0,
                IsDirect = downloadReq.Direct,
                ConnectionCount = _settingsManager.Current.MaxConnectionsPerDownload
            };

            _queueManager.Add(task, autoStart: true);
            System.Diagnostics.Trace.TraceInformation("HANDOFF_ACCEPTED requestId={0} taskId={1} elapsedMs={2}", LogId(downloadReq), task.Id, timer.ElapsedMilliseconds);
            return new(200, new { success = true, action = "task_queued", requestId = downloadReq.RequestId, id = task.Id, fileName = task.FileName });
        }
        catch (OperationCanceledException) { return WindowUnavailable(downloadReq); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("HANDOFF_FAILED requestId={0} category={1}", LogId(downloadReq), error.GetType().Name);
            return new(500, new { success = false, error = "Download handoff failed" });
        }
    }

    private static string LogId(DownloadRequest request) => Guid.TryParse(request.RequestId, out var id) ? id.ToString() : "-";

    private static HandoffReply WindowUnavailable(DownloadRequest request)
    {
        System.Diagnostics.Trace.TraceWarning("WINDOW_UNAVAILABLE requestId={0}", LogId(request));
        return new(503, new { success = false, error = "IDMLite download window could not be opened" });
    }

    public void Start()
    {
        if (_isRunning) return;

        try
        {
            _cts = new CancellationTokenSource();
            _isRunning = true;
            string pipeName = NativePipe.Name(_settingsManager.Current.IpcToken);
            for (int i = 0; i < 8; i++) _ = ListenNativeAsync(pipeName, _cts.Token);
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try
            {
                _listener.Prefixes.Add($"http://localhost:{Port}/");
                _listener.Start();
            }
            catch
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
            }
            _ = ListenLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IPC Server Error] Failed to start on port {Port}: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
    }

    private async Task ListenNativeAsync(string name, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, NativePipe.Options);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                string message = await NativePipe.ReadAsync(pipe, readTimeout.Token).ConfigureAwait(false);
                object response = await HandleNativeAsync(message, ct).ConfigureAwait(false);
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await NativePipe.WriteAsync(pipe, JsonSerializer.Serialize(response), writeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Retry listening after broken connections or transient pipe creation errors.
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    private async Task<object> HandleNativeAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string action = doc.RootElement.TryGetProperty("action", out var value) ? value.GetString() ?? "" : "download";
            return action switch
            {
                "checkStatus" => StatusReply(true),
                "download" => (await DownloadAsync(ParseDownload(json)).ConfigureAwait(false)).Body,
                "preview" => (await PreviewAsync(ParseDownload(json), ct).ConfigureAwait(false)).Body,
                _ => new { success = false, error = "Unsupported native action." }
            };
        }
        catch (Exception)
        {
            return new { success = false, error = "Native request failed." };
        }
    }

    private object StatusReply(bool authorized)
    {
        var snapshot = _queueManager.GetTasksSnapshot();
        return new
        {
            status = "ok", online = true, appName = "IDMLite", version = "1.0.2",
            activeCount = authorized ? snapshot.Count(t => t.Status == DownloadStatus.Downloading) : 0,
            queueCount = authorized ? snapshot.Count : 0
        };
    }

    private static DownloadRequest? ParseDownload(string body) =>
        JsonSerializer.Deserialize<DownloadRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    private async Task<HandoffReply> PreviewAsync(DownloadRequest? request, CancellationToken ct)
    {
        if (request == null || !Services.MediaUrl.IsSocialPage(request.Url))
            return new(400, new { success = false, error = "Unsupported preview page" });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(46));
        var result = await _previewResolver(request.Url, request.Cookies, timeout.Token).ConfigureAwait(false);
        var display = new Resolvers.ResolvedLinkResult
        {
            IsSuccess = result.IsSuccess, OriginalUrl = request.Url, DirectUrl = request.Url,
            SuggestedFileName = result.SuggestedFileName, FileSize = result.FileSize,
            Streams = result.Streams.Select(stream => new Resolvers.MediaStreamInfo
            {
                Url = request.Url, Title = stream.Title, Format = stream.Format, Quality = stream.Quality,
                FileSize = stream.FileSize, FileSizeIsEstimated = stream.FileSizeIsEstimated,
                IsAudioOnly = stream.IsAudioOnly, SuggestedFileName = stream.SuggestedFileName
            }).ToList()
        };
        return new(200, new { success = display.IsSuccess, preview = display });
    }

    private Task<HandoffReply> DownloadAsync(DownloadRequest? request)
    {
        if (request == null || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return Task.FromResult(new HandoffReply(400, new { success = false, error = "Invalid download URL" }));

        request.Url = SegmentedDownloader.NormalizeGitHubRawUrl(request.Url);
        if (!string.IsNullOrWhiteSpace(request.FileName))
            request.FileName = SegmentedDownloader.CleanFileName(request.FileName);
        if (request.RequestId is { Length: > 128 })
            return Task.FromResult(new HandoffReply(400, new { success = false, error = "Invalid request ID" }));

        return GetHandoffAsync(request);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = ProcessRequestAsync(context);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch { }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        string? origin = req.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) &&
            origin != "chrome-extension://gdcnkipjepgidojhonbcfpgagchjcdci" &&
            !(Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && originUri.Scheme == "moz-extension"))
        {
            await SendJsonResponseAsync(res, 403, new { success = false, error = "Untrusted origin" });
            return;
        }

        // Apply CORS & Chromium Private Network Access headers for browser extension
        if (!string.IsNullOrEmpty(origin)) res.AddHeader("Access-Control-Allow-Origin", origin);
        res.AddHeader("Vary", "Origin");
        res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        res.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization, X-IDMLite-Token");
        res.AddHeader("Access-Control-Allow-Private-Network", "true");

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        try
        {
            string path = req.Url?.AbsolutePath.ToLowerInvariant() ?? "";
            string suppliedToken = req.Headers["X-IDMLite-Token"] ?? "";
            byte[] expected = Encoding.UTF8.GetBytes(_settingsManager.Current.IpcToken);
            byte[] supplied = Encoding.UTF8.GetBytes(suppliedToken);
            bool authorized = supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
            if (!authorized && !(req.HttpMethod == "GET" && path == "/api/status"))
            {
                await SendJsonResponseAsync(res, 401, new { success = false, error = "Authentication required" });
                return;
            }

            if (req.HttpMethod == "GET" && path == "/api/status")
            {
                await SendJsonResponseAsync(res, 200, StatusReply(authorized)).ConfigureAwait(false);
            }
            else if (req.HttpMethod == "GET" && path == "/api/downloads")
            {
                var snapshot = _queueManager.GetTasksSnapshot();
                var list = snapshot.Select(t => new
                {
                    id = t.Id,
                    fileName = t.FileName,
                    url = t.Url,
                    status = t.Status.ToString(),
                    progress = t.ProgressPercentage,
                    downloaded = t.DownloadedSize,
                    total = t.TotalSize,
                    speed = t.SpeedBytesPerSec
                }).ToList();
                await SendJsonResponseAsync(res, 200, list).ConfigureAwait(false);
            }
            else if (req.HttpMethod == "POST" && path is "/api/preview" or "/api/download")
            {
                var request = ParseDownload(await ReadBodyAsync(req).ConfigureAwait(false));
                var reply = path == "/api/preview"
                    ? await PreviewAsync(request, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false)
                    : await DownloadAsync(request).ConfigureAwait(false);
                await SendJsonResponseAsync(res, reply.Code, reply.Body).ConfigureAwait(false);
            }
            else if (req.HttpMethod == "POST" && path == "/api/action")
            {
                string body = await ReadBodyAsync(req).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.GetString(), out var taskId))
                {
                    var snapshot = _queueManager.GetTasksSnapshot();
                    var task = snapshot.FirstOrDefault(t => t.Id == taskId);
                    if (task != null && root.TryGetProperty("action", out var actionProp))
                    {
                        string action = actionProp.GetString()?.ToLowerInvariant() ?? "";
                        switch (action)
                        {
                            case "start":
                            case "resume":
                                _queueManager.Start(task);
                                break;
                            case "pause":
                                _queueManager.Pause(task);
                                break;
                            case "delete":
                                _queueManager.Remove(task);
                                break;
                        }
                        await SendJsonResponseAsync(res, 200, new { success = true }).ConfigureAwait(false);
                        return;
                    }
                }
                await SendJsonResponseAsync(res, 400, new { error = "Invalid task ID or action" }).ConfigureAwait(false);
            }
            else
            {
                res.StatusCode = 404;
                res.Close();
            }
        }
        catch (InvalidDataException)
        {
            await SendJsonResponseAsync(res, 413, new { success = false, error = "Request too large" });
        }
        catch (OperationCanceledException)
        {
            await SendJsonResponseAsync(res, 408, new { success = false, error = "Request timed out" });
        }
        catch (JsonException)
        {
            await SendJsonResponseAsync(res, 400, new { success = false, error = "Invalid JSON" });
        }
        catch (Exception)
        {
            await SendJsonResponseAsync(res, 500, new { error = "Request failed" }).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        const int limit = 1024 * 1024;
        if (request.ContentLength64 > limit) throw new InvalidDataException();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var body = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await request.InputStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (body.Length + count > limit) throw new InvalidDataException();
            body.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(body.GetBuffer(), 0, checked((int)body.Length));
    }

    private static async Task SendJsonResponseAsync(HttpListenerResponse res, int statusCode, object obj)
    {
        res.StatusCode = statusCode;
        res.ContentType = "application/json; charset=utf-8";
        string json = JsonSerializer.Serialize(obj);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.Close();
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
        _cts?.Dispose();
    }
}

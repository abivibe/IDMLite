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

            string fileName = downloadReq.FileName ?? SegmentedDownloader.ExtractFileNameFromUrl(downloadReq.Url);
            string ext = Path.GetExtension(fileName);
            var category = !string.IsNullOrEmpty(downloadReq.Category) && Enum.TryParse<DownloadCategory>(downloadReq.Category, true, out var cat)
                ? cat
                : CategoryDetector.FromExtension(ext);

            var task = new DownloadTask
            {
                OriginRequestId = downloadReq.RequestId,
                Url = downloadReq.Url,
                FallbackStreamUrl = downloadReq.ResolvedStreamUrl,
                FileName = fileName,
                SaveDirectory = _settingsManager.GetDirectoryForCategory(category),
                Category = category,
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
            _isRunning = true;

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
                var snapshot = _queueManager.GetTasksSnapshot();
                var statusObj = new
                {
                    status = "ok",
                    online = true,
                    appName = "IDMLite",
                    version = "1.0.1",
                    activeCount = authorized ? snapshot.Count(t => t.Status == DownloadStatus.Downloading) : 0,
                    queueCount = authorized ? snapshot.Count : 0
                };
                await SendJsonResponseAsync(res, 200, statusObj).ConfigureAwait(false);
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
            else if (req.HttpMethod == "POST" && path == "/api/preview")
            {
                var previewRequest = JsonSerializer.Deserialize<DownloadRequest>(await ReadBodyAsync(req), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (previewRequest == null || !Services.MediaUrl.IsSocialPage(previewRequest.Url))
                {
                    await SendJsonResponseAsync(res, 400, new { success = false, error = "Unsupported preview page" });
                    return;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
                timeout.CancelAfter(TimeSpan.FromSeconds(46));
                var preview = await _previewResolver(previewRequest.Url, previewRequest.Cookies, timeout.Token);
                // Browser session cache needs display metadata, never credentials or expiring CDN URLs.
                var display = new Resolvers.ResolvedLinkResult {
                    IsSuccess = preview.IsSuccess, OriginalUrl = previewRequest.Url, DirectUrl = previewRequest.Url,
                    SuggestedFileName = preview.SuggestedFileName, FileSize = preview.FileSize, ErrorMessage = preview.ErrorMessage,
                    Streams = preview.Streams.Select(stream => new Resolvers.MediaStreamInfo {
                        Url = previewRequest.Url, Title = stream.Title, Format = stream.Format, Quality = stream.Quality,
                        FileSize = stream.FileSize, FileSizeIsEstimated = stream.FileSizeIsEstimated,
                        IsAudioOnly = stream.IsAudioOnly, SuggestedFileName = stream.SuggestedFileName
                    }).ToList()
                };
                await SendJsonResponseAsync(res, 200, new { success = display.IsSuccess, preview = display });
            }
            else if (req.HttpMethod == "POST" && path == "/api/download")
            {
                string body = await ReadBodyAsync(req).ConfigureAwait(false);

                var downloadReq = JsonSerializer.Deserialize<DownloadRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (downloadReq == null || !Uri.TryCreate(downloadReq.Url, UriKind.Absolute, out var downloadUri) ||
                    downloadUri.Scheme is not ("http" or "https"))
                {
                    await SendJsonResponseAsync(res, 400, new { error = "Invalid download request. 'url' is required." }).ConfigureAwait(false);
                    return;
                }

                // Normalize GitHub /blob/ and /raw/ links immediately
                downloadReq.Url = SegmentedDownloader.NormalizeGitHubRawUrl(downloadReq.Url);
                if (!string.IsNullOrWhiteSpace(downloadReq.FileName))
                {
                    downloadReq.FileName = Path.GetFileName(downloadReq.FileName);
                    foreach (char invalid in Path.GetInvalidFileNameChars()) downloadReq.FileName = downloadReq.FileName.Replace(invalid, '_');
                }

                if (downloadReq.RequestId is { Length: > 128 })
                {
                    await SendJsonResponseAsync(res, 400, new { success = false, error = "Invalid request ID" });
                    return;
                }
                var reply = await GetHandoffAsync(downloadReq).ConfigureAwait(false);
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

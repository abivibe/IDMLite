using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using IDMLite.Core.Resolvers;
using IDMLite.Core.Services;
using Microsoft.Win32.SafeHandles;

namespace IDMLite.Core;

public class DownloadProgressEventArgs : EventArgs
{
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double ProgressPercentage { get; init; }
    public double SpeedBytesPerSec { get; init; }
    public TimeSpan? TimeRemaining { get; init; }
    public List<DownloadSegment> Segments { get; init; } = new();
}

public class ServerDoesNotSupportRangeException : Exception
{
    public ServerDoesNotSupportRangeException(string message) : base(message) { }
}

public class SegmentedDownloader : IDisposable
{
    private static readonly HttpClient HttpClientInstance;

    static SegmentedDownloader()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(25)
        };
        HttpClientInstance = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromHours(24)
        };
    }

    public string Url { get; private set; }
    public string DestinationPath { get; }
    public int ConnectionCount { get; set; } = 8;
    public SpeedLimiter SpeedLimiter { get; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookies { get; set; }
    public string? CookieSourceUrl { get; set; }
    public Dictionary<string, string>? CustomHeaders { get; set; }

    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; private set; }
    public bool SupportsRange { get; set; }
    public string? ETag { get; private set; }
    public DateTimeOffset? LastModified { get; private set; }
    public string? RecoveryFilePath { get; set; }
    public string? SuggestedFileName { get; set; }
    public DownloadStatus Status { get; private set; } = DownloadStatus.Queued;

    public List<DownloadSegment> Segments { get; } = new();
    public List<MediaStreamInfo> ResolvedStreams { get; } = new();

    public string? ComputedSha256 { get; private set; }
    public double VerificationProgress { get; private set; }
    public event EventHandler<double>? VerificationProgressChanged;

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStatus>? StatusChanged;
    public event EventHandler<string>? Completed;
    public event EventHandler<Exception>? Error;

    private CancellationTokenSource? _cts;
    private readonly object _stateLock = new();
    private string MetadataPath => RecoveryFilePath ?? DestinationPath + ".idmlite.meta";
    private SafeFileHandle? _checkpointHandle;
    private long _lastCheckpointTicks;
    private readonly AdaptiveTransferEngine _transferEngine = new();

    public SegmentedDownloader(
        string url,
        string destinationPath,
        int connectionCount = 8,
        SpeedLimiter? speedLimiter = null)
    {
        Url = NormalizeGitHubRawUrl(url);
        DestinationPath = destinationPath;
        ConnectionCount = Math.Clamp(connectionCount, 1, 32);
        SpeedLimiter = speedLimiter ?? new SpeedLimiter(0);
    }

    public static string NormalizeGitHubRawUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        try
        {
            // 1. https://github.com/{owner}/{repo}/blob/{ref}/{path} -> https://raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}
            var blobMatch = Regex.Match(url, @"^https?:\/\/(?:www\.)?github\.com\/([^\/]+)\/([^\/]+)\/blob\/([^?#]+)(?:\?.*)?$", RegexOptions.IgnoreCase);
            if (blobMatch.Success)
            {
                string owner = blobMatch.Groups[1].Value;
                string repo = blobMatch.Groups[2].Value;
                string refAndPath = blobMatch.Groups[3].Value;
                if (refAndPath.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
                    refAndPath = refAndPath.Substring("refs/heads/".Length);
                else if (refAndPath.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase))
                    refAndPath = refAndPath.Substring("refs/tags/".Length);
                return $"https://raw.githubusercontent.com/{owner}/{repo}/{refAndPath}";
            }

            // 2. https://github.com/{owner}/{repo}/raw/refs/heads/{branch}/{path} or /raw/{ref}/{path}
            var rawMatch = Regex.Match(url, @"^https?:\/\/(?:www\.)?github\.com\/([^\/]+)\/([^\/]+)\/raw\/([^?#]+)(?:\?.*)?$", RegexOptions.IgnoreCase);
            if (rawMatch.Success)
            {
                string owner = rawMatch.Groups[1].Value;
                string repo = rawMatch.Groups[2].Value;
                string refAndPath = rawMatch.Groups[3].Value;
                if (refAndPath.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
                    refAndPath = refAndPath.Substring("refs/heads/".Length);
                else if (refAndPath.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase))
                    refAndPath = refAndPath.Substring("refs/tags/".Length);
                return $"https://raw.githubusercontent.com/{owner}/{repo}/{refAndPath}";
            }
        }
        catch { }
        return url;
    }

    private void SetStatus(DownloadStatus newStatus)
    {
        lock (_stateLock)
        {
            Status = newStatus;
        }
        StatusChanged?.Invoke(this, newStatus);
    }

    /// <summary>True when the URL came straight from a browser download trigger and must not be re-resolved.</summary>
    public bool SkipResolver { get; set; }
    public bool AllowVideoRanges { get; set; }

    /// <summary>
    /// Checks if this download is a video stream or file based on extension, destination path or URL.
    /// Video downloads bypass multi-threaded chunking to avoid container corruption, CDN rate-limiting, and 403 errors.
    /// </summary>
    public bool IsVideoDownload()
    {
        string testPath = !string.IsNullOrEmpty(DestinationPath) ? DestinationPath : (SuggestedFileName ?? Url);
        string ext = Path.GetExtension(testPath).ToLowerInvariant();
        if (ext is ".mp4" or ".webm" or ".mkv" or ".flv" or ".avi" or ".mov" or ".wmv" or ".m4v" or ".ts" or ".3gp")
        {
            return true;
        }

        if (Url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("twimg.com", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("tiktokcdn.com", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("cdninstagram.com", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("video.", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("/video/", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("videoplayback", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the URL is a dynamic Google Takeout archive generation endpoint
    /// where sequential single-stream downloading is preferred to avoid 302 Range rejections.
    /// Standard Google Drive files support full multi-segmented downloading.
    /// </summary>
    public bool IsDynamicTakeoutStream()
    {
        if (string.IsNullOrWhiteSpace(Url)) return false;
        return Url.Contains("takeout-download", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the URL is a Google Drive or Google storage CDN endpoint.
    /// </summary>
    public bool IsGoogleDriveStream()
    {
        if (string.IsNullOrWhiteSpace(Url)) return false;
        return Url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
               Url.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase) ||
               Url.Contains("docs.googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               Url.Contains("takeout-download", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Avoid extra connections for tiny files and single-use streams. Other
    /// files use verified byte ranges; compressed responses fall back safely.
    /// </summary>
    public bool IsSingleStreamPreferred()
    {
        if ((IsVideoDownload() && !AllowVideoRanges) || IsDynamicTakeoutStream()) return true;

        if (TotalBytes > 0 && TotalBytes < 64 * 1024 && ConnectionCount > 1)
        {
            return true;
        }

        return false;
    }

    private async Task ProbeDirectAsync(CancellationToken cancellationToken)
    {
        Url = NormalizeGitHubRawUrl(Url);
        long knownBytes = TotalBytes;
        bool rangeOk = false;
        string? mimeExt = null;

        // If this is a dynamic Takeout stream with knownBytes, preserve single-stream
        if (IsDynamicTakeoutStream() && knownBytes > 0)
        {
            TotalBytes = knownBytes;
            SupportsRange = false;
            if (string.IsNullOrWhiteSpace(SuggestedFileName) || !SuggestedFileName.Contains('.'))
            {
                SuggestedFileName = ResolveProperFileName(Url, null);
            }
            return;
        }

        try
        {
            using var response = await SendWithRedirectsAsync(HttpMethod.Head, Url, null, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                string? mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                bool isHtml = mediaType == "text/html";
                bool isCompressedWire = response.Content.Headers.ContentEncoding.Count > 0 &&
                                        !response.Content.Headers.ContentEncoding.All(e => string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase));
                if (!isHtml)
                {
                    if (!isCompressedWire && response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > 0)
                    {
                        TotalBytes = response.Content.Headers.ContentLength.Value;
                    }
                    if (!string.IsNullOrEmpty(mediaType)) mimeExt = GetExtensionFromMimeType(mediaType);
                    rangeOk = !isCompressedWire && response.Headers.AcceptRanges.Contains("bytes");
                }

                if (response.Headers.ETag != null) ETag = response.Headers.ETag.ToString();
                LastModified = response.Content.Headers.LastModified;

                var cd = response.Content.Headers.ContentDisposition;
                string? name = cd?.FileNameStar ?? cd?.FileName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    string cleaned = CleanFileName(name);
                    if (!string.IsNullOrWhiteSpace(cleaned)) SuggestedFileName = cleaned;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        if (TotalBytes <= 0 && knownBytes > 0) TotalBytes = knownBytes;
        if (!rangeOk && !IsSingleStreamPreferred() && !IsGoogleDriveStream())
        {
            try {
                using var range = await SendWithRedirectsAsync(HttpMethod.Get, Url, new RangeHeaderValue(0, 0), cancellationToken).ConfigureAwait(false);
                if (range.StatusCode == HttpStatusCode.PartialContent && range.Content.Headers.ContentRange is { From: 0, To: 0, Length: > 0 } contentRange) {
                    TotalBytes = contentRange.Length.Value;
                    ETag = range.Headers.ETag?.ToString() ?? ETag;
                    LastModified = range.Content.Headers.LastModified ?? LastModified;
                    rangeOk = true;
                }
            } catch (OperationCanceledException) { throw; } catch { }
        }
        SupportsRange = rangeOk && TotalBytes > 0 && !IsSingleStreamPreferred();

        if (string.IsNullOrWhiteSpace(SuggestedFileName) || !SuggestedFileName.Contains('.'))
        {
            SuggestedFileName = ResolveProperFileName(Url, mimeExt);
        }
    }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        InitializeCookies();
        SetStatus(DownloadStatus.Connecting);
        Url = NormalizeGitHubRawUrl(Url);

        // Direct mode: the browser already produced the final download URL (cookies, size, name),
        // so skip the link resolvers (they would re-request single-use tokens) and just read headers.
        if (SkipResolver)
        {
            await ProbeDirectAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Auto-resolve Google Drive, MediaFire, YouTube or hosting links
        if (LinkResolverManager.Instance.HasResolverFor(Url))
        {
            var resolved = await LinkResolverManager.Instance.ResolveLinkAsync(Url, cancellationToken, Cookies).ConfigureAwait(false);
            if (resolved.IsSuccess && !string.IsNullOrEmpty(resolved.DirectUrl))
            {
                if (Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                    Url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                {
                    // Keep original YouTube watch/shorts URL for high-quality audio+video muxing via YtDlpDownloader
                }
                else
                {
                    Url = resolved.DirectUrl;
                }
                if (!string.IsNullOrEmpty(resolved.SuggestedFileName))
                {
                    SuggestedFileName = resolved.SuggestedFileName;
                }
                if (resolved.FileSize.HasValue && resolved.FileSize.Value > 0)
                {
                    TotalBytes = resolved.FileSize.Value;
                }
                if (!string.IsNullOrEmpty(resolved.Cookies))
                {
                    lock (_cookieLock)
                        foreach (string part in resolved.Cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            int equals = part.IndexOf('=');
                            if (equals <= 0) continue;
                            try { _cookies.Add(new Uri(Url), new Cookie(part[..equals].Trim(), part[(equals + 1)..].Trim(), "/") { Secure = new Uri(Url).Scheme == "https" }); }
                            catch (CookieException) { }
                        }
                }
                if (resolved.Streams != null && resolved.Streams.Count > 0)
                {
                    ResolvedStreams.Clear();
                    ResolvedStreams.AddRange(resolved.Streams);
                }
            }
        }

        // For social platforms, dynamic Takeout direct streams with known metadata, or when streams were already populated by link resolvers,
        // do not send extra HTTP HEAD/GET requests (avoids 403 / HTML resets / consuming single-use tokens).
        if (Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("instagram.com/", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("twitter.com/", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("x.com/", StringComparison.OrdinalIgnoreCase) ||
            Url.Contains("tiktok.com/", StringComparison.OrdinalIgnoreCase) ||
            (IsDynamicTakeoutStream() && (TotalBytes > 0 || !string.IsNullOrEmpty(SuggestedFileName))) ||
            ResolvedStreams.Count > 0)
        {
            if (IsDynamicTakeoutStream())
            {
                SupportsRange = false;
                if (string.IsNullOrWhiteSpace(SuggestedFileName) || !SuggestedFileName.Contains('.'))
                {
                    SuggestedFileName = ResolveProperFileName(Url, null);
                }
            }
            return;
        }

        HttpResponseMessage response;
        try
        {
            if (IsGoogleDriveStream())
            {
                // Google Drive storage endpoints reject HEAD and reject Range on initial token redirects.
                // A plain GET with ResponseHeadersRead reads Content-Length and Content-Disposition safely without buffering the body.
                response = await SendWithRedirectsAsync(HttpMethod.Get, Url, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                response = await SendWithRedirectsAsync(HttpMethod.Head, Url, null, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    response.Dispose();
                    response = await SendWithRedirectsAsync(HttpMethod.Get, Url, new RangeHeaderValue(0, 0), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            response = await SendWithRedirectsAsync(HttpMethod.Get, Url, IsGoogleDriveStream() ? null : new RangeHeaderValue(0, 0), cancellationToken).ConfigureAwait(false);
        }

        long initialTotalBytes = TotalBytes;
        bool isHtmlProbe = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() == "text/html";
        using (response)
        {
            if (!isHtmlProbe)
            {
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > 1)
                {
                    TotalBytes = response.Content.Headers.ContentLength.Value;
                }

                if (response.Content.Headers.ContentRange?.Length.HasValue == true)
                {
                    TotalBytes = response.Content.Headers.ContentRange.Length.Value;
                }
            }

            if (response.Headers.ETag != null)
            {
                ETag = response.Headers.ETag.ToString();
            }
            LastModified = response.Content.Headers.LastModified;

            // Check Accept-Ranges
            SupportsRange = (response.Headers.AcceptRanges.Contains("bytes") ||
                            response.StatusCode == HttpStatusCode.PartialContent) && !IsSingleStreamPreferred();

            // Check if query parameters specify clen (Google Video / CDN streams)
            if (TotalBytes <= 1)
            {
                try
                {
                    var uri = new Uri(Url);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    string? clen = query["clen"] ?? query["content_length"] ?? query["size"];
                    if (!string.IsNullOrEmpty(clen) && long.TryParse(clen, out long clenBytes) && clenBytes > 1)
                    {
                        TotalBytes = clenBytes;
                    }
                }
                catch { }
            }

            if (TotalBytes <= 1 && !IsGoogleDriveStream())
            {
                try
                {
                    using var rangeRes = await SendWithRedirectsAsync(HttpMethod.Get, Url, new RangeHeaderValue(0, 0), cancellationToken).ConfigureAwait(false);
                    if (rangeRes.Content.Headers.ContentRange?.Length.HasValue == true)
                    {
                        TotalBytes = rangeRes.Content.Headers.ContentRange.Length.Value;
                        SupportsRange = !IsSingleStreamPreferred();
                    }
                    else if (rangeRes.Content.Headers.ContentLength.HasValue && rangeRes.Content.Headers.ContentLength.Value > 1)
                    {
                        TotalBytes = rangeRes.Content.Headers.ContentLength.Value;
                    }

                    if (string.IsNullOrEmpty(SuggestedFileName))
                    {
                        var rcd = rangeRes.Content.Headers.ContentDisposition;
                        string? rname = rcd?.FileNameStar ?? rcd?.FileName;
                        if (!string.IsNullOrWhiteSpace(rname))
                        {
                            string cleaned = CleanFileName(rname);
                            if (!string.IsNullOrWhiteSpace(cleaned)) SuggestedFileName = cleaned;
                        }
                    }
                }
                catch { }
            }

            if (TotalBytes <= 1 && initialTotalBytes > 1)
            {
                TotalBytes = initialTotalBytes;
            }

            // Extract file name from disposition if present
            var disp = response.Content.Headers.ContentDisposition;
            string? dispName = disp?.FileNameStar ?? disp?.FileName;
            if (!string.IsNullOrWhiteSpace(dispName))
            {
                string cleaned = CleanFileName(dispName);
                if (!string.IsNullOrWhiteSpace(cleaned)) SuggestedFileName = cleaned;
            }

            // Inspect ContentType for media/archive extension detection
            string? mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            string? mimeExt = !string.IsNullOrEmpty(mediaType) ? GetExtensionFromMimeType(mediaType) : null;

            if (string.IsNullOrWhiteSpace(SuggestedFileName) || 
                SuggestedFileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                SuggestedFileName.Equals("view", StringComparison.OrdinalIgnoreCase) ||
                SuggestedFileName.Equals("watch", StringComparison.OrdinalIgnoreCase) ||
                SuggestedFileName.Equals("videoplayback", StringComparison.OrdinalIgnoreCase) ||
                !SuggestedFileName.Contains('.'))
            {
                SuggestedFileName = ResolveProperFileName(response.RequestMessage?.RequestUri?.ToString() ?? Url, mimeExt);
            }
        }
    }

    /// <summary>
    /// For multi-stream Google Drive downloads, follows the initial 302 redirect to resolve the final
    /// Google storage CDN URL before parallel workers connect with concurrent Range headers.
    /// </summary>
    private async Task EnsureDirectStreamUrlResolvedAsync(CancellationToken ct)
    {
        if (IsGoogleDriveStream() && !IsDynamicTakeoutStream())
        {
            if (Url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
                Url.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var res = await SendWithRedirectsAsync(HttpMethod.Get, Url, null, ct).ConfigureAwait(false);
                    if (res.RequestMessage?.RequestUri != null)
                    {
                        string finalUrl = res.RequestMessage.RequestUri.ToString();
                        if (!string.IsNullOrWhiteSpace(finalUrl) && !finalUrl.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
                        {
                            Url = finalUrl;
                        }
                    }
                }
                catch { }
            }
        }
    }

    public async Task StartAsync()
    {
        if (Status == DownloadStatus.Downloading) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            // A known browser size is not proof of range support or file identity.
            await ProbeAsync(ct).ConfigureAwait(false);

            if (Url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
                Url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("YouTube doğrudan video akışı çözümlenemedi. YouTube video sayfaları doğrudan indirilemez; lütfen videoyu oynatırken tarayıcı eklentisindeki 'Bu Videoyu İndir' butonunu kullanarak doğrudan video akışını indirin.");
            }

            SetStatus(DownloadStatus.Downloading);

            string? dir = Path.GetDirectoryName(DestinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Load existing metadata if available
            bool canResume = LoadMetadata();

            if (!canResume)
            {
                InitializeSegments();
                SaveMetadata();
            }

            // Start speed tracking loop
            using var progressCts = new CancellationTokenSource();
            Task progressReporter = RunProgressReporterAsync(progressCts.Token);

            try
            {
            bool isSingleStream = IsSingleStreamPreferred();
            bool fallbackToSingleStream = false;

            if (isSingleStream)
            {
                // Video, text (.txt, etc.) and small files (<64 KB): Use standard sequential single-stream
                // to prevent CDN rate-limiting, compressed Content-Length range mismatches, and MP4 container corruption.
                Segments.Clear();
                Segments.Add(new DownloadSegment(0, 0, TotalBytes > 0 ? TotalBytes - 1 : 0));
                using (var fileStream = new FileStream(
                    DestinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous))
                {
                    await DownloadSingleStreamAsync(fileStream, ct).ConfigureAwait(false);
                    await fileStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            else if (SupportsRange && TotalBytes > 0 && Segments.Count > 0)
            {
                try
                {
                    await EnsureDirectStreamUrlResolvedAsync(ct).ConfigureAwait(false);

                    // Ensure destination file exists and pre-allocate size for deterministic segmented writing
                    using (var fileStream = new FileStream(
                        DestinationPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite,
                        64 * 1024,
                        FileOptions.Asynchronous))
                    {
                        fileStream.SetLength(TotalBytes);
                        SafeFileHandle safeHandle = fileStream.SafeFileHandle;

                        lock (_stateLock) _checkpointHandle = safeHandle;
                        try
                        {
                            await _transferEngine.TransferAsync(Segments, ConnectionCount,
                                (segment, workerToken) => DownloadSegmentWorkerAsync(segment, safeHandle, workerToken), ct).ConfigureAwait(false);
                            await fileStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                            fileStream.SetLength(TotalBytes);
                        }
                        finally
                        {
                            SaveMetadata();
                            lock (_stateLock) _checkpointHandle = null;
                        }
                    }

                    // Verify all segments finished completely
                    foreach (var seg in Segments)
                    {
                        if (!seg.IsCompleted || (seg.EndByte >= seg.StartByte && seg.CurrentByte <= seg.EndByte))
                        {
                            throw new IOException($"Segment #{seg.Index} tamamlanamadı ({seg.DownloadedBytes}/{seg.TotalBytes} bayt).");
                        }
                    }
                }
                catch (Exception ex) when (ex is ServerDoesNotSupportRangeException or IOException && !ct.IsCancellationRequested)
                {
                    fallbackToSingleStream = true;
                    SupportsRange = false;
                    lock (_stateLock) Segments.Clear();
                    if (RecoveryFilePath == null) { try { File.Delete(MetadataPath); } catch { } }
                }
            }

            if (!isSingleStream && (!SupportsRange || fallbackToSingleStream || TotalBytes <= 0 || Segments.Count == 0))
            {
                // Fallback single stream download (seek 0, write sequentially)
                using (var fileStream = new FileStream(
                    DestinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous))
                {
                    await DownloadSingleStreamAsync(fileStream, ct).ConfigureAwait(false);
                    await fileStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            }
            finally
            {
                progressCts.Cancel();
                try { await progressReporter.ConfigureAwait(false); } catch { }
            }

            // Final file verification and size synchronization
            var fi = new FileInfo(DestinationPath);
            fi.Refresh();

            TotalBytes = fi.Length;
            DownloadedBytes = fi.Length;

            // MP4 duration optimization
            if (DestinationPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                try { await Mp4Fixer.FixDurationIfNeededAsync(DestinationPath, cancellationToken: ct).ConfigureAwait(false); } catch { }
            }

            // Extra automatic integrity verification
            SetStatus(DownloadStatus.Verifying);
            try
            {
                var verifyProg = new Progress<double>(p =>
                {
                    VerificationProgress = p;
                    VerificationProgressChanged?.Invoke(this, p);
                });
                ComputedSha256 = await ChecksumHelper.ComputeHashAsync(DestinationPath, "SHA256", verifyProg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception verifyEx)
            {
                System.Diagnostics.Debug.WriteLine($"Checksum calculation note: {verifyEx.Message}");
            }

            ct.ThrowIfCancellationRequested();
            SetStatus(DownloadStatus.Completed);

            // Clean up metadata
            if (RecoveryFilePath == null && File.Exists(MetadataPath))
            {
                try { File.Delete(MetadataPath); } catch { }
            }

            Completed?.Invoke(this, DestinationPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(DownloadStatus.Paused);
            SaveMetadata();
        }
        catch (Exception ex)
        {
            SetStatus(DownloadStatus.Error);
            SaveMetadata();
            Error?.Invoke(this, ex);
            throw;
        }
    }

    public void Pause()
    {
        _cts?.Cancel();
    }

    private void InitializeSegments()
    {
        Segments.Clear();
        if (!SupportsRange || TotalBytes <= 0 || ConnectionCount <= 1 || IsSingleStreamPreferred())
        {
            Segments.Add(new DownloadSegment(0, 0, Math.Max(0, TotalBytes - 1)));
            return;
        }

        Segments.AddRange(AdaptiveTransferEngine.Plan(TotalBytes, ConnectionCount));
    }

    private async Task DownloadSegmentWorkerAsync(DownloadSegment segment, SafeFileHandle handle, CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        int retryCount = 0;
        const int maxRetries = 10;

        while (!segment.IsCompleted && segment.CurrentByte <= segment.EndByte)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var res = await SendWithRedirectsAsync(
                    HttpMethod.Get,
                    Url,
                    new RangeHeaderValue(segment.CurrentByte, segment.EndByte),
                    ct).ConfigureAwait(false);

                res.EnsureSuccessStatusCode();

                if (res.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new ServerDoesNotSupportRangeException("Sıkıştırılmış yanıt tek bağlantıya alınıyor.");

                if (res.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = res.Content.Headers.ContentRange;
                    if (range?.From != segment.CurrentByte || range.To != segment.EndByte ||
                        (range.Length.HasValue && range.Length != TotalBytes))
                        throw new ServerDoesNotSupportRangeException("Sunucu tutarsız Content-Range döndürdü; tek bağlantı ile yeniden deneniyor.");
                }

                if (res.StatusCode == HttpStatusCode.OK)
                {
                    if (segment.Index > 0 || segment.CurrentByte > 0 || (segment.EndByte < TotalBytes - 1 && TotalBytes > 0))
                    {
                        throw new ServerDoesNotSupportRangeException("Sunucu parçalı indirmeyi veya duraklat/devam et özelliğini desteklemiyor.");
                    }
                }

                string? mediaType = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (mediaType == "text/html" &&
                    !DestinationPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                    !DestinationPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                {
                    string errHtml = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    string errorMsg = ExtractServerHtmlErrorMessage(errHtml, Url);
                    throw new InvalidOperationException(errorMsg);
                }

                using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                long lastReportTicks = Stopwatch.GetTimestamp();
                long bytesSinceReport = 0;

                while (segment.CurrentByte <= segment.EndByte)
                {
                    long remaining = segment.EndByte - segment.CurrentByte + 1;
                    if (remaining <= 0) break;

                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(35));
                    int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), readCts.Token).ConfigureAwait(false);
                    if (bytesRead <= 0)
                    {
                        // Server closed connection before segment finished; loop will reconnect with updated CurrentByte
                        break;
                    }

                    if (segment.Index == 0 && segment.CurrentByte == segment.StartByte && IsVideoDownload() && bytesRead >= 4)
                    {
                        bool isJpeg = buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF;
                        bool isPng = buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47;
                        if (isJpeg || isPng)
                        {
                            throw new InvalidOperationException("İndirilen içerik bir video değil, resim/kapak görselidir (sunucu JPEG/PNG döndürdü).");
                        }
                    }

                    await SpeedLimiter.ThrottleAsync(bytesRead, ct).ConfigureAwait(false);

                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, bytesRead), segment.CurrentByte, ct).ConfigureAwait(false);

                    segment.CurrentByte += bytesRead;
                    bytesSinceReport += bytesRead;

                    long now = Stopwatch.GetTimestamp();
                    double elapsedSec = (double)(now - lastReportTicks) / Stopwatch.Frequency;
                    if (elapsedSec >= 0.25)
                    {
                        segment.CurrentSpeedBytesPerSec = bytesSinceReport / elapsedSec;
                        bytesSinceReport = 0;
                        lastReportTicks = now;
                    }
                }

                if (segment.CurrentByte > segment.EndByte)
                {
                    segment.IsCompleted = true;
                    segment.CurrentSpeedBytesPerSec = 0;
                    return;
                }

                // If not finished and stream closed, increment retry
                retryCount++;
                if (retryCount > maxRetries)
                {
                    throw new IOException($"Segment #{segment.Index} {maxRetries} denemeden sonra tamamlanamadı.");
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ServerDoesNotSupportRangeException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // If segment 0 or any other segment gets a 302, 403, 416 or redirect error at byte 0, immediately trigger fallback without 10 retries
                if (segment.DownloadedBytes == 0 && (ex is HttpRequestException || ex.Message.Contains("302") || ex.Message.Contains("403") || ex.Message.Contains("416")))
                {
                    throw new ServerDoesNotSupportRangeException($"Sunucu parçalı isteğe izin vermedi: {ex.Message}");
                }

                retryCount++;
                if (retryCount > maxRetries) throw;
                await Task.Delay(500 * retryCount, ct).ConfigureAwait(false);
            }
        }

        segment.IsCompleted = true;
        segment.CurrentSpeedBytesPerSec = 0;
    }

    private async Task DownloadSingleStreamAsync(FileStream fileStream, CancellationToken ct)
    {
        Url = NormalizeGitHubRawUrl(Url);
        var res = await SendWithRedirectsAsync(HttpMethod.Get, Url, null, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        string? mediaType = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (mediaType == "text/html" &&
            !DestinationPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
            !DestinationPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            res.Dispose();

            // Try to auto-confirm Google Drive in-flight
            if (TryExtractGoogleDriveConfirmation(html, Url, out string? confirmedUrl))
            {
                Url = confirmedUrl!;
                res = await SendWithRedirectsAsync(HttpMethod.Get, Url, null, ct).ConfigureAwait(false);
                res.EnsureSuccessStatusCode();
                mediaType = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            }

            if (mediaType == "text/html" &&
                !DestinationPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                !DestinationPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                string errorMsg = ExtractServerHtmlErrorMessage(html, Url);
                res.Dispose();
                throw new InvalidOperationException(errorMsg);
            }
        }

        using (res)
        {
            bool isCompressedEncoding = res.Content.Headers.ContentEncoding.Count > 0 &&
                                        !res.Content.Headers.ContentEncoding.All(e => string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase));

            if (!isCompressedEncoding && res.Content.Headers.ContentLength.HasValue && res.Content.Headers.ContentLength.Value > 0)
            {
                TotalBytes = res.Content.Headers.ContentLength.Value;
                if (Segments.Count > 0)
                {
                    Segments[0].EndByte = TotalBytes - 1;
                }
            }

            using var rawNetStream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            Stream netStream = rawNetStream;
            if (res.Content.Headers.ContentEncoding.Any(e => string.Equals(e, "gzip", StringComparison.OrdinalIgnoreCase)))
            {
                netStream = new System.IO.Compression.GZipStream(rawNetStream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            }
            else if (res.Content.Headers.ContentEncoding.Any(e => string.Equals(e, "deflate", StringComparison.OrdinalIgnoreCase)))
            {
                netStream = new System.IO.Compression.DeflateStream(rawNetStream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            }
            else if (res.Content.Headers.ContentEncoding.Any(e => string.Equals(e, "br", StringComparison.OrdinalIgnoreCase)))
            {
                netStream = new System.IO.Compression.BrotliStream(rawNetStream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            }

            using (netStream)
            {
                byte[] buffer = new byte[64 * 1024];
                int bytesRead;

                fileStream.Seek(0, SeekOrigin.Begin);
                fileStream.SetLength(0);
                bool firstChunk = true;
                long lastSpeedTicks = Stopwatch.GetTimestamp();
                long bytesSinceSpeedReport = 0;

                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(35));
                    bytesRead = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token).ConfigureAwait(false);
                    if (bytesRead <= 0) break;

                    if (firstChunk && IsVideoDownload())
                    {
                        firstChunk = false;
                        if (bytesRead >= 4)
                        {
                            bool isJpeg = buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF;
                            bool isPng = buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47;
                            bool isWebp = bytesRead >= 12 && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 &&
                                          buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50;

                            if (isJpeg || isPng || isWebp)
                            {
                                throw new InvalidOperationException("İndirilen içerik bir video değil, resim/kapak görselidir (sunucu JPEG/PNG döndürdü).");
                            }
                        }

                        string prefix = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, 256)).TrimStart();
                        if (prefix.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                            prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                            prefix.StartsWith("{\"error\"", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("İndirilen içerik geçerli bir video dosyası değil (sunucudan HTML sayfası veya hata mesajı döndü).");
                        }
                    }

                    await SpeedLimiter.ThrottleAsync(bytesRead, ct).ConfigureAwait(false);
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

                    bytesSinceSpeedReport += bytesRead;
                    if (Segments.Count > 0)
                    {
                        Segments[0].CurrentByte += bytesRead;
                    }

                    long now = Stopwatch.GetTimestamp();
                    double elapsedSpeedSec = (double)(now - lastSpeedTicks) / Stopwatch.Frequency;
                    if (elapsedSpeedSec >= 0.25)
                    {
                        if (Segments.Count > 0)
                        {
                            Segments[0].CurrentSpeedBytesPerSec = bytesSinceSpeedReport / elapsedSpeedSec;
                        }
                        bytesSinceSpeedReport = 0;
                        lastSpeedTicks = now;
                    }
                }
            }

            if (!isCompressedEncoding && res.Content.Headers.ContentLength is long expectedLength &&
                fileStream.Position != expectedLength)
                throw new IOException($"İndirme eksik: {fileStream.Position}/{expectedLength} bayt.");

            if (Segments.Count > 0)
            {
                Segments[0].IsCompleted = true;
                Segments[0].CurrentSpeedBytesPerSec = 0;
            }

            TotalBytes = fileStream.Position;
            DownloadedBytes = fileStream.Position;
        }
    }

    private async Task RunProgressReporterAsync(CancellationToken ct)
    {
        long lastDownloaded = 0;
        long lastTicks = Stopwatch.GetTimestamp();

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(400, ct).ConfigureAwait(false);

            long currentDownloaded = 0;
            lock (_stateLock)
            {
                if (Segments.Count > 0)
                {
                    currentDownloaded = Segments.Sum(s => s.DownloadedBytes);
                }
                else if (File.Exists(DestinationPath))
                {
                    try { currentDownloaded = new FileInfo(DestinationPath).Length; } catch { }
                }
            }

            DownloadedBytes = currentDownloaded;

            long now = Stopwatch.GetTimestamp();
            double elapsed = (double)(now - lastTicks) / Stopwatch.Frequency;
            double speed = elapsed > 0 ? (currentDownloaded - lastDownloaded) / elapsed : 0;
            lastDownloaded = currentDownloaded;
            lastTicks = now;

            double percent = TotalBytes > 0 ? (double)currentDownloaded / TotalBytes * 100.0 : 0.0;
            TimeSpan? remaining = null;
            if (speed > 0 && TotalBytes > currentDownloaded)
            {
                remaining = TimeSpan.FromSeconds((TotalBytes - currentDownloaded) / speed);
            }

            List<DownloadSegment> snapshot;
            lock (_stateLock) snapshot = Segments.ToList();
            if (snapshot.Count == 1)
            {
                snapshot[0].CurrentSpeedBytesPerSec = Math.Max(0, speed);
                snapshot[0].NotifyProgress(force: true);
            }
            else
            {
                foreach (var seg in snapshot)
                {
                    if (seg.IsCompleted)
                    {
                        seg.CurrentSpeedBytesPerSec = 0;
                    }
                    seg.NotifyProgress(force: true);
                }
            }

            var activeSegments = _transferEngine.ActiveSegments;
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                DownloadedBytes = currentDownloaded,
                TotalBytes = TotalBytes,
                ProgressPercentage = Math.Clamp(percent, 0.0, 100.0),
                SpeedBytesPerSec = Math.Max(0, speed),
                TimeRemaining = remaining,
                Segments = activeSegments.Count > 0 ? activeSegments : snapshot.Take(Math.Clamp(ConnectionCount, 1, 32)).ToList()
            });

            // Checkpoint at most once per second; network progress remains frequent.
            if (Stopwatch.GetTimestamp() - _lastCheckpointTicks >= Stopwatch.Frequency) {
                SaveMetadata(); _lastCheckpointTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    private readonly CookieContainer _cookies = new();
    private readonly object _cookieLock = new();
    private Uri? _cookieOrigin;

    private void InitializeCookies()
    {
        lock (_cookieLock)
        {
            if (_cookieOrigin != null) return;
            _cookieOrigin = new Uri(CookieSourceUrl ?? Url);
            if (string.IsNullOrWhiteSpace(Cookies)) return;
            foreach (string part in Cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;
                try { _cookies.Add(_cookieOrigin, new Cookie(part[..equals].Trim(), part[(equals + 1)..].Trim(), "/") { Secure = _cookieOrigin.Scheme == "https" }); }
                catch (CookieException) { }
            }
        }
    }

    private string CookiesFor(Uri uri)
    {
        InitializeCookies();
        lock (_cookieLock) return _cookies.GetCookieHeader(uri);
    }

    private void CaptureSetCookiesFromResponse(HttpResponseMessage response, Uri uri)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return;
        lock (_cookieLock)
            foreach (string header in headers)
                try { _cookies.SetCookies(uri, header); } catch (CookieException) { }
    }
    private void ApplyHeadersWithoutCookies(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(UserAgent))
        {
            request.Headers.UserAgent.ParseAdd(UserAgent);
        }
        else
        {
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }

        if (!string.IsNullOrEmpty(Referer))
        {
            try { request.Headers.Referrer = new Uri(Referer); } catch { }
        }

        if (CustomHeaders != null)
        {
            foreach (var kvp in CustomHeaders)
            {
                if (kvp.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || kvp.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (kvp.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    (_cookieOrigin == null || request.RequestUri?.GetLeftPart(UriPartial.Authority) != _cookieOrigin.GetLeftPart(UriPartial.Authority))) continue;
                request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }

        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
    }

    internal Task<HttpResponseMessage> SendRequestAsync(string url, CancellationToken ct) => SendWithRedirectsAsync(HttpMethod.Get, url, null, ct);

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        HttpMethod method,
        string startUrl,
        RangeHeaderValue? range,
        CancellationToken ct,
        int maxRedirects = 12)
    {
        string currentUrl = startUrl;
        HttpMethod currentMethod = method;
        InitializeCookies();

        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int hop = 0; hop <= maxRedirects; hop++)
        {
            ct.ThrowIfCancellationRequested();

            if (!visitedUrls.Add(currentUrl) && hop > 2)
            {
                throw new InvalidOperationException($"Yönlendirme döngüsü tespit edildi: {currentUrl}");
            }

            var currentUri = new Uri(currentUrl);
            var req = new HttpRequestMessage(currentMethod, currentUrl);
            ApplyHeadersWithoutCookies(req);

            string cookieHeader = CookiesFor(currentUri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            // Do not send Range header to accounts.google.com during auth hops
            if (range != null && !currentUri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Range = range;
                if (!string.IsNullOrEmpty(ETag) && !ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                    req.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(ETag));
                else if (LastModified.HasValue)
                    req.Headers.IfRange = new RangeConditionHeaderValue(LastModified.Value);
            }

            HttpResponseMessage res;
            try
            {
                res = await HttpClientInstance.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch
            {
                req.Dispose();
                throw;
            }

            CaptureSetCookiesFromResponse(res, currentUri);

            int statusCode = (int)res.StatusCode;
            bool isRedirect = statusCode is 301 or 302 or 303 or 307 or 308;

            if (isRedirect && res.Headers.Location != null)
            {
                Uri redirectUri = res.Headers.Location.IsAbsoluteUri
                    ? res.Headers.Location
                    : new Uri(currentUri, res.Headers.Location);

                res.Dispose();
                if (redirectUri.Scheme is not ("http" or "https") || (currentUri.Scheme == "https" && redirectUri.Scheme != "https"))
                    throw new InvalidOperationException("Unsafe redirect rejected.");

                // If redirected to an interactive signin or accounts.google.com login page
                if (redirectUri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) &&
                    (redirectUri.AbsolutePath.Contains("/v3/signin", StringComparison.OrdinalIgnoreCase) ||
                     redirectUri.AbsolutePath.Contains("/ServiceLogin", StringComparison.OrdinalIgnoreCase)))
                {
                    // Check if we have auth cookies (SID, HSID, etc.)
                    bool hasAuth = !string.IsNullOrEmpty(CookiesFor(redirectUri));
                    if (!hasAuth && hop >= 1)
                    {
                        throw new InvalidOperationException("Google Drive indirme bağlantısı kimlik doğrulaması gerektiriyor ancak geçerli oturum çerezi bulunamadı. Lütfen tarayıcıda Google hesabınıza giriş yapıldığından emin olun.");
                    }
                }

                // Method transition on redirect
                if (statusCode == 303 || ((statusCode == 301 || statusCode == 302) && currentMethod == HttpMethod.Post))
                {
                    currentMethod = HttpMethod.Get;
                }

                // If redirected to accounts.google.com, strip OSID and COMPASS so clean origin authentication occurs
                if (redirectUri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    // CookieContainer preserves domain, path and Secure scope.
                }

                currentUrl = redirectUri.ToString();

                // Update Url only if not on accounts.google.com
                if (!redirectUri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    Url = currentUrl;
                }

                continue;
            }

            // If we landed on accounts.google.com returning 200 OK (an HTML signin form)
            if (currentUri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) && res.IsSuccessStatusCode)
            {
                res.Dispose();
                throw new InvalidOperationException("Google Drive oturum doğrulaması tamamlanamadı (oturum açma sayfasına yönlendirildi). Lütfen tarayıcıdan Google Drive sekmesini yenileyip indirmeyi tekrar başlatın.");
            }

            return res;
        }

        throw new InvalidOperationException($"Maksimum yönlendirme sınırı ({maxRedirects}) aşıldı.");
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        ApplyHeadersWithoutCookies(request);

        string scopedCookies = request.RequestUri == null ? "" : CookiesFor(request.RequestUri);
        if (!string.IsNullOrEmpty(scopedCookies))
        {
            request.Headers.Add("Cookie", scopedCookies);
        }
    }

    private void SaveMetadata()
    {
        try
        {
            lock (_stateLock)
            {
                var meta = new DownloadMetadata
                {
                    Url = Url,
                    DestinationPath = Path.GetFullPath(DestinationPath),
                    TotalBytes = TotalBytes,
                    ETag = ETag,
                    LastModified = LastModified,
                    SupportsRange = SupportsRange,
                    Segments = Segments.Select(s => {
                        long current = s.CurrentByte;
                        return new DownloadSegment(s.Index, s.StartByte, s.EndByte) { CurrentByte = current, IsCompleted = current > s.EndByte };
                    }).ToList()
                };
                if (meta.Segments.Any(s => s.DownloadedBytes > 0))
                {
                    if (_checkpointHandle == null || _checkpointHandle.IsClosed) return;
                    RandomAccess.FlushToDisk(_checkpointHandle);
                }
                if (RecoveryFilePath != null) { RecoveryStore.SaveHttp(RecoveryFilePath, meta); return; }
                string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
                RecoveryStore.AtomicWrite(MetadataPath, System.Text.Encoding.UTF8.GetBytes(json));
            }
        }
        catch { }
    }

    private bool LoadMetadata()
    {
        try
        {
            if (!File.Exists(DestinationPath))
                return false;

            var meta = RecoveryFilePath != null ? RecoveryStore.Load(RecoveryFilePath)?.Http
                : File.Exists(MetadataPath) ? JsonSerializer.Deserialize<DownloadMetadata>(File.ReadAllText(MetadataPath)) : null;
            if (meta == null || meta.Url != Url || meta.TotalBytes != TotalBytes || !SupportsRange ||
                new FileInfo(DestinationPath).Length != TotalBytes ||
                (meta.DestinationPath != null && !Path.GetFullPath(DestinationPath).Equals(meta.DestinationPath, StringComparison.OrdinalIgnoreCase)))
                return false;
            bool sameIdentity = !string.IsNullOrEmpty(ETag) && !ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
                ? ETag == meta.ETag : LastModified.HasValue && LastModified == meta.LastModified;
            if (!sameIdentity) return false;

            // If no bytes were downloaded yet or single stream is preferred, start cleanly instead of resuming broken metadata
            if (meta.Segments == null || meta.Segments.Count == 0 || meta.Segments.Sum(s => s.DownloadedBytes) <= 0 || IsSingleStreamPreferred())
                return false;

            long next = 0;
            foreach (var part in meta.Segments.OrderBy(s => s.StartByte)) {
                if (part.StartByte != next || part.EndByte < part.StartByte || part.EndByte >= TotalBytes ||
                    part.CurrentByte < part.StartByte || part.CurrentByte > part.EndByte + 1) return false;
                next = part.EndByte + 1;
                part.IsCompleted = part.CurrentByte == next;
            }
            if (next != TotalBytes || meta.Segments.Select(s => s.Index).Distinct().Count() != meta.Segments.Count) return false;
            Segments.Clear(); Segments.AddRange(meta.Segments);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ExtractFileNameFromUrl(string url)
    {
        return ResolveProperFileName(url, null);
    }

    public static string CleanFileName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

        rawName = rawName.Trim('"', '\'', ' ');

        var rfc5987Match = Regex.Match(rawName, @"^(?:UTF-8|ISO-8859-1)''(.+)$", RegexOptions.IgnoreCase);
        if (rfc5987Match.Success)
        {
            try { rawName = Uri.UnescapeDataString(rfc5987Match.Groups[1].Value); } catch { }
        }
        else if (rawName.Contains('%'))
        {
            try { rawName = Uri.UnescapeDataString(rawName); } catch { }
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            rawName = rawName.Replace(c, '_');
        }

        return rawName.Trim();
    }

    public static string ResolveProperFileName(string url, string? mimeExtension)
    {
        try
        {
            var uri = new Uri(url);
            string rawName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(rawName))
            {
                rawName = WebUtility.UrlDecode(rawName);
                if (rawName.Contains('.') && !rawName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    return rawName;
                }

                // If path has a sensible name without extension, attach detected mime extension
                if (!string.IsNullOrWhiteSpace(mimeExtension) && 
                    !rawName.Equals("view", StringComparison.OrdinalIgnoreCase) &&
                    !rawName.Equals("watch", StringComparison.OrdinalIgnoreCase) &&
                    !rawName.Equals("stream", StringComparison.OrdinalIgnoreCase) &&
                    !rawName.Equals("videoplayback", StringComparison.OrdinalIgnoreCase))
                {
                    return rawName + mimeExtension;
                }
            }
        }
        catch { }

        string ext = mimeExtension ?? ".bin";
        if (ext.StartsWith(".")) ext = ext.Substring(1);

        string prefix = (mimeExtension == ".mp4" || mimeExtension == ".webm" || mimeExtension == ".mkv" || mimeExtension == ".m3u8")
            ? "video"
            : (mimeExtension == ".mp3" || mimeExtension == ".aac" || mimeExtension == ".wav")
                ? "audio"
                : "download";

        return $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}";
    }

    public static string? GetExtensionFromMimeType(string mime)
    {
        return mime switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            "video/quicktime" => ".mov",
            "video/x-msvideo" => ".avi",
            "video/mp2t" => ".mp4",
            "application/vnd.apple.mpegurl" => ".m3u8",
            "application/x-mpegurl" => ".m3u8",
            "application/dash+xml" => ".mpd",
            "audio/mpeg" => ".mp3",
            "audio/mp3" => ".mp3",
            "audio/ogg" => ".ogg",
            "audio/wav" => ".wav",
            "audio/aac" => ".aac",
            "audio/flac" => ".flac",
            "application/zip" => ".zip",
            "application/x-zip-compressed" => ".zip",
            "application/x-rar-compressed" => ".rar",
            "application/vnd.rar" => ".rar",
            "application/x-7z-compressed" => ".7z",
            "application/pdf" => ".pdf",
            "text/plain" => ".txt",
            "text/markdown" => ".md",
            "text/csv" => ".csv",
            "application/json" => ".json",
            "application/xml" => ".xml",
            "text/xml" => ".xml",
            "application/x-msdownload" => ".exe",
            "application/x-iso9660-image" => ".iso",
            _ => null
        };
    }

    private static bool TryExtractGoogleDriveConfirmation(string html, string currentUrl, out string? confirmedUrl)
    {
        confirmedUrl = null;
        if (string.IsNullOrWhiteSpace(html)) return false;

        bool hasForm = html.Contains("download-form", StringComparison.OrdinalIgnoreCase) ||
                       html.Contains("name=\"confirm\"", StringComparison.OrdinalIgnoreCase) ||
                       html.Contains("uc-name-size", StringComparison.OrdinalIgnoreCase) ||
                       html.Contains("uc-download-link", StringComparison.OrdinalIgnoreCase) ||
                       html.Contains("Virus scan warning", StringComparison.OrdinalIgnoreCase);

        if (!hasForm) return false;

        string fileId = "";
        var idMatch = Regex.Match(currentUrl, @"(?:/file/d/|id=|/open\?id=|/uc\?id=)([a-zA-Z0-9_-]{15,60})", RegexOptions.IgnoreCase);
        if (idMatch.Success) fileId = idMatch.Groups[1].Value;

        string confirm = ExtractHtmlInputValue(html, "confirm");
        if (string.IsNullOrEmpty(confirm)) confirm = "t";

        string uuid = ExtractHtmlInputValue(html, "uuid");

        string formId = ExtractHtmlInputValue(html, "id");
        if (string.IsNullOrEmpty(formId)) formId = fileId;

        string formExport = ExtractHtmlInputValue(html, "export");
        if (string.IsNullOrEmpty(formExport)) formExport = "download";

        // Check href for uc-download-link
        var hrefMatch = Regex.Match(html, @"href=""(\/uc\?export=download[^""]+)""", RegexOptions.IgnoreCase);
        if (hrefMatch.Success)
        {
            confirmedUrl = "https://docs.google.com" + WebUtility.HtmlDecode(hrefMatch.Groups[1].Value).Replace("&amp;", "&");
            return true;
        }

        // Check form action
        string action = "https://drive.usercontent.google.com/download";
        var formActionMatch = Regex.Match(html, @"<form[^>]+action=""([^""]*)""", RegexOptions.IgnoreCase);
        if (formActionMatch.Success)
        {
            string rawAction = WebUtility.HtmlDecode(formActionMatch.Groups[1].Value);
            if (rawAction.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                action = rawAction.Split('?')[0];
            }
            else if (rawAction.StartsWith("/"))
            {
                action = "https://drive.usercontent.google.com" + rawAction.Split('?')[0];
            }
        }

        if (string.IsNullOrEmpty(formId)) return false;

        confirmedUrl = $"{action}?id={formId}&export={formExport}&confirm={confirm}";
        if (!string.IsNullOrEmpty(uuid))
        {
            confirmedUrl += $"&uuid={uuid}";
        }

        return true;
    }

    private static string ExtractHtmlInputValue(string html, string inputName)
    {
        var regex = new Regex($@"<input[^>]+name=""{inputName}""[^>]+value=""([^""]*)""", RegexOptions.IgnoreCase);
        var match = regex.Match(html);
        if (match.Success) return match.Groups[1].Value;

        var reverseRegex = new Regex($@"<input[^>]+value=""([^""]*)""[^>]+name=""{inputName}""", RegexOptions.IgnoreCase);
        var reverseMatch = reverseRegex.Match(html);
        return reverseMatch.Success ? reverseMatch.Groups[1].Value : "";
    }

    private static string ExtractServerHtmlErrorMessage(string html, string url)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "Sunucu beklenen ikili dosya yerine boş bir yanıt döndürdü.";
        }

        bool isGoogleDrive = url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains("docs.google.com", StringComparison.OrdinalIgnoreCase);

        if (isGoogleDrive)
        {
            var subcaptionMatch = Regex.Match(html, @"<p class=""uc-error-subcaption"">([^<]+)</p>", RegexOptions.IgnoreCase);
            if (subcaptionMatch.Success)
            {
                return $"Google Drive: {WebUtility.HtmlDecode(subcaptionMatch.Groups[1].Value.Trim())}";
            }

            var captionMatch = Regex.Match(html, @"<div class=""uc-error-caption"">([^<]+)</div>", RegexOptions.IgnoreCase);
            if (captionMatch.Success)
            {
                return $"Google Drive: {WebUtility.HtmlDecode(captionMatch.Groups[1].Value.Trim())}";
            }

            if (html.Contains("Download quota", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("indirme kotası", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("kota aşıldı", StringComparison.OrdinalIgnoreCase))
            {
                return "Google Drive: Bu dosyanın indirme kotası aşıldı. Dosyaya çok fazla erişim yapıldığı için Google şu anda indirmeye izin vermiyor.";
            }

            if (html.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("You need access", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("Erişim izni", StringComparison.OrdinalIgnoreCase))
            {
                return "Google Drive: Bu dosyayı indirmek için Google hesabı erişim izni gerekiyor.";
            }

            return "Google Drive: Sunucu ikili dosya yerine bir web/HTML sayfası döndürdü (onay veya kota engeli).";
        }

        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube: Sunucu doğrudan video/ses akışı yerine web sayfası döndürdü. YouTube sayfaları doğrudan indirilemez; lütfen tarayıcı eklentisindeki 'Bu Videoyu İndir' butonunu kullanarak oynatılan video akışını indirin.";
        }

        return "Sunucu ikili dosya yerine bir web/HTML sayfası döndürdü. İndirme bağlantısı doğrudan bir dosya veya medya akışı sağlamıyor.";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

public class DownloadMetadata
{
    public string Url { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public string? ETag { get; set; }
    public string? DestinationPath { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public bool SupportsRange { get; set; }
    public List<DownloadSegment> Segments { get; set; } = new();
}

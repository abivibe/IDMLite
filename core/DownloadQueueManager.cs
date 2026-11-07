using System.Collections.ObjectModel;
using System.Text.Json;
using IDMLite.Core.Services;

namespace IDMLite.Core;

public class DownloadQueueManager : IDisposable
{
    private string AppDataFolder => _settingsManager.StorageDirectory;
    private string QueueFile => Path.Combine(AppDataFolder, "downloads.json");

    private readonly SettingsManager _settingsManager;
    public SettingsManager Settings => _settingsManager;
    private readonly SpeedLimiter _sharedSpeedLimiter;
    private readonly object _lock = new();
    private readonly HashSet<Guid> _running = new();
    private readonly HashSet<Guid> _pauseRequested = new();
    private readonly HashSet<Guid> _restartRequested = new();
    private readonly CancellationTokenSource _lifetime = new();

    public ObservableCollection<DownloadTask> Tasks { get; } = new();

    public List<DownloadTask> GetTasksSnapshot()
    {
        lock (_lock)
        {
            return Tasks.ToList();
        }
    }

    public event EventHandler<DownloadTask>? TaskAdded;
    public event EventHandler<DownloadTask>? TaskUpdated;
    public event EventHandler<DownloadTask>? TaskCompleted;
    public event EventHandler<DownloadTask>? TaskRemoved;
    public event EventHandler<double>? TotalSpeedChanged;

    public DownloadQueueManager(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        long limit = _settingsManager.Current.SpeedLimitEnabled ? _settingsManager.Current.SpeedLimitBytesPerSec : 0;
        _sharedSpeedLimiter = new SpeedLimiter(limit);

        LoadQueue();
        SaveQueue();
        _ = RunQueueSchedulerAsync();
        _ = CheckpointLoopAsync();
    }

    public void UpdateSpeedLimit(long maxBytesPerSec, bool enabled)
    {
        _sharedSpeedLimiter.MaxBytesPerSecond = enabled ? maxBytesPerSec : 0;
    }

    public void Add(DownloadTask task, bool autoStart = false)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(task.SaveDirectory))
            {
                task.SaveDirectory = _settingsManager.GetDirectoryForCategory(task.Category);
            }

            if (task.ConnectionCount <= 0)
            {
                task.ConnectionCount = _settingsManager.Current.MaxConnectionsPerDownload;
            }

            task.Status = autoStart ? DownloadStatus.Queued : DownloadStatus.Paused;
            task.ResumeOnRestart = autoStart;
            task.RecoveryFilePath = RecoveryStore.PathFor(_settingsManager.Current.MetadataDirectory, task.Id);
            RecoveryStore.SaveTask(task);
            Tasks.Insert(0, task);
        }

        SaveQueue();
        TaskAdded?.Invoke(this, task);

        if (autoStart)
        {
            TriggerScheduler();
        }
    }

    public void Start(DownloadTask task)
    {
        lock (_lock)
        {
            if (_running.Contains(task.Id))
            {
                if (_pauseRequested.Contains(task.Id)) _restartRequested.Add(task.Id);
                return;
            }
            if (task.Status is DownloadStatus.Downloading or DownloadStatus.Connecting) return;
            task.Status = DownloadStatus.Queued;
            task.ResumeOnRestart = true;
            task.RetryAfterUtc = null;
        }
        SaveQueue();
        TaskUpdated?.Invoke(this, task);
        TriggerScheduler();
    }

    public void Pause(DownloadTask task)
    {
        lock (_lock)
        {
            if (_running.Contains(task.Id)) _pauseRequested.Add(task.Id);
            _restartRequested.Remove(task.Id);
            task.ResumeOnRestart = false;
            task.HlsEngine?.Pause();
            task.YtDlpEngine?.Pause();
            task.UniversalVideoEngine?.Pause();
            task.Downloader?.Pause();
            task.Status = DownloadStatus.Paused;
            task.SpeedBytesPerSec = 0;
            task.TimeRemaining = null;
        }
        SaveQueue();
        TaskUpdated?.Invoke(this, task);
        TriggerScheduler();
    }

    public void PauseAll()
    {
        lock (_lock)
        {
            foreach (var task in Tasks.Where(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Assembling or DownloadStatus.Verifying))
            {
                Pause(task);
            }
        }
    }

    public void ResumeAll()
    {
        foreach (var task in GetTasksSnapshot().Where(t => t.Status == DownloadStatus.Paused)) Start(task);
    }

    public void Remove(DownloadTask task, bool deleteFiles = false)
    {
        Pause(task);

        lock (_lock)
        {
            Tasks.Remove(task);
            if (task.RecoveryFilePath != null) RecoveryStore.Delete(task.RecoveryFilePath);
            if (!_running.Contains(task.Id))
            {
            task.Downloader?.Dispose();
            task.Downloader = null;
            task.HlsEngine?.Dispose();
            task.HlsEngine = null;
            task.YtDlpEngine?.Dispose();
            task.YtDlpEngine = null;
            task.UniversalVideoEngine?.Dispose();
            task.UniversalVideoEngine = null;
            }
        }

        if (deleteFiles)
        {
            try
            {
                if (File.Exists(task.FullPath))
                {
                    File.Delete(task.FullPath);
                }
                string meta = task.FullPath + ".idmlite.meta";
                if (File.Exists(meta))
                {
                    File.Delete(meta);
                }
            }
            catch { }
        }

        SaveQueue();
        TaskRemoved?.Invoke(this, task);
    }

    private readonly SemaphoreSlim _schedulerTrigger = new(0, 100);

    private void TriggerScheduler()
    {
        try
        {
            if (_schedulerTrigger.CurrentCount == 0)
                _schedulerTrigger.Release();
        }
        catch { }
    }

    private async Task RunQueueSchedulerAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                int maxConcurrent = _settingsManager.Current.MaxConcurrentDownloads;
                List<DownloadTask> queuedToStart = new();

                lock (_lock)
                {
                    foreach (var retry in Tasks.Where(t => t.Status == DownloadStatus.Error && t.ResumeOnRestart &&
                        _settingsManager.Current.ResumeInterruptedDownloads && t.RetryAfterUtc <= DateTime.UtcNow))
                        retry.Status = DownloadStatus.Queued;
                    int activeCount = _running.Count;
                    int slotsAvailable = Math.Max(0, maxConcurrent - activeCount);

                    if (slotsAvailable > 0)
                    {
                        queuedToStart = Tasks
                            .Where(t => t.Status == DownloadStatus.Queued && !_running.Contains(t.Id))
                            .Take(slotsAvailable)
                            .ToList();
                        foreach (var next in queuedToStart)
                        {
                            _running.Add(next.Id);
                            _pauseRequested.Remove(next.Id);
                            next.Status = DownloadStatus.Connecting;
                        }
                    }
                }

                foreach (var task in queuedToStart)
                {
                    _ = ExecuteDownloadTaskAsync(task);
                }

                // Calculate total speed
                double totalSpeed = 0;
                lock (_lock)
                {
                    totalSpeed = Tasks.Where(t => t.Status == DownloadStatus.Downloading).Sum(t => t.SpeedBytesPerSec);
                }
                TotalSpeedChanged?.Invoke(this, totalSpeed);

                await _schedulerTrigger.WaitAsync(1000, _lifetime.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task ExecuteDownloadTaskAsync(DownloadTask task)
    {
        try
        {
            lock (_lock)
            {
                if (_pauseRequested.Contains(task.Id) || !Tasks.Contains(task)) return;
            }
            task.ErrorMessage = null;
            SaveQueue();
            // Enforce .mp4 for any video or HLS task that might have had .ts
            if (task.FileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                task.FileName = Path.ChangeExtension(task.FileName, ".mp4");
            }

            // Normalize GitHub /blob/ and /raw/ links
            task.Url = SegmentedDownloader.NormalizeGitHubRawUrl(task.Url);

            // If task URL is an ephemeral googlevideo stream and we have a YouTube watch/shorts referer, recover the original YouTube watch URL
            if (task.Url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(task.Referer) &&
                    (task.Referer.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
                     task.Referer.Contains("youtube.com/shorts/", StringComparison.OrdinalIgnoreCase) ||
                     task.Referer.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase)))
                {
                    task.Url = task.Referer;
                }
            }

            task.Status = DownloadStatus.Connecting;
            TaskUpdated?.Invoke(this, task);

            if ((task.IsSocialVideo || task.IsHls) && YtDlpHelper.IsAvailable)
            {
                if (task.Url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
                {
                    task.Url = Resolvers.InstagramResolver.NormalizeInstagramPostUrl(task.Url);
                    var cachedIg = Resolvers.InstagramResolver.TryGetCachedResult(task.Url);
                    if (cachedIg != null && !string.IsNullOrWhiteSpace(cachedIg.DirectUrl) &&
                        !cachedIg.DirectUrl.Contains("instagram.com/p/", StringComparison.OrdinalIgnoreCase) &&
                        !cachedIg.DirectUrl.Contains("instagram.com/reel/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(task.FallbackStreamUrl))
                        {
                            task.FallbackStreamUrl = cachedIg.DirectUrl;
                        }
                        if (task.TotalSize <= 0 && cachedIg.FileSize.HasValue && cachedIg.FileSize.Value > 0)
                        {
                            task.TotalSize = cachedIg.FileSize.Value;
                        }
                    }
                }

                var ytDlp = new YtDlpDownloader(task.Url, task.FullPath)
                {
                    FragmentConcurrency = _settingsManager.Current.MaxConnectionsPerDownload,
                    Cookies = task.Cookies,
                    Referer = task.Referer,
                    UserAgent = task.UserAgent
                };
                task.YtDlpEngine = ytDlp;

                ytDlp.ProgressChanged += (s, e) =>
                {
                    task.DownloadedSize = e.DownloadedBytes;
                    task.TotalSize = e.TotalBytes;
                    task.SpeedBytesPerSec = e.SpeedBytesPerSec;
                    task.TimeRemaining = e.TimeRemaining;
                    TaskUpdated?.Invoke(this, task);
                };

                ytDlp.StatusChanged += (s, status) =>
                {
                    task.Status = status;
                    TaskUpdated?.Invoke(this, task);
                };

                ytDlp.VerificationProgressChanged += (s, p) =>
                {
                    task.VerificationProgress = p;
                    TaskUpdated?.Invoke(this, task);
                };

                ytDlp.Completed += (s, finalFile) =>
                {
                    if (!string.IsNullOrEmpty(finalFile) && !string.Equals(task.FullPath, finalFile, StringComparison.OrdinalIgnoreCase))
                    {
                        task.FileName = Path.GetFileName(finalFile);
                        task.SaveDirectory = Path.GetDirectoryName(finalFile) ?? task.SaveDirectory;
                    }
                    task.ActualChecksum = ytDlp.ComputedSha256;
                    task.IsVerified = !string.IsNullOrEmpty(task.ActualChecksum);
                };

                try
                {
                    await ytDlp.StartAsync().ConfigureAwait(false);
                    task.DownloadedSize = ytDlp.DownloadedBytes;
                    task.TotalSize = ytDlp.TotalBytes;
                }
                catch (Exception ex) when (ex is not OperationCanceledException &&
                                           !string.IsNullOrWhiteSpace(task.FallbackStreamUrl) &&
                                           task.FallbackStreamUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    ytDlp.Dispose();
                    task.YtDlpEngine = null;
                    task.FileName = UniversalVideoDownloader.EnsureMp4Extension(task.FileName);
                    task.ConnectionCount = _settingsManager.Current.MaxConnectionsPerDownload;

                    var univFallback = new UniversalVideoDownloader(task.FallbackStreamUrl, task.FullPath, _sharedSpeedLimiter)
                    {
                        CookieSourceUrl = task.Url,
                        RecoveryFilePath = task.RecoveryFilePath,
                        ConnectionCount = task.ConnectionCount,
                        Referer = task.Referer,
                        UserAgent = task.UserAgent,
                        Cookies = task.Cookies,
                        CustomHeaders = task.CustomHeaders,
                        TotalBytes = task.TotalSize
                    };
                    task.UniversalVideoEngine = univFallback;

                    univFallback.ProgressChanged += (s, e) =>
                    {
                        task.DownloadedSize = e.DownloadedBytes;
                        if (e.TotalBytes > 0) task.TotalSize = e.TotalBytes;
                        task.SpeedBytesPerSec = e.SpeedBytesPerSec;
                        task.TimeRemaining = e.TimeRemaining;
                        TaskUpdated?.Invoke(this, task);
                    };

                    univFallback.StatusChanged += (s, status) =>
                    {
                        task.Status = status;
                        TaskUpdated?.Invoke(this, task);
                    };

                    univFallback.VerificationProgressChanged += (s, p) =>
                    {
                        task.VerificationProgress = p;
                        TaskUpdated?.Invoke(this, task);
                    };

                    univFallback.Completed += (s, finalFile) =>
                    {
                        if (!string.IsNullOrEmpty(finalFile) && !string.Equals(task.FullPath, finalFile, StringComparison.OrdinalIgnoreCase))
                        {
                            task.FileName = Path.GetFileName(finalFile);
                            task.SaveDirectory = Path.GetDirectoryName(finalFile) ?? task.SaveDirectory;
                        }
                        task.ActualChecksum = univFallback.ComputedSha256;
                        task.IsVerified = !string.IsNullOrEmpty(task.ActualChecksum);
                    };

                    await univFallback.StartAsync().ConfigureAwait(false);
                    task.DownloadedSize = univFallback.DownloadedBytes;
                    if (task.TotalSize <= 0) task.TotalSize = univFallback.DownloadedBytes;
                }
            }
            else if (task.IsHls)
            {
                var hls = new HlsDownloader(task.Url, task.FullPath, _settingsManager.Current.MaxConnectionsPerDownload, _sharedSpeedLimiter)
                {
                    Referer = task.Referer,
                    UserAgent = task.UserAgent,
                    Cookies = task.Cookies
                };
                task.HlsEngine = hls;

                hls.ProgressChanged += (s, e) =>
                {
                    task.DownloadedSize = e.DownloadedBytes;
                    task.TotalSize = e.TotalBytes;
                    task.SpeedBytesPerSec = e.SpeedBytesPerSec;
                    task.TimeRemaining = e.TimeRemaining;
                    TaskUpdated?.Invoke(this, task);
                };

                hls.StatusChanged += (s, status) =>
                {
                    task.Status = status;
                    TaskUpdated?.Invoke(this, task);
                };

                hls.VerificationProgressChanged += (s, p) =>
                {
                    task.VerificationProgress = p;
                    TaskUpdated?.Invoke(this, task);
                };

                hls.Completed += (s, finalFile) =>
                {
                    if (!string.IsNullOrEmpty(finalFile) && !string.Equals(task.FullPath, finalFile, StringComparison.OrdinalIgnoreCase))
                    {
                        task.FileName = Path.GetFileName(finalFile);
                        task.SaveDirectory = Path.GetDirectoryName(finalFile) ?? task.SaveDirectory;
                    }
                    task.ActualChecksum = hls.ComputedSha256;
                    task.IsVerified = !string.IsNullOrEmpty(task.ActualChecksum);
                };

                await hls.StartAsync().ConfigureAwait(false);
                task.DownloadedSize = hls.TotalBytesDownloaded;
                if (task.TotalSize <= 0) task.TotalSize = hls.TotalBytesDownloaded;
            }
            else if (UniversalVideoDownloader.ShouldHandleTask(task))
            {
                task.FileName = UniversalVideoDownloader.EnsureMp4Extension(task.FileName);
                task.ConnectionCount = _settingsManager.Current.MaxConnectionsPerDownload;

                var univ = new UniversalVideoDownloader(task.Url, task.FullPath, _sharedSpeedLimiter)
                {
                    RecoveryFilePath = task.RecoveryFilePath,
                    ConnectionCount = task.ConnectionCount,
                    Referer = task.Referer,
                    UserAgent = task.UserAgent,
                    Cookies = task.Cookies,
                    CustomHeaders = task.CustomHeaders,
                    TotalBytes = task.TotalSize
                };
                task.UniversalVideoEngine = univ;

                univ.ProgressChanged += (s, e) =>
                {
                    task.DownloadedSize = e.DownloadedBytes;
                    if (e.TotalBytes > 0) task.TotalSize = e.TotalBytes;
                    task.SpeedBytesPerSec = e.SpeedBytesPerSec;
                    task.TimeRemaining = e.TimeRemaining;
                    TaskUpdated?.Invoke(this, task);
                };

                univ.StatusChanged += (s, status) =>
                {
                    task.Status = status;
                    TaskUpdated?.Invoke(this, task);
                };

                univ.VerificationProgressChanged += (s, p) =>
                {
                    task.VerificationProgress = p;
                    TaskUpdated?.Invoke(this, task);
                };

                univ.Completed += (s, finalFile) =>
                {
                    if (!string.IsNullOrEmpty(finalFile) && !string.Equals(task.FullPath, finalFile, StringComparison.OrdinalIgnoreCase))
                    {
                        task.FileName = Path.GetFileName(finalFile);
                        task.SaveDirectory = Path.GetDirectoryName(finalFile) ?? task.SaveDirectory;
                    }
                    task.ActualChecksum = univ.ComputedSha256;
                    task.IsVerified = !string.IsNullOrEmpty(task.ActualChecksum);
                };

                await univ.StartAsync().ConfigureAwait(false);
                task.DownloadedSize = univ.DownloadedBytes;
                if (univ.TotalBytes > 0) task.TotalSize = univ.TotalBytes;
            }
            else
            {
                var downloader = new SegmentedDownloader(
                    task.Url,
                    task.FullPath,
                    task.ConnectionCount,
                    _sharedSpeedLimiter)
                {
                    Referer = task.Referer,
                    UserAgent = task.UserAgent,
                    Cookies = task.Cookies,
                    CustomHeaders = task.CustomHeaders,
                    SkipResolver = task.IsDirect,
                    RecoveryFilePath = task.RecoveryFilePath,
                    TotalBytes = task.TotalSize
                };

                if (task.Category == DownloadCategory.Video || downloader.IsVideoDownload() || downloader.IsSingleStreamPreferred())
                {
                    task.ConnectionCount = 1;
                    downloader.ConnectionCount = 1;
                    downloader.SupportsRange = false;
                }

                task.Downloader = downloader;

                downloader.ProgressChanged += (s, e) =>
                {
                    task.DownloadedSize = e.DownloadedBytes;
                    task.TotalSize = e.TotalBytes;
                    task.SpeedBytesPerSec = e.SpeedBytesPerSec;
                    task.TimeRemaining = e.TimeRemaining;
                    task.Segments = e.Segments;
                    TaskUpdated?.Invoke(this, task);
                };

                downloader.StatusChanged += (s, status) =>
                {
                    task.Status = status;
                    TaskUpdated?.Invoke(this, task);
                };

                downloader.VerificationProgressChanged += (s, p) =>
                {
                    task.VerificationProgress = p;
                    TaskUpdated?.Invoke(this, task);
                };

                await downloader.StartAsync().ConfigureAwait(false);
                task.DownloadedSize = downloader.DownloadedBytes;
                task.TotalSize = downloader.TotalBytes;
            }

            lock (_lock)
            {
                if (_pauseRequested.Contains(task.Id) || !Tasks.Contains(task) || task.Status == DownloadStatus.Paused) return;
            }
            if (!File.Exists(task.FullPath)) throw new IOException("İndirme motoru çıktı dosyası üretmedi.");

            string? computedHash = task.Downloader?.ComputedSha256
                                   ?? task.HlsEngine?.ComputedSha256
                                   ?? task.YtDlpEngine?.ComputedSha256
                                   ?? task.UniversalVideoEngine?.ComputedSha256;
            if (string.IsNullOrEmpty(computedHash) && File.Exists(task.FullPath))
            {
                try
                {
                    computedHash = await ChecksumHelper.ComputeHashAsync(task.FullPath, task.ChecksumType ?? "SHA256").ConfigureAwait(false);
                }
                catch { }
            }

            task.ActualChecksum = computedHash;
            task.VerificationProgress = 100.0;
            task.ErrorMessage = null;
            task.SpeedBytesPerSec = 0;
            task.TimeRemaining = null;
            task.ResumeOnRestart = false;
            task.RetryCount = 0;

            if (!string.IsNullOrWhiteSpace(task.ExpectedChecksum) && !string.IsNullOrWhiteSpace(task.ActualChecksum))
            {
                bool match = string.Equals(task.ActualChecksum.Trim(), task.ExpectedChecksum.Trim(), StringComparison.OrdinalIgnoreCase);
                task.IsVerified = match;
                if (!match)
                {
                    task.Status = DownloadStatus.Error;
                    task.ErrorMessage = "Doğrulama Başarısız (Özet uyuşmazlığı)";
                }
                else
                {
                    task.Status = DownloadStatus.Completed;
                }
            }
            else
            {
                task.IsVerified = !string.IsNullOrWhiteSpace(computedHash);
                task.Status = DownloadStatus.Completed;
            }

            SaveQueue();
            if (task.Status == DownloadStatus.Completed) TaskCompleted?.Invoke(this, task);
            TaskUpdated?.Invoke(this, task);
        }
        catch (OperationCanceledException) when (PauseWasRequested(task))
        {
            task.Status = DownloadStatus.Paused;
            task.SpeedBytesPerSec = 0;
            task.TimeRemaining = null;
            TaskUpdated?.Invoke(this, task);
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Error;
            task.ErrorMessage = ex.Message;
            bool transient = ex is TimeoutException or OperationCanceledException ||
                ex is HttpRequestException http && (http.StatusCode == null || (int)http.StatusCode is 408 or 429 or >= 500);
            task.ResumeOnRestart = transient;
            task.RetryCount++;
            task.RetryAfterUtc = transient ? DateTime.UtcNow.AddSeconds(Math.Min(60, task.RetryCount * 5)) : null;
            task.SpeedBytesPerSec = 0;
            task.TimeRemaining = null;
            TaskUpdated?.Invoke(this, task);
        }
        finally
        {
            lock (_lock)
            {
                task.Downloader?.Dispose(); task.Downloader = null;
                task.HlsEngine?.Dispose(); task.HlsEngine = null;
                task.YtDlpEngine?.Dispose(); task.YtDlpEngine = null;
                task.UniversalVideoEngine?.Dispose(); task.UniversalVideoEngine = null;
                _running.Remove(task.Id);
                if (_pauseRequested.Contains(task.Id)) task.Status = DownloadStatus.Paused;
                _pauseRequested.Remove(task.Id);
                if (_restartRequested.Remove(task.Id) && Tasks.Contains(task)) task.Status = DownloadStatus.Queued;
            }
            SaveQueue();
            TriggerScheduler();
        }
    }

    private static readonly JsonSerializerOptions QueueJson = CreateQueueJson();

    private static JsonSerializerOptions CreateQueueJson()
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(DownloadTask)) return;
            foreach (var property in type.Properties)
            {
                if (property.Name is nameof(DownloadTask.Cookies) or nameof(DownloadTask.CustomHeaders) or
                    nameof(DownloadTask.FallbackStreamUrl) or nameof(DownloadTask.ErrorMessage))
                    property.ShouldSerialize = (_, _) => false;
            }
        });
        return new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = resolver };
    }

    public void SaveQueue()
    {
        try
        {
            lock (_lock)
            {
                if (!Directory.Exists(AppDataFolder))
                {
                    Directory.CreateDirectory(AppDataFolder);
                }

                var list = Tasks.ToList();
                foreach (var task in list) {
                    task.RecoveryFilePath ??= RecoveryStore.PathFor(_settingsManager.Current.MetadataDirectory, task.Id);
                    RecoveryStore.SaveTask(task);
                }
                string json = JsonSerializer.Serialize(list, QueueJson);
                byte[] index = System.Text.Encoding.UTF8.GetBytes(json);
                RecoveryStore.AtomicWrite(QueueFile, index);
                if (_rewriteLegacyQueueBackup)
                {
                    RecoveryStore.AtomicWrite(QueueFile, index);
                    _rewriteLegacyQueueBackup = false;
                }
            }
        }
        catch { }
    }

    private bool PauseWasRequested(DownloadTask task)
    { lock (_lock) return _pauseRequested.Contains(task.Id); }

    private async Task CheckpointLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested) {
            try {
                await Task.Delay(2000, _lifetime.Token).ConfigureAwait(false);
                List<DownloadTask> active;
                lock (_lock) active = Tasks.Where(t => _running.Contains(t.Id)).ToList();
                foreach (var task in active) RecoveryStore.SaveTask(task);
            } catch (OperationCanceledException) { break; }
              catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
        }
    }

    public void ChangeMetadataDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        PrivateStorage.EnsureDirectory(directory);
        string test = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".write-test");
        using (var probe = new FileStream(test, FileMode.CreateNew, FileAccess.Write, FileShare.None)) probe.Flush(true);
        File.Delete(test);
        lock (_lock) {
            var settings = _settingsManager.Current;
            settings.KnownMetadataDirectories.Add(settings.MetadataDirectory);
            settings.KnownMetadataDirectories = settings.KnownMetadataDirectories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            settings.MetadataDirectory = directory;
            _settingsManager.Save();
            foreach (var task in Tasks) {
                string target = RecoveryStore.PathFor(directory, task.Id);
                if (task.RecoveryFilePath != null) RecoveryStore.Move(task.RecoveryFilePath, target);
                task.RecoveryFilePath = target;
                RecoveryStore.SaveTask(task);
            }
        }
        SaveQueue();
    }

    private bool _rewriteLegacyQueueBackup = true;

    private void LoadQueue()
    {
        var loaded = new List<DownloadTask>();
        foreach (string candidate in new[] { QueueFile, QueueFile + ".bak" }) {
            try { if (File.Exists(candidate)) { loaded = JsonSerializer.Deserialize<List<DownloadTask>>(File.ReadAllText(candidate)) ?? new(); break; } }
            catch { }
        }
        var directories = _settingsManager.Current.KnownMetadataDirectories
            .Append(_settingsManager.Current.MetadataDirectory)
            .Concat(loaded.Where(t => t.RecoveryFilePath != null).Select(t => Path.GetDirectoryName(t.RecoveryFilePath!)!));
        var byId = loaded.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var record in RecoveryStore.Scan(directories).OrderBy(r => r.WrittenUtc)) byId[record.Task.Id] = record.Task;
        foreach (var item in byId.Values.OrderByDescending(t => t.DateAdded)) {
            bool interrupted = item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Queued or DownloadStatus.Assembling or DownloadStatus.Verifying ||
                item.ResumeOnRestart && item.Status is DownloadStatus.Error or DownloadStatus.Paused;
            if (interrupted) {
                item.Status = _settingsManager.Current.ResumeInterruptedDownloads ? DownloadStatus.Queued : DownloadStatus.Paused;
                item.ResumeOnRestart = true;
            }
            item.RecoveryFilePath ??= RecoveryStore.PathFor(_settingsManager.Current.MetadataDirectory, item.Id);
            string legacy = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Temp", "IDMLite");
            if (Path.GetDirectoryName(item.RecoveryFilePath)?.TrimEnd('\\').Equals(legacy, StringComparison.OrdinalIgnoreCase) == true)
            {
                string destination = RecoveryStore.PathFor(_settingsManager.Current.MetadataDirectory, item.Id);
                RecoveryStore.Move(item.RecoveryFilePath, destination);
                item.RecoveryFilePath = destination;
            }
            Tasks.Add(item);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        List<DownloadTask> interrupted;
        lock (_lock) interrupted = Tasks.Where(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Queued or DownloadStatus.Assembling or DownloadStatus.Verifying).ToList();
        PauseAll();
        foreach (var task in interrupted) task.ResumeOnRestart = true;
        SaveQueue();
    }
}

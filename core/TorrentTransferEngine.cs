using System.Diagnostics;
using System.Text.Json;
using MonoTorrent;
using MonoTorrent.Client;

namespace IDMLite.Core;

public sealed record TorrentCheckpoint(Guid Id, string Folder, string[] Selected, long Limit, bool Completed, long Downloaded, bool Publishing = false);

public sealed class TorrentTransferEngine(ClientEngine engine, string storage, Guid id)
{
    private readonly Queue<(long Time, long Bytes)> _samples = new();
    private long _lastByteAt;
    private long _lastReceived;
    private int _recoveries;

    public TorrentManager? Manager { get; private set; }
    public string MetadataPath => Path.Combine(storage, $"{id:N}.torrent");
    private string CheckpointPath => Path.Combine(storage, $"{id:N}.json");

    public static EngineSettings Settings(string cache) => new EngineSettingsBuilder
    {
        CacheDirectory = cache,
        DiskCacheBytes = 32 * 1024 * 1024,
        MaximumHalfOpenConnections = 16,
        StaleRequestTimeout = TimeSpan.FromSeconds(25),
        FastResumeMode = FastResumeMode.Accurate,
        UsePartialFiles = false
    }.ToSettings();

    public void SaveMetadata(byte[] metadata) => RecoveryStore.AtomicWrite(MetadataPath, metadata);

    public void Save(TorrentCheckpoint checkpoint) =>
        RecoveryStore.AtomicWrite(CheckpointPath, JsonSerializer.SerializeToUtf8Bytes(checkpoint));

    public static IEnumerable<TorrentCheckpoint> ReadCheckpoints(string storage)
    {
        if (!Directory.Exists(storage)) yield break;
        foreach (string file in Directory.EnumerateFiles(storage, "*.json"))
        {
            TorrentCheckpoint? checkpoint = null;
            foreach (string candidate in new[] { file, file + ".bak" })
            {
                try { checkpoint = JsonSerializer.Deserialize<TorrentCheckpoint>(File.ReadAllBytes(candidate)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
                if (checkpoint != null) break;
            }
            if (checkpoint is { Selected.Length: > 0 } && Path.IsPathFullyQualified(checkpoint.Folder) &&
                Path.GetFileNameWithoutExtension(file) == checkpoint.Id.ToString("N"))
                yield return checkpoint;
        }
    }

    public async Task StartAsync(Torrent torrent, string folder, HashSet<string> selected, long limit)
    {
        if (Manager == null)
        {
            var manager = await engine.AddAsync(torrent, Path.Combine(folder, "source"), CreateSettings(limit)).ConfigureAwait(false);
            string parts = Path.Combine(folder, "parts");
            Directory.CreateDirectory(parts);
            try
            {
                for (int i = 0; i < manager.Files.Count; i++)
                {
                    var file = manager.Files[i];
                    string staged = Path.Combine(parts, WorkingFileName(i));
                    string publishing = Path.Combine(folder, "payload", file.Path);
                    if (File.Exists(publishing)) File.Move(publishing, staged);
                    await manager.MoveFileAsync(file, staged).ConfigureAwait(false);
                }
                Manager = manager;
            }
            catch
            {
                await engine.RemoveAsync(manager).ConfigureAwait(false);
                throw;
            }
        }
        await Manager.UpdateSettingsAsync(CreateSettings(limit)).ConfigureAwait(false);
        foreach (var file in Manager.Files)
        {
            var priority = selected.Contains(file.Path) ? Priority.Normal : Priority.DoNotDownload;
            if (file.Priority != priority) await Manager.SetFilePriorityAsync(file, priority).ConfigureAwait(false);
        }
        await Manager.StartAsync().ConfigureAwait(false);
        ResetSamples();
        _recoveries = 0;
    }

    private static TorrentSettings CreateSettings(long limit) => new TorrentSettingsBuilder
    {
        CreateContainingDirectory = false,
        MaximumConnections = 90,
        MaximumDownloadRate = (int)Math.Clamp(limit, 0, int.MaxValue)
    }.ToSettings();

    public Task UpdateLimitAsync(long limit) =>
        Manager?.UpdateSettingsAsync(CreateSettings(limit)) ?? Task.CompletedTask;

    private void ResetSamples()
    {
        _samples.Clear();
        _lastByteAt = Stopwatch.GetTimestamp();
        _lastReceived = Manager!.Monitor.DataBytesReceived;
        _samples.Enqueue((_lastByteAt, _lastReceived));
    }

    public double SampleSpeed()
    {
        long now = Stopwatch.GetTimestamp();
        long bytes = Manager!.Monitor.DataBytesReceived;
        if (bytes != _lastReceived)
        {
            _lastByteAt = now;
            _lastReceived = bytes;
            _recoveries = 0;
        }
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 1 && Stopwatch.GetElapsedTime(_samples.Peek().Time, now).TotalSeconds > 6)
            _samples.Dequeue();
        var first = _samples.Peek();
        double seconds = Stopwatch.GetElapsedTime(first.Time, now).TotalSeconds;
        return seconds > 0 ? Math.Max(0, bytes - first.Bytes) / seconds : 0;
    }

    public async Task RetryIfStalledAsync()
    {
        if (Manager?.State != TorrentState.Downloading || _recoveries >= 3 ||
            Stopwatch.GetElapsedTime(_lastByteAt) < TimeSpan.FromSeconds(55 * (_recoveries + 1))) return;

        _recoveries++;
        var peers = (await Manager.GetPeersAsync().ConfigureAwait(false)).Select(p => new PeerInfo(p.Uri)).ToArray();
        await StopAsync().ConfigureAwait(false);
        await Manager.StartAsync().ConfigureAwait(false);
        if (!Manager.Torrent!.IsPrivate && peers.Length > 0) await Manager.AddPeersAsync(peers).ConfigureAwait(false);
        ResetSamples();
    }

    private static string WorkingFileName(int index) => $"IDMLDownload-{index + 1:D6}.idml";

    public void Publish(string folder, string destination, IReadOnlySet<string> selected)
    {
        var files = Manager!.Files;
        string payload = Path.Combine(folder, "payload");
        Directory.CreateDirectory(payload);
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (!selected.Contains(file.Path)) continue;
            string path = Path.Combine(payload, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (file.Length == 0 && !File.Exists(file.FullPath))
            {
                using var empty = File.Create(path);
            }
            else File.Move(file.FullPath, path);
        }
        Directory.Move(payload, destination);
        for (int i = 0; i < files.Count; i++)
            File.Delete(Path.Combine(folder, "parts", WorkingFileName(i)));
        string parts = Path.Combine(folder, "parts");
        if (!Directory.EnumerateFileSystemEntries(parts).Any()) Directory.Delete(parts);
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    public Task StopAsync() => Manager?.StopAsync(TimeSpan.FromSeconds(2)) ?? Task.CompletedTask;

    public async Task DetachAsync()
    {
        if (Manager == null) return;
        if (Manager.State != TorrentState.Stopped) await StopAsync().ConfigureAwait(false);
        await engine.RemoveAsync(Manager).ConfigureAwait(false);
        Manager = null;
    }

    public void DeleteCheckpoint()
    {
        foreach (string path in new[] { CheckpointPath, CheckpointPath + ".bak", MetadataPath, MetadataPath + ".bak" })
            File.Delete(path);
    }
}

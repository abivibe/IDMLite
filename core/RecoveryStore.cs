using System.Security.Cryptography;
using System.Text.Json;

namespace IDMLite.Core;

public sealed class RecoveryRecord
{
    public int Version { get; set; } = 1;
    public DateTime WrittenUtc { get; set; }
    public DownloadTask Task { get; set; } = new();
    public DownloadMetadata? Http { get; set; }
}

/// <summary>One encrypted, atomically replaced recovery record per download.</summary>
public static class RecoveryStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Moves = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Deleted = new(StringComparer.OrdinalIgnoreCase);
    private sealed record SavedTask(byte[] Fingerprint, long Length, DateTime WrittenUtc);
    private static readonly Dictionary<string, SavedTask> SavedTasks = new(StringComparer.OrdinalIgnoreCase);
    private static string Resolve(string path) { while (Moves.TryGetValue(path, out var next)) path = next; return path; }

    public static string PathFor(string directory, Guid id) => Path.Combine(directory, id.ToString("N") + ".idmlitemeta");

    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            foreach (string candidate in new[] { path, path + ".bak" })
                if (File.Exists(candidate) && File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Linked recovery files are not supported.");
            if (File.Exists(path)) File.Replace(pending, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(pending, path);
        }
        finally { File.Delete(pending); }
    }

    private static RecoveryRecord? Read(string path) => Read(path, out _);
    private static RecoveryRecord? Read(string path, out bool validPrimary)
    {
        validPrimary = false;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recovery records require Windows user data protection.");
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            try {
                if (!File.Exists(candidate)) continue;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(candidate), null, DataProtectionScope.CurrentUser);
                var record = JsonSerializer.Deserialize<RecoveryRecord>(plain);
                if (record?.Version == 1 && record.Task.Id != Guid.Empty) { validPrimary = candidate == path; return record; }
            } catch { }
        }
        return null;
    }

    private static void Write(string path, RecoveryRecord record)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recovery records require Windows user data protection.");
        if (Deleted.Contains(path)) return;
        record.WrittenUtc = DateTime.UtcNow;
        PrivateStorage.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicWrite(path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(record), null, DataProtectionScope.CurrentUser));
    }

    public static RecoveryRecord? Load(string path) { lock (Gate) return Read(Resolve(path)); }
    public static void SaveTask(DownloadTask task)
    {
        if (string.IsNullOrWhiteSpace(task.RecoveryFilePath)) return;
        lock (Gate) {
            string path = Resolve(task.RecoveryFilePath);
            if (Deleted.Contains(path)) return;
            byte[] fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(task));
            var file = new FileInfo(path);
            if (file.Exists && SavedTasks.TryGetValue(path, out var previous) && fingerprint.SequenceEqual(previous.Fingerprint) &&
                file.Length == previous.Length && file.LastWriteTimeUtc == previous.WrittenUtc) return;
            var record = Read(path, out bool validPrimary) ?? new RecoveryRecord();
            if (validPrimary && fingerprint.SequenceEqual(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record.Task)))) {
                SavedTasks[path] = new(fingerprint, file.Length, file.LastWriteTimeUtc); return;
            }
            record.Task = task;
            Write(path, record);
            file.Refresh();
            SavedTasks[path] = new(fingerprint, file.Length, file.LastWriteTimeUtc);
        }
    }
    public static void SaveHttp(string path, DownloadMetadata metadata)
    {
        lock (Gate) {
            path = Resolve(path);
            var record = Read(path);
            if (record == null) return;
            record.Http = metadata;
            Write(path, record);
        }
    }
    public static void Move(string source, string destination)
    {
        lock (Gate) {
            source = Resolve(source);
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return;
            var record = Read(source);
            if (record != null) {
                record.Task.RecoveryFilePath = destination;
                Write(destination, record);
            }
            Moves[source] = destination;
            try { File.Delete(source); File.Delete(source + ".bak"); } catch (UnauthorizedAccessException) { }
        }
    }
    public static void Delete(string path)
    {
        lock (Gate) { path = Resolve(path); Deleted.Add(path); SavedTasks.Remove(path); File.Delete(path); File.Delete(path + ".bak"); }
    }
    public static List<RecoveryRecord> Scan(IEnumerable<string> directories)
    {
        lock (Gate) {
            var records = new List<RecoveryRecord>();
            foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase)) {
                try {
                    if (!Directory.Exists(directory)) continue;
                    foreach (string path in Directory.EnumerateFiles(directory, "*.idmlitemeta")) {
                        var record = Read(path);
                        if (record != null) { record.Task.RecoveryFilePath = path; records.Add(record); }
                    }
                } catch { }
            }
            return records;
        }
    }
}

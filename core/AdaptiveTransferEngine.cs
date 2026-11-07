using System.Collections.Concurrent;

namespace IDMLite.Core;

/// <summary>Bounded workers pull the next available range instead of waiting for a fixed slow connection.</summary>
public sealed class AdaptiveTransferEngine
{
    private readonly ConcurrentDictionary<int, DownloadSegment> _active = new();
    public List<DownloadSegment> ActiveSegments => _active.Values.OrderBy(s => s.Index).ToList();

    public static List<DownloadSegment> Plan(long size, int connections)
    {
        if (size <= 0) return new() { new(0, 0, 0) };
        connections = Math.Clamp(connections, 1, 32);
        if (connections == 1) return new() { new(0, 0, size - 1) };
        long partSize = Math.Clamp((size - 1) / (connections * 4) + 1, 256 * 1024, 4 * 1024 * 1024);
        partSize = Math.Max(partSize, (size - 1) / 1024 + 1);
        var result = new List<DownloadSegment>();
        for (long start = 0; start < size; start += partSize)
            result.Add(new(result.Count, start, Math.Min(size - 1, start + partSize - 1)));
        return result;
    }

    public Task TransferAsync(IEnumerable<DownloadSegment> segments, int connections,
        Func<DownloadSegment, CancellationToken, Task> transfer, CancellationToken ct) =>
        Parallel.ForEachAsync(segments.Where(s => !s.IsCompleted), new ParallelOptions {
            MaxDegreeOfParallelism = Math.Clamp(connections, 1, 32), CancellationToken = ct
        }, async (segment, workerToken) => {
            _active[segment.Index] = segment;
            try { await transfer(segment, workerToken).ConfigureAwait(false); }
            finally { segment.CurrentSpeedBytesPerSec = 0; _active.TryRemove(segment.Index, out _); }
        });
}

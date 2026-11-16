namespace IDMLite.Core;

/// <summary>Plans a bounded set of logical parts and transfers them concurrently.</summary>
public sealed class AdaptiveTransferEngine
{
    public static List<DownloadSegment> Plan(long size, int connections)
    {
        if (size <= 0) return new() { new(0, 0, 0) };
        const int targetMinimumPartSize = 256 * 1024;
        int count = (int)Math.Min(Math.Clamp(connections, 1, 32), (size - 1) / targetMinimumPartSize + 1);
        long partSize = size / count;
        long extraBytes = size % count;
        var result = new List<DownloadSegment>(count);
        long start = 0;
        for (int index = 0; index < count; index++)
        {
            long end = start + partSize + (index < extraBytes ? 1 : 0);
            result.Add(new(index, start, end - 1));
            start = end;
        }
        return result;
    }

    public Task TransferAsync(IEnumerable<DownloadSegment> segments, int connections,
        Func<DownloadSegment, CancellationToken, Task> transfer, CancellationToken ct) =>
        Parallel.ForEachAsync(segments.Where(s => !s.IsCompleted), new ParallelOptions {
            MaxDegreeOfParallelism = Math.Clamp(connections, 1, 32), CancellationToken = ct
        }, (segment, workerToken) => new ValueTask(transfer(segment, workerToken)));
}

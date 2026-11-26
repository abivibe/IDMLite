using System.Diagnostics;

namespace IDMLite.Core;

public class SpeedLimiter
{
    private readonly object _lock = new();
    private long _maxBytesPerSecond;
    private double _availableTokens;
    private long _lastTicks;
    private TaskCompletionSource _limitChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SpeedLimiter? Parent { get; init; }
    public long EffectiveLimit
    {
        get
        {
            long own = MaxBytesPerSecond;
            long parent = Parent?.EffectiveLimit ?? 0;
            return own == 0 ? parent : parent == 0 ? own : Math.Min(own, parent);
        }
    }

    public long MaxBytesPerSecond
    {
        get => Interlocked.Read(ref _maxBytesPerSecond);
        set
        {
            lock (_lock)
            {
                if (_maxBytesPerSecond == Math.Max(0, value)) return;
                _limitChanged.TrySetResult();
                _limitChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _maxBytesPerSecond, Math.Max(0, value));
                _availableTokens = _maxBytesPerSecond / 4.0;
                _lastTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    public SpeedLimiter(long maxBytesPerSecond = 0)
    {
        MaxBytesPerSecond = maxBytesPerSecond;
        _lastTicks = Stopwatch.GetTimestamp();
    }

    public async Task ThrottleAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        if (byteCount <= 0) return;
        TimeSpan delay = TimeSpan.Zero;
        Task limitChanged;
        lock (_lock)
        {
            long limit = _maxBytesPerSecond;
            if (limit > 0)
            {
                long now = Stopwatch.GetTimestamp();
                double seconds = Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds;
                _lastTicks = now;
                _availableTokens = Math.Min(limit / 4.0, _availableTokens + seconds * limit) - byteCount;
                if (_availableTokens < 0) delay = TimeSpan.FromSeconds(-_availableTokens / limit);
            }
            limitChanged = _limitChanged.Task;
        }

        if (delay > TimeSpan.Zero)
        {
            try { await limitChanged.WaitAsync(delay, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        if (Parent != null) await Parent.ThrottleAsync(byteCount, cancellationToken).ConfigureAwait(false);
    }
}

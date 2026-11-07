using System.Diagnostics;

namespace IDMLite.Core;

public class SpeedLimiter
{
    private readonly object _lock = new();
    private long _maxBytesPerSecond;
    private double _availableTokens;
    private long _lastTicks;

    public long MaxBytesPerSecond
    {
        get => Interlocked.Read(ref _maxBytesPerSecond);
        set
        {
            lock (_lock)
            {
                Interlocked.Exchange(ref _maxBytesPerSecond, Math.Max(0, value));
                _availableTokens = _maxBytesPerSecond;
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
        long limit = MaxBytesPerSecond;
        if (limit <= 0 || byteCount <= 0)
        {
            return;
        }

        double secondsToWait = 0;
        lock (_lock)
        {
            long currentTicks = Stopwatch.GetTimestamp();
            double elapsedSeconds = (double)(currentTicks - _lastTicks) / Stopwatch.Frequency;
            _lastTicks = currentTicks;

            // Refill tokens based on elapsed time, capped at 1 second of bandwidth
            _availableTokens = Math.Min(limit, _availableTokens + (elapsedSeconds * limit));

            // Subtract consumed bytes
            _availableTokens -= byteCount;

            // If deficit, calculate required wait time
            if (_availableTokens < 0)
            {
                secondsToWait = -_availableTokens / limit;
            }
        }

        if (secondsToWait > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(secondsToWait, 5.0)), cancellationToken).ConfigureAwait(false);
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace IDMLite.Core;

public class DownloadSegment : INotifyPropertyChanged
{
    private static readonly long NotifyIntervalTicks = (long)(0.35 * Stopwatch.Frequency);
    private long _lastNotifyTicks = 0;

    private int _index;
    private long _startByte;
    private long _endByte;
    private long _currentByte;
    private bool _isCompleted;
    private double _currentSpeedBytesPerSec;

    public int Index
    {
        get => _index;
        set => SetField(ref _index, value);
    }

    public long StartByte
    {
        get => _startByte;
        set
        {
            if (SetField(ref _startByte, value))
            {
                NotifyProgress(force: true);
            }
        }
    }

    public long EndByte
    {
        get => _endByte;
        set
        {
            if (SetField(ref _endByte, value))
            {
                NotifyProgress(force: true);
            }
        }
    }

    public long CurrentByte
    {
        get => _currentByte;
        set
        {
            if (_currentByte != value)
            {
                _currentByte = value;
                // Throttled notification: don't flood WPF Dispatcher on every single 64KB network buffer
                NotifyProgress(force: false);
            }
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (SetField(ref _isCompleted, value))
            {
                NotifyProgress(force: true);
            }
        }
    }

    [JsonIgnore]
    public double CurrentSpeedBytesPerSec
    {
        get => _currentSpeedBytesPerSec;
        set
        {
            if (Math.Abs(_currentSpeedBytesPerSec - value) > 1e-3)
            {
                _currentSpeedBytesPerSec = value;
                OnPropertyChanged(nameof(SpeedMegabytesFormatted));
            }
        }
    }

    [JsonIgnore]
    public long TotalBytes => EndByte >= StartByte ? EndByte - StartByte + 1 : 0;

    [JsonIgnore]
    public long DownloadedBytes => Math.Max(0, CurrentByte - StartByte);

    [JsonIgnore]
    public string DownloadedMegabytesFormatted => $"{(DownloadedBytes / (1024.0 * 1024.0)):0.00} MB";

    [JsonIgnore]
    public string SpeedMegabytesFormatted => $"{(CurrentSpeedBytesPerSec / (1024.0 * 1024.0)):0.00} MB/s";

    [JsonIgnore]
    public double ProgressPercentage
    {
        get
        {
            if (IsCompleted) return 100.0;
            if (TotalBytes <= 0) return 0.0;
            return Math.Clamp((double)DownloadedBytes / TotalBytes * 100.0, 0.0, 100.0);
        }
        set { }
    }

    public void NotifyProgress(bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && (now - _lastNotifyTicks) < NotifyIntervalTicks)
        {
            return;
        }
        _lastNotifyTicks = now;

        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(DownloadedBytes));
        OnPropertyChanged(nameof(DownloadedMegabytesFormatted));
        OnPropertyChanged(nameof(ProgressPercentage));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public DownloadSegment() { }

    public DownloadSegment(int index, long startByte, long endByte)
    {
        Index = index;
        StartByte = startByte;
        EndByte = endByte;
        CurrentByte = startByte;
        IsCompleted = false;
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using IDMLite.Core;
using IDMLite.Services;

namespace IDMLite.Models;

public class DownloadItemViewModel : INotifyPropertyChanged
{
    private readonly DownloadTask _task;

    public DownloadTask Task => _task;
    public Guid Id => _task.Id;
    public string Url => _task.Url;
    public string FileName => _task.FileName;
    public ImageSource? FileIcon => WindowsFileIcon.Get(FileName, Status == DownloadStatus.Completed ? FullPath : null);
    public string FullPath => _task.FullPath;
    public DownloadCategory Category => _task.Category;
    public int ConnectionCount => _task.ConnectionCount;

    public DownloadStatus Status => _task.Status;

    public string StatusText
    {
        get
        {
            var loc = LocalizationManager.Instance;
            return _task.Status switch
            {
                DownloadStatus.Queued => loc.Get("Str_Connecting", "Queued"),
                DownloadStatus.Connecting => loc.Get("Str_Connecting", "Connecting..."),
                DownloadStatus.Downloading => loc.Get("Str_Downloading", "Downloading"),
                DownloadStatus.Paused => loc.Get("Str_Paused", "Paused"),
                DownloadStatus.Completed => _task.IsVerified ? $"✅ {loc.Get("Str_Completed", "Completed")} ({loc.Get("Str_Verified", "Verified")})" : loc.Get("Str_Completed", "Completed"),
                DownloadStatus.Error => $"{loc.Get("Str_Error", "Error:")} " + (_task.ErrorMessage ?? loc.Get("Str_Unknown", "Unknown")),
                DownloadStatus.Assembling => loc.Get("Str_Assembling", "Assembling..."),
                DownloadStatus.Verifying => $"{loc.Get("Str_Verifying", "Verifying")} ({_task.VerificationProgress:0.#}%)",
                _ => _task.Status.ToString()
            };
        }
    }

    public System.Windows.Media.Brush StatusBrush
    {
        get
        {
            return _task.Status switch
            {
                DownloadStatus.Downloading => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 113, 227)),
                DownloadStatus.Completed => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 199, 89)),
                DownloadStatus.Paused => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 149, 0)),
                DownloadStatus.Error => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 59, 48)),
                DownloadStatus.Verifying => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 68, 173)),
                _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 110, 115))
            };
        }
    }

    public double ProgressPercentage
    {
        get => _task.ProgressPercentage;
        set { }
    }

    public long TotalSize => _task.TotalSize;
    public long DownloadedSize => _task.DownloadedSize;

    public string TotalSizeFormatted => _task.TotalSize > 0 
        ? FormatBytes(_task.TotalSize) 
        : (_task.Status == DownloadStatus.Completed ? FormatBytes(_task.DownloadedSize) : LocalizationManager.Instance.Get("Str_DynamicStream", "Dynamic Stream"));
    public string DownloadedSizeFormatted => FormatBytes(_task.DownloadedSize);

    public string SpeedFormatted
    {
        get
        {
            if (_task.Status != DownloadStatus.Downloading && _task.Status != DownloadStatus.Assembling)
                return "-";
            return $"{(_task.SpeedBytesPerSec / (1024.0 * 1024.0)):0.00} MB/s";
        }
    }

    public string TimeRemainingFormatted
    {
        get
        {
            if (_task.Status != DownloadStatus.Downloading || !_task.TimeRemaining.HasValue)
                return "-";

            var t = _task.TimeRemaining.Value;
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}h {t.Minutes}m";
            if (t.TotalMinutes >= 1)
                return $"{t.Minutes}m {t.Seconds}s";
            return $"{Math.Max(0, t.Seconds)}s";
        }
    }

    public string DateAddedFormatted => _task.DateAdded.ToString("dd.MM.yyyy HH:mm");

    public string CategoryText => _task.Category switch
    {
        DownloadCategory.Compressed => LocalizationManager.Instance.Get("Str_Compressed", "Compressed"),
        DownloadCategory.Documents => LocalizationManager.Instance.Get("Str_Documents", "Documents"),
        DownloadCategory.Music => LocalizationManager.Instance.Get("Str_Music", "Music"),
        DownloadCategory.Video => LocalizationManager.Instance.Get("Str_Video", "Video"),
        DownloadCategory.Programs => LocalizationManager.Instance.Get("Str_Programs", "Programs"),
        _ => LocalizationManager.Instance.Get("Str_Other", "General")
    };

    public DownloadItemViewModel(DownloadTask task)
    {
        _task = task;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ProgressPercentage));
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(DownloadedSize));
        OnPropertyChanged(nameof(TotalSizeFormatted));
        OnPropertyChanged(nameof(DownloadedSizeFormatted));
        OnPropertyChanged(nameof(SpeedFormatted));
        OnPropertyChanged(nameof(TimeRemainingFormatted));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileIcon));
        OnPropertyChanged(nameof(FullPath));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < suffixes.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.##} {suffixes[i]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

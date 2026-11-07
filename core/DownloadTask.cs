using System.Text.Json.Serialization;

namespace IDMLite.Core;

public class DownloadTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? OriginRequestId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SaveDirectory { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public long DownloadedSize { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DownloadCategory Category { get; set; } = DownloadCategory.Other;
    public int ConnectionCount { get; set; } = 8;
    public DateTime DateAdded { get; set; } = DateTime.Now;

    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookies { get; set; }
    public string? FallbackStreamUrl { get; set; }
    public Dictionary<string, string>? CustomHeaders { get; set; }
    public bool IsDirect { get; set; }
    public string? RecoveryFilePath { get; set; }
    public bool ResumeOnRestart { get; set; }
    public DateTime? RetryAfterUtc { get; set; }
    public int RetryCount { get; set; }
    public string? ExpectedChecksum { get; set; }
    public string? ActualChecksum { get; set; }
    public string? ChecksumType { get; set; } = "SHA256";
    public bool IsVerified { get; set; }
    public string? ErrorMessage { get; set; }

    [JsonIgnore]
    public string FullPath
    {
        get
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SaveDirectory)) return FileName;
                if (string.IsNullOrWhiteSpace(FileName)) return SaveDirectory;
                return Path.Combine(SaveDirectory, FileName);
            }
            catch
            {
                return $"{SaveDirectory}\\{FileName}";
            }
        }
    }

    [JsonIgnore]
    public double SpeedBytesPerSec { get; set; }

    [JsonIgnore]
    public TimeSpan? TimeRemaining { get; set; }

    [JsonIgnore]
    public double VerificationProgress { get; set; }

    [JsonIgnore]
    public double ProgressPercentage
    {
        get
        {
            if (Status == DownloadStatus.Completed) return 100.0;
            if (Status == DownloadStatus.Verifying) return VerificationProgress;
            if (TotalSize <= 0) return 0.0;
            return Math.Clamp((double)DownloadedSize / TotalSize * 100.0, 0.0, 100.0);
        }
        set { }
    }

    [JsonIgnore]
    public List<DownloadSegment> Segments { get; set; } = new();

    [JsonIgnore]
    public SegmentedDownloader? Downloader { get; set; }

    [JsonIgnore]
    public HlsDownloader? HlsEngine { get; set; }

    [JsonIgnore]
    public YtDlpDownloader? YtDlpEngine { get; set; }

    [JsonIgnore]
    public UniversalVideoDownloader? UniversalVideoEngine { get; set; }

    public bool IsHls => Url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                         Url.Contains(".m3u8?", StringComparison.OrdinalIgnoreCase);

    public bool IsSocialVideo => Services.MediaUrl.IsSocialPage(Url) ||
        (Services.MediaUrl.HasHost(Url, "googlevideo.com") && Services.MediaUrl.IsSocialPage(Referer));
}

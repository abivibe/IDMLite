namespace IDMLite.Core;

public enum DownloadStatus
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Completed,
    Error,
    Assembling,
    Verifying
}

public enum DownloadCategory
{
    All,
    Documents,
    Compressed,
    Music,
    Video,
    Programs,
    Other
}

public static class CategoryDetector
{
    private static readonly HashSet<string> DocumentExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".epub", ".csv"
    };

    private static readonly HashSet<string> CompressedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".tgz", ".cab", ".dmg"
    };

    private static readonly HashSet<string> MusicExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".alac"
    };

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".vob", ".m4v", ".3gp", ".m3u8", ".ts"
    };

    private static readonly HashSet<string> ProgramExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".sh", ".apk", ".deb", ".rpm", ".appimage", ".jar"
    };

    public static DownloadCategory FromExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return DownloadCategory.Other;
        if (!extension.StartsWith('.')) extension = "." + extension;

        if (DocumentExts.Contains(extension)) return DownloadCategory.Documents;
        if (CompressedExts.Contains(extension)) return DownloadCategory.Compressed;
        if (MusicExts.Contains(extension)) return DownloadCategory.Music;
        if (VideoExts.Contains(extension)) return DownloadCategory.Video;
        if (ProgramExts.Contains(extension)) return DownloadCategory.Programs;

        return DownloadCategory.Other;
    }
}

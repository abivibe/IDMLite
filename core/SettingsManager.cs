using System.Text.Json;

namespace IDMLite.Core;

public class AppSettings
{
    public int MaxConnectionsPerDownload { get; set; } = 8;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public long SpeedLimitBytesPerSec { get; set; } = 0;
    public bool SpeedLimitEnabled { get; set; } = false;
    public string DefaultDownloadDirectory { get; set; } = string.Empty;
    public Dictionary<DownloadCategory, string> CategoryDirectories { get; set; } = new();
    public int IpcPort { get; set; } = 41718;
    public string IpcToken { get; set; } = Guid.NewGuid().ToString("N");
    public string Theme { get; set; } = "Dark"; // AMOLED Dark Mode by default
    public string Language { get; set; } = "en"; // English by default
    public bool StartWithWindows { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool AutoStartDownloads { get; set; } = false;
    public bool ShowDownloadDialog { get; set; } = true;
    public bool PlaySoundOnComplete { get; set; } = true;
    public bool ShowNotificationOnComplete { get; set; } = true;
    public string MetadataDirectory { get; set; } = string.Empty;
    public List<string> KnownMetadataDirectories { get; set; } = new();
    public bool ResumeInterruptedDownloads { get; set; } = true;
}

public class SettingsManager
{
    public string StorageDirectory { get; }
    private string AppDataFolder => StorageDirectory;
    private string SettingsFile => Path.Combine(StorageDirectory, "settings.json");

    public AppSettings Current { get; private set; }

    public SettingsManager(string? storageDirectory = null)
    {
        StorageDirectory = storageDirectory ?? Environment.GetEnvironmentVariable("IDMLITE_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IDMLite");
        PrivateStorage.EnsureDirectory(StorageDirectory);
        Current = Load();
    }

    public AppSettings Load()
    {
        foreach (string candidate in new[] { SettingsFile, SettingsFile + ".bak" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate));
                    if (settings != null) { EnsureDefaultPaths(settings); Save(settings); return settings; }
                }
            }
            catch { }
        }

        var def = new AppSettings();
        EnsureDefaultPaths(def);
        Save(def);
        return def;
    }

    public void Save(AppSettings? settings = null)
    {
        try
        {
            if (settings != null) Current = settings;
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }
            string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            RecoveryStore.AtomicWrite(SettingsFile, System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch { }
    }

    private void EnsureDefaultPaths(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.IpcToken) || settings.IpcToken.Length < 32)
            settings.IpcToken = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(settings.MetadataDirectory))
            settings.MetadataDirectory = Environment.GetEnvironmentVariable("IDMLITE_DATA_DIR") != null
                ? Path.Combine(StorageDirectory, "Recovery")
                : Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Temp", "IDMLite", PrivateStorage.UserId);
        string legacyMetadata = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Temp", "IDMLite");
        if (Path.GetFullPath(settings.MetadataDirectory).TrimEnd('\\').Equals(legacyMetadata, StringComparison.OrdinalIgnoreCase))
        {
            settings.KnownMetadataDirectories ??= new();
            settings.KnownMetadataDirectories.Add(legacyMetadata);
            settings.MetadataDirectory = Path.Combine(legacyMetadata, PrivateStorage.UserId);
        }
        settings.KnownMetadataDirectories ??= new();
        string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (string.IsNullOrWhiteSpace(settings.DefaultDownloadDirectory))
        {
            settings.DefaultDownloadDirectory = userDownloads;
        }

        if (settings.CategoryDirectories == null)
        {
            settings.CategoryDirectories = new();
        }

        EnsureCategoryPath(settings, DownloadCategory.Documents, Path.Combine(userDownloads, "Documents"));
        EnsureCategoryPath(settings, DownloadCategory.Compressed, Path.Combine(userDownloads, "Compressed"));
        EnsureCategoryPath(settings, DownloadCategory.Music, Path.Combine(userDownloads, "Music"));
        EnsureCategoryPath(settings, DownloadCategory.Video, Path.Combine(userDownloads, "Video"));
        EnsureCategoryPath(settings, DownloadCategory.Programs, Path.Combine(userDownloads, "Programs"));
        EnsureCategoryPath(settings, DownloadCategory.Other, Path.Combine(userDownloads, "General"));
    }

    private void EnsureCategoryPath(AppSettings settings, DownloadCategory cat, string defaultPath)
    {
        if (!settings.CategoryDirectories.ContainsKey(cat) || string.IsNullOrWhiteSpace(settings.CategoryDirectories[cat]))
        {
            settings.CategoryDirectories[cat] = defaultPath;
        }
    }

    public string GetDirectoryForCategory(DownloadCategory category)
    {
        if (Current.CategoryDirectories.TryGetValue(category, out var dir) && !string.IsNullOrWhiteSpace(dir))
        {
            return dir;
        }
        return Current.DefaultDownloadDirectory;
    }
}

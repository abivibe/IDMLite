using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IDMLite.Core;
using IDMLite.Services;
using Microsoft.Win32;

namespace IDMLite.Windows;

public partial class SettingsDialog : Window
{
    private readonly SettingsManager _settingsManager;
    private readonly DownloadQueueManager _queueManager;

    public SettingsDialog(SettingsManager settingsManager, DownloadQueueManager queueManager)
    {
        InitializeComponent();
        _settingsManager = settingsManager;
        _queueManager = queueManager;

        LoadSettingsToUI();
    }

    private void LoadSettingsToUI()
    {
        var s = _settingsManager.Current;

        // Language
        CmbLanguage.ItemsSource = LocalizationManager.SupportedLanguages;
        string currentLang = s.Language ?? LocalizationManager.Instance.CurrentLanguage;
        var selectedLang = LocalizationManager.SupportedLanguages.FirstOrDefault(l => l.Code.Equals(currentLang, StringComparison.OrdinalIgnoreCase))
                           ?? LocalizationManager.SupportedLanguages[0];
        CmbLanguage.SelectedItem = selectedLang;

        SldConnections.Value = s.MaxConnectionsPerDownload;
        SldConcurrent.Value = s.MaxConcurrentDownloads;

        ChkSpeedLimit.IsChecked = s.SpeedLimitEnabled;
        if (s.SpeedLimitBytesPerSec >= 1024 * 1024)
        {
            TxtSpeedLimitValue.Text = (s.SpeedLimitBytesPerSec / (1024.0 * 1024.0)).ToString("0.#");
            CmbSpeedUnit.SelectedIndex = 1; // MB/s
        }
        else
        {
            TxtSpeedLimitValue.Text = (s.SpeedLimitBytesPerSec / 1024.0).ToString("0.#");
            CmbSpeedUnit.SelectedIndex = 0; // KB/s
        }

        ChkCloseToTray.IsChecked = s.CloseToTray;
        ChkNotification.IsChecked = s.ShowNotificationOnComplete;
        ChkSound.IsChecked = s.PlaySoundOnComplete;

        TxtDirDefault.Text = s.DefaultDownloadDirectory;
        TxtMetadataDirectory.Text = s.MetadataDirectory;
        ChkResumeInterrupted.IsChecked = s.ResumeInterruptedDownloads;
        TxtDirDocuments.Text = s.CategoryDirectories.GetValueOrDefault(DownloadCategory.Documents, "");
        TxtDirCompressed.Text = s.CategoryDirectories.GetValueOrDefault(DownloadCategory.Compressed, "");
        TxtDirMusic.Text = s.CategoryDirectories.GetValueOrDefault(DownloadCategory.Music, "");
        TxtDirVideo.Text = s.CategoryDirectories.GetValueOrDefault(DownloadCategory.Video, "");
        TxtDirPrograms.Text = s.CategoryDirectories.GetValueOrDefault(DownloadCategory.Programs, "");

        TxtIpcPort.Text = s.IpcPort.ToString();

        switch (s.Theme?.ToLowerInvariant())
        {
            case "light":
                RbThemeLight.IsChecked = true;
                break;
            case "dark":
                RbThemeDark.IsChecked = true;
                break;
            default:
                RbThemeDark.IsChecked = true;
                break;
        }

        UpdateSpeedLimitControlsState();
    }

    private void ChkSpeedLimit_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSpeedLimitControlsState();
    }

    private void UpdateSpeedLimitControlsState()
    {
        bool enabled = ChkSpeedLimit.IsChecked == true;
        TxtSpeedLimitValue.IsEnabled = enabled;
        CmbSpeedUnit.IsEnabled = enabled;
    }

    private void BtnBrowseDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var dialog = new OpenFolderDialog { Title = "Klasör Seçin" };
            if (dialog.ShowDialog() == true)
            {
                switch (tag)
                {
                    case "Recovery": TxtMetadataDirectory.Text = dialog.FolderName; break;
                    case "Default": TxtDirDefault.Text = dialog.FolderName; break;
                    case "Documents": TxtDirDocuments.Text = dialog.FolderName; break;
                    case "Compressed": TxtDirCompressed.Text = dialog.FolderName; break;
                    case "Music": TxtDirMusic.Text = dialog.FolderName; break;
                    case "Video": TxtDirVideo.Text = dialog.FolderName; break;
                    case "Programs": TxtDirPrograms.Text = dialog.FolderName; break;
                }
            }
        }
    }

    private void RbTheme_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        string theme = "Dark";
        if (RbThemeLight.IsChecked == true) theme = "Light";
        else if (RbThemeDark.IsChecked == true) theme = "Dark";
        else if (RbThemeSystem.IsChecked == true) theme = "System";

        App.ApplyTheme(theme);
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (CmbLanguage.SelectedItem is LanguageOption opt)
        {
            LocalizationManager.Instance.SetLanguage(opt.Code);
            _settingsManager.Current.Language = opt.Code;
            _settingsManager.Save();
        }
    }

    private void BtnRegisterNativeHost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RegisterNativeMessagingHost();
            MessageBox.Show("Chrome, Edge, Brave ve Firefox için Native Messaging Host kayıtları başarıyla eklendi!", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Kayıt Defteri yazılırken hata: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static void RegisterNativeMessagingHost()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string exePath = Path.Combine(baseDir, "IDMLite.exe");
        string manifestDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IDMLite", "NativeMessagingHost");
        Directory.CreateDirectory(manifestDir);

        string chromeManifest = Path.Combine(manifestDir, "com.idmlite.nativehost.json");
        string chromeJson = $$"""
{
  "name": "com.idmlite.nativehost",
  "description": "IDMLite Native Messaging Host for Chromium Browsers",
  "path": "{{exePath.Replace("\\", "\\\\")}}",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://gdcnkipjepgidojhonbcfpgagchjcdci/"
  ]
}
""";
        File.WriteAllText(chromeManifest, chromeJson);

        // Registry paths for Chromium Native Messaging Host
        string[] chromeRegRoots = {
            @"Software\Google\Chrome\NativeMessagingHosts\com.idmlite.nativehost",
            @"Software\Microsoft\Edge\NativeMessagingHosts\com.idmlite.nativehost",
            @"Software\BraveSoftware\Brave\NativeMessagingHosts\com.idmlite.nativehost",
            @"Software\Chromium\NativeMessagingHosts\com.idmlite.nativehost",
            @"Software\Yandex\YandexBrowser\NativeMessagingHosts\com.idmlite.nativehost"
        };

        foreach (var regSub in chromeRegRoots)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(regSub);
                key.SetValue("", chromeManifest);
            }
            catch { }
        }

        string extId = "gdcnkipjepgidojhonbcfpgagchjcdci";

        // Clean up legacy sideloaded registry keys that cause Chrome to lock off-store extensions
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Google\Chrome\Extensions\{extId}", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Edge\Extensions\{extId}", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\BraveSoftware\Brave\Extensions\{extId}", false); } catch { }

        // Firefox manifest & registry
        string firefoxManifest = Path.Combine(manifestDir, "com.idmlite.nativehost.firefox.json");
        string firefoxJson = $$"""
{
  "name": "com.idmlite.nativehost",
  "description": "IDMLite Native Messaging Host for Firefox",
  "path": "{{exePath.Replace("\\", "\\\\")}}",
  "type": "stdio",
  "allowed_extensions": [
    "idmlite@extension"
  ]
}
""";
        File.WriteAllText(firefoxManifest, firefoxJson);

        try
        {
            using var ffKey = Registry.CurrentUser.CreateSubKey(@"Software\Mozilla\NativeMessagingHosts\com.idmlite.nativehost");
            ffKey.SetValue("", firefoxManifest);
        }
        catch { }
    }

    private void BtnQuickExtension_Click(object sender, RoutedEventArgs e)
    {
        OpenBrowserExtensionHelper(this);
    }

    public static void OpenBrowserExtensionHelper(Window? owner = null)
        => BrowserExtensionSetup.Show(owner);

    private void BtnOpenExtensionFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string extDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extension");
            if (!Directory.Exists(extDir))
            {
                extDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\extension"));
            }

            if (Directory.Exists(extDir))
            {
                Process.Start("explorer.exe", extDir);
            }
            else
            {
                MessageBox.Show("Eklenti dizini bulunamadı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Klasör açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var s = _settingsManager.Current;
        try {
            if (string.IsNullOrWhiteSpace(TxtMetadataDirectory.Text) || !Path.IsPathFullyQualified(TxtMetadataDirectory.Text))
                throw new ArgumentException("Kurtarma klasörü için tam bir klasör yolu girin.");
            if (!Path.GetFullPath(TxtMetadataDirectory.Text).Equals(s.MetadataDirectory, StringComparison.OrdinalIgnoreCase))
                _queueManager.ChangeMetadataDirectory(TxtMetadataDirectory.Text);
        } catch (Exception error) {
            MessageBox.Show($"Kurtarma klasörü kaydedilemedi: {error.Message}", "IDMLite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        s.ResumeInterruptedDownloads = ChkResumeInterrupted.IsChecked == true;

        s.MaxConnectionsPerDownload = (int)SldConnections.Value;
        s.MaxConcurrentDownloads = (int)SldConcurrent.Value;

        s.SpeedLimitEnabled = ChkSpeedLimit.IsChecked == true;
        if (double.TryParse(TxtSpeedLimitValue.Text, out double speedVal))
        {
            s.SpeedLimitBytesPerSec = CmbSpeedUnit.SelectedIndex == 1
                ? (long)(speedVal * 1024 * 1024)
                : (long)(speedVal * 1024);
        }

        s.CloseToTray = ChkCloseToTray.IsChecked == true;
        s.ShowNotificationOnComplete = ChkNotification.IsChecked == true;
        s.PlaySoundOnComplete = ChkSound.IsChecked == true;

        s.DefaultDownloadDirectory = TxtDirDefault.Text;
        s.CategoryDirectories[DownloadCategory.Documents] = TxtDirDocuments.Text;
        s.CategoryDirectories[DownloadCategory.Compressed] = TxtDirCompressed.Text;
        s.CategoryDirectories[DownloadCategory.Music] = TxtDirMusic.Text;
        s.CategoryDirectories[DownloadCategory.Video] = TxtDirVideo.Text;
        s.CategoryDirectories[DownloadCategory.Programs] = TxtDirPrograms.Text;

        if (int.TryParse(TxtIpcPort.Text, out int port) && port > 1024 && port < 65535)
        {
            s.IpcPort = port;
        }

        if (RbThemeLight.IsChecked == true) s.Theme = "Light";
        else if (RbThemeDark.IsChecked == true) s.Theme = "Dark";
        else s.Theme = "System";

        if (CmbLanguage.SelectedItem is LanguageOption langOpt)
        {
            s.Language = langOpt.Code;
        }

        _settingsManager.Save(s);
        _queueManager.UpdateSpeedLimit(s.SpeedLimitBytesPerSec, s.SpeedLimitEnabled);

        DialogResult = true;
        Close();
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IDMLite.Core;
using IDMLite.Services;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace IDMLite.Windows;

public partial class SettingsDialog : Window
{
    private readonly SettingsManager _settingsManager;
    private readonly DownloadQueueManager _queueManager;

    private readonly List<(Button NavBtn, System.Windows.Shapes.Path Icon, UIElement Page)> _tabs = new();
    private int _currentTabIndex = 0;
    private int _connectionsValue = 8;
    private int _concurrentValue = 3;

    public SettingsDialog(SettingsManager settingsManager, DownloadQueueManager queueManager)
    {
        InitializeComponent();
        _settingsManager = settingsManager;
        _queueManager = queueManager;

        _tabs.Add((TabNavConnection, IconTabConn, PageConnection));
        _tabs.Add((TabNavFolders, IconTabFolders, PageFolders));
        _tabs.Add((TabNavBrowser, IconTabBrowser, PageBrowser));
        _tabs.Add((TabNavAppearance, IconTabAppearance, PageAppearance));
        _tabs.Add((TabNavGeneral, IconTabGeneral, PageGeneral));

        StateChanged += (_, _) => UpdateChromeState();
        Loaded += (_, _) => SelectSettingsTab(0, animate: false);

        LoadSettingsToUI();
    }

    private void UpdateChromeState()
    {
        if (WindowState == WindowState.Maximized)
        {
            RootBorder.Padding = new Thickness(7);
            RootBorder.BorderThickness = new Thickness(0);
            IconWinMaximize.Data = Geometry.Parse("M2,0.5 H9.5 V8 M0.5,2.5 H7.5 V9.5 H0.5 Z");
        }
        else
        {
            RootBorder.Padding = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(1);
            IconWinMaximize.Data = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
        }
    }

    private void BtnWinMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnWinMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnWinClose_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int index))
        {
            SelectSettingsTab(index, animate: true);
        }
    }

    private void SelectSettingsTab(int index, bool animate)
    {
        if (index < 0 || index >= _tabs.Count) index = 0;
        bool changed = index != _currentTabIndex;
        int direction = index >= _currentTabIndex ? 1 : -1;
        _currentTabIndex = index;

        for (int i = 0; i < _tabs.Count; i++)
        {
            bool selected = i == index;
            var (btn, icon, page) = _tabs[i];
            btn.SetResourceReference(ForegroundProperty, selected ? "TextBrush" : "TextSecondaryBrush");
            btn.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Medium;
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, selected ? "AccentBrush" : "TextSecondaryBrush");
            page.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        // Slide active sidebar pill
        try
        {
            var targetBtn = _tabs[index].NavBtn;
            SettingsNavGrid.UpdateLayout();
            var pt = targetBtn.TransformToAncestor(SettingsNavGrid).Transform(new System.Windows.Point(0, 0));
            if (!animate || SettingsNavIndicator.Opacity < 0.01)
            {
                SettingsNavTransform.BeginAnimation(TranslateTransform.YProperty, null);
                SettingsNavTransform.Y = pt.Y;
                SettingsNavIndicator.Height = targetBtn.ActualHeight > 0 ? targetBtn.ActualHeight : 36;
                SettingsNavIndicator.Opacity = 1;
            }
            else
            {
                SettingsNavTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
                {
                    To = pt.Y,
                    Duration = TimeSpan.FromMilliseconds(220),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
        }
        catch { }

        // Animate page content transition
        if (animate && changed)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            SettingsPageSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = direction * 12,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(210),
                EasingFunction = ease
            });
            SettingsPagesHost.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.25,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(210),
                EasingFunction = ease
            });
        }
    }

    private void BtnConnDec_Click(object sender, RoutedEventArgs e)
    {
        _connectionsValue = Math.Clamp(_connectionsValue - 1, 1, 32);
        TxtConnectionsVal.Text = _connectionsValue.ToString();
    }

    private void BtnConnInc_Click(object sender, RoutedEventArgs e)
    {
        _connectionsValue = Math.Clamp(_connectionsValue + 1, 1, 32);
        TxtConnectionsVal.Text = _connectionsValue.ToString();
    }

    private void BtnConcDec_Click(object sender, RoutedEventArgs e)
    {
        _concurrentValue = Math.Clamp(_concurrentValue - 1, 1, 10);
        TxtConcurrentVal.Text = _concurrentValue.ToString();
    }

    private void BtnConcInc_Click(object sender, RoutedEventArgs e)
    {
        _concurrentValue = Math.Clamp(_concurrentValue + 1, 1, 10);
        TxtConcurrentVal.Text = _concurrentValue.ToString();
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

        _connectionsValue = Math.Clamp(s.MaxConnectionsPerDownload, 1, 32);
        _concurrentValue = Math.Clamp(s.MaxConcurrentDownloads, 1, 10);
        TxtConnectionsVal.Text = _connectionsValue.ToString();
        TxtConcurrentVal.Text = _concurrentValue.ToString();

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
        ChkShowDownloadDialog.IsChecked = s.ShowDownloadDialog;
        ChkAutoStartDownloads.IsChecked = s.AutoStartDownloads;

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
            case "system":
                RbThemeSystem.IsChecked = true;
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

        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Google\Chrome\Extensions\{extId}", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Edge\Extensions\{extId}", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\BraveSoftware\Brave\Extensions\{extId}", false); } catch { }

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
        try
        {
            if (string.IsNullOrWhiteSpace(TxtMetadataDirectory.Text) || !Path.IsPathFullyQualified(TxtMetadataDirectory.Text))
                throw new ArgumentException("Kurtarma klasörü için tam bir klasör yolu girin.");
            if (!Path.GetFullPath(TxtMetadataDirectory.Text).Equals(s.MetadataDirectory, StringComparison.OrdinalIgnoreCase))
                _queueManager.ChangeMetadataDirectory(TxtMetadataDirectory.Text);
        }
        catch (Exception error)
        {
            MessageBox.Show($"Kurtarma klasörü kaydedilemedi: {error.Message}", "IDMLite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        s.ResumeInterruptedDownloads = ChkResumeInterrupted.IsChecked == true;

        s.MaxConnectionsPerDownload = _connectionsValue;
        s.MaxConcurrentDownloads = _concurrentValue;

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
        s.ShowDownloadDialog = ChkShowDownloadDialog.IsChecked == true;
        s.AutoStartDownloads = ChkAutoStartDownloads.IsChecked == true;

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

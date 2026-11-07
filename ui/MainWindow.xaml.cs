using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using IDMLite.Core;
using IDMLite.Models;
using IDMLite.Services;
using IDMLite.Windows;

namespace IDMLite;

public partial class MainWindow : Window
{
    private readonly SettingsManager _settingsManager;
    private readonly DownloadQueueManager _queueManager;
    private readonly IpcServer _ipcServer;

    private readonly ObservableCollection<DownloadItemViewModel> _allViewModels = new();
    private readonly ObservableCollection<DownloadItemViewModel> _filteredViewModels = new();
    private readonly HashSet<Guid> _notifiedCompletedTasks = new();

    private string _currentCategoryFilter = "All";
    private string _currentSearchText = "";
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _isExplicitExit = false;

    public MainWindow(SettingsManager settingsManager, DownloadQueueManager queueManager, IpcServer ipcServer)
    {
        InitializeComponent();
        _settingsManager = settingsManager;
        _queueManager = queueManager;
        _ipcServer = ipcServer;

        GridDownloads.ItemsSource = _filteredViewModels;

        // Populate existing items
        foreach (var task in _queueManager.Tasks)
        {
            if (task.Status == DownloadStatus.Completed)
            {
                _notifiedCompletedTasks.Add(task.Id);
            }
            var vm = new DownloadItemViewModel(task);
            _allViewModels.Add(vm);
        }
        ApplyFilter();

        // Subscribe to queue events
        _queueManager.TaskAdded += QueueManager_TaskAdded;
        _queueManager.TaskUpdated += QueueManager_TaskUpdated;
        _queueManager.TaskRemoved += QueueManager_TaskRemoved;
        _queueManager.TotalSpeedChanged += QueueManager_TotalSpeedChanged;

        // Subscribe to IPC server events
        _ipcServer.OpenDownloadWindowAsync = OpenDownloadWindowAsync;

        // Setup System Tray
        InitNotifyIcon();

        // Subscribe to Live Language Swapping
        LocalizationManager.Instance.LanguageChanged += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatusBar();
                UpdateTrayContextMenu();
            });
        };

        UpdateStatusBar();
    }

    private async Task<bool> OpenDownloadWindowAsync(DownloadRequest req, CancellationToken cancellationToken)
    {
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = cancellationToken.Register(() => opened.TrySetResult(false));
        _ = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (opened.Task.IsCompleted) return;
                var dialog = new AddDownloadDialog(_settingsManager, req) { Topmost = true };
                if (opened.Task.IsCompleted) return;
                dialog.ContentRendered += (_, _) =>
                {
                    if (!opened.Task.IsCompletedSuccessfully || !opened.Task.Result)
                    {
                        if (!dialog.IsVisible || !opened.TrySetResult(true)) dialog.Close();
                    }
                };
                if (dialog.ShowDialog() == true && opened.Task.IsCompletedSuccessfully && opened.Task.Result && dialog.CreatedTask != null)
                {
                    dialog.CreatedTask.OriginRequestId = req.RequestId;
                    _queueManager.Add(dialog.CreatedTask, dialog.AutoStart);
                    if (dialog.AutoStart)
                    {
                        try
                        {
                            var progressDlg = new DownloadProgressDialog(dialog.CreatedTask, _queueManager);
                            progressDlg.Show();
                        }
                        catch (Exception pEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"ProgressDlg Show error: {pEx}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("WINDOW_FAILED category={0}", ex.GetType().Name);
            }
            finally { opened.TrySetResult(false); }
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);
        return await opened.Task.ConfigureAwait(false);
    }

    private void QueueManager_TaskAdded(object? sender, DownloadTask task)
    {
        Dispatcher.Invoke(() =>
        {
            var vm = new DownloadItemViewModel(task);
            _allViewModels.Insert(0, vm);
            ApplyFilter();
            UpdateStatusBar();
        });
    }

    private void QueueManager_TaskUpdated(object? sender, DownloadTask task)
    {
        Dispatcher.Invoke(() =>
        {
            var vm = _allViewModels.FirstOrDefault(v => v.Id == task.Id);
            vm?.Refresh();

            if (task.Status == DownloadStatus.Completed && _notifiedCompletedTasks.Add(task.Id))
            {
                // Highlight/flash behind the taskbar icon when download finishes
                ShellHelper.FlashTaskbarIcon(this, 5);
                if (_settingsManager.Current.ShowNotificationOnComplete)
                {
                    var loc = LocalizationManager.Instance;
                    _notifyIcon?.ShowBalloonTip(
                        2500,
                        loc.Get("Str_Completed", "Download Completed!"),
                        task.FileName,
                        System.Windows.Forms.ToolTipIcon.Info
                    );
                }
            }

            UpdateStatusBar();
        });
    }

    private void QueueManager_TaskRemoved(object? sender, DownloadTask task)
    {
        Dispatcher.Invoke(() =>
        {
            _notifiedCompletedTasks.Remove(task.Id);
            var vm = _allViewModels.FirstOrDefault(v => v.Id == task.Id);
            if (vm != null)
            {
                _allViewModels.Remove(vm);
                ApplyFilter();
            }
            UpdateStatusBar();
        });
    }

    private void QueueManager_TotalSpeedChanged(object? sender, double totalSpeed)
    {
        Dispatcher.Invoke(() =>
        {
            string prefix = LocalizationManager.Instance.Get("Str_TotalSpeed", "⬇️ Total Speed:");
            LblTotalSpeed.Text = $"{prefix} {DownloadItemViewModel.FormatBytes((long)totalSpeed)}/s";
        });
    }

    private void UpdateStatusBar()
    {
        if (LblActiveDownloads == null || LblSpeedLimitStatus == null || LblItemCount == null || _settingsManager == null) return;

        var loc = LocalizationManager.Instance;
        int active = _allViewModels.Count(v => v.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Assembling or DownloadStatus.Verifying);
        int total = _allViewModels.Count;
        LblActiveDownloads.Text = $"{loc.Get("Str_ActiveDownloads", "Active:")} {active} | {loc.Get("Str_Total", "Total:")} {total}";

        var s = _settingsManager.Current;
        LblSpeedLimitStatus.Text = s.SpeedLimitEnabled
            ? $"{loc.Get("Str_SpeedLimit", "⚡ Speed Limit:")} {DownloadItemViewModel.FormatBytes(s.SpeedLimitBytesPerSec)}/s"
            : $"{loc.Get("Str_SpeedLimit", "⚡ Speed Limit:")} {loc.Get("Str_Unlimited", "Unlimited")}";

        LblItemCount.Text = $"{_filteredViewModels.Count} {loc.Get("Str_FilesCount", "files")}";

        UpdateMainTaskbarProgress();
    }

    private void UpdateMainTaskbarProgress()
    {
        if (MainTaskbarInfo == null) return;

        var activeTasks = _allViewModels
            .Where(v => v.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Assembling or DownloadStatus.Verifying)
            .ToList();

        if (activeTasks.Count > 0)
        {
            var sizedTasks = activeTasks.Where(v => v.TotalSize > 0).ToList();
            if (sizedTasks.Count > 0)
            {
                double avgProgress = sizedTasks.Average(v => v.ProgressPercentage) / 100.0;
                MainTaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                MainTaskbarInfo.ProgressValue = Math.Clamp(avgProgress, 0.02, 1.0);
            }
            else
            {
                MainTaskbarInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
            }
        }
        else if (_allViewModels.Any(v => v.Status == DownloadStatus.Error))
        {
            MainTaskbarInfo.ProgressState = TaskbarItemProgressState.Error;
            MainTaskbarInfo.ProgressValue = 1.0;
        }
        else if (_allViewModels.Any(v => v.Status == DownloadStatus.Paused))
        {
            MainTaskbarInfo.ProgressState = TaskbarItemProgressState.Paused;
            MainTaskbarInfo.ProgressValue = 0.5;
        }
        else
        {
            MainTaskbarInfo.ProgressState = TaskbarItemProgressState.None;
            MainTaskbarInfo.ProgressValue = 0.0;
        }
    }

    private void ApplyFilter()
    {
        _filteredViewModels.Clear();

        var query = _allViewModels.AsEnumerable();

        // Category filter
        query = _currentCategoryFilter switch
        {
            "Unfinished" => query.Where(v => v.Status != DownloadStatus.Completed),
            "Finished" => query.Where(v => v.Status == DownloadStatus.Completed),
            "Compressed" => query.Where(v => v.Category == DownloadCategory.Compressed),
            "Documents" => query.Where(v => v.Category == DownloadCategory.Documents),
            "Music" => query.Where(v => v.Category == DownloadCategory.Music),
            "Video" => query.Where(v => v.Category == DownloadCategory.Video),
            "Programs" => query.Where(v => v.Category == DownloadCategory.Programs),
            "Other" => query.Where(v => v.Category == DownloadCategory.Other),
            _ => query
        };

        // Text search
        if (!string.IsNullOrWhiteSpace(_currentSearchText))
        {
            query = query.Where(v =>
                v.FileName.Contains(_currentSearchText, StringComparison.OrdinalIgnoreCase) ||
                v.Url.Contains(_currentSearchText, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            _filteredViewModels.Add(item);
        }

        UpdateStatusBar();
    }

    private void TreeCategories_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!IsLoaded) return;

        if (e.NewValue is TreeViewItem item && item.Tag is string tag)
        {
            _currentCategoryFilter = tag;
            ApplyFilter();
        }
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentSearchText = TxtSearch.Text.Trim();
        ApplyFilter();
    }

    private DownloadItemViewModel? GetSelectedViewModel()
    {
        return GridDownloads.SelectedItem as DownloadItemViewModel;
    }

    private void BtnAddUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new AddDownloadDialog(_settingsManager) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.CreatedTask != null)
            {
                _queueManager.Add(dialog.CreatedTask, dialog.AutoStart);
                if (dialog.AutoStart)
                {
                    try
                    {
                        var progressDlg = new DownloadProgressDialog(dialog.CreatedTask, _queueManager);
                        progressDlg.Show();
                    }
                    catch (Exception pEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"ProgressDlg Show error: {pEx}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"İndirme penceresi açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnResume_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            _queueManager.Start(selected.Task);
        }
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            _queueManager.Pause(selected.Task);
        }
    }

    private void BtnStopAll_Click(object sender, RoutedEventArgs e)
    {
        _queueManager.PauseAll();
    }

    private void BtnResumeAll_Click(object sender, RoutedEventArgs e)
    {
        _queueManager.ResumeAll();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            var res = MessageBox.Show(
                $"'{selected.FileName}' indirmesini listeden kaldırmak istiyor musunuz?",
                "İndirmeyi Kaldır",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                _queueManager.Remove(selected.Task, deleteFiles: false);
            }
        }
    }

    private void ContextDeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            var res = MessageBox.Show(
                $"'{selected.FileName}' dosyasını hem listeden hem de diskten tamamen silmek istediğinize emin misiniz?",
                "Dosyayı ve Listeyi Sil",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
            {
                _queueManager.Remove(selected.Task, deleteFiles: true);
            }
        }
    }

    private void GridDownloads_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            string? fallbackDir = !string.IsNullOrWhiteSpace(selected.Task.SaveDirectory)
                ? selected.Task.SaveDirectory
                : _settingsManager.GetDirectoryForCategory(selected.Category);

            if (selected.Task.Status == DownloadStatus.Completed || File.Exists(selected.FullPath))
            {
                ShellHelper.OpenFile(selected.FullPath, fallbackDir);
            }
            else
            {
                OpenSelectedDetails();
            }
        }
    }

    private void ContextDetails_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedDetails();
    }

    private void OpenSelectedDetails()
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            try
            {
                var dlg = new DownloadProgressDialog(selected.Task, _queueManager);
                dlg.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"İlerleme penceresi açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            string? fallbackDir = !string.IsNullOrWhiteSpace(selected.Task.SaveDirectory)
                ? selected.Task.SaveDirectory
                : _settingsManager.GetDirectoryForCategory(selected.Category);
            ShellHelper.OpenFile(selected.FullPath, fallbackDir);
        }
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            string? fallbackDir = !string.IsNullOrWhiteSpace(selected.Task.SaveDirectory)
                ? selected.Task.SaveDirectory
                : _settingsManager.GetDirectoryForCategory(selected.Category);
            ShellHelper.OpenFolderAndSelect(selected.FullPath, fallbackDir);
        }
        else
        {
            ShellHelper.OpenFolderAndSelect(null, _settingsManager.Current.DefaultDownloadDirectory);
        }
    }

    private void ContextOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            string? fallbackDir = !string.IsNullOrWhiteSpace(selected.Task.SaveDirectory)
                ? selected.Task.SaveDirectory
                : _settingsManager.GetDirectoryForCategory(selected.Category);
            ShellHelper.OpenFolderAndSelect(selected.FullPath, fallbackDir);
        }
        else
        {
            ShellHelper.OpenFolderAndSelect(null, _settingsManager.Current.DefaultDownloadDirectory);
        }
    }

    private async void ContextVerifyChecksum_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null && File.Exists(selected.FullPath))
        {
            try
            {
                string hash = await ChecksumHelper.ComputeHashAsync(selected.FullPath, "SHA256");
                MessageBox.Show($"Dosya: {selected.FileName}\n\nSHA-256 Özeti:\n{hash}", "Dosya Bütünlüğü (Checksum)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Özet hesaplanırken hata: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            MessageBox.Show("Dosya henüz tamamlanmamış veya bulunamadı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ContextCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedViewModel();
        if (selected != null)
        {
            Clipboard.SetText(selected.Url);
        }
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_settingsManager, _queueManager) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            UpdateStatusBar();
        }
    }

    private void BtnExtension_Click(object sender, RoutedEventArgs e)
    {
        SettingsDialog.OpenBrowserExtensionHelper(this);
    }

    private void BtnSupport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SupportDialog { Owner = this };
        dlg.ShowDialog();
    }

    private void MenuAdd_Click(object sender, RoutedEventArgs e) => BtnAddUrl_Click(sender, e);
    
    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        _isExplicitExit = true;
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        Application.Current.Shutdown();
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "IDMLite v1.0.1\n\nAçık Kaynak Kodlu Çok Kanallı İndirme Yöneticisi\n\n• Multi-Thread Eşzamanlı Parça İndirme (Range Request)\n• HLS (.m3u8) Video İndirme Motoru\n• Manifest V3 Tarayıcı Video Yakalama Paneli\n• Native Messaging Host & Yerel REST IPC",
            "Hakkında - IDMLite",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void InitNotifyIcon()
    {
        try
        {
            System.Drawing.Icon? appIcon = null;
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (File.Exists(exe))
            {
                appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            }
            if (appIcon == null)
            {
                appIcon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = appIcon,
                Text = "IDMLite",
                Visible = true
            };

            _notifyIcon.DoubleClick += (s, e) => ShowAndRestore();
            UpdateTrayContextMenu();
        }
        catch { }
    }

    private void UpdateTrayContextMenu()
    {
        if (_notifyIcon == null) return;

        var loc = LocalizationManager.Instance;
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var itemShow = new System.Windows.Forms.ToolStripMenuItem(loc.Get("Str_TrayShow", "IDMLite'ı Aç / Göster"));
        itemShow.Font = new System.Drawing.Font(itemShow.Font, System.Drawing.FontStyle.Bold);
        itemShow.Click += (s, e) => ShowAndRestore();
        menu.Items.Add(itemShow);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var itemAdd = new System.Windows.Forms.ToolStripMenuItem(loc.Get("Str_NewDownload", "Yeni İndirme Ekle..."));
        itemAdd.Click += (s, e) => Dispatcher.Invoke(() => BtnAddUrl_Click(this, new RoutedEventArgs()));
        menu.Items.Add(itemAdd);

        var itemResumeAll = new System.Windows.Forms.ToolStripMenuItem(loc.Get("Str_TrayResumeAll", "Tümünü Başlat"));
        itemResumeAll.Click += (s, e) => Dispatcher.Invoke(() => BtnResumeAll_Click(this, new RoutedEventArgs()));
        menu.Items.Add(itemResumeAll);

        var itemPauseAll = new System.Windows.Forms.ToolStripMenuItem(loc.Get("Str_TrayPauseAll", "Tümünü Durdur"));
        itemPauseAll.Click += (s, e) => Dispatcher.Invoke(() => BtnStopAll_Click(this, new RoutedEventArgs()));
        menu.Items.Add(itemPauseAll);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var itemExit = new System.Windows.Forms.ToolStripMenuItem(loc.Get("Str_TrayExit", "Çıkış"));
        itemExit.Click += (s, e) => Dispatcher.Invoke(() => MenuExit_Click(this, new RoutedEventArgs()));
        menu.Items.Add(itemExit);

        _notifyIcon.ContextMenuStrip = menu;
    }

    public void ShowAndRestore()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            Focus();
        });
    }

    private void BtnTrayMinimize_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _notifyIcon?.ShowBalloonTip(
            1500,
            "IDMLite",
            LocalizationManager.Instance.Get("Str_MinimizeToTray", "Sistem tepsisine küçültüldü"),
            System.Windows.Forms.ToolTipIcon.Info
        );
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit && _settingsManager.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon?.ShowBalloonTip(
                1500,
                "IDMLite",
                LocalizationManager.Instance.Get("Str_MinimizeToTray", "IDMLite sistem tepsisinde çalışmaya devam ediyor"),
                System.Windows.Forms.ToolTipIcon.Info
            );
        }
        else
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            base.OnClosing(e);
        }
    }
}

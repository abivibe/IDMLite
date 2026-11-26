using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using IDMLite.Core;
using IDMLite.Models;
using IDMLite.Services;

namespace IDMLite.Windows;

public partial class DownloadProgressDialog : Window
{
    private readonly DownloadTask _task;
    private readonly DownloadQueueManager _queueManager;
    private bool _hasFlashedCompletion = false;
    private bool _segmentsVisible = true;

    public DownloadProgressDialog(DownloadTask task, DownloadQueueManager queueManager)
    {
        InitializeComponent();
        _task = task;
        _queueManager = queueManager;
        if (_task.Status == DownloadStatus.Completed)
        {
            _hasFlashedCompletion = true;
        }

        StateChanged += (_, _) => UpdateChromeState();
        _queueManager.TaskUpdated += QueueManager_TaskUpdated;
        Closed += (s, e) => _queueManager.TaskUpdated -= QueueManager_TaskUpdated;

        UpdateUI();
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

    private void BtnToggleSegments_Click(object sender, RoutedEventArgs e)
    {
        _segmentsVisible = !_segmentsVisible;
        SegmentsSection.Visibility = _segmentsVisible ? Visibility.Visible : Visibility.Collapsed;

        var anim = new DoubleAnimation
        {
            To = _segmentsVisible ? 0 : -90,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SegmentsChevronRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private void QueueManager_TaskUpdated(object? sender, DownloadTask updated)
    {
        if (updated.Id == _task.Id)
        {
            Dispatcher.Invoke(UpdateUI);
        }
    }

    private void UpdateUI()
    {
        var loc = LocalizationManager.Instance;
        TxtFileName.Text = _task.DisplayName;
        TxtFileName.ToolTip = _task.FileName;
        string pathDisplay = _task.Status == DownloadStatus.Completed ? _task.FullPath : _task.SaveDirectory;
        TxtSavePath.Text = pathDisplay;
        TxtSavePath.ToolTip = pathDisplay;

        ImgFileIcon.Source = WindowsFileIcon.Get(_task.FileName, _task.Status == DownloadStatus.Completed ? _task.FullPath : null);

        string verifiedSuffix = _task.IsVerified ? $" ({loc.Get("Str_Verified", "Doğrulandı").Replace("✅", "").Trim()} ✓)" : "";
        TxtStatus.Text = _task.Status switch
        {
            DownloadStatus.Queued => loc.Get("Str_Connecting", "Bağlanıyor..."),
            DownloadStatus.Connecting => loc.Get("Str_Connecting", "Bağlanıyor..."),
            DownloadStatus.Downloading => loc.Get("Str_Downloading", "İndiriliyor"),
            DownloadStatus.Paused => loc.Get("Str_Paused", "Duraklatıldı"),
            DownloadStatus.Completed => $"{loc.Get("Str_ProgressCompletedTitle", "İndirme Tamamlandı")}{verifiedSuffix}",
            DownloadStatus.Error => $"{loc.Get("Str_Error", "Hata:")} " + (_task.ErrorMessage ?? loc.Get("Str_Unknown", "Bilinmiyor")),
            DownloadStatus.Assembling => loc.Get("Str_Assembling", "Parçalar birleştiriliyor..."),
            DownloadStatus.Verifying => $"{loc.Get("Str_Verifying", "Bütünlük sınanıyor...")} ({_task.VerificationProgress:0.#}%)",
            _ => _task.Status.ToString()
        };

        TxtHeadlineStatus.Text = _task.Status switch
        {
            DownloadStatus.Completed => loc.Get("Str_ProgressCompletedTitle", "İndirme Tamamlandı"),
            DownloadStatus.Downloading => loc.Get("Str_Downloading", "İndiriliyor..."),
            DownloadStatus.Paused => loc.Get("Str_Paused", "Duraklatıldı"),
            DownloadStatus.Error => loc.Get("Str_StatusErrorShort", "Hata"),
            DownloadStatus.Verifying => loc.Get("Str_Verifying", "Bütünlük sınanıyor..."),
            DownloadStatus.Assembling => loc.Get("Str_Assembling", "Parçalar birleştiriliyor..."),
            _ => loc.Get("Str_Connecting", "Bağlanıyor...")
        };

        try
        {
            var statusBrush = _task.Status switch
            {
                DownloadStatus.Completed => (System.Windows.Media.Brush)FindResource("SuccessBrush"),
                DownloadStatus.Error => (System.Windows.Media.Brush)FindResource("ErrorBrush"),
                DownloadStatus.Paused => (System.Windows.Media.Brush)FindResource("WarningBrush"),
                DownloadStatus.Verifying => (System.Windows.Media.Brush)FindResource("WarningBrush"),
                _ => (System.Windows.Media.Brush)FindResource("AccentBrush")
            };
            TxtStatus.Foreground = statusBrush;
            StatusBadgeCircle.Background = statusBrush;
            TxtPercent.Foreground = statusBrush;

            StatusBadgeIcon.Data = _task.Status switch
            {
                DownloadStatus.Completed => Geometry.Parse("M3,8.5 L6.8,12.2 L13.5,4.8"),
                DownloadStatus.Paused => Geometry.Parse("M5.5,3.5 V12.5 M10.5,3.5 V12.5"),
                DownloadStatus.Error => Geometry.Parse("M4,4 L12,12 M12,4 L4,12"),
                _ => Geometry.Parse("M8,2.5 V11.5 M4.5,8 L8,11.5 L11.5,8 M3,13.5 H13")
            };
        }
        catch { }

        if (_task.Status == DownloadStatus.Verifying)
        {
            TxtSizeRatio.Text = $"{DownloadItemViewModel.FormatBytes(_task.DownloadedSize)} / {DownloadItemViewModel.FormatBytes(_task.DownloadedSize)}";
            PrgTotal.Value = _task.VerificationProgress;
            TxtPercent.Text = $"%{_task.VerificationProgress:0.#}";
        }
        else if (_task.TotalSize > 0)
        {
            TxtSizeRatio.Text = $"{DownloadItemViewModel.FormatBytes(_task.DownloadedSize)} / {DownloadItemViewModel.FormatBytes(_task.TotalSize)}";
            double percent = _task.ProgressPercentage;
            PrgTotal.Value = percent;
            TxtPercent.Text = percent >= 99.95 ? "%100" : $"%{percent:0.#}";
        }
        else
        {
            if (_task.Status == DownloadStatus.Completed)
            {
                TxtSizeRatio.Text = $"{DownloadItemViewModel.FormatBytes(_task.DownloadedSize)} / {DownloadItemViewModel.FormatBytes(_task.DownloadedSize)}";
                PrgTotal.Value = 100;
                TxtPercent.Text = "%100";
            }
            else
            {
                TxtSizeRatio.Text = $"{DownloadItemViewModel.FormatBytes(_task.DownloadedSize)} / {loc.Get("Str_DynamicStream", "Dynamic Stream")}";
                PrgTotal.Value = 0;
                TxtPercent.Text = DownloadItemViewModel.FormatBytes(_task.DownloadedSize);
            }
        }

        // Inline Speed & ETA only while actively transferring
        bool showLiveSpeed = _task.Status == DownloadStatus.Downloading;
        TxtSpeedSep.Visibility = showLiveSpeed ? Visibility.Visible : Visibility.Collapsed;
        TxtSpeed.Visibility = showLiveSpeed ? Visibility.Visible : Visibility.Collapsed;
        TxtSpeed.Text = showLiveSpeed ? $"{(_task.SpeedBytesPerSec / (1024.0 * 1024.0)):0.00} MB/s" : "";

        bool showEta = showLiveSpeed && _task.TimeRemaining.HasValue;
        TxtEtaSep.Visibility = showEta ? Visibility.Visible : Visibility.Collapsed;
        TxtEta.Visibility = showEta ? Visibility.Visible : Visibility.Collapsed;
        if (showEta && _task.TimeRemaining.HasValue)
        {
            var t = _task.TimeRemaining.Value;
            TxtEta.Text = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}h {t.Minutes}m"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
        }
        else
        {
            TxtEta.Text = "";
        }

        if (_task.Status == DownloadStatus.Completed && !string.IsNullOrWhiteSpace(_task.ActualChecksum))
        {
            PnlChecksum.Visibility = Visibility.Visible;
            TxtChecksum.Text = _task.ActualChecksum;
        }
        else
        {
            PnlChecksum.Visibility = Visibility.Collapsed;
        }

        // Segments list
        if (_task.Segments != null && _task.Segments.Count > 0)
        {
            if (LstSegments.ItemsSource != _task.Segments || LstSegments.Items.Count != _task.Segments.Count)
            {
                LstSegments.ItemsSource = _task.Segments;
            }
        }
        else if (LstSegments.ItemsSource != null)
        {
            LstSegments.ItemsSource = null;
        }

        // Open File button state
        bool canOpenFile = _task.Status == DownloadStatus.Completed || (!string.IsNullOrWhiteSpace(_task.FullPath) && File.Exists(_task.FullPath));
        BtnOpenFile.IsEnabled = canOpenFile;

        UpdateTaskbarProgress();

        // Pause/Resume button visible only while not completed (matches 4-button completed reference)
        if (_task.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Queued)
        {
            TxtPauseResumeLabel.Text = loc.Get("Str_PauseClean", "Duraklat");
            BtnPauseResume.Visibility = Visibility.Visible;
            BtnPauseResume.IsEnabled = true;
        }
        else if (_task.Status is DownloadStatus.Paused or DownloadStatus.Error)
        {
            TxtPauseResumeLabel.Text = _task.Status == DownloadStatus.Error
                ? loc.Get("Str_Retry", "Tekrar Dene")
                : loc.Get("Str_ResumeClean", "Devam Ettir");
            BtnPauseResume.Visibility = Visibility.Visible;
            BtnPauseResume.IsEnabled = true;
        }
        else
        {
            BtnPauseResume.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseDialogImmediately()
    {
        try
        {
            _queueManager.TaskUpdated -= QueueManager_TaskUpdated;
            Hide();
            Close();
        }
        catch { }
    }

    private void UpdateTaskbarProgress()
    {
        if (WinTaskbarInfo == null) return;

        switch (_task.Status)
        {
            case DownloadStatus.Queued:
            case DownloadStatus.Connecting:
                WinTaskbarInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
                break;

            case DownloadStatus.Downloading:
            case DownloadStatus.Assembling:
                if (_task.TotalSize > 0)
                {
                    WinTaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                    WinTaskbarInfo.ProgressValue = Math.Clamp(_task.ProgressPercentage / 100.0, 0.01, 1.0);
                }
                else
                {
                    WinTaskbarInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
                }
                break;

            case DownloadStatus.Verifying:
                WinTaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                WinTaskbarInfo.ProgressValue = Math.Clamp(_task.VerificationProgress / 100.0, 0.01, 1.0);
                break;

            case DownloadStatus.Paused:
                WinTaskbarInfo.ProgressState = TaskbarItemProgressState.Paused;
                WinTaskbarInfo.ProgressValue = _task.TotalSize > 0
                    ? Math.Clamp(_task.ProgressPercentage / 100.0, 0.05, 1.0)
                    : 0.5;
                break;

            case DownloadStatus.Error:
                WinTaskbarInfo.ProgressState = TaskbarItemProgressState.Error;
                WinTaskbarInfo.ProgressValue = 1.0;
                break;

            case DownloadStatus.Completed:
                WinTaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                WinTaskbarInfo.ProgressValue = 1.0;
                if (!_hasFlashedCompletion)
                {
                    _hasFlashedCompletion = true;
                    ShellHelper.FlashTaskbarIcon(this, 5);
                }
                break;
        }
    }

    private void BtnSpeedLimit_Click(object sender, RoutedEventArgs e)
    {
        if (SpeedLimitDialog.Prompt(this, _task.SpeedLimitBytesPerSecond) is long limit)
            _queueManager.SetTaskSpeedLimit(_task, limit);
    }

    private void BtnPauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_task.Status == DownloadStatus.Completed)
        {
            CloseDialogImmediately();
            return;
        }

        if (_task.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Queued)
        {
            _queueManager.Pause(_task);
        }
        else if (_task.Status is DownloadStatus.Paused or DownloadStatus.Error)
        {
            _queueManager.Start(_task);
        }
        UpdateUI();
    }

    private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
    {
        string? fallbackDir = !string.IsNullOrWhiteSpace(_task.SaveDirectory)
            ? _task.SaveDirectory
            : _queueManager.Settings.GetDirectoryForCategory(_task.Category);
        ShellHelper.OpenFile(_task.FullPath, fallbackDir);
        CloseDialogImmediately();
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string? fallbackDir = !string.IsNullOrWhiteSpace(_task.SaveDirectory)
            ? _task.SaveDirectory
            : _queueManager.Settings.GetDirectoryForCategory(_task.Category);
        ShellHelper.OpenFolderAndSelect(_task.FullPath, fallbackDir);
        CloseDialogImmediately();
    }

    private void BtnSendToMain_Click(object sender, RoutedEventArgs e)
    {
        var mainWin = Application.Current.MainWindow;
        if (mainWin != null)
        {
            if (mainWin.WindowState == WindowState.Minimized)
            {
                mainWin.WindowState = WindowState.Normal;
            }
            mainWin.Show();
            mainWin.Activate();
        }
        CloseDialogImmediately();
    }

    private void BtnCopyChecksum_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TxtChecksum.Text))
        {
            try { Clipboard.SetText(TxtChecksum.Text); } catch { }
        }
        if (_task.Status == DownloadStatus.Completed)
        {
            CloseDialogImmediately();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        CloseDialogImmediately();
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var cur = dep;
            while (cur != null)
            {
                if (cur is Button || cur is System.Windows.Controls.Primitives.ScrollBar) return;
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        string? fallbackDir = !string.IsNullOrWhiteSpace(_task.SaveDirectory)
            ? _task.SaveDirectory
            : _queueManager.Settings.GetDirectoryForCategory(_task.Category);
        ShellHelper.OpenFile(_task.FullPath, fallbackDir);
        CloseDialogImmediately();
    }
}

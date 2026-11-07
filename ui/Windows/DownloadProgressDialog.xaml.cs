using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    public DownloadProgressDialog(DownloadTask task, DownloadQueueManager queueManager)
    {
        InitializeComponent();
        _task = task;
        _queueManager = queueManager;
        if (_task.Status == DownloadStatus.Completed)
        {
            _hasFlashedCompletion = true;
        }

        TxtFileName.Text = _task.FileName;
        TxtSavePath.Text = _task.FullPath;

        _queueManager.TaskUpdated += QueueManager_TaskUpdated;
        Closed += (s, e) => _queueManager.TaskUpdated -= QueueManager_TaskUpdated;

        UpdateUI();
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
        TxtFileName.Text = _task.FileName;
        TxtSavePath.Text = _task.FullPath;

        TxtStatus.Text = _task.Status switch
        {
            DownloadStatus.Queued => loc.Get("Str_Connecting", "Connecting..."),
            DownloadStatus.Connecting => loc.Get("Str_Connecting", "Connecting..."),
            DownloadStatus.Downloading => loc.Get("Str_Downloading", "Downloading"),
            DownloadStatus.Paused => loc.Get("Str_Paused", "Paused"),
            DownloadStatus.Completed => _task.IsVerified ? $"{loc.Get("Str_Completed", "Download Completed!")} ({loc.Get("Str_Verified", "Verified ✅")})" : loc.Get("Str_Completed", "Download Completed!"),
            DownloadStatus.Error => $"{loc.Get("Str_Error", "Error:")} " + (_task.ErrorMessage ?? loc.Get("Str_Unknown", "Unknown")),
            DownloadStatus.Assembling => loc.Get("Str_Assembling", "Assembling segments..."),
            DownloadStatus.Verifying => $"{loc.Get("Str_Verifying", "Verifying integrity...")} ({_task.VerificationProgress:0.#}%)",
            _ => _task.Status.ToString()
        };

        try
        {
            TxtStatus.Foreground = _task.Status switch
            {
                DownloadStatus.Completed => (System.Windows.Media.Brush)FindResource("SuccessBrush"),
                DownloadStatus.Error => (System.Windows.Media.Brush)FindResource("ErrorBrush"),
                DownloadStatus.Paused => (System.Windows.Media.Brush)FindResource("WarningBrush"),
                DownloadStatus.Verifying => (System.Windows.Media.Brush)FindResource("WarningBrush"),
                _ => (System.Windows.Media.Brush)FindResource("AccentBrush")
            };
        }
        catch { }

        if (_task.Status == DownloadStatus.Verifying)
        {
            TxtSizeRatio.Text = $"{DownloadItemViewModel.FormatBytes(_task.DownloadedSize)} / {DownloadItemViewModel.FormatBytes(_task.DownloadedSize)}";
            PrgTotal.Value = _task.VerificationProgress;
            TxtPercent.Text = $"{_task.VerificationProgress:0.#} %";
        }
        else if (_task.TotalSize > 0)
        {
            TxtSizeRatio.Text = $"{DownloadItemViewModel.FormatBytes(_task.DownloadedSize)} / {DownloadItemViewModel.FormatBytes(_task.TotalSize)}";
            double percent = _task.ProgressPercentage;
            PrgTotal.Value = percent;
            TxtPercent.Text = $"{percent:0.0} %";
        }
        else
        {
            if (_task.Status == DownloadStatus.Completed)
            {
                TxtSizeRatio.Text = $"{DownloadItemViewModel.FormatBytes(_task.DownloadedSize)} / {DownloadItemViewModel.FormatBytes(_task.DownloadedSize)}";
                PrgTotal.Value = 100;
                TxtPercent.Text = "100 %";
            }
            else
            {
                TxtSizeRatio.Text = $"{DownloadItemViewModel.FormatBytes(_task.DownloadedSize)} / {loc.Get("Str_DynamicStream", "Dynamic Stream")}";
                PrgTotal.Value = 0;
                TxtPercent.Text = DownloadItemViewModel.FormatBytes(_task.DownloadedSize);
            }
        }

        TxtSpeed.Text = _task.Status == DownloadStatus.Downloading
            ? $"{(_task.SpeedBytesPerSec / (1024.0 * 1024.0)):0.00} MB/s"
            : (_task.Status == DownloadStatus.Verifying ? loc.Get("Str_Verifying", "Verifying...") : "-");

        if (_task.Status == DownloadStatus.Verifying)
        {
            TxtEta.Text = loc.Get("Str_Verifying", "Verifying...");
        }
        else if (_task.TimeRemaining.HasValue && _task.Status == DownloadStatus.Downloading)
        {
            var t = _task.TimeRemaining.Value;
            TxtEta.Text = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}h {t.Minutes}m"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
        }
        else
        {
            TxtEta.Text = "-";
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
        BtnOpenFile.Opacity = canOpenFile ? 1.0 : 0.55;

        // Taskbar Progress & Completion Notification (Behind Taskbar Icon)
        UpdateTaskbarProgress();

        // Pause/Resume button state
        if (_task.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Queued)
        {
            BtnPauseResume.Content = $"⏸️ {loc.Get("Str_Pause", "Pause")}";
            BtnPauseResume.IsEnabled = true;
        }
        else if (_task.Status is DownloadStatus.Paused or DownloadStatus.Error)
        {
            BtnPauseResume.Content = _task.Status == DownloadStatus.Error
                ? $"🔄 {loc.Get("Str_Retry", "Retry")}"
                : $"▶️ {loc.Get("Str_Resume", "Resume")}";
            BtnPauseResume.IsEnabled = true;
        }
        else if (_task.Status == DownloadStatus.Completed)
        {
            BtnPauseResume.Content = $"✅ {loc.Get("Str_Close", "Close")}";
            BtnPauseResume.IsEnabled = true;
        }
        else
        {
            BtnPauseResume.Content = "✅";
            BtnPauseResume.IsEnabled = false;
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
        // Don't trigger if user clicked on a button or scrollbar
        if (e.OriginalSource is DependencyObject dep)
        {
            var cur = dep;
            while (cur != null)
            {
                if (cur is Button || cur is System.Windows.Controls.Primitives.ScrollBar) return;
                cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
            }
        }

        string? fallbackDir = !string.IsNullOrWhiteSpace(_task.SaveDirectory)
            ? _task.SaveDirectory
            : _queueManager.Settings.GetDirectoryForCategory(_task.Category);
        ShellHelper.OpenFile(_task.FullPath, fallbackDir);
        CloseDialogImmediately();
    }
}

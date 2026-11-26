using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using IDMLite.Core;
using IDMLite.Models;
using IDMLite.Services;
using IDMLite.Windows;
using Path = System.IO.Path;

namespace IDMLite;

public partial class MainWindow : Window
{
    private readonly SettingsManager _settingsManager;
    private readonly DownloadQueueManager _queueManager;
    private readonly IpcServer _ipcServer;

    private readonly ObservableCollection<DownloadItemViewModel> _allViewModels = new();
    private readonly ObservableCollection<DownloadItemViewModel> _filteredViewModels = new();
    private readonly HashSet<Guid> _notifiedCompletedTasks = new();

    private readonly List<(Button Button, System.Windows.Shapes.Path Icon, string Tag)> _navItems = new();
    private Button? _activeNavButton;
    private int _activeNavOrder = 0;
    private bool _isTypesExpanded = true;
    private double _subTypesFullHeight = 198;

    private double _httpSpeed;
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

        RegisterNavigationItems();

        Torrents.JobOpened += AddTorrentRow;
        Torrents.CacheDirectory = Path.Combine(settingsManager.StorageDirectory, "TorrentCache");
        Torrents.DefaultDirectory = settingsManager.Current.DefaultDownloadDirectory;
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
                UpdateHeaderForCategory(_currentCategoryFilter, animate: false);
                UpdateStatusBar();
                UpdateTrayContextMenu();
            });
        };

        StateChanged += (_, _) => UpdateWindowStateChrome();
        Loaded += MainWindow_Loaded;
        UpdateStatusBar();
    }

    private void RegisterNavigationItems()
    {
        _navItems.Clear();
        _navItems.Add((NavAll, IconAll, "All"));
        _navItems.Add((NavUnfinished, IconUnfinished, "Unfinished"));
        _navItems.Add((NavFinished, IconFinished, "Finished"));
        _navItems.Add((NavTorrent, IconTorrent, "Torrent"));
        _navItems.Add((NavTypes, IconTypes, "Types"));
        _navItems.Add((NavCompressed, IconCompressed, "Compressed"));
        _navItems.Add((NavDocuments, IconDocuments, "Documents"));
        _navItems.Add((NavMusic, IconMusic, "Music"));
        _navItems.Add((NavVideo, IconVideo, "Video"));
        _navItems.Add((NavPrograms, IconPrograms, "Programs"));
        _navItems.Add((NavOther, IconOther, "Other"));
        _navItems.Add((NavMainQueue, IconMainQueue, "MainQueue"));
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Torrents.Restore();
        SubTypesPanel.UpdateLayout();
        if (SubTypesPanel.ActualHeight > 10)
        {
            _subTypesFullHeight = SubTypesPanel.ActualHeight;
        }
        SelectCategory("All", animate: false);
        UpdateWindowStateChrome();
    }

    private void UpdateWindowStateChrome()
    {
        if (WindowState == WindowState.Maximized)
        {
            RootWindowBorder.Padding = new Thickness(7);
            RootWindowBorder.BorderThickness = new Thickness(0);
            IconWinMaximize.Data = Geometry.Parse("M2,0.5 H9.5 V8 M0.5,2.5 H7.5 V9.5 H0.5 Z");
        }
        else
        {
            RootWindowBorder.Padding = new Thickness(0);
            RootWindowBorder.BorderThickness = new Thickness(1);
            IconWinMaximize.Data = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
        }
    }

    private void BtnWinMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnWinMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnWinClose_Click(object sender, RoutedEventArgs e) => Close();

    private void CmdNewDownload_Executed(object sender, ExecutedRoutedEventArgs e) => BtnAddUrl_Click(sender, e);

    private void CmdSettings_Executed(object sender, ExecutedRoutedEventArgs e) => BtnSettings_Click(sender, e);

    // =========================================================================
    //  ANIMATED SIDEBAR NAVIGATION & CONTENT TRANSITIONS
    // =========================================================================

    private void SidebarNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            SelectCategory(tag, animate: true);
        }
    }

    private void SidebarTypesToggle_Click(object sender, RoutedEventArgs e)
    {
        // If already on "Types", toggle collapse/expand; otherwise select "Types" and expand if collapsed
        if (_currentCategoryFilter == "Types")
        {
            ToggleFileTypesSubmenu(!_isTypesExpanded);
        }
        else
        {
            if (!_isTypesExpanded)
            {
                ToggleFileTypesSubmenu(true);
            }
            SelectCategory("Types", animate: true);
        }
    }

    private void ToggleFileTypesSubmenu(bool expand)
    {
        _isTypesExpanded = expand;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(220);

        // Rotate chevron smoothly
        var rotateAnim = new DoubleAnimation
        {
            To = expand ? 0 : -90,
            Duration = duration,
            EasingFunction = ease
        };
        TypesChevronRotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);

        if (expand)
        {
            SubTypesPanel.Visibility = Visibility.Visible;
            double targetH = _subTypesFullHeight > 10 ? _subTypesFullHeight : 198;
            var heightAnim = new DoubleAnimation
            {
                From = double.IsNaN(SubTypesPanel.Height) ? 0 : SubTypesPanel.Height,
                To = targetH,
                Duration = duration,
                EasingFunction = ease
            };
            heightAnim.Completed += (_, _) =>
            {
                SubTypesPanel.BeginAnimation(HeightProperty, null);
                SubTypesPanel.Height = double.NaN;
                if (_activeNavButton != null) PositionNavIndicator(_activeNavButton, animate: true);
            };
            var opacityAnim = new DoubleAnimation
            {
                From = SubTypesPanel.Opacity,
                To = 1.0,
                Duration = duration,
                EasingFunction = ease
            };
            SubTypesPanel.BeginAnimation(HeightProperty, heightAnim);
            SubTypesPanel.BeginAnimation(OpacityProperty, opacityAnim);
        }
        else
        {
            if (SubTypesPanel.ActualHeight > 10)
            {
                _subTypesFullHeight = SubTypesPanel.ActualHeight;
            }
            var heightAnim = new DoubleAnimation
            {
                From = SubTypesPanel.ActualHeight,
                To = 0,
                Duration = duration,
                EasingFunction = ease
            };
            heightAnim.Completed += (_, _) =>
            {
                if (!_isTypesExpanded)
                {
                    SubTypesPanel.Visibility = Visibility.Collapsed;
                    if (_activeNavButton != null) PositionNavIndicator(_activeNavButton, animate: true);
                }
            };
            var opacityAnim = new DoubleAnimation
            {
                From = SubTypesPanel.Opacity,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = ease
            };
            SubTypesPanel.BeginAnimation(HeightProperty, heightAnim);
            SubTypesPanel.BeginAnimation(OpacityProperty, opacityAnim);
        }
    }

    private void SelectCategory(string tag, bool animate)
    {
        int newIndex = _navItems.FindIndex(item => item.Tag == tag);
        if (newIndex < 0) newIndex = 0;

        var targetItem = _navItems[newIndex];
        bool changed = !string.Equals(_currentCategoryFilter, targetItem.Tag, StringComparison.Ordinal);
        int direction = newIndex >= _activeNavOrder ? 1 : -1;

        _activeNavOrder = newIndex;
        _activeNavButton = targetItem.Button;
        _currentCategoryFilter = targetItem.Tag;

        // Update visual active styling across sidebar items
        foreach (var (button, icon, itemTag) in _navItems)
        {
            bool isSelected = itemTag == tag;
            button.SetResourceReference(ForegroundProperty, isSelected ? "TextBrush" : "TextSecondaryBrush");
            button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium;
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, isSelected ? "AccentBrush" : "TextSecondaryBrush");
        }

        // Slide the active indicator pill smoothly to the clicked item
        PositionNavIndicator(targetItem.Button, animate);

        // Toggle BitTorrent workspace panel visibility
        Torrents.Visibility = tag == "Torrent" ? Visibility.Visible : Visibility.Collapsed;

        // Refresh filtered list
        ApplyFilter();

        // Update header title + play animated transition on content & header
        UpdateHeaderForCategory(tag, animate && changed);

        if (animate && changed)
        {
            AnimateWorkspaceTransition(direction);
        }
    }

    private void PositionNavIndicator(Button targetButton, bool animate)
    {
        if (!IsLoaded || targetButton.Visibility != Visibility.Visible) return;

        try
        {
            SidebarNavGrid.UpdateLayout();
            var transform = targetButton.TransformToAncestor(SidebarNavGrid);
            var pt = transform.Transform(new System.Windows.Point(0, 0));
            double targetY = pt.Y;
            double targetHeight = targetButton.ActualHeight > 0 ? targetButton.ActualHeight : 34;

            if (!animate || NavSelectionIndicator.Opacity < 0.01)
            {
                NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
                NavSelectionIndicator.BeginAnimation(HeightProperty, null);
                NavIndicatorTransform.Y = targetY;
                NavSelectionIndicator.Height = targetHeight;
                NavSelectionIndicator.Opacity = 1;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slideAnim = new DoubleAnimation
            {
                To = targetY,
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = ease
            };
            var heightAnim = new DoubleAnimation
            {
                To = targetHeight,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = ease
            };
            var accentPulse = new DoubleAnimation
            {
                From = 0.55,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
            };

            NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            NavSelectionIndicator.BeginAnimation(HeightProperty, heightAnim);
            NavAccentScale.BeginAnimation(ScaleTransform.ScaleYProperty, accentPulse);
        }
        catch { }
    }

    private void AnimateWorkspaceTransition(int direction)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slideAnim = new DoubleAnimation
        {
            From = direction * 15,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = ease
        };
        var fadeAnim = new DoubleAnimation
        {
            From = 0.22,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(230),
            EasingFunction = ease
        };

        ContentSlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        WorkspaceContentHost.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private void UpdateHeaderForCategory(string tag, bool animate)
    {
        var loc = LocalizationManager.Instance;
        string title = tag switch
        {
            "All" or "Types" => loc.Get("Str_Downloads", "İndirilenler"),
            "Unfinished" => loc.Get("Str_ActiveDownloadsNav", "Aktif İndirmeler"),
            "Finished" => loc.Get("Str_Finished", "Tamamlananlar"),
            "Torrent" => "BitTorrent",
            "Compressed" => loc.Get("Str_Compressed", "Sıkıştırılmış"),
            "Documents" => loc.Get("Str_Documents", "Belgeler"),
            "Music" => loc.Get("Str_Music", "Müzik"),
            "Video" => loc.Get("Str_Video", "Video"),
            "Programs" => loc.Get("Str_Programs", "Programlar"),
            "Other" => loc.Get("Str_Other", "Diğer"),
            "MainQueue" => loc.Get("Str_MainQueue", "Ana Kuyruk"),
            _ => loc.Get("Str_Downloads", "İndirilenler")
        };

        LblPageTitle.Text = title;
        LblPageSubtitle.Text = loc.Get("Str_SubtitleManageFiles", "Dosyalarınızı yönetin");

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            HeaderSlideTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                From = -10,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(210),
                EasingFunction = ease
            });
            HeaderTitlePanel.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.25,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(210),
                EasingFunction = ease
            });
        }
    }

    // =========================================================================
    //  IPC & TORRENT INTEGRATION
    // =========================================================================

    private async Task<bool> OpenDownloadWindowAsync(DownloadRequest req, CancellationToken cancellationToken)
    {
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = cancellationToken.Register(() => opened.TrySetResult(false));
        _ = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (opened.Task.IsCompleted) return;
                if (TorrentSource.Matches(req.Url, req.FileName))
                {
                    ShowTorrent(req.Url, req.Cookies);
                    opened.TrySetResult(true);
                    return;
                }
                var dialog = new AddDownloadDialog(_settingsManager, req) { Topmost = true };
                if (opened.Task.IsCompleted) return;
                dialog.ContentRendered += (_, _) =>
                {
                    if (!opened.Task.IsCompletedSuccessfully || !opened.Task.Result)
                    {
                        if (!dialog.IsVisible || !opened.TrySetResult(true)) dialog.Close();
                    }
                };
                bool? accepted = dialog.ShowDialog();
                if (accepted == true && dialog.TorrentUrl != null) { ShowTorrent(dialog.TorrentUrl); return; }
                if (accepted == true && opened.Task.IsCompletedSuccessfully && opened.Task.Result && dialog.CreatedTask != null)
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

    private void AddTorrentRow(TorrentJob job)
    {
        var row = new DownloadItemViewModel(job.Summary, isSingleFile: false);
        _allViewModels.Insert(0, row);
        var previousStatus = row.Status;
        string previousName = row.FileName;
        void Refresh()
        {
            row.Refresh();
            bool refilter = previousStatus != row.Status && (_currentCategoryFilter is "Finished" or "Unfinished") ||
                previousName != row.FileName && _currentSearchText.Length > 0;
            previousStatus = row.Status;
            previousName = row.FileName;
            if (refilter) ApplyFilter();
            else UpdateStatusBar();
        }
        job.Changed += Refresh;
        job.Closed += () =>
        {
            job.Changed -= Refresh;
            _allViewModels.Remove(row);
            ApplyFilter();
        };
        if (IsLoaded) SelectCategory("All", animate: true);
        TxtSearch.Clear();
        ApplyFilter();
        GridDownloads.SelectedItem = row;
    }

    private TorrentJob? SelectedTorrent() => Torrents.Jobs.FirstOrDefault(job => job.Summary == GetSelectedViewModel()?.Task);

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
            _httpSpeed = totalSpeed;
            UpdateTotalSpeed();
        });
    }

    private void UpdateTotalSpeed()
    {
        double speed = _httpSpeed + Torrents.Jobs.Sum(job => job.Summary.SpeedBytesPerSec);
        LblTotalSpeed.Text = $"{LocalizationManager.Instance.Get("Str_TotalSpeedClean", "Toplam Hız:")} {DownloadItemViewModel.FormatBytes((long)speed)}/s";
    }

    private void UpdateStatusBar()
    {
        if (LblActiveDownloads == null || LblSpeedLimitStatus == null || LblItemCount == null || _settingsManager == null) return;

        var loc = LocalizationManager.Instance;
        int active = _allViewModels.Count(v => v.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Assembling or DownloadStatus.Verifying);
        int unfinished = _allViewModels.Count(v => v.Status != DownloadStatus.Completed);
        int completed = _allViewModels.Count(v => v.Status == DownloadStatus.Completed);
        int torrents = _allViewModels.Count(v => !v.IsSingleFile);
        int total = _allViewModels.Count;

        // Sidebar counts
        TxtCountAll.Text = total.ToString();
        TxtCountActive.Text = unfinished.ToString();
        TxtCountCompleted.Text = completed.ToString();
        TxtCountTorrent.Text = torrents > 0 ? torrents.ToString() : "";

        // Bottom Status Bar
        LblIpcStatus.Text = string.Format(loc.Get("Str_BrowserConnectedFmt", "Tarayıcı Köprüsü: Bağlı (Port {0})"), _settingsManager.Current.IpcPort);
        LblActiveDownloads.Text = $"{loc.Get("Str_ActiveDownloads", "Aktif:")} {active}";
        LblItemCount.Text = $"{loc.Get("Str_Total", "Toplam:")} {total}";

        var s = _settingsManager.Current;
        LblSpeedLimitStatus.Text = s.SpeedLimitEnabled && s.SpeedLimitBytesPerSec > 0
            ? $"• {loc.Get("Str_SpeedLimit", "Hız Sınırı:")} {DownloadItemViewModel.FormatBytes(s.SpeedLimitBytesPerSec)}/s"
            : "";

        UpdateTotalSpeed();
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
            "Torrent" => query.Where(v => !v.IsSingleFile),
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

    private void ShowTorrent(string source, string? cookies = null)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Torrents.Open(source, cookies);
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentSearchText = TxtSearch.Text.Trim();
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        IconSearchSuffix.Data = string.IsNullOrEmpty(TxtSearch.Text)
            ? Geometry.Parse("M1,3.5 H13 M1,9.5 H13 M4,1.5 V5.5 M9.5,7.5 V11.5")
            : Geometry.Parse("M2,2 L11,11 M11,2 L2,11");
        ApplyFilter();
    }

    private void BtnSearchFilterOrClear_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtSearch.Text))
        {
            TxtSearch.Clear();
            TxtSearch.Focus();
        }
        else
        {
            BtnToolsDropdown_Click(sender, e);
        }
    }

    private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox chk)
        {
            bool check = chk.IsChecked == true;
            foreach (var item in _filteredViewModels)
            {
                item.IsChecked = check;
            }
        }
    }

    private void RowOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is DownloadItemViewModel vm)
        {
            GridDownloads.SelectedItem = vm;
            if (FindResource("DownloadRowContextMenu") is ContextMenu menu)
            {
                menu.DataContext = vm;
                menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }
    }

    private void BtnToolsDropdown_Click(object sender, RoutedEventArgs e)
    {
        if (FindResource("ToolsDropdownMenu") is ContextMenu menu)
        {
            menu.PlacementTarget = BtnToolsDropdown;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private DownloadItemViewModel? GetSelectedViewModel()
    {
        return GridDownloads.SelectedItem as DownloadItemViewModel;
    }

    private List<DownloadItemViewModel> GetTargetViewModels()
    {
        var checkedItems = _filteredViewModels.Where(v => v.IsChecked).ToList();
        if (checkedItems.Count > 0) return checkedItems;
        var selected = GetSelectedViewModel();
        return selected != null ? new List<DownloadItemViewModel> { selected } : new List<DownloadItemViewModel>();
    }

    private void BtnAddUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new AddDownloadDialog(_settingsManager) { Owner = this };
            bool? accepted = dialog.ShowDialog();
            if (accepted == true && dialog.TorrentUrl != null) { ShowTorrent(dialog.TorrentUrl); return; }
            if (accepted == true && dialog.CreatedTask != null)
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
        foreach (var item in GetTargetViewModels())
        {
            var torrent = Torrents.Jobs.FirstOrDefault(j => j.Summary == item.Task);
            if (torrent != null) torrent.Resume();
            else _queueManager.Start(item.Task);
        }
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in GetTargetViewModels())
        {
            var torrent = Torrents.Jobs.FirstOrDefault(j => j.Summary == item.Task);
            if (torrent != null) torrent.Pause();
            else _queueManager.Pause(item.Task);
        }
    }

    private void BtnStopAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in Torrents.Jobs) job.Pause();
        _queueManager.PauseAll();
    }

    private void BtnResumeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in Torrents.Jobs.Where(j => j.Summary.Status == DownloadStatus.Paused)) job.Resume();
        _queueManager.ResumeAll();
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetTargetViewModels();
        if (targets.Count == 0) return;

        string prompt = targets.Count == 1
            ? $"'{targets[0].FileName}' indirmesini listeden kaldırmak istiyor musunuz?"
            : $"{targets.Count} indirmeyi listeden kaldırmak istiyor musunuz?";

        var res = MessageBox.Show(prompt, "İndirmeyi Kaldır", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        foreach (var item in targets)
        {
            var torrent = Torrents.Jobs.FirstOrDefault(j => j.Summary == item.Task);
            if (torrent != null) await torrent.RemoveAsync();
            else _queueManager.Remove(item.Task, deleteFiles: false);
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
        // Ignore double-click on header or checkbox
        if (e.OriginalSource is DependencyObject dep)
        {
            var cur = dep;
            while (cur != null)
            {
                if (cur is System.Windows.Controls.Primitives.DataGridColumnHeader || cur is CheckBox || cur is Button) return;
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        if (SelectedTorrent() is { } job) { job.ShowDetails(); return; }
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

    private async void ContextSpeedLimit_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedViewModel() is not { } row) return;
        var torrent = SelectedTorrent();
        if (SpeedLimitDialog.Prompt(this, row.Task.SpeedLimitBytesPerSecond) is not long limit) return;
        if (torrent != null) await torrent.SetSpeedLimitAsync(limit);
        else _queueManager.SetTaskSpeedLimit(row.Task, limit);
    }

    private void ContextDetails_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedDetails();
    }

    private void OpenSelectedDetails()
    {
        if (SelectedTorrent() is { } job) { job.ShowDetails(); return; }
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
        if (SelectedTorrent() is { } job) { ShellHelper.OpenFolderAndSelect(null, job.Folder); return; }
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
        if (SelectedTorrent() is { } job) { ShellHelper.OpenFolderAndSelect(null, job.Folder); return; }
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
        BtnOpenFolder_Click(sender, e);
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
            "IDMLite v1.0.3\n\nAçık Kaynak Kodlu Çok Kanallı İndirme Yöneticisi\n\n• Multi-Thread Eşzamanlı Parça İndirme (Range Request)\n• HLS (.m3u8) Video İndirme Motoru\n• Manifest V3 Tarayıcı Video Yakalama Paneli\n• Native Messaging Host & Yerel REST IPC",
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
            Torrents.Dispose();
            base.OnClosing(e);
        }
    }
}

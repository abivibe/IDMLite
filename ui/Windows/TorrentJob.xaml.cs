using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IDMLite.Core;
using IDMLite.Models;
using IDMLite.Services;
using MonoTorrent;
using MonoTorrent.Client;

namespace IDMLite.Windows;

public partial class TorrentJob : UserControl
{
    public sealed class FileChoice(ITorrentFile file)
    {
        public string Path => file.Path;
        public long Length => file.Length;
        public string Size => DownloadItemViewModel.FormatBytes(Length);
        public bool Selected { get; set; } = true;
    }

    private readonly ClientEngine _engine;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Torrent? _torrent;
    private readonly TorrentTransferEngine _transfer;
    private TorrentManager? Manager => _transfer.Manager;
    private readonly TorrentCheckpoint? _checkpoint;
    private string? _finalFolder;
    private string StageFolder => Path.Combine(Path.GetDirectoryName(_finalFolder!)!, $"IDMLDownload-{Summary.Id:N}");
    private List<FileChoice> _files = [];
    private bool _running;
    private bool _busy;
    private bool _closed;
    private Window? _details;
    public DownloadTask Summary { get; }
    public event Action? Changed;
    public string Folder => _finalFolder == null || Summary.Status == DownloadStatus.Completed ? Destination.Text : StageFolder;

    public void ShowDetails()
    {
        if (_closed) return;
        if (_details == null)
        {
            _details = new Window
            {
                Content = this, Title = Summary.FileName, Width = 750, Height = 510,
                MinWidth = 620, MinHeight = 440, WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            _details.SetResourceReference(Window.BackgroundProperty, "WindowBgBrush");
            _details.Closing += (_, e) => { if (!_closed) { e.Cancel = true; _details.Hide(); } };
        }
        _details.Show();
        if (_details.WindowState == WindowState.Minimized) _details.WindowState = WindowState.Normal;
        _details.Activate();
    }

    public void Resume()
    {
        if (!_running && !_busy && ActionButton.IsEnabled) Action_Click(this, new RoutedEventArgs());
    }

    public void Pause()
    {
        if (_running && !_busy) Action_Click(this, new RoutedEventArgs());
    }

    public event Action? Closed;

    public TorrentJob(ClientEngine engine, string directory, string storage, TorrentCheckpoint? checkpoint = null)
    {
        InitializeComponent();
        _engine = engine;
        _checkpoint = checkpoint;
        Summary = new DownloadTask
        {
            Id = checkpoint?.Id ?? Guid.NewGuid(), FileName = "BitTorrent", UseStaging = true,
            Status = DownloadStatus.Connecting, ConnectionCount = 0, SpeedLimitBytesPerSecond = checkpoint?.Limit ?? 0
        };
        _transfer = new TorrentTransferEngine(engine, storage, Summary.Id);
        Destination.Text = checkpoint?.Folder ?? directory;
        _timer.Tick += RefreshProgress;
    }

    private static string Label(string key) => LocalizationManager.Instance.Get("Torrent_" + key);

    internal async void LoadSource(string source, string? cookies)
    {
        Status.Text = Label("Loading");
        FileTitle.Text = Summary.DisplayName;
        Progress.IsIndeterminate = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            _torrent = await TorrentSource.ReadAsync(_engine, _checkpoint == null ? source : _transfer.MetadataPath, timeout.Token,
                cookies, _checkpoint == null ? _transfer.SaveMetadata : null);
            if (_closed) return;
            Summary.FileName = _torrent.Name;
            Summary.Status = DownloadStatus.Queued;
            if (_details != null) _details.Title = $"BitTorrent · {_torrent.Name}";
            _files = _torrent.Files.Select(file => new FileChoice(file)).ToList();
            FileList.ItemsSource = _files;
            Status.Text = Label("Ready");
            if (_checkpoint != null)
            {
                _finalFolder = _checkpoint.Folder;
                Summary.SaveDirectory = Path.GetDirectoryName(_finalFolder)!;
                Summary.FileName = Path.GetFileName(_finalFolder);
                var selected = _checkpoint.Selected.ToHashSet(StringComparer.Ordinal);
                foreach (var file in _files) file.Selected = selected.Contains(file.Path);
                bool complete = _checkpoint.Completed || (_checkpoint.Publishing &&
                    !Directory.Exists(Path.Combine(StageFolder, "payload")) && Directory.Exists(_finalFolder) &&
                    _files.Where(f => f.Selected).All(f => new FileInfo(Path.Combine(_finalFolder, f.Path)) is { Exists: true } info && info.Length == f.Length));
                Summary.Status = complete ? DownloadStatus.Completed : DownloadStatus.Paused;
                Summary.DownloadedSize = _checkpoint.Downloaded;
                Status.Text = Label(complete ? "Complete" : "Paused");
            }
            UpdateControls();
        }
        catch (OperationCanceledException) { SetError(Label("Timeout")); }
        catch (Exception ex) { SetError(ex.Message); }
        finally { Progress.IsIndeterminate = false; }
    }

    private void SetError(string message)
    {
        Summary.Status = DownloadStatus.Error;
        Summary.ErrorMessage = Status.Text = message;
        Changed?.Invoke();
    }

    private void Selection_Click(object sender, RoutedEventArgs e)
    {
        UpdateControls();
        if (_files.Any(f => f.Selected)) SaveCheckpoint();
    }

    private void UpdateControls()
    {
        var chosen = _files.Where(f => f.Selected).ToList();
        Selection.Text = $"{Label("Selected")}  {chosen.Count} / {_files.Count}  ·  {DownloadItemViewModel.FormatBytes(chosen.Sum(f => f.Length))}";
        FileList.IsEnabled = !_busy && !_running && Summary.Status != DownloadStatus.Completed;
        FolderButton.IsEnabled = !_busy && _torrent != null;
        Summary.TotalSize = chosen.Sum(f => f.Length);
        if (Summary.Status == DownloadStatus.Completed) Summary.DownloadedSize = Summary.TotalSize;
        UpdateDisplay();
        ActionButton.IsEnabled = !_busy && chosen.Count > 0 && Summary.Status != DownloadStatus.Completed;
        ActionButton.SetResourceReference(ContentControl.ContentProperty, _running ? "Torrent_Pause" : Summary.Status == DownloadStatus.Paused ? "Str_Resume" : "Torrent_Start");
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Destination.Text };
        if (_finalFolder != null)
        {
            IDMLite.ShellHelper.OpenFolderAndSelect(null, Summary.Status == DownloadStatus.Completed ? _finalFolder : StageFolder);
        }
        else if (picker.ShowDialog(Window.GetWindow(this)) == true) Destination.Text = picker.FolderName;
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _torrent == null || !_files.Any(f => f.Selected)) return;
        _busy = true;
        UpdateControls();
        try
        {
            if (_running)
            {
                await StopAsync(Label("Paused"), DownloadStatus.Paused);
                return;
            }
            if (_finalFolder == null)
            {
                string root = Path.Combine(Destination.Text, _torrent.Name);
                _finalFolder = root;
                for (int i = 2; Directory.Exists(_finalFolder) || File.Exists(_finalFolder); i++)
                    _finalFolder = $"{root} ({i})";
                Destination.Text = _finalFolder;
                Summary.SaveDirectory = Path.GetDirectoryName(_finalFolder)!;
                Summary.FileName = Path.GetFileName(_finalFolder);
            }
            Directory.CreateDirectory(StageFolder);
            SaveCheckpoint();
            var selected = _files.Where(f => f.Selected).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            await _transfer.StartAsync(_torrent, StageFolder, selected, Summary.SpeedLimitBytesPerSecond);
            Summary.Status = DownloadStatus.Downloading;
            _running = true;
            _timer.Start();
            Status.Text = Manager!.State.ToString();
        }
        catch (Exception ex) { SetError(ex.Message); }
        finally { _busy = false; UpdateControls(); }
    }

    private async Task StopAsync(string status, DownloadStatus state)
    {
        await _transfer.StopAsync();
        _timer.Stop();
        _running = false;
        Summary.ErrorMessage = state == DownloadStatus.Error ? status : null;
        Summary.SpeedBytesPerSec = 0;
        Summary.ConnectionCount = 0;
        Summary.TimeRemaining = null;
        if (state == DownloadStatus.Completed) Summary.DownloadedSize = Summary.TotalSize;
        if (state == DownloadStatus.Completed)
        {
            string root = _finalFolder!;
            for (int i = 2; Directory.Exists(_finalFolder) || File.Exists(_finalFolder); i++)
                _finalFolder = $"{root} ({i})";
            SaveCheckpoint(completed: false, publishing: true);
            var selected = _files.Where(f => f.Selected).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            _transfer.Publish(StageFolder, _finalFolder!, selected);
            await _transfer.DetachAsync();
            Summary.FileName = Path.GetFileName(_finalFolder)!;
            Destination.Text = _finalFolder;
        }
        Summary.Status = state;
        Status.Text = status;
        SaveCheckpoint();
    }

    private void SaveCheckpoint(bool? completed = null, bool publishing = false)
    {
        if (_finalFolder == null) return;
        _transfer.Save(new TorrentCheckpoint(Summary.Id, _finalFolder, _files.Where(f => f.Selected).Select(f => f.Path).ToArray(),
            Summary.SpeedLimitBytesPerSecond, completed ?? Summary.Status == DownloadStatus.Completed, Summary.DownloadedSize, publishing));
    }

    private void UpdateDisplay()
    {
        var vm = new DownloadItemViewModel(Summary, false);
        FileTitle.Text = Summary.DisplayName;
        FileTitle.ToolTip = Summary.FileName;
        Speed.Text = vm.SpeedFormatted;
        Ratio.Text = $"{vm.DownloadedSizeFormatted} / {DownloadItemViewModel.FormatBytes(Summary.TotalSize)}";
        Eta.Text = vm.TimeRemainingFormatted;
        PeerCount.Text = Summary.ConnectionCount.ToString();
        Progress.Value = Summary.ProgressPercentage;
        Percent.Text = $"{Progress.Value:F1}%";
        Changed?.Invoke();
    }

    public async Task SetSpeedLimitAsync(long limit)
    {
        try
        {
            await _transfer.UpdateLimitAsync(limit);
            Summary.SpeedLimitBytesPerSecond = limit;
            SaveCheckpoint();
        }
        catch (Exception ex) { SetError(ex.Message); }
    }

    private async void Limit_Click(object sender, RoutedEventArgs e)
    {
        if (SpeedLimitDialog.Prompt(Window.GetWindow(this), Summary.SpeedLimitBytesPerSecond) is long limit)
            await SetSpeedLimitAsync(limit);
    }

    private async void RefreshProgress(object? sender, EventArgs e)
    {
        if (_closed || Manager is not { } manager || _busy || !_running) return;
        double progress = manager.PartialProgress;
        double speed = _transfer.SampleSpeed();
        Summary.DownloadedSize = (long)(Summary.TotalSize * (progress / 100));
        Summary.SpeedBytesPerSec = speed;
        Summary.ConnectionCount = manager.OpenConnections;
        double remaining = speed > 0 ? (Summary.TotalSize - Summary.DownloadedSize) / speed : double.PositiveInfinity;
        Summary.TimeRemaining = remaining < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(remaining) : null;
        Status.Text = LocalizationManager.Instance.Get("Str_Downloading");
        UpdateDisplay();
        bool complete = (progress >= 100 || Summary.TotalSize == 0) &&
            manager.Files.Where(f => f.Priority != Priority.DoNotDownload).All(f => f.Length == 0 || f.BitField.AllTrue);
        _busy = true;
        try
        {
            if (PeerTab.IsSelected && _details?.IsVisible == true)
                PeerList.ItemsSource = (await manager.GetPeersAsync()).Select(peer => new
                {
                    Client = peer.ClientApp.ToString(), Address = peer.Uri.Authority,
                    Speed = DownloadItemViewModel.FormatBytes(peer.Monitor.DownloadRate) + "/s"
                }).ToList();
            if (complete || manager.State == TorrentState.Error)
                await StopAsync(complete ? Label("Complete") : manager.Error?.Exception.Message ?? "Torrent error",
                    complete ? DownloadStatus.Completed : DownloadStatus.Error);
            else await _transfer.RetryIfStalledAsync();
        }
        catch (Exception ex) { SetError(ex.Message); }
        finally
        {
            _busy = false;
            if (!_running) UpdateControls();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _details?.Hide();

    public async Task RemoveAsync()
    {
        if (_busy) return;
        _busy = true;
        UpdateControls();
        try
        {
            if (_running) await StopAsync(Label("Paused"), DownloadStatus.Paused);
            await _transfer.DetachAsync();
            Shutdown();
            _transfer.DeleteCheckpoint();
            Closed?.Invoke();
        }
        catch (Exception ex) { _busy = false; Status.Text = ex.Message; UpdateControls(); }
    }

    internal void Shutdown()
    {
        _closed = true;
        _timer.Stop();
        _lifetime.Cancel();
        if (_finalFolder != null)
        {
            try { SaveCheckpoint(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning(ex.Message); }
        }
        _details?.Close();
    }
}

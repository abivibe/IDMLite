using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IDMLite.Core;
using IDMLite.Windows;
using MonoTorrent;
using MonoTorrent.Client;

internal static class TorrentTransfer
{
    internal static void Run(string root, Action<Func<bool>, int> wait, Action<bool, string> check)
    {
        string seedFolder = Directory.CreateDirectory(Path.Combine(root, "fixture")).FullName;
        var payload = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(payload);
        File.WriteAllBytes(Path.Combine(seedFolder, "selected.bin"), payload);
        File.WriteAllBytes(Path.Combine(seedFolder, "skipped.bin"), new byte[32768]);
        string metadata = Path.Combine(root, "fixture.torrent");
        Task.Run(() => new TorrentCreator { PieceLength = 16384 }.Create(new TorrentFileSource(seedFolder), metadata)).GetAwaiter().GetResult();
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var seedAddress = (IPEndPoint)socket.LocalEndpoint;
        socket.Stop();

        ClientEngine Engine(string name, int port) => new(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(root, name),
            DhtEndPoint = null, AllowPortForwarding = false, AllowLocalPeerDiscovery = false,
            UsePartialFiles = false, FastResumeMode = FastResumeMode.Accurate,
            ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, port) }
        }.ToSettings());
        using var seed = Engine("seed-cache", seedAddress.Port);
        using var leecher = Engine("client-cache", 0);
        var seedManager = Task.Run(() => seed.AddAsync(Torrent.Load(metadata), seedFolder,
            new TorrentSettingsBuilder { CreateContainingDirectory = false, MaximumUploadRate = 1024 * 1024 }.ToSettings())).GetAwaiter().GetResult();
        Task.Run(() => seedManager.StartAsync()).GetAwaiter().GetResult();
        wait(() => seedManager.State == TorrentState.Seeding, 15000);
        CheckSources(leecher, seedManager, metadata, seedAddress.Port, check);
        var settings = new SettingsManager(root);
        settings.Current.DefaultDownloadDirectory = Path.Combine(root, "received");
        settings.Current.CloseToTray = false;
        using var queue = new DownloadQueueManager(settings);
        using var ipc = new IpcServer(queue, settings);
        IDMLite.MainWindow window = new IDMLite.MainWindow(settings, queue, ipc);
        window.Show();
        var workspace = (TorrentWorkspace)window.FindName("Torrents");
        typeof(TorrentWorkspace).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, leecher);
        workspace.Open(metadata);
        var job = workspace.Jobs.Single();
        var details = Window.GetWindow(job)!;
        var grid = (DataGrid)window.FindName("GridDownloads");
        ClientEngine? restoredEngine = null;
        try
        {
            check(details.IsVisible && details != window && grid.Items.Count == 1 && grid.IsVisible,
                "torrent opens a separate progress window and a row in All Downloads");
            var list = (ListView)job.FindName("FileList");
            wait(() => list.Items.Count == 2, 8000);
            check(leecher.Torrents.Count == 0, "listing torrent files starts no payload transfer");
            list.UpdateLayout();
            var choice = list.Items.Cast<TorrentJob.FileChoice>().Single(f => f.Path == "skipped.bin");
            var box = FindCheckBox((ListViewItem)list.ItemContainerGenerator.ContainerFromItem(choice))!;
            box.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            box.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent, box));
            var start = (Button)job.FindName("ActionButton");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, start));
            wait(() => leecher.Torrents.Any(m => m.State == TorrentState.Downloading), 12000);
            var manager = leecher.Torrents.Single();
            check(manager.Files.Single(f => f.Path == "skipped.bin").Priority == Priority.DoNotDownload,
                "unchecking a file sets its engine priority before transfer");
            Task.Run(() => manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{seedAddress.Port}")))).GetAwaiter().GetResult();
            var speedSamples = new List<double>();
            long sampleAt = 0;
            void RecordSpeed()
            {
                if (job.Summary.SpeedBytesPerSec > 0 && Environment.TickCount64 - sampleAt >= 500)
                {
                    speedSamples.Add(job.Summary.SpeedBytesPerSec);
                    sampleAt = Environment.TickCount64;
                }
            }
            job.Changed += RecordSpeed;
            details.Close();
            wait(() => !details.IsVisible && job.Summary.SpeedBytesPerSec > 0, 20000);
            var row = (IDMLite.Models.DownloadItemViewModel)grid.Items[0];
            check(row.Task == job.Summary && row.TotalSize == payload.Length && row.SpeedFormatted != "-",
                "main row reports selected size and live transfer speed with details hidden");
            grid.SelectedItem = row;
            typeof(IDMLite.MainWindow).GetMethod("OpenSelectedDetails", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            check(details.IsVisible && Window.GetWindow(job) == details, "details reopens the original transfer window");
            check(((TabItem)job.FindName("PeerTab")).IsEnabled && row.TorrentIconVisibility == Visibility.Visible,
                "torrent row has the torrent icon and its window provides the peer tab");
            check(File.Exists(manager.Files[0].FullPath) && Path.GetFileName(manager.Files[0].FullPath).StartsWith("IDMLDownload-"),
                "active torrent payload uses a numbered staging file");
            wait(() => speedSamples.Count >= 3, 15000);
            job.Pause();
            wait(() => job.Summary.Status == DownloadStatus.Paused, 8000);
            string recoveryCache = workspace.CacheDirectory;
            check(TorrentTransferEngine.ReadCheckpoints(recoveryCache).Single().Selected.SequenceEqual(new[] { "selected.bin" }),
                "pause persists selected files");
            job.Changed -= RecordSpeed;
            window.Close();
            restoredEngine = Engine("client-cache", 0);
            window = new IDMLite.MainWindow(settings, queue, ipc);
            workspace = (TorrentWorkspace)window.FindName("Torrents");
            typeof(TorrentWorkspace).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, restoredEngine);
            window.Show();
            wait(() => workspace.Jobs.Count == 1 && workspace.Jobs[0].Summary.Status == DownloadStatus.Paused, 8000);
            job = workspace.Jobs.Single();
            grid = (DataGrid)window.FindName("GridDownloads");
            row = (IDMLite.Models.DownloadItemViewModel)grid.Items[0];
            check(row.Status == DownloadStatus.Paused && row.DownloadedSize > 0, "restarted application restores paused torrent and progress");
            job.Resume();
            wait(() => restoredEngine.Torrents.Any(m => m.State == TorrentState.Downloading), 12000);
            manager = restoredEngine.Torrents.Single();
            check(manager.PartialProgress > 0, "restored torrent resumes with verified existing pieces");
            var limiterChange = job.SetSpeedLimitAsync(512 * 1024);
            wait(() => limiterChange.IsCompleted, 4000);
            check(manager.Settings.MaximumDownloadRate == 512 * 1024, "custom torrent limit applies to the active engine");
            Task.Run(() => manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{seedAddress.Port}")))).GetAwaiter().GetResult();
            wait(() => job.Summary.Status == DownloadStatus.Completed, 45000);
            job.Changed -= RecordSpeed;
            check(speedSamples.Count >= 3, "sustained transfer reports multiple payload speed samples");
            Console.WriteLine($"Local seed capped at 1 MiB/s: median displayed rate {speedSamples.Order().ElementAt(speedSamples.Count / 2) / 1048576:F2} MiB/s");
            check(row.ProgressPercentage == 100 && row.Task.SpeedBytesPerSec == 0, "completed row resets speed and shows full progress");
            check(File.ReadAllBytes(Path.Combine(job.Folder, "selected.bin")).SequenceEqual(payload),
                "selected payload matches the local seed byte for byte");
            check(!File.Exists(Path.Combine(job.Folder, "skipped.bin")),
                "deselected file with independent pieces is absent");
        }
        finally
        {
            typeof(TorrentWorkspace).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, null);
            window.Close();
            if (restoredEngine != null)
            {
                Task.Run(() => restoredEngine.StopAllAsync(TimeSpan.Zero)).GetAwaiter().GetResult();
                restoredEngine.Dispose();
            }
            Task.Run(() => seed.StopAllAsync(TimeSpan.Zero)).GetAwaiter().GetResult();
        }
    }

    private static void CheckSources(ClientEngine engine, TorrentManager seed, string file, int peerPort, Action<bool, string> check)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        string root = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(root);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] metadata = File.ReadAllBytes(file);
        var responder = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using var response = context.Response;
                if (context.Request.Url!.AbsolutePath == "/file.torrent")
                {
                    response.Redirect(root + "metadata");
                    continue;
                }
                if (context.Request.Headers["Cookie"] != null) throw new Exception("Fixture cookie escaped initial request");
                byte[] body = context.Request.Url.AbsolutePath == "/metadata" ? metadata :
                    [.."d8:intervali30e5:peers6:"u8, 127, 0, 0, 1, (byte)(peerPort >> 8), (byte)peerPort, (byte)'e'];
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body, timeout.Token);
            }
        });
        try
        {
            var remote = Task.Run(() => TorrentSource.ReadAsync(engine, root + "file.torrent", timeout.Token, "fixture=1")).GetAwaiter().GetResult();
            check(remote.InfoHashes == seed.InfoHashes || remote.InfoHashes.Equals(seed.InfoHashes), "HTTP torrent metadata resolves after redirect");
            string magnet = new MagnetLink(seed.InfoHashes, seed.Name, [root + "announce"]).ToV1String();
            var resolved = Task.Run(() => TorrentSource.ReadAsync(engine, magnet, timeout.Token)).GetAwaiter().GetResult();
            check(resolved.Files.Count == 2 && engine.Torrents.Count == 0, "magnet resolves via local peer without starting payload transfer");
        }
        finally
        {
            listener.Stop();
            try { responder.GetAwaiter().GetResult(); }
            catch (HttpListenerException) when (!listener.IsListening) { }
            catch (ObjectDisposedException) when (!listener.IsListening) { }
        }
    }

    private static CheckBox? FindCheckBox(DependencyObject item)
    {
        if (item is CheckBox box) return box;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            if (FindCheckBox(VisualTreeHelper.GetChild(item, i)) is { } child) return child;
        return null;
    }
}

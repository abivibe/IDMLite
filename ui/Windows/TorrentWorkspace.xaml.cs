using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using IDMLite.Core;
using IDMLite.Services;
using MonoTorrent.Client;

namespace IDMLite.Windows;

public partial class TorrentWorkspace : UserControl, IDisposable
{
    public string CacheDirectory { get; set; } = "";
    public string DefaultDirectory { get; set; } = "";
    private ClientEngine? _engine;

    public event Action<TorrentJob>? JobOpened;
    public List<TorrentJob> Jobs { get; } = [];

    public TorrentWorkspace() => InitializeComponent();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "BitTorrent (*.torrent)|*.torrent" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) Open(picker.FileName);
    }

    private void Source_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Resolve_Click(sender, e);
    }

    private void Resolve_Click(object sender, RoutedEventArgs e)
    {
        string source = SourceBox.Text.Trim();
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("magnet" or "http" or "https"))
        {
            MessageBox.Show(LocalizationManager.Instance.Get("Torrent_Source"));
            return;
        }
        Open(source);
        SourceBox.Clear();
    }

    public void Restore()
    {
        foreach (var checkpoint in TorrentTransferEngine.ReadCheckpoints(CacheDirectory))
            Open("", checkpoint: checkpoint);
    }

    public void Open(string source, string? cookies = null, TorrentCheckpoint? checkpoint = null)
    {
        try
        {
            PrivateStorage.EnsureDirectory(CacheDirectory);
            _engine ??= new ClientEngine(TorrentTransferEngine.Settings(CacheDirectory));
            var job = new TorrentJob(_engine, DefaultDirectory, CacheDirectory, checkpoint);
            job.Summary.Url = source;
            Jobs.Add(job);
            job.Closed += () => Jobs.Remove(job);
            JobOpened?.Invoke(job);
            if (checkpoint == null) job.ShowDetails();
            job.LoadSource(source, cookies);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "BitTorrent"); }
    }

    public void Dispose()
    {
        foreach (var job in Jobs.ToArray()) job.Shutdown();
        if (_engine != null)
        {
            var engine = _engine;
            try { Task.Run(() => engine.StopAllAsync(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult(); }
            finally { engine.Dispose(); }
        }
        _engine = null;
    }
}

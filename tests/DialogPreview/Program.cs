using System.Windows;
using System.IO;
using System.Windows.Controls;
using IDMLite.Core;
using IDMLite.Windows;
using IDMLite.Core.Resolvers;
using IDMLite.Services;
using IDMLite.Models;
using System.Reflection;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "IDMLite-dialog-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("IDMLITE_DATA_DIR", directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        int passed = 0;
        void Check(bool result, string name) { if (!result) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
        void PumpUntil(Func<bool> condition, int timeout = 12000)
        {
            var frame = new DispatcherFrame();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) => {
                if (condition() || clock.ElapsedMilliseconds > timeout) frame.Continue = false;
            }, Dispatcher.CurrentDispatcher);
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            if (!condition()) throw new Exception("Timed out waiting for live preview");
        }
        ResolvedLinkResult Result(string title, long? size = 20 * 1024 * 1024) => new() {
            IsSuccess = true, OriginalUrl = "https://www.instagram.com/reel/dummy/", SuggestedFileName = title + ".mp4", FileSize = size,
            Streams = new() {
                new() { Url = "https://www.instagram.com/reel/dummy/", Title = title, SuggestedFileName = title + ".mp4", Quality = "1080p", FileSize = size },
                new() { Url = "https://www.instagram.com/reel/dummy/", Title = title, SuggestedFileName = title + ".mp3", Quality = "128kbps", IsAudioOnly = true, FileSize = 2 * 1024 * 1024 }
            }
        };
        AddDownloadDialog Create(Func<string, CancellationToken, Task<ResolvedLinkResult>> resolve, ResolvedLinkResult? cached = null, DateTime? cachedAt = null)
        {
            var settings = new SettingsManager(directory);
            settings.Current.DefaultDownloadDirectory = directory;
            foreach (var category in settings.Current.CategoryDirectories.Keys.ToArray()) settings.Current.CategoryDirectories[category] = directory;
            return new(settings, new DownloadRequest {
                Url = "https://www.instagram.com/reel/dummy/", FileName = "Browser title.mp4", Direct = true,
                Preview = cached, PreviewCachedAtUtc = cachedAt ?? DateTime.UtcNow
            }, resolve);
        }
        TextBlock Size(AddDownloadDialog dialog) => (TextBlock)dialog.FindName("LblFileSize");
        TextBox Name(AddDownloadDialog dialog) => (TextBox)dialog.FindName("TxtFileName");
        try
        {
            var archiveSettings = new SettingsManager(directory);
            string compressed = Path.Combine(directory, "Compressed");
            string custom = Path.Combine(directory, "Custom");
            archiveSettings.Current.DefaultDownloadDirectory = directory;
            archiveSettings.Current.CategoryDirectories[DownloadCategory.Compressed] = compressed;
            var validate = typeof(AddDownloadDialog).GetMethod("ValidateAndCreateTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (string name in new[] { "Browser archive.ZIP", "archive.rar", "archive.7z", "archive.tar", "archive.tar.zst" })
            {
                var archive = new AddDownloadDialog(archiveSettings, new DownloadRequest {
                    Url = "http://127.0.0.1:1/fetch", FileName = name, Direct = true
                });
                Check((bool)validate.Invoke(archive, null)! && archive.CreatedTask?.Category == DownloadCategory.Compressed &&
                    archive.CreatedTask.SaveDirectory == compressed, $"{name} from browser uses the Compressed folder");
                archive.Close();
            }
            var renamed = new AddDownloadDialog(archiveSettings, new DownloadRequest { Url = "http://127.0.0.1:1/fetch" });
            var folder = (TextBox)renamed.FindName("TxtDirectory");
            Name(renamed).Text = "archive.tar.gz";
            Check(folder.Text == compressed, "A filename learned after the URL updates the automatic destination");
            folder.Text = custom;
            Name(renamed).Text = "another.7z";
            Check((bool)validate.Invoke(renamed, null)! && renamed.CreatedTask?.SaveDirectory == custom, "Archive detection preserves an explicitly edited folder");
            renamed.Close();

            var portProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            portProbe.Start();
            int probePort = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            using (var destination = new System.Net.HttpListener())
            {
                string editedUrl = $"http://127.0.0.1:{probePort}/replacement.bin";
                destination.Prefixes.Add($"http://127.0.0.1:{probePort}/");
                destination.Start();
                var observed = Task.Run(async () =>
                {
                    var request = await destination.GetContextAsync();
                    string? cookies = request.Request.Headers["Cookie"];
                    request.Response.ContentType = "application/octet-stream";
                    request.Response.ContentLength64 = 65536;
                    request.Response.AddHeader("Accept-Ranges", "bytes");
                    request.Response.Close();
                    return cookies;
                });
                var edited = new AddDownloadDialog(archiveSettings, new DownloadRequest
                {
                    Url = "https://original.invalid/authenticated.bin", FileName = "authenticated.bin",
                    FileSize = 65536, Cookies = "SESSION=DUMMY_WPF_COOKIE", Direct = true
                });
                ((TextBox)edited.FindName("TxtUrl")).Text = editedUrl;
                PumpUntil(() => observed.IsCompleted);
                Check(observed.GetAwaiter().GetResult() == null, "Edited URL receives no cookie from the initial browser request");
                Check((bool)validate.Invoke(edited, null)! && edited.CreatedTask?.Url == editedUrl &&
                    edited.CreatedTask.Cookies == null, "Submitted download retains no cookie after the URL is edited");
                edited.Close();
            }

            if (args.FirstOrDefault() == "--policies")
            {
                var service = typeof(IDMLite.MainWindow).Assembly.GetType("IDMLite.Services.ManagedBrowserPolicy")!;
                object? Invoke(string method, params object[] parameters) =>
                    service.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, parameters);
                string root = @"Software\IDMLite-Policy-Test-" + Guid.NewGuid().ToString("N");
                try
                {
                    using var registry = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root);
                    using var owners = registry.CreateSubKey("owners");
                    using var chrome = registry.CreateSubKey(@"Software\Policies\Google\Chrome\ExtensionInstallForcelist");
                    chrome.SetValue("1", "unrelated-policy");
                    Check((int)Invoke("ApplyChromium", registry, owners, "Chrome", "https://example.test/update.xml")! == 0,
                        "FORCE adds Chromium policy in an unoccupied slot");
                    Check((string?)chrome.GetValue("1") == "unrelated-policy" &&
                        (string?)chrome.GetValue("2") == "gdcnkipjepgidojhonbcfpgagchjcdci;https://example.test/update.xml",
                        "Chromium policy leaves existing entries untouched");
                    Invoke("Remove", registry, owners, "Chrome");
                    Check(chrome.GetValue("2") is null && (string?)chrome.GetValue("1") == "unrelated-policy",
                        "FORCE removal clears only the owned Chromium entry");
                    using var firefox = registry.CreateSubKey(@"Software\Policies\Mozilla\Firefox");
                    firefox.SetValue("ExtensionSettings", new[] { """{"other@example.test":{"installation_mode":"allowed"}}""" },
                        Microsoft.Win32.RegistryValueKind.MultiString);
                    Check((int)Invoke("ApplyFirefox", registry, owners, "https://example.test/signed.xpi")! == 0,
                        "FORCE merges Firefox configuration");
                    string firefoxPolicy = ((string[])firefox.GetValue("ExtensionSettings")!)[0];
                    Check(firefoxPolicy.Contains("other@example.test") && firefoxPolicy.Contains("force_installed"),
                        "Firefox policy preserves existing add-ons");
                    Invoke("Remove", registry, owners, "Firefox");
                    firefoxPolicy = ((string[])firefox.GetValue("ExtensionSettings")!)[0];
                    Check(firefoxPolicy.Contains("other@example.test") && !firefoxPolicy.Contains("idmlite@extension"),
                        "Firefox removal restores unrelated policy");
                    Console.WriteLine($"RESULT: {passed} passed");
                }
                finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, false); }
                return 0;
            }
            if (args.FirstOrDefault() == "--visual")
            {
                string output = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(output);
                if (args.Length > 2) LocalizationManager.Instance.SetLanguage(args[2]);
                foreach (var pair in LocalizationManager.Instance.GetCurrentDictionary()) app.Resources[pair.Key] = pair.Value;
                var settings = new SettingsManager(directory);
                using var queue = new DownloadQueueManager(settings);
                using var ipc = new IpcServer(queue, settings);
                var demoSegments = Enumerable.Range(0, 8).Select(i => new DownloadSegment(i, i * 85_217_280L, (i + 1) * 85_217_280L - 1)
                {
                    CurrentByte = (i + 1) * 85_217_280L,
                    IsCompleted = true
                }).ToList();
                var demoTask = new DownloadTask
                {
                    FileName = "DLSS.5.MANAGER.1.3.8.exe",
                    Url = "https://example.test/DLSS.5.MANAGER.1.3.8.exe",
                    SaveDirectory = Path.Combine(directory, "Programs"),
                    Category = DownloadCategory.Programs,
                    TotalSize = 681_738_240,
                    DownloadedSize = 681_738_240,
                    ConnectionCount = 8,
                    Status = DownloadStatus.Completed,
                    IsVerified = true,
                    Segments = demoSegments
                };
                queue.Add(demoTask);
                demoTask.Status = DownloadStatus.Completed;
                demoTask.IsVerified = true;
                demoTask.FileName = "DLSS.5.MANAGER.1.3.8.exe";
                foreach (var (name, size, cat, parts) in new[] {
                    ("Microsoft.WebView2.Fix.exe", 437_855_027L, DownloadCategory.Programs, 8),
                    ("Blackwater-doctor-1-0.rar", 6_738L, DownloadCategory.Compressed, 1),
                    ("tarihialan_- TR Kurt bakışı.pdf", 4_676_648L, DownloadCategory.Documents, 32),
                    ("Travel footage.mp4", 127_340_032L, DownloadCategory.Video, 8)
                })
                {
                    var t = new DownloadTask
                    {
                        FileName = name, Url = "https://example.test/" + name, SaveDirectory = directory,
                        Category = cat, TotalSize = size, DownloadedSize = size, ConnectionCount = parts
                    };
                    queue.Add(t);
                    t.FileName = name;
                    t.Status = DownloadStatus.Completed;
                    t.IsVerified = true;
                }
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    app.Resources.MergedDictionaries.Clear();
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"/IDMLite;component/Themes/{theme}Theme.xaml", UriKind.Relative)
                    });
                    var main = new IDMLite.MainWindow(settings, queue, ipc);
                    Capture(main, "main-" + theme);
                    var progress = new DownloadProgressDialog(demoTask, queue);
                    Capture(progress, "progress-" + theme);
                    var settingsDlg = new SettingsDialog(settings, queue);
                    Capture(settingsDlg, "settings-" + theme);
                    var preview = Create((_, _) => new TaskCompletionSource<ResolvedLinkResult>().Task);
                    Capture(preview, "preview-" + theme);
                }
                return 0;

                void Capture(Window window, string name)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -10000; window.Top = -10000; window.ShowActivated = false; window.ShowInTaskbar = false;
                    window.Show();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var content = (FrameworkElement)window.Content;
                    var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
                        (int)Math.Ceiling(content.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    image.Render(content);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    using var stream = File.Create(Path.Combine(output, name + ".png"));
                    png.Save(stream);
                    window.Hide();
                }
            }
            if (args.FirstOrDefault() == "--live")
            {
                var live = new AddDownloadDialog(new SettingsManager(directory), new DownloadRequest {
                    Url = args[1], FileName = "Browser video.mp4", Direct = true
                });
                PumpUntil(() => Size(live).Text != "—" && Size(live).Text != LocalizationManager.Instance.Get("Str_Determining"), 60000);
                Check(Size(live).Text != LocalizationManager.Instance.Get("Str_SizeUnavailable") && Size(live).Text != LocalizationManager.Instance.Get("Str_Unknown"), "live public video resolves size in the open dialog without clicking Download");
                Console.WriteLine("LIVE PREVIEW: " + Size(live).Text + "; " + ((RadioButton)live.FindName("RbStreamVideo")).Content);
                live.Close(); return 0;
            }
            var complete = new TaskCompletionSource<ResolvedLinkResult>();
            int calls = 0; CancellationToken previewToken = default;
            var dialog = Create((_, ct) => { calls++; previewToken = ct; return complete.Task; });
            Check(calls == 1 && Size(dialog).Text == "—", "browser title starts live metadata immediately without fake size");
            Check(((TextBlock)dialog.FindName("LblPreviewStatus")).Text == LocalizationManager.Instance.Get("Str_PreviewUpdating"),
                "video panel explains that downloading is available immediately");
            bool valid = (bool)typeof(AddDownloadDialog).GetMethod("ValidateAndCreateTask", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null)!;
            Check(valid && dialog.CreatedTask?.Url.Contains("instagram.com/reel/dummy") == true, "download remains available while metadata resolves");
            var wait = System.Diagnostics.Stopwatch.StartNew();
            PumpUntil(() => wait.ElapsedMilliseconds > 8300);
            Check(!previewToken.IsCancellationRequested && Size(dialog).Text == "—", "preview remains active after old eight-second cutoff");
            complete.SetResult(Result("Resolved title"));
            PumpUntil(() => Size(dialog).Text == DownloadItemViewModel.FormatBytes(20 * 1024 * 1024));
            Check(((Border)dialog.FindName("PreviewNotice")).Visibility == Visibility.Collapsed, "resolved preview clears the pending notice");
            Check(Name(dialog).Text == "Resolved title.mp4", "resolved title and size update the existing dialog live");
            Check(((RadioButton)dialog.FindName("RbStreamVideo")).Content.ToString()!.Contains("1080p"), "resolved video quality is displayed");
            ((RadioButton)dialog.FindName("RbStreamAudio")).IsChecked = true;
            typeof(AddDownloadDialog).GetMethod("UpdateSelectedStreamUI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, new object[] { true });
            Check(Size(dialog).Text == DownloadItemViewModel.FormatBytes(2 * 1024 * 1024) && Name(dialog).Text.EndsWith(".mp3"), "audio choice displays its own resolved size and filename");
            dialog.Close();
            var audioFirst = new TaskCompletionSource<ResolvedLinkResult>();
            dialog = Create((_, _) => audioFirst.Task);
            ((RadioButton)dialog.FindName("RbStreamAudio")).IsChecked = true;
            typeof(AddDownloadDialog).GetMethod("UpdateSelectedStreamUI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, new object[] { true });
            audioFirst.SetResult(Result("Audio selected"));
            PumpUntil(() => Size(dialog).Text == DownloadItemViewModel.FormatBytes(2 * 1024 * 1024));
            Check(Name(dialog).Text == "Audio selected.mp3", "audio chosen during probing remains selected when real metadata arrives");
            dialog.Close();
            var obsolete = new TaskCompletionSource<ResolvedLinkResult>(); var current = new TaskCompletionSource<ResolvedLinkResult>();
            dialog = Create((url, _) => url.Contains("second") ? current.Task : obsolete.Task);
            ((TextBox)dialog.FindName("TxtUrl")).Text = "https://www.instagram.com/reel/second/";
            Name(dialog).Text = "My custom filename.mp4";
            current.SetResult(Result("New result", 40 * 1024 * 1024));
            PumpUntil(() => Size(dialog).Text == DownloadItemViewModel.FormatBytes(40 * 1024 * 1024));
            Check(Name(dialog).Text == "My custom filename.mp4", "background result preserves a filename edited by the user");
            obsolete.SetResult(Result("Stale result"));
            PumpUntil(() => obsolete.Task.IsCompleted);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(Size(dialog).Text == DownloadItemViewModel.FormatBytes(40 * 1024 * 1024), "old URL result cannot overwrite current preview");
            dialog.Close();
            var afterClose = new TaskCompletionSource<ResolvedLinkResult>();
            dialog = Create((_, ct) => { previewToken = ct; return afterClose.Task; });
            dialog.Close(); afterClose.SetResult(Result("Closed result"));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(previewToken.IsCancellationRequested, "closing the window cancels its preview subscription");
            dialog = Create((_, _) => Task.FromResult(Result("Cached title")));
            Check(Name(dialog).Text == "Cached title.mp4" && Size(dialog).Text == DownloadItemViewModel.FormatBytes(20 * 1024 * 1024), "cached metadata is not overwritten during window initialization");
            dialog.Close();
            dialog = Create((_, _) => Task.FromResult(Result("No size", null)));
            Check(Size(dialog).Text == LocalizationManager.Instance.Get("Str_SizeUnavailable"), "server missing size is distinct from an unfinished probe");
            dialog.Close();
            var refresh = new TaskCompletionSource<ResolvedLinkResult>();
            var hint = Result("Prefetched title"); hint.Streams[0].FileSizeIsEstimated = true;
            dialog = Create((_, _) => refresh.Task, hint);
            Check(Size(dialog).Text == "≈ " + DownloadItemViewModel.FormatBytes(20 * 1024 * 1024), "prefetched size appears synchronously before resolver completion with approximation marker");
            Check(((RadioButton)dialog.FindName("RbStreamVideo")).Content.ToString()!.Contains("1080p"), "prefetched quality is visible immediately");
            refresh.SetResult(Result("Exact title"));
            PumpUntil(() => !Size(dialog).Text.StartsWith("≈"));
            Check(Size(dialog).Text == DownloadItemViewModel.FormatBytes(20 * 1024 * 1024), "exact refresh replaces approximate size without a blank interval");
            dialog.Close();
            dialog = Create((_, _) => new TaskCompletionSource<ResolvedLinkResult>().Task, hint, DateTime.UtcNow.AddMinutes(-4));
            Check(Size(dialog).Text == "—", "expired browser hint is not displayed"); dialog.Close();
            hint.OriginalUrl = "https://www.instagram.com/reel/other/";
            dialog = Create((_, _) => new TaskCompletionSource<ResolvedLinkResult>().Task, hint);
            Check(Size(dialog).Text == "—", "cached metadata cannot cross video identity"); dialog.Close();
            TorrentTransfer.Run(directory, PumpUntil, Check);
            Console.WriteLine($"RESULT: {passed} passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally { app.Shutdown(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

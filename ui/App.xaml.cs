using System.IO;
using System.Windows;
using IDMLite.Core;
using IDMLite.Windows;
using Microsoft.Win32;

namespace IDMLite;

public partial class App : System.Windows.Application
{
    private SettingsManager? _settingsManager;
    private DownloadQueueManager? _queueManager;
    private IpcServer? _ipcServer;
    private MainWindow? _mainWindow;
    private System.Threading.Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (s, args) =>
        {
            args.Handled = true;
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IDMLite");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "crash.log"), $"{DateTime.Now}: [DispatcherUnhandledException]\n{args.Exception}\n\n");
            }
            catch { }

            string detail = args.Exception.InnerException != null
                ? $"{args.Exception.Message}\n\nDetay: {args.Exception.InnerException.Message}"
                : args.Exception.Message;

            MessageBox.Show($"Bir arayüz hatası oluştu:\n{detail}", "IDMLite", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                if (args.ExceptionObject is Exception ex)
                {
                    string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IDMLite");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(Path.Combine(logDir, "crash.log"), $"{DateTime.Now}: [AppDomainUnhandledException]\n{ex}\n\n");
                }
            }
            catch { }
        };

        if (e.Args.FirstOrDefault() is "--apply-idmlite-browser-policy" or "--remove-idmlite-browser-policies")
        {
            Shutdown(IDMLite.Services.ManagedBrowserPolicy.Run(e.Args));
            return;
        }

        // Chromium & Firefox Native Messaging Host Mode
        if (e.Args.Any(a => a.Contains("native-messaging", StringComparison.OrdinalIgnoreCase) ||
                            a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                            a.Equals("idmlite@extension", StringComparison.OrdinalIgnoreCase)))
        {
            NativeMessagingHandler.RunAsync().GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        _settingsManager = new SettingsManager();
        string instanceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(_settingsManager.StorageDirectory).ToUpperInvariant())))[..20];
        _instanceMutex = new System.Threading.Mutex(true, @"Local\IDMLite-" + instanceKey, out bool firstInstance);
        if (!firstInstance) { _instanceMutex.Dispose(); _instanceMutex = null; Shutdown(); return; }
        ApplyTheme(_settingsManager.Current.Theme);

        IDMLite.Services.LocalizationManager.Instance.LanguageChanged += (s, ev) =>
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var (k, v) in IDMLite.Services.LocalizationManager.Instance.GetCurrentDictionary())
                {
                    Resources[k] = v;
                }
            });
        };
        IDMLite.Services.LocalizationManager.Instance.SetLanguage(_settingsManager.Current.Language);
        foreach (var (k, v) in IDMLite.Services.LocalizationManager.Instance.GetCurrentDictionary())
        {
            Resources[k] = v;
        }

        // Auto-register native messaging host in Windows Registry (zero configuration)
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDMLITE_DATA_DIR")))
            Task.Run(() => SettingsDialog.RegisterNativeMessagingHost());

        _queueManager = new DownloadQueueManager(_settingsManager);

        _ipcServer = new IpcServer(_queueManager, _settingsManager);
        _mainWindow = new MainWindow(_settingsManager, _queueManager, _ipcServer);
        _ipcServer.Start();
        if (e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            _mainWindow.ShowActivated = false;
            _mainWindow.WindowState = WindowState.Minimized;
            _mainWindow.Show();
        }
        else if (e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase)))
        {
            _mainWindow.ShowActivated = false;
            _mainWindow.WindowState = WindowState.Minimized;
            _mainWindow.Hide();
        }
        else
        {
            _mainWindow.Show();
        }
    }

    public static void ApplyTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme) || theme.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            theme = IsWindowsDarkMode() ? "Dark" : "Light";
        }

        string themeFile = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            ? "DarkTheme.xaml"
            : "LightTheme.xaml";

        try
        {
            var app = Current ?? Application.Current;
            if (app == null) return;

            var packUri = new Uri($"pack://application:,,,/IDMLite;component/Themes/{themeFile}", UriKind.Absolute);
            var dict = new ResourceDictionary { Source = packUri };
            
            // Safely swap theme dictionary without clearing other merged resources
            var existingTheme = app.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Theme.xaml", StringComparison.OrdinalIgnoreCase));
            
            if (existingTheme != null)
            {
                app.Resources.MergedDictionaries.Remove(existingTheme);
            }
            app.Resources.MergedDictionaries.Add(dict);
        }
        catch (Exception ex)
        {
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IDMLite");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "crash.log"), $"{DateTime.Now}: [ApplyTheme ERROR] {ex}\n\n");
            }
            catch { }
        }
    }

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                object? val = key.GetValue("AppsUseLightTheme");
                if (val is int intVal)
                {
                    return intVal == 0;
                }
            }
        }
        catch { }
        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipcServer?.Stop();
        _queueManager?.Dispose();
        if (_instanceMutex != null) { _instanceMutex.ReleaseMutex(); _instanceMutex.Dispose(); }
        base.OnExit(e);
    }
}

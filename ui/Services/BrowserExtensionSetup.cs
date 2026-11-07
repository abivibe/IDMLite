using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace IDMLite.Services;

internal static class BrowserExtensionSetup
{
    private const string ChromiumId = "gdcnkipjepgidojhonbcfpgagchjcdci";
    private const string FirefoxId = "idmlite@extension";
    private sealed record Browser(string Name, string Page, string[] Paths, bool Managed = false);

    private static readonly Browser[] Browsers =
    [
        new("Brave", "brave://extensions", [@"BraveSoftware\Brave-Browser\Application\brave.exe"], true),
        new("Chrome", "chrome://extensions", [@"Google\Chrome\Application\chrome.exe"], true),
        new("Firefox", "about:debugging#/runtime/this-firefox", [@"Mozilla Firefox\firefox.exe"], true),
        new("Opera", "opera://extensions", [@"Programs\Opera\launcher.exe", @"Opera\launcher.exe", @"Programs\Opera\opera.exe", @"Opera\opera.exe"]),
        new("Opera GX", "opera://extensions", [@"Programs\Opera GX\launcher.exe", @"Opera GX\launcher.exe", @"Programs\Opera GX\opera.exe", @"Opera GX\opera.exe"]),
        new("Yandex", "browser://extensions", [@"Yandex\YandexBrowser\Application\browser.exe"], true),
        new("Edge", "edge://extensions", [@"Microsoft\Edge\Application\msedge.exe"], true)
    ];

    public static void Show(Window? owner)
    {
        var tr = IDMLite.Services.LocalizationManager.Instance.CurrentLanguage == "tr";
        string T(string turkish, string english) => tr ? turkish : english;
        var window = new Window
        {
            Title = T("Tarayıcı uzantısını kur", "Set up browser extension"), Width = 565,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = (System.Windows.Media.Brush?)Application.Current.TryFindResource("WindowBgBrush")
        };
        if (owner is { IsVisible: true }) window.Owner = owner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        window.Content = panel;
        void Text(string value, int size = 13)
            => panel.Children.Add(new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        Text(T("Tarayıcı uzantısı", "Browser extension"), 23);
        var chooser = new ComboBox { ItemsSource = Browsers, DisplayMemberPath = nameof(Browser.Name), SelectedIndex = 0, MinHeight = 36 };
        panel.Children.Add(chooser);
        var instructions = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
        panel.Children.Add(instructions);
        var launch = new Button { Content = T("Kurulum sayfasını aç ve yolu kopyala", "Open setup page and copy path"), MinHeight = 40 };
        panel.Children.Add(launch);
        Text(T("Kurumsal FORCE: Güvenilir HTTPS dağıtım adresi gerekir. Firefox için Mozilla imzalı XPI, Chromium için güncelleme manifesti kullanın.",
            "Managed FORCE requires a trusted HTTPS URL: a Mozilla-signed XPI for Firefox or an update manifest for Chromium."));
        var endpoint = new TextBox { MinHeight = 34, ToolTip = T("HTTPS güncelleme manifesti veya imzalı Firefox XPI adresi", "HTTPS update manifest or signed Firefox XPI URL") };
        System.Windows.Automation.AutomationProperties.SetName(endpoint, T("HTTPS dağıtım adresi", "HTTPS distribution URL"));
        panel.Children.Add(endpoint);
        var force = new Button { Content = T("FORCE politikası oluştur", "Export FORCE policy"), MinHeight = 38, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(force);
        var apply = new Button { Content = T("FORCE politikasını uygula (yönetici)", "Apply FORCE policy (administrator)"),
            MinHeight = 38, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(apply);
        var remove = new Button { Content = T("IDMLite FORCE politikalarını kaldır", "Remove IDMLite FORCE policies"),
            MinHeight = 34, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(remove);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(feedback);

        Browser Selected() => (Browser)chooser.SelectedItem;
        void Refresh()
        {
            var browser = Selected();
            force.IsEnabled = endpoint.IsEnabled = browser.Managed;
            apply.IsEnabled = ManagedBrowserPolicy.CanApply(browser.Name);
            feedback.Text = "";
            instructions.Text = browser.Name switch
            {
                "Firefox" => T("Geçici Eklenti Yükle ile kopyalanan manifest.json dosyasını seçin. Kalıcı kurulum Mozilla imzası gerektirir.",
                    "Use Load Temporary Add-on and select the copied manifest.json. Permanent installation requires Mozilla signing."),
                "Yandex" => T("Paketlenmemiş yükleme seçeneği sunulursa geliştirici modundan devam edin. Kişisel Yandex mağaza dışı uzantıları engelleyebilir.",
                    "If offered, use Developer mode to load the unpacked package. Consumer Yandex may block off-store extensions."),
                _ => T("Geliştirici modunu açın, Paketlenmemiş öğe yükle seçeneğine basın ve kopyalanan klasör yolunu yapıştırın.",
                    "Enable Developer mode, choose Load unpacked, and paste the copied folder path.")
            };
        }
        chooser.SelectionChanged += (_, _) => Refresh();
        Refresh();
        launch.Click += (_, _) =>
        {
            try
            {
                var browser = Selected();
                string folder = Path.Combine(AppContext.BaseDirectory, browser.Name == "Firefox" ? "extension_firefox" : "extension");
                if (!Directory.Exists(folder) && browser.Name != "Firefox")
                    folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\extension"));
                string manifest = Path.Combine(folder, "manifest.json");
                if (!File.Exists(manifest)) throw new IOException(T("Uzantı paketi bulunamadı.", "Extension package not found."));
                string executable = FindExecutable(browser) ?? throw new IOException(T("Tarayıcı bulunamadı.", "Browser not found."));
                IDMLite.Windows.SettingsDialog.RegisterNativeMessagingHost();
                System.Windows.Clipboard.SetText(browser.Name == "Firefox" ? manifest : folder);
                var start = new ProcessStartInfo(executable) { UseShellExecute = false };
                start.ArgumentList.Add(browser.Page);
                Process.Start(start)?.Dispose();
                feedback.Text = T("Kurulum sayfası açıldı, yol kopyalandı. Tarayıcıda yüklemeyi tamamlayın.", "Setup page opened and path copied. Complete installation in the browser.");
            }
            catch (Exception ex) { feedback.Text = ex.Message; }
        };
        force.Click += (_, _) =>
        {
            try
            {
                string policy = CreatePolicy(Selected().Name, endpoint.Text.Trim());
                var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = $"idmlite-{Selected().Name.ToLowerInvariant()}-policy.json" };
                if (dialog.ShowDialog(window) == true)
                {
                    File.WriteAllText(dialog.FileName, policy);
                    feedback.Text = T("Politika kaydedildi. Tarayıcıya henüz uygulanmadı.", "Policy saved. It has not yet been applied to the browser.");
                }
            }
            catch (Exception ex) { feedback.Text = ex.Message; }
        };
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                string browser = Selected().Name;
                string address = endpoint.Text.Trim();
                _ = CreatePolicy(browser, address);
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add("--apply-idmlite-browser-policy");
                start.ArgumentList.Add(browser);
                start.ArgumentList.Add(address);
                using var process = Process.Start(start) ?? throw new IOException("Policy helper did not start.");
                await process.WaitForExitAsync();
                feedback.Text = process.ExitCode switch
                {
                    0 => T("Politika yazıldı. Tarayıcıyı yeniden başlatıp uzantıyı kontrol edin.", "Policy written. Restart the browser and verify the extension."),
                    4 => T("Mağaza dışı FORCE için Active Directory etki alanı gerekir.", "Off-store FORCE requires an Active Directory domain."),
                    5 => T("Bu uzantı için mevcut yönetici politikası korunuyor.", "The existing administrator policy for this extension was preserved."),
                    _ => T("Politika yazılamadı.", "Could not write the policy.")
                };
            }
            catch (Exception ex) { feedback.Text = ex.Message; }
            finally { apply.IsEnabled = ManagedBrowserPolicy.CanApply(Selected().Name); }
        };
        remove.Click += async (_, _) =>
        {
            remove.IsEnabled = false;
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add("--remove-idmlite-browser-policies");
                using var process = Process.Start(start) ?? throw new IOException("Policy helper did not start.");
                await process.WaitForExitAsync();
                feedback.Text = process.ExitCode == 0
                    ? T("IDMLite'a ait politikalar kaldırıldı. Tarayıcıyı yeniden başlatın.", "IDMLite policies removed. Restart the browser.")
                    : T("Politika kaldırılamadı.", "Could not remove the policy.");
            }
            catch (Exception ex) { feedback.Text = ex.Message; }
            finally { remove.IsEnabled = true; }
        };
        window.ShowDialog();
    }

    internal static string CreatePolicy(string browser, string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || address.Contains(';'))
            throw new ArgumentException("Use a valid HTTPS distribution URL.");
        object policy = browser switch
        {
            "Firefox" => new { policies = new { ExtensionSettings = new Dictionary<string, object> {
                [FirefoxId] = new { installation_mode = "force_installed", install_url = uri.AbsoluteUri }
            } } },
            "Brave" or "Chrome" or "Yandex" or "Edge" => new { ExtensionInstallForcelist = new[] { $"{ChromiumId};{uri.AbsoluteUri}" } },
            _ => throw new NotSupportedException("This browser has no supported FORCE policy in IDMLite.")
        };
        return JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? FindExecutable(Browser browser)
    {
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)];
        return roots.Where(root => !string.IsNullOrEmpty(root))
            .SelectMany(root => browser.Paths.Select(path => Path.Combine(root, path))).FirstOrDefault(File.Exists);
    }
}

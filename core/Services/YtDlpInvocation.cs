using System.Diagnostics;
using System.Text;

namespace IDMLite.Core.Services;

/// <summary>Shared, shell-independent options for metadata and download processes.</summary>
public sealed class YtDlpInvocation : IDisposable
{
    public const string VideoSelector = "bestvideo[vcodec^=avc1][ext=mp4]+bestaudio[ext=m4a]/bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best";
    private string? _cookieFile;

    public YtDlpInvocation(ProcessStartInfo process, string url, string? cookies)
    {
        foreach (string arg in new[] { "--ignore-config", "--no-plugin-dirs", "--no-playlist", "--socket-timeout", "25" })
            process.ArgumentList.Add(arg);
        // Some Windows/network IPv6 paths fail the Python TLS handshake or bind
        // CDN URLs to a different route. Keep YouTube extraction and transfer on
        // the same IPv4 path; retain normal certificate verification.
        if (MediaUrl.HasHost(url, "youtube.com", "youtu.be", "googlevideo.com"))
            process.ArgumentList.Add("--force-ipv4");
        string node = Path.Combine(AppContext.BaseDirectory, "node.exe");
        process.ArgumentList.Add("--js-runtimes");
        process.ArgumentList.Add(File.Exists(node) ? "node:" + node : "node");

        // A global Cookie header leaks login cookies to CDN/redirect hosts and
        // does not provide the cookie jar required by authenticated extractors.
        if (!string.IsNullOrWhiteSpace(cookies) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string host = uri.Host;
            var lines = new List<string> { "# Netscape HTTP Cookie File" };
            foreach (string part in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0 || part.Contains('\r') || part.Contains('\n') || part.Contains('\t')) continue;
                lines.Add($"{host}\tFALSE\t/\tTRUE\t0\t{part[..equals].Trim()}\t{part[(equals + 1)..].Trim()}");
            }
            var directory = Path.Combine(Environment.GetEnvironmentVariable("IDMLITE_DATA_DIR") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IDMLite"), "ExtractorTemp");
            PrivateStorage.EnsureDirectory(directory);
            _cookieFile = Path.Combine(directory, $"idmlite-cookies-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(_cookieFile, lines, new UTF8Encoding(false));
            process.ArgumentList.Add("--cookies");
            process.ArgumentList.Add(_cookieFile);
        }
    }

    public void Dispose()
    {
        if (_cookieFile != null) { try { File.Delete(_cookieFile); } catch { } }
    }
}

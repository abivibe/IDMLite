using MonoTorrent;
using MonoTorrent.Client;

namespace IDMLite.Core;

public static class TorrentSource
{
    private const int MetadataLimit = 16 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        MaxResponseContentBufferSize = MetadataLimit
    };

    public static bool Matches(string source, string? fileName = null) =>
        source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        (uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ||
         fileName?.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == true);

    public static async Task<Torrent> ReadAsync(ClientEngine engine, string source, CancellationToken token, string? cookies = null, Action<byte[]>? persist = null)
    {
        byte[] metadata;
        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            metadata = (await engine.DownloadMetadataAsync(MagnetLink.Parse(source), token).ConfigureAwait(false)).ToArray();
        }
        else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            metadata = await DownloadMetadataAsync(uri, cookies, token).ConfigureAwait(false);
        }
        else
        {
            await using var stream = File.OpenRead(source);
            if (stream.Length > MetadataLimit) throw new InvalidDataException("Torrent metadata exceeds 16 MiB.");
            metadata = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(metadata, token).ConfigureAwait(false);
        }

        return await Task.Run(() =>
        {
            if (metadata.Length > MetadataLimit) throw new InvalidDataException("Torrent metadata exceeds 16 MiB.");
            var torrent = Torrent.Load(metadata);
            ValidateNames(torrent);
            persist?.Invoke(metadata);
            return torrent;
        }, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> DownloadMetadataAsync(Uri origin, string? cookies, CancellationToken token)
    {
        Uri uri = origin;
        for (int hop = 0; hop <= 10; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (hop == 0 && !string.IsNullOrEmpty(cookies))
                request.Headers.Add("Cookie", cookies);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 &&
                response.Headers.Location is { } location && Uri.TryCreate(uri, location, out var next) &&
                next.Scheme is "http" or "https")
            {
                uri = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(MetadataLimit, token);
            return await response.Content.ReadAsByteArrayAsync(token);
        }
        throw new HttpRequestException("Too many torrent metadata redirects.");
    }

    private static void ValidateNames(Torrent torrent)
    {
        static bool Safe(string part) => part.Length > 0 && part == SegmentedDownloader.MakeSafeFileName(part);
        if (!Safe(torrent.Name)) throw new InvalidDataException("Invalid torrent name.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in torrent.Files)
        {
            string path = file.Path.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(path) || !path.Split(Path.DirectorySeparatorChar).All(Safe) ||
                !paths.Add(path) || folders.Contains(path))
                throw new InvalidDataException("Invalid torrent file path.");
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (paths.Contains(parent)) throw new InvalidDataException("Conflicting torrent file paths.");
                folders.Add(parent);
            }
        }
    }
}

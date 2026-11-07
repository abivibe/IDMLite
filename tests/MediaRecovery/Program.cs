using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IDMLite.Core;
using IDMLite.Core.Services;

if (args.FirstOrDefault() == "worker") {
    using var queue = new DownloadQueueManager(new SettingsManager(args[1]));
    queue.Add(new DownloadTask { Url = args[2], FileName = args[3], SaveDirectory = args[4], Category = DownloadCategory.Video, IsDirect = true, ConnectionCount = 4 }, true);
    await Task.Delay(Timeout.Infinite); return;
}
async Task Until(Func<bool> check) { using var timeout = new CancellationTokenSource(45000); while (!check()) await Task.Delay(30, timeout.Token); }
void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
async Task<string> Run(string executable, params string[] arguments) {
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string arg in arguments) info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!;
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    await Task.WhenAll(output, error, process.WaitForExitAsync());
    if (process.ExitCode != 0) throw new Exception(await error);
    return await output;
}
string root = Path.Combine(Path.GetTempPath(), "IDMLite-media-recovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    string ffmpeg = YtDlpHelper.FindFfmpegPath() ?? throw new Exception("Packaged FFmpeg unavailable");
    string source = Path.Combine(root, "source.mp4");
    await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "20", "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "4M", "-g", "30", "-sc_threshold", "0", "-c:a", "aac", "-movflags", "+faststart", source);
    await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", source, "-c", "copy", "-hls_time", "1", "-hls_list_size", "0", "-hls_playlist_type", "vod", "-hls_segment_filename", Path.Combine(root, "seg%03d.ts"), Path.Combine(root, "playlist.m3u8"));
    using var server = new MediaFixture(root);
    foreach (string type in new[] { "direct", "hls" }) {
        string state = Path.Combine(root, type + "-state"), output = Path.Combine(root, type + "-output"); Directory.CreateDirectory(output);
        var settings = new SettingsManager(state); settings.Current.MetadataDirectory = Path.Combine(state, "recovery"); settings.Current.MaxConnectionsPerDownload = 4; settings.Save();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "worker", state, server.Url + (type == "direct" ? "source.mp4" : "playlist.m3u8"), "clip.mp4", output }) info.ArgumentList.Add(arg);
        string? metadata = null;
        using (var child = Process.Start(info)!) {
            try {
                await Until(() => {
                    metadata = Directory.Exists(settings.Current.MetadataDirectory) ? Directory.EnumerateFiles(settings.Current.MetadataDirectory, "*.idmlitemeta").FirstOrDefault() : null;
                    if (metadata == null) return false;
                    if (type == "direct") return RecoveryStore.Load(metadata)?.Http?.Segments.Sum(s => s.DownloadedBytes) > 0;
                    foreach (string journal in Directory.EnumerateFiles(output, "*.ytdl")) {
                        try { using var doc = JsonDocument.Parse(File.ReadAllText(journal)); return doc.RootElement.GetProperty("downloader").GetProperty("current_fragment").GetProperty("index").GetInt32() >= 4; } catch { }
                    }
                    return false;
                });
            } finally { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
        Check(metadata != null && RecoveryStore.Load(metadata) != null, type + " leaves a recovery file after abrupt termination");
        File.Delete(Path.Combine(state, "downloads.json")); File.Delete(Path.Combine(state, "downloads.json.bak"));
        int segmentsBefore = server.SegmentGets;
        using var recovered = new DownloadQueueManager(new SettingsManager(state));
        var item = recovered.GetTasksSnapshot().Single();
        await Until(() => item.Status is DownloadStatus.Completed or DownloadStatus.Error);
        Check(item.Status == DownloadStatus.Completed, type + " automatically resumes: " + item.ErrorMessage);
        string probeJson = await Run(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"), "-v", "error", "-show_entries", "stream=codec_type:format=duration", "-of", "json", item.FullPath);
        using var probe = JsonDocument.Parse(probeJson);
        var streamTypes = probe.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToList();
        Check(streamTypes.Contains("video") && streamTypes.Contains("audio") && double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 19, type + " recovered file contains full-duration video and audio");
        if (type == "direct") Check(File.ReadAllBytes(item.FullPath).SequenceEqual(File.ReadAllBytes(source)), "Direct video resumes without changing any source bytes");
        else Check(server.SegmentGets - segmentsBefore < server.SegmentCount, "HLS resumes its native fragment journal without redownloading all fragments");
    }
}
finally { Directory.Delete(root, true); }

sealed class MediaFixture : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, byte[]> files;
    public string Url { get; }
    public int SegmentGets;
    public int SegmentCount => files.Keys.Count(n => n.EndsWith(".ts"));
    public MediaFixture(string root) {
        files = Directory.GetFiles(root).Where(p => p.EndsWith(".mp4") || p.EndsWith(".ts") || p.EndsWith(".m3u8")).ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
        var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        Url = $"http://127.0.0.1:{port}/"; listener.Prefixes.Add(Url); listener.Start(); _ = Serve();
    }
    private async Task Serve() { while (listener.IsListening) { try { var context = await listener.GetContextAsync(); _ = Reply(context); } catch { break; } } }
    private async Task Reply(HttpListenerContext context) {
        try {
            string name = Path.GetFileName(context.Request.Url!.AbsolutePath); var response = context.Response;
            if (!files.TryGetValue(name, out var data)) { response.StatusCode = 404; response.Close(); return; }
            response.ContentType = name.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : name.EndsWith(".ts") ? "video/mp2t" : "video/mp4";
            response.AddHeader("ETag", "\"media-v1\""); response.AddHeader("Accept-Ranges", "bytes");
            int start = 0, end = data.Length - 1;
            if (context.Request.Headers["Range"] is string range) {
                var parts = range[6..].Split('-'); start = int.Parse(parts[0]); end = parts[1].Length > 0 ? int.Parse(parts[1]) : end;
                response.StatusCode = 206; response.AddHeader("Content-Range", $"bytes {start}-{end}/{data.Length}");
            }
            response.ContentLength64 = end - start + 1;
            if (context.Request.HttpMethod != "HEAD") {
                if (name.EndsWith(".ts")) { Interlocked.Increment(ref SegmentGets); await Task.Delay(700); }
                for (int pos = start; pos <= end; pos += 8192) {
                    await response.OutputStream.WriteAsync(data.AsMemory(pos, Math.Min(8192, end - pos + 1)));
                    if (name.EndsWith(".mp4")) await Task.Delay(12);
                }
            }
            response.Close();
        } catch { try { context.Response.Abort(); } catch { } }
    }
    public void Dispose() { listener.Stop(); listener.Close(); }
}

using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using IDMLite.Core;
using IDMLite.Core.Resolvers;

int passed = 0;
void Check(bool result, string name) { if (!result) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
int FreePort() { var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port; }
string directory = Path.Combine(Path.GetTempPath(), "IDMLite-security-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
Environment.SetEnvironmentVariable("IDMLITE_DATA_DIR", directory);
try
{
    var settings = new SettingsManager(Path.Combine(directory, "state"));
    settings.Current.MetadataDirectory = Path.Combine(directory, "recovery");
    settings.Current.IpcPort = FreePort();
    using var queue = new DownloadQueueManager(settings);
    var task = new DownloadTask { Url = "http://127.0.0.1/dummy", FileName = "dummy.bin", SaveDirectory = directory };
    queue.Add(task);
    int previewCalls = 0;
    using var ipc = new IpcServer(queue, settings, (url, cookies, ct) => {
        previewCalls++;
        return Task.FromResult(new ResolvedLinkResult { OriginalUrl = url, DirectUrl = "https://cdn.example/signed?secret=DUMMY", Cookies = "DUMMY_SECRET",
            FileSize = 100000, Streams = new() { new() { Url = "https://cdn.example/signed?secret=DUMMY", FileSize = 100000, Quality = "1080p" } } });
    }); ipc.Start();
    string endpoint = $"http://127.0.0.1:{ipc.Port}";
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    using (var response = await client.GetAsync(endpoint + "/api/downloads")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated queue read rejected");
    using (var response = await client.PostAsync(endpoint + "/api/preview", new StringContent("{\"url\":\"https://www.youtube.com/watch?v=dummy\"}")))
        Check(response.StatusCode == HttpStatusCode.Unauthorized && previewCalls == 0, "metadata prefetch requires authentication");
    using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/api/action") { Content = new StringContent($"{{\"id\":\"{task.Id}\",\"action\":\"delete\"}}") })
    {
        request.Headers.Add("Origin", "moz-extension://unrelated-fixture");
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.Unauthorized && queue.GetTasksSnapshot().Any(t => t.Id == task.Id), "unrelated Firefox extension cannot delete a download");
    }
    client.DefaultRequestHeaders.Add("X-IDMLite-Token", "invalid-dummy-token");
    using (var response = await client.GetAsync(endpoint + "/api/downloads")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "incorrect token rejected");
    client.DefaultRequestHeaders.Remove("X-IDMLite-Token"); client.DefaultRequestHeaders.Add("X-IDMLite-Token", settings.Current.IpcToken);
    using (var response = await client.GetAsync(endpoint + "/api/downloads")) Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("dummy.bin"), "authorized owner reads its queue");
    using (var response = await client.PostAsync(endpoint + "/api/preview", new StringContent("{\"url\":\"https://www.youtube.com/watch?v=dummy\"}")))
    {
        string json = await response.Content.ReadAsStringAsync();
        Check(response.IsSuccessStatusCode && previewCalls == 1 && queue.GetTasksSnapshot().Count == 1, "authenticated prefetch resolves without adding a download");
        Check(!json.Contains("DUMMY_SECRET") && !json.Contains("signed?secret"), "browser cache response excludes cookies and signed CDN URLs");
    }
    using (var response = await client.PostAsync(endpoint + "/api/preview", new StringContent("{\"url\":\"http://127.0.0.1/internal\"}")))
        Check(response.StatusCode == HttpStatusCode.BadRequest && previewCalls == 1, "prefetch endpoint rejects arbitrary non-platform destinations");
    foreach (string route in new[] { "/api/download", "/api/action", "/api/preview" })
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + route) { Content = new StreamContent(new MemoryStream(new byte[1024 * 1024 + 1])) };
        request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        Check((int)response.StatusCode == 413, "chunked oversized request rejected at " + route);
    }
    using (var request = new HttpRequestMessage(HttpMethod.Options, endpoint + "/api/download"))
    {
        request.Headers.Add("Origin", "chrome-extension://gdcnkipjepgidojhonbcfpgagchjcdci");
        using var response = await client.SendAsync(request);
        Check(response.Headers.GetValues("Access-Control-Allow-Origin").Single() != "*", "CORS replies with explicit origin");
    }
    using (var response = await client.PostAsync(endpoint + "/api/action", new StringContent("{"))) Check((int)response.StatusCode == 400, "malformed JSON is a bounded client error");

    int port = FreePort();
    using var fixture = new HttpListener(); fixture.Prefixes.Add($"http://127.0.0.1:{port}/"); fixture.Prefixes.Add($"http://localhost:{port}/"); fixture.Start();
    var seen = new List<string?>();
    var serving = Task.Run(async () => {
        for (int i = 0; i < 2; i++)
        {
            var context = await fixture.GetContextAsync(); seen.Add(context.Request.Headers["Cookie"]);
            if (i == 0) { context.Response.StatusCode = 302; context.Response.RedirectLocation = $"http://localhost:{port}/destination"; context.Response.Headers.Add("Set-Cookie", "foreign=DUMMY; Domain=localhost; Path=/"); }
            else { context.Response.ContentLength64 = 1; await context.Response.OutputStream.WriteAsync(new byte[] { 42 }); }
            context.Response.Close();
        }
    });
    using (var download = new SegmentedDownloader($"http://127.0.0.1:{port}/start", Path.Combine(directory, "dummy.bin")) { Cookies = "session=DUMMY_OWNER", SkipResolver = true, CustomHeaders = new() { ["Cookie"] = "override=DUMMY" } })
    {
        var method = typeof(SegmentedDownloader).GetMethod("SendWithRedirectsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var response = await (Task<HttpResponseMessage>)method.Invoke(download, new object?[] { HttpMethod.Get, download.Url, null, CancellationToken.None, 12 })!;
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
        Check(seen[0] == "session=DUMMY_OWNER", "initial request uses only scoped owner cookies");
        Check(string.IsNullOrEmpty(seen[1]), "cross-host redirect strips cookies and rejects foreign Set-Cookie");
    }
    fixture.Stop();

    using var rsa = RSA.Create(2048);
    var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    foreach (Type resolver in new[] { typeof(CobaltResolver), typeof(CustomPlayerResolver) })
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int tlsPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connection = Task.Run(async () => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(socket.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                var buffer = new byte[4096]; _ = await tls.ReadAsync(buffer, timeout.Token);
                await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"), timeout.Token);
            } catch (Exception) { }
        });
        var http = (HttpClient)resolver.GetField("HttpClient", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        bool rejected = false;
        try { using var response = await http.GetAsync($"https://localhost:{tlsPort}/"); } catch (HttpRequestException) { rejected = true; }
        listener.Stop(); await connection.WaitAsync(TimeSpan.FromSeconds(12));
        Check(rejected, resolver.Name + " rejects untrusted TLS certificate");
    }
    if (OperatingSystem.IsWindows())
    {
        var security = new DirectoryInfo(settings.Current.MetadataDirectory).GetAccessControl();
        Check(security.AreAccessRulesProtected, "recovery directory blocks inherited broad ACLs");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var allowed = new[] { currentUser.Value, "S-1-5-18", "S-1-5-32-544" };
        Check(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().All(rule => OperatingSystem.IsWindows() && (rule.AccessControlType != AccessControlType.Allow || allowed.Contains(rule.IdentityReference.Value))), "recovery access limited to owner SYSTEM and administrators");
        byte[] encrypted = File.ReadAllBytes(task.RecoveryFilePath!);
        Check(!Encoding.UTF8.GetString(encrypted).Contains("dummy.bin"), "recovery task data is encrypted at rest");
    }
    string atomic = Path.Combine(directory, "atomic.json");
    File.WriteAllText(atomic + ".tmp", "DUMMY_SENTINEL");
    RecoveryStore.AtomicWrite(atomic, Encoding.UTF8.GetBytes("new"));
    Check(File.ReadAllText(atomic + ".tmp") == "DUMMY_SENTINEL", "atomic writer does not overwrite predictable temporary path");
    using (var hls = new HlsDownloader("https://media.example.test/list.m3u8", Path.Combine(directory, "out.mp4")) { Cookies = "session=DUMMY_HLS" })
    {
        var method = typeof(HlsDownloader).GetMethod("FfmpegStartInfo", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var invocation = (System.Diagnostics.ProcessStartInfo)method.Invoke(hls, new object?[] { "ffmpeg.exe", Path.Combine(directory, "out.mp4"), false, null })!;
        string headers = invocation.ArgumentList[invocation.ArgumentList.IndexOf("-headers") + 1];
        string scoped = invocation.ArgumentList[invocation.ArgumentList.IndexOf("-cookies") + 1];
        Check(!headers.Contains("Cookie:") && scoped.Contains("domain=media.example.test") && scoped.Contains("secure;"), "FFmpeg uses scoped cookies instead of a global Cookie header");
        Check(invocation.ArgumentList.Contains("-tls_verify") && invocation.Arguments.Length == 0, "FFmpeg verifies TLS and uses shell-independent argument boundaries");
    }
    Console.WriteLine($"RESULT: {passed} passed");
}
finally { Directory.Delete(directory, true); }

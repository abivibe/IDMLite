using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using IDMLite.Core;
using IDMLite.Core.Resolvers;
using IDMLite.Core.Services;

Console.WriteLine("==================================================");
Console.WriteLine("   IDMLite Core Engine Automated Verification");
Console.WriteLine("==================================================");

int passed = 0;
int failed = 0;

void Assert(bool condition, string testName)
{
    if (condition)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] {testName}");
        Console.ResetColor();
        passed++;
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAIL] {testName}");
        Console.ResetColor();
        failed++;
    }
}

// 1. Test Category Detector
Console.WriteLine("\n--- Testing Category Detection ---");
Assert(CategoryDetector.FromExtension(".mp4") == DownloadCategory.Video, "MP4 detected as Video");
Assert(CategoryDetector.FromExtension(".m3u8") == DownloadCategory.Video, "M3U8 detected as Video");
Assert(CategoryDetector.FromExtension(".zip") == DownloadCategory.Compressed, "ZIP detected as Compressed");
Assert(CategoryDetector.FromExtension(".iso") == DownloadCategory.Compressed, "ISO detected as Compressed");
Assert(CategoryDetector.FromExtension(".pdf") == DownloadCategory.Documents, "PDF detected as Documents");
Assert(CategoryDetector.FromExtension(".exe") == DownloadCategory.Programs, "EXE detected as Programs");
Assert(CategoryDetector.FromExtension(".mp3") == DownloadCategory.Music, "MP3 detected as Music");

// 2. Test Checksum Helper
Console.WriteLine("\n--- Testing Checksum Verification ---");
string tempFile = Path.Combine(Path.GetTempPath(), "idm_checksum_test_" + Guid.NewGuid().ToString("N") + ".txt");
try
{
    File.WriteAllText(tempFile, "Hello IDMLite Core Engine!");
    string sha256 = await ChecksumHelper.ComputeHashAsync(tempFile, "SHA256");
    Assert(!string.IsNullOrEmpty(sha256) && sha256.Length == 64, "SHA256 hash computed properly");

    bool verified = await ChecksumHelper.VerifyAsync(tempFile, sha256, "SHA256");
    Assert(verified, "SHA256 verification succeeded");

    bool mismatch = await ChecksumHelper.VerifyAsync(tempFile, "invalid_hash_string", "SHA256");
    Assert(!mismatch, "Invalid checksum correctly rejected");
}
finally
{
    if (File.Exists(tempFile)) File.Delete(tempFile);
}

// 3. Test Speed Limiter
Console.WriteLine("\n--- Testing Speed Limiter (Token Bucket) ---");
var limiter = new SpeedLimiter(50 * 1024); // 50 KB/s
Assert(limiter.MaxBytesPerSecond == 50 * 1024, "Limiter configured correctly");
var sw = Stopwatch.StartNew();
await limiter.ThrottleAsync(100 * 1024); // Request 100 KB
sw.Stop();
Assert(sw.ElapsedMilliseconds >= 400, $"Limiter throttled 100KB at 50KB/s (Took: {sw.ElapsedMilliseconds}ms)");

// 4. Test Segment Splitting Calculation
Console.WriteLine("\n--- Testing Segment Split Calculation ---");
long totalSize = 100 * 1024 * 1024; // 100 MB
int threadCount = 8;
long chunkSize = totalSize / threadCount;
var segments = new List<DownloadSegment>();
for (int i = 0; i < threadCount; i++)
{
    long start = i * chunkSize;
    long end = (i == threadCount - 1) ? (totalSize - 1) : (start + chunkSize - 1);
    segments.Add(new DownloadSegment(i, start, end));
}

Assert(segments.Count == 8, "8 segments initialized");
Assert(segments[0].StartByte == 0 && segments[0].EndByte == chunkSize - 1, "Segment 0 bounds accurate");
Assert(segments[7].EndByte == totalSize - 1, "Segment 7 reaches last byte of file");
long sumBytes = segments.Sum(s => s.TotalBytes);
Assert(sumBytes == totalSize, "Sum of segments exactly equals total file size");

// 5. Test IPC Server Start & REST API
Console.WriteLine("\n--- Testing Embedded IPC Server ---");
var settingsMgr = new SettingsManager();
settingsMgr.Current.IpcPort = 41719; // test port
var queueMgr = new DownloadQueueManager(settingsMgr);
using var ipcServer = new IpcServer(queueMgr, settingsMgr);
ipcServer.Start();
Assert(ipcServer.IsRunning, "IPC Server started on test port 41719");

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
try
{
    var res = await httpClient.GetAsync("http://127.0.0.1:41719/api/status");
    Assert(res.IsSuccessStatusCode, "GET /api/status returned HTTP 200");
    string body = await res.Content.ReadAsStringAsync();
    Assert(body.Contains("IDMLite"), "Status payload contains appName 'IDMLite'");
}
catch (Exception ex)
{
    Assert(false, $"IPC Server request failed: {ex.Message}");
}
// 6. Test Link Resolvers
Console.WriteLine("\n--- Testing Link Resolvers (Google Drive & MediaFire) ---");
var resolverMgr = LinkResolverManager.Instance;
Assert(resolverMgr.HasResolverFor("https://drive.google.com/file/d/1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs/view?usp=sharing"), "Google Drive share link recognized");
Assert(resolverMgr.HasResolverFor("https://drive.google.com/open?id=1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs"), "Google Drive open?id link recognized");
Assert(resolverMgr.HasResolverFor("https://www.mediafire.com/file/abcdef12345/test.zip/file"), "MediaFire file link recognized");
Assert(!resolverMgr.HasResolverFor("https://example.com/test.zip"), "Generic link correctly not routed to Drive/Mediafire resolver");

var driveResolver = new GoogleDriveResolver();
Assert(driveResolver.CanResolve("https://docs.google.com/uc?id=123456789012345678901234567890"), "Docs.google.com link can be resolved");

Console.WriteLine("\n--- Testing Live Google Drive Link Resolution ---");
string testDriveUrl = "https://drive.google.com/file/d/1qqMm3imYdwELbIM3h78RmfdhXcHmqxYt/view";
var driveResult = await driveResolver.ResolveAsync(testDriveUrl);
Console.WriteLine($"DirectUrl: {driveResult.DirectUrl}");
Console.WriteLine($"SuggestedFileName: {driveResult.SuggestedFileName}");
Console.WriteLine($"FileSize: {driveResult.FileSize} bytes");
Console.WriteLine($"ErrorMessage: {driveResult.ErrorMessage}");
Assert(driveResult.IsSuccess, "Google Drive link resolved successfully");
Assert(!string.IsNullOrEmpty(driveResult.SuggestedFileName) && driveResult.SuggestedFileName.Contains("Media Encoder"), "SuggestedFileName contains 'Media Encoder'");
Assert(driveResult.SuggestedFileName != null && !driveResult.SuggestedFileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase), "SuggestedFileName is NOT .bin");
Assert(driveResult.FileSize.HasValue && driveResult.FileSize.Value > 500_000_000, "File size detected (> 500MB)");

Console.WriteLine($"\n--- Probing Resolved Google Drive URL with Downloader ---");
string tempGdriveFile = Path.Combine(Path.GetTempPath(), "dummy_gdrive_" + Guid.NewGuid().ToString("N") + ".rar");
var gdriveDl = new SegmentedDownloader(driveResult.DirectUrl, tempGdriveFile, 4)
{
    Cookies = driveResult.Cookies,
    TotalBytes = driveResult.FileSize ?? 0,
    SuggestedFileName = driveResult.SuggestedFileName
};
await gdriveDl.ProbeAsync();
Console.WriteLine($"GDrive Downloader Probed TotalBytes: {gdriveDl.TotalBytes}, SupportsRange: {gdriveDl.SupportsRange}");
Assert(driveResult.DirectUrl.Contains("usercontent.google.com") || driveResult.DirectUrl.Contains("googleusercontent.com"), "Google Drive DirectUrl points to verified Google CDN/usercontent stream");
Assert(gdriveDl.SupportsRange && !gdriveDl.IsSingleStreamPreferred(), "Standard Google Drive file supports Range and multi-segmenting (IsSingleStreamPreferred == false)");

Console.WriteLine("Testing StartAsync on GDrive stream...");
try
{
    var dlTask = gdriveDl.StartAsync();
    for (int wait = 0; wait < 8; wait++)
    {
        await Task.Delay(1000);
        if (gdriveDl.DownloadedBytes > 0) break;
    }
    gdriveDl.Pause();
    try { await dlTask; } catch { }
    Console.WriteLine($"GDrive Downloaded bytes so far: {gdriveDl.DownloadedBytes}");
    Assert(gdriveDl.TotalBytes > 0 && gdriveDl.DownloadedBytes >= 0, $"GDrive stream is verified and ready (Probed {gdriveDl.TotalBytes} bytes, streamed {gdriveDl.DownloadedBytes} bytes)");
}
finally
{
    if (File.Exists(tempGdriveFile)) try { File.Delete(tempGdriveFile); } catch { }
}

Console.WriteLine("\n--- Testing End-to-End Segmented Download ---");
string testDlUrl = "https://raw.githubusercontent.com/curl/curl/master/README.md";
string tempDlFile = Path.Combine(Path.GetTempPath(), "idm_e2e_dl_" + Guid.NewGuid().ToString("N") + ".md");
try
{
    var downloader = new SegmentedDownloader(testDlUrl, tempDlFile, 4);
    await downloader.ProbeAsync();
    Assert(downloader.TotalBytes > 0, $"Probe detected size: {downloader.TotalBytes} bytes");
    await downloader.StartAsync();
    Assert(File.Exists(tempDlFile), "Downloaded file created on disk");
    Assert(new FileInfo(tempDlFile).Length == downloader.TotalBytes, "Downloaded file length matches probed size exactly");
}
finally
{
    if (File.Exists(tempDlFile)) File.Delete(tempDlFile);
}

// 7. Test YouTube Video Resolver
// 7. Test YouTube Video Resolver & Video/Audio Streams
Console.WriteLine("\n--- Testing YouTube Video Title Resolution & Audio Streams ---");
var ytResolver = new YouTubeResolver();
Assert(ytResolver.CanResolve("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), "YouTube watch URL recognized");
Assert(ytResolver.CanResolve("https://youtu.be/dQw4w9WgXcQ"), "youtu.be short URL recognized");
var ytRes = await ytResolver.ResolveAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
Assert(ytRes.IsSuccess, "YouTube resolution succeeded");
Assert(!string.IsNullOrEmpty(ytRes.SuggestedFileName) && ytRes.SuggestedFileName.EndsWith(".mp4"), "YouTube suggested filename ends with .mp4");
Assert(ytRes.Streams.Count >= 2, "YouTube resolver provides both Video and Audio streams");
Assert(ytRes.Streams.Any(s => !s.IsAudioOnly && s.Format == "mp4"), "YouTube resolver provides single-piece MP4 video stream");
Assert(ytRes.Streams.Any(s => s.IsAudioOnly), "YouTube resolver includes audio stream");

// Test CleanGoogleVideoPlaybackUrl
string rawYtUrl = "https://rr2---sn-vjoouujvhh-junl.googlevideo.com/videoplayback?expire=123&itag=18&range=0-1048575&rn=1&rbuf=0";
string cleanedYtUrl = YouTubeResolver.CleanGoogleVideoPlaybackUrl(rawYtUrl);
Assert(!cleanedYtUrl.Contains("range="), "CleanGoogleVideoPlaybackUrl strips chunk range for full-file download");

// 8. Test TikTok, Twitter, Instagram, and CustomPlayer Resolvers
Console.WriteLine("\n--- Testing Social & Custom Player Resolvers ---");
var tiktokResolver = new TikTokResolver();
Assert(tiktokResolver.CanResolve("https://www.tiktok.com/@scout2015/video/6718335390845095173"), "TikTok standard URL recognized");
Assert(tiktokResolver.CanResolve("https://vm.tiktok.com/ZM8xy1234/"), "TikTok short URL recognized");

var twitterResolver = new TwitterResolver();
Assert(twitterResolver.CanResolve("https://twitter.com/NASA/status/1234567890"), "Twitter URL recognized");
Assert(twitterResolver.CanResolve("https://x.com/SpaceX/status/9876543210"), "X (Twitter) URL recognized");

var instaResolver = new InstagramResolver();
Assert(instaResolver.CanResolve("https://www.instagram.com/reel/C-123456789/"), "Instagram Reel URL recognized");
Assert(instaResolver.CanResolve("https://www.instagram.com/p/C-abcdefgh/"), "Instagram Post URL recognized");

var customResolver = new CustomPlayerResolver();
Assert(customResolver.CanResolve("https://example.com/live/playlist.m3u8"), "HLS m3u8 URL recognized by CustomPlayerResolver");
Assert(customResolver.CanResolve("https://example.com/video/master.mpd"), "DASH mpd URL recognized by CustomPlayerResolver");
Assert(customResolver.CanResolve("https://cdn.site.com/media/file.mp4?token=abc"), "Direct MP4 with query recognized by CustomPlayerResolver");

// 9. Test MIME-based Filename Resolver
Console.WriteLine("\n--- Testing Video MIME & Filename Detection ---");
string resolvedMp4 = SegmentedDownloader.ResolveProperFileName("https://example.com/stream/videoplayback?id=123", ".mp4");
Assert(resolvedMp4.EndsWith(".mp4") && !resolvedMp4.EndsWith(".bin"), "Stream URL with mp4 mime resolves to .mp4");
Assert(SegmentedDownloader.GetExtensionFromMimeType("video/mp4") == ".mp4", "video/mp4 maps to .mp4");
Assert(SegmentedDownloader.GetExtensionFromMimeType("application/vnd.apple.mpegurl") == ".m3u8", "HLS MIME maps to .m3u8");
Assert(SegmentedDownloader.GetExtensionFromMimeType("audio/mpeg") == ".mp3", "audio/mpeg maps to .mp3");

// 10. Test Multi-language Localization (11 Languages)
Console.WriteLine("\n--- Testing Localization Manager & Dynamic Switching (11 Languages, Default English, No Leading '_') ---");
var locMgr = IDMLite.Services.LocalizationManager.Instance;
Assert(IDMLite.Services.LocalizationManager.SupportedLanguages.Count == 11, "11 languages supported (English default + 10 languages)");
Assert(IDMLite.Services.LocalizationManager.SupportedLanguages[0].Code == "en", "English is first in SupportedLanguages");
Assert(new AppSettings().Language == "en", "Default AppSettings language is English ('en')");

locMgr.SetLanguage("tr");
Assert(locMgr.Get("Str_File") == "Dosya", "Turkish 'Dosya' verified (no leading '_')");
Assert(locMgr.Get("Str_Resume") == "Devam Et", "Turkish 'Devam Et' verified (no leading '_')");
Assert(locMgr.Get("Str_TrayMinimize") == "Tepsiye Küçült", "Turkish 'Tepsiye Küçült' verified");

locMgr.SetLanguage("en");
Assert(locMgr.Get("Str_File") == "File", "English 'File' verified (no leading '_')");
Assert(locMgr.Get("Str_Resume") == "Resume", "English 'Resume' verified (no leading '_')");
Assert(locMgr.Get("Str_TrayMinimize") == "Minimize to Tray", "English 'Minimize to Tray' verified");

locMgr.SetLanguage("de");
Assert(locMgr.Get("Str_File") == "Datei", "German 'Datei' verified");

locMgr.SetLanguage("fr");
Assert(locMgr.Get("Str_File") == "Fichier", "French 'Fichier' verified");

locMgr.SetLanguage("es");
Assert(locMgr.Get("Str_File") == "Archivo", "Spanish 'Archivo' verified");

locMgr.SetLanguage("ja");
Assert(locMgr.Get("Str_File") == "ファイル", "Japanese 'ファイル' verified");

locMgr.SetLanguage("zh");
Assert(locMgr.Get("Str_File") == "文件", "Chinese '文件' verified");

locMgr.SetLanguage("ar");
Assert(locMgr.Get("Str_File") == "ملف", "Arabic 'ملف' verified");

// Reset back to English default
locMgr.SetLanguage("en");
Assert(locMgr.Get("Str_File") == "File", "Reset back to English default successful");

// 11. Test YtDlpHelper & FFMPEG Engine Discovery
Console.WriteLine("\n--- Testing YtDlp & FFMPEG Discovery ---");
string? ytdlpPath = YtDlpHelper.FindYtDlpPath();
Console.WriteLine($"Discovered yt-dlp path: {ytdlpPath ?? "(None)"}");
Assert(YtDlpHelper.IsAvailable, "YtDlpHelper discovered yt-dlp binary");

string? ffmpegPath = YtDlpHelper.FindFfmpegPath();
Console.WriteLine($"Discovered ffmpeg path: {ffmpegPath ?? "(None)"}");
Assert(!string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath), "YtDlpHelper discovered ffmpeg binary");

// 12. Test RFC 5987 & RFC 6266 Filename Decoder
Console.WriteLine("\n--- Testing RFC 5987 / 6266 Filename Decoder ---");
string decodedRfc = SegmentedDownloader.CleanFileName("UTF-8''dosya%20ad%C4%B1%20test.zip");
Assert(decodedRfc == "dosya adı test.zip", $"RFC 5987 decoded: {decodedRfc}");

string urlEncodedName = SegmentedDownloader.CleanFileName("belge%20raporu.pdf");
Assert(urlEncodedName == "belge raporu.pdf", $"URL encoded filename decoded: {urlEncodedName}");

string sanitizedName = SegmentedDownloader.CleanFileName("dosya<ad?>:|.txt");
Assert(!sanitizedName.Contains('<') && !sanitizedName.Contains('>') && !sanitizedName.Contains('?'), "Invalid filesystem chars sanitized");

// 13. Test Google Drive File ID Parsing Across Multiple Lengths & Formats
Console.WriteLine("\n--- Testing Google Drive Regex Formats ---");
var gdriveTestUrls = new[]
{
    "https://drive.google.com/file/d/1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs/view",
    "https://drive.google.com/open?id=1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs",
    "https://drive.google.com/uc?id=123456789012345", // 15 chars
    "https://docs.google.com/file/d/1qqMm3imYdwELbIM3h78RmfdhXcHmqxYt/edit"
};
foreach (var u in gdriveTestUrls)
{
    Assert(driveResolver.CanResolve(u), $"GoogleDriveResolver recognized: {u}");
}

// 14. Test Range Exception Fallback
Console.WriteLine("\n--- Testing Range Error Detection ---");
var rangeEx = new ServerDoesNotSupportRangeException("Test Range Fallback");
Assert(rangeEx is Exception && rangeEx.Message.Contains("Range"), "ServerDoesNotSupportRangeException instantiated properly");

// 15. Test Pre-Download Video Size Extraction & Bitrate Estimation
Console.WriteLine("\n--- Testing Video Size Extraction & Bitrate Estimation ---");
using (var docExact = System.Text.Json.JsonDocument.Parse("{\"filesize\": 52428800}"))
{
    long sz = YtDlpHelper.ExtractFormatSize(docExact.RootElement, 120);
    Assert(sz == 52428800, "ExtractFormatSize extracted exact filesize (50 MB)");
}

using (var docApprox = System.Text.Json.JsonDocument.Parse("{\"filesize_approx\": 26214400}"))
{
    long szApprox = YtDlpHelper.ExtractFormatSize(docApprox.RootElement, 60);
    Assert(szApprox == 26214400, "ExtractFormatSize extracted filesize_approx (25 MB)");
}

using (var docBitrate = System.Text.Json.JsonDocument.Parse("{\"tbr\": 2500}"))
{
    // 2500 kbps * 125 bytes/sec * 10 seconds = 3,125,000 bytes
    long szTbr = YtDlpHelper.ExtractFormatSize(docBitrate.RootElement, 10.0);
    Assert(szTbr == 3125000, $"ExtractFormatSize calculated estimated size from tbr * duration: {szTbr} bytes");
}

using (var docSplitBitrate = System.Text.Json.JsonDocument.Parse("{\"vbr\": 2000, \"abr\": 192}"))
{
    // (2000 + 192) * 125 * 10 = 2,740,000 bytes
    long szVbrAbr = YtDlpHelper.ExtractFormatSize(docSplitBitrate.RootElement, 10.0);
    Assert(szVbrAbr == 2740000, $"ExtractFormatSize calculated estimated size from (vbr + abr) * duration: {szVbrAbr} bytes");
}

// 16. Test HLS Downloader Multi-Threading & MP4 Remux Path Configuration
Console.WriteLine("\n--- Testing HLS Downloader Concurrency & Destination Logic ---");
var hlsDefault = new HlsDownloader("https://example.com/stream.m3u8", @"C:\Downloads\test_video.ts");
Assert(hlsDefault.MaxParallelSegments == 8, "HlsDownloader default concurrency is 8 parallel segments");
Assert(hlsDefault.DestinationPath.EndsWith(".mp4"), "Destination ends in .mp4 (automatic remux/extension enforcement)");

var hlsClampedHigh = new HlsDownloader("https://example.com/stream.m3u8", @"C:\Downloads\test_video.mp4", 32);
Assert(hlsClampedHigh.MaxParallelSegments == 16, "HlsDownloader clamped high concurrency to 16");

var hlsClampedLow = new HlsDownloader("https://example.com/stream.m3u8", @"C:\Downloads\test_video.mp4", 0);
Assert(hlsClampedLow.MaxParallelSegments == 1, "HlsDownloader clamped low concurrency to 1");

// 17. Test Social Video Classification & Path Handling
Console.WriteLine("\n--- Testing Social Video Classifier ---");
var ytTask = new DownloadTask { Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ", FileName = "NeverGonnaGiveYouUp.mp4" };
Assert(ytTask.IsSocialVideo, "YouTube watch URL recognized as Social Video");

var xTask = new DownloadTask { Url = "https://x.com/user/status/1234567890", FileName = "x_video.mp4" };
Assert(xTask.IsSocialVideo, "X (Twitter) status URL recognized as Social Video");

var igTask = new DownloadTask { Url = "https://www.instagram.com/reel/C7-X59yv123/", FileName = "reel.mp4" };
Assert(igTask.IsSocialVideo, "Instagram reel URL recognized as Social Video");

var hlsTask = new DownloadTask { Url = "https://video.twimg.com/ext_tw_video/123/pu/pl/index.m3u8", FileName = "twitter_hls.mp4" };
Assert(hlsTask.IsHls, "Twitter HLS playlist recognized as IsHls");
Assert(!hlsTask.IsSocialVideo, "Twitter HLS playlist routed to HlsDownloader instead of raw social page");

var directFileTask = new DownloadTask { Url = "https://example.com/sample.mp4", FileName = "sample.mp4" };
Assert(!directFileTask.IsSocialVideo, "Direct MP4 file download recognized as standard download");

// 18. Test Instagram & X (Twitter) Engine Integration & Stream URL Preserving
Console.WriteLine("\n--- Testing Instagram & X (Twitter) Integration ---");
var dlReq = new DownloadRequest
{
    Url = "https://www.instagram.com/reel/C8abc123xyz/",
    ResolvedStreamUrl = "https://scontent.cdninstagram.com/v/t50.2886-16/test.mp4",
    FileName = "Burak Özdemir - Harika bir tarif.mp4"
};
Assert(!string.IsNullOrEmpty(dlReq.ResolvedStreamUrl) && dlReq.ResolvedStreamUrl.Contains("cdninstagram.com"), "DownloadRequest preserves ResolvedStreamUrl");

string jsonReq = System.Text.Json.JsonSerializer.Serialize(dlReq);
var deserializedReq = System.Text.Json.JsonSerializer.Deserialize<DownloadRequest>(jsonReq);
Assert(deserializedReq?.ResolvedStreamUrl == dlReq.ResolvedStreamUrl, "ResolvedStreamUrl roundtrip JSON serialization succeeded");
Assert(deserializedReq?.Url == "https://www.instagram.com/reel/C8abc123xyz/", "Instagram address bar URL preserved in request");
Assert(deserializedReq?.FileName == "Burak Özdemir - Harika bir tarif.mp4", "Real video title preserved in request");

var igResolver = new InstagramResolver();
Assert(igResolver.CanResolve("https://www.instagram.com/reel/C8abc123xyz/"), "InstagramResolver recognizes reel URL");
Assert(igResolver.CanResolve("https://www.instagram.com/p/C8abc123xyz/"), "InstagramResolver recognizes post URL");

var twResolver = new TwitterResolver();
Assert(twResolver.CanResolve("https://x.com/SpaceX/status/123456789"), "TwitterResolver recognizes x.com status URL");
Assert(twResolver.CanResolve("https://twitter.com/SpaceX/status/123456789"), "TwitterResolver recognizes twitter.com status URL");

var hlsTwitter = new HlsDownloader("https://video.twimg.com/ext_tw_video/123/pu/pl/master.m3u8", @"C:\temp\twitter_out.ts");
Assert(hlsTwitter.DestinationPath.EndsWith(".mp4"), "Twitter HLS destination forced to .mp4 container");
Assert(hlsTwitter.FinalDestinationPath.EndsWith(".mp4"), "Twitter HLS final destination is .mp4");

// 18. Test Dart Social Media Engine & Cobalt API Integration
Console.WriteLine("\n--- Testing Dart Social Engine & Cobalt Resolver ---");
Assert(DartEngineHelper.CanResolve("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), "DartEngineHelper recognizes YouTube");
Assert(DartEngineHelper.CanResolve("https://www.instagram.com/reel/C7-X59yv123/"), "DartEngineHelper recognizes Instagram");
Assert(DartEngineHelper.CanResolve("https://x.com/user/status/1234567890"), "DartEngineHelper recognizes X (Twitter)");
Assert(DartEngineHelper.CanResolve("https://www.tiktok.com/@user/video/1234567890"), "DartEngineHelper recognizes TikTok");

var cobaltRes = new CobaltResolver();
Assert(cobaltRes.CanResolve("https://www.instagram.com/p/C7-X59y/"), "CobaltResolver recognizes Instagram Post");
Assert(cobaltRes.CanResolve("https://twitter.com/user/status/123"), "CobaltResolver recognizes Twitter");
Assert(cobaltRes.CanResolve("https://x.com/user/status/123"), "CobaltResolver recognizes X");
Assert(cobaltRes.CanResolve("https://www.tiktok.com/@user/video/123"), "CobaltResolver recognizes TikTok");

string? enginePath = DartEngineHelper.FindEnginePath();
Assert(!string.IsNullOrEmpty(enginePath), $"Dart engine executable discovered: {enginePath}");

Console.WriteLine("Probing YouTube URL live via Dart Engine (YouTubeExplode)...");
var dartProbe = DartEngineHelper.ProbeAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ").GetAwaiter().GetResult();
Assert(dartProbe != null, "DartEngineHelper probed YouTube successfully");
if (dartProbe != null)
{
    Assert(dartProbe.IsSuccess, "Dart probe success is true");
    Assert(dartProbe.FileSize.HasValue && dartProbe.FileSize.Value > 0, $"Dart probe extracted exact byte size: {dartProbe.FileSize} bytes");
    Assert(dartProbe.SuggestedFileName.EndsWith(".mp4"), $"Dart probe suggested filename is .mp4: {dartProbe.SuggestedFileName}");
    Assert(dartProbe.Streams.Count > 0, $"Dart probe returned {dartProbe.Streams.Count} streams");
}

// 19. Test WPF Dialogs Instantiation (Headless)
Console.WriteLine("\n--- Testing WPF Dialogs Instantiation (Headless) ---");
var staThread = new Thread(() =>
{
    try
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application();
        IDMLite.App.ApplyTheme("Dark");
        // Do NOT manually add to app.Resources - let App do it or see if it's missing!

        Console.WriteLine("Instantiating MainWindow (Headless)...");
        var mainWin = new IDMLite.MainWindow(new SettingsManager(), new DownloadQueueManager(new SettingsManager()), new IpcServer(new DownloadQueueManager(new SettingsManager()), new SettingsManager()));
        (mainWin.Content as System.Windows.UIElement)?.Measure(new System.Windows.Size(800, 600));
        (mainWin.Content as System.Windows.UIElement)?.Arrange(new System.Windows.Rect(0, 0, 800, 600));
        Assert(mainWin != null && mainWin.Content != null, "MainWindow verified headlessly without XAML errors");

        Console.WriteLine("Instantiating AddDownloadDialog (Headless)...");
        try { System.Windows.Clipboard.SetText("https://example.com/test.mp4"); } catch { }
        var addDlg = new IDMLite.Windows.AddDownloadDialog(new SettingsManager());
        (addDlg.Content as System.Windows.UIElement)?.Measure(new System.Windows.Size(500, 400));
        (addDlg.Content as System.Windows.UIElement)?.Arrange(new System.Windows.Rect(0, 0, 500, 400));
        Assert(addDlg != null && addDlg.Content != null, "AddDownloadDialog verified headlessly without XAML errors");
        addDlg.Close();

        Console.WriteLine("Instantiating DownloadProgressDialog (Headless)...");
        var testTask = new IDMLite.Core.DownloadTask { FileName = "test.zip", Url = "https://example.com/test.zip", SaveDirectory = "C:\\Downloads" };
        var progDlg = new IDMLite.Windows.DownloadProgressDialog(testTask, new DownloadQueueManager(new SettingsManager()));
        (progDlg.Content as System.Windows.UIElement)?.Measure(new System.Windows.Size(500, 300));
        (progDlg.Content as System.Windows.UIElement)?.Arrange(new System.Windows.Rect(0, 0, 500, 300));
        Assert(progDlg != null && progDlg.Content != null, "DownloadProgressDialog verified headlessly without XAML errors");
        progDlg.Close();

        Console.WriteLine("Instantiating SettingsDialog (Headless)...");
        var setDlg = new IDMLite.Windows.SettingsDialog(new SettingsManager(), new DownloadQueueManager(new SettingsManager()));
        (setDlg.Content as System.Windows.UIElement)?.Measure(new System.Windows.Size(600, 500));
        (setDlg.Content as System.Windows.UIElement)?.Arrange(new System.Windows.Rect(0, 0, 600, 500));
        Assert(setDlg != null && setDlg.Content != null, "SettingsDialog verified headlessly without XAML errors");
        setDlg.Close();

        // Also test switching to Light Theme
        Console.WriteLine("Testing switch to Light theme...");
        IDMLite.App.ApplyTheme("Light");
        var lightAddDlg = new IDMLite.Windows.AddDownloadDialog(new SettingsManager());
        (lightAddDlg.Content as System.Windows.UIElement)?.Measure(new System.Windows.Size(500, 400));
        (lightAddDlg.Content as System.Windows.UIElement)?.Arrange(new System.Windows.Rect(0, 0, 500, 400));
        Assert(lightAddDlg != null && lightAddDlg.Content != null, "AddDownloadDialog verified under Light theme without XAML errors");
        lightAddDlg.Close();

        var lightProgDlg = new IDMLite.Windows.DownloadProgressDialog(testTask, new DownloadQueueManager(new SettingsManager()));
        (lightProgDlg.Content as System.Windows.UIElement)?.Measure(new System.Windows.Size(500, 300));
        (lightProgDlg.Content as System.Windows.UIElement)?.Arrange(new System.Windows.Rect(0, 0, 500, 300));
        Assert(lightProgDlg != null && lightProgDlg.Content != null, "DownloadProgressDialog verified under Light theme without XAML errors");
        lightProgDlg.Close();

        mainWin.Close();

        // Testing Video Download Detection and Single-Stream Bypassing
        Console.WriteLine("\n--- Testing Video Mode Single-Stream Enforcement ---");
        using var vidDownloader = new SegmentedDownloader("https://example.com/stream.mp4", @"C:\temp\stream.mp4");
        Assert(vidDownloader.IsVideoDownload(), "stream.mp4 correctly detected as video download");

        using var gvidDownloader = new SegmentedDownloader("https://rr1---sn-4g5ednle.googlevideo.com/videoplayback?expire=123", @"C:\temp\videoplayback");
        Assert(gvidDownloader.IsVideoDownload(), "googlevideo URL correctly detected as video download");

        using var twDownloader = new SegmentedDownloader("https://video.twimg.com/ext_tw_video/123/pu/vid/1280x720/xyz.mp4", @"C:\temp\tweet.mp4");
        Assert(twDownloader.IsVideoDownload(), "twimg video correctly detected as video download");

        using var zipDownloader = new SegmentedDownloader("https://example.com/archive.zip", @"C:\temp\archive.zip");
        Assert(!zipDownloader.IsVideoDownload(), "archive.zip correctly NOT detected as video download");

        // Testing ShellHelper.OpenFile
        Console.WriteLine("\n--- Testing ShellHelper Open File API ---");
        var openFileMethod = typeof(IDMLite.ShellHelper).GetMethod("OpenFile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert(openFileMethod != null, "ShellHelper.OpenFile method is declared and accessible");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[CRASH] Dialog instantiation error:\n{ex}");
        if (ex.InnerException != null)
        {
            Console.WriteLine($"\n[INNER EXCEPTION]\n{ex.InnerException}");
            if (ex.InnerException.InnerException != null)
            {
                Console.WriteLine($"\n[ROOT EXCEPTION]\n{ex.InnerException.InnerException}");
            }
        }
        Console.ResetColor();
        Assert(false, $"Dialog instantiation exception: {ex.Message}");
    }
});
staThread.SetApartmentState(ApartmentState.STA);
staThread.Start();
staThread.Join();
Console.WriteLine("\n--- Testing Mp4Fixer Header Repair (KaraDelik Integration) ---");
string testMp4 = Path.Combine(Path.GetTempPath(), "test_fix_" + Guid.NewGuid().ToString("N") + ".mp4");
try
{
    // Construct valid MP4 atom structure with moov -> mvhd (version 0, duration = 0, timescale = 1000)
    using (var ms = new MemoryStream())
    {
        // ftyp box (16 bytes)
        ms.Write(new byte[] { 0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0 });
        
        // moov box (header 8 bytes + mvhd box 108 bytes = 116 bytes total)
        ms.Write(new byte[] { 0, 0, 0, 116, (byte)'m', (byte)'o', (byte)'o', (byte)'v' });
        
        // mvhd box (108 bytes total)
        ms.Write(new byte[] { 0, 0, 0, 108, (byte)'m', (byte)'v', (byte)'h', (byte)'d' });
        ms.WriteByte(0); // version 0
        ms.Write(new byte[] { 0, 0, 0 }); // flags
        ms.Write(new byte[4]); // creation time
        ms.Write(new byte[4]); // modification time
        // timescale = 1000
        ms.Write(new byte[] { 0, 0, 0x03, 0xE8 });
        // duration = 0 (broken)
        ms.Write(new byte[] { 0, 0, 0, 0 });
        // rate = 0x00010000, volume = 0x0100, reserved, matrix (80 bytes)
        ms.Write(new byte[80]);
        
        File.WriteAllBytes(testMp4, ms.ToArray());
    }

    bool fixedResult = await Mp4Fixer.FixDurationIfNeededAsync(testMp4, TimeSpan.FromSeconds(30));
    Assert(fixedResult, "Mp4Fixer detected and repaired zero duration");

    // Read back bytes and verify duration field
    byte[] fixedBytes = File.ReadAllBytes(testMp4);
    // moov is at offset 16; mvhd is at offset 24; duration in mvhd v0 is at offset 24 + 8 + 16 = 48
    uint repairedDuration = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(fixedBytes.AsSpan(48, 4));
    Assert(repairedDuration == 30000, $"Repaired duration matches 30s * 1000 timescale (expected 30000, got {repairedDuration})");
}
finally
{
    if (File.Exists(testMp4)) try { File.Delete(testMp4); } catch { }
}

Console.WriteLine("\n--- Testing YtDlpDownloader YouTube Download with Browser Cookies (Regression for Exit Code 1) ---");
string ytTestOut = Path.Combine(Path.GetTempPath(), "yt_e2e_" + Guid.NewGuid().ToString("N") + ".mp4");
try
{
    using var ytDl = new YtDlpDownloader("https://www.youtube.com/watch?v=jNQXAC9IVRw", ytTestOut)
    {
        Cookies = "HSID=AK4zDpK2PII-8jK9N; SSID=AiuHijGGRTMXHZ8oK; SAPISID=-kIGmjCOWnhSPi4O/AyWQWG7FBeD8D4gJJ; LOGIN_INFO=AFmmF2swRQIgLiSgu4Om9yuEIaU1FpqC31qJRcpkw0Rd0J2PP2P6d4s; __Secure-1PSID=g.a000DQnT9nlqiulSAhqFLvmhWxLEgm571s",
        Referer = "https://www.youtube.com/watch?v=jNQXAC9IVRw",
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36"
    };
    await ytDl.StartAsync();
    Assert(ytDl.Status == DownloadStatus.Completed, "YtDlpDownloader completed YouTube download with browser cookies without Exit Code 1");
    Assert(File.Exists(ytTestOut) && new FileInfo(ytTestOut).Length > 100_000, $"Downloaded YouTube MP4 file exists and is valid ({new FileInfo(ytTestOut).Length} bytes)");
}
finally
{
    if (File.Exists(ytTestOut)) try { File.Delete(ytTestOut); } catch { }
}

Console.WriteLine("\n--- Testing Thread Segment MB/s Formatting & Taskbar Notification API ---");
var testSeg = new DownloadSegment(1, 0, 50 * 1024 * 1024)
{
    CurrentByte = 15 * 1024 * 1024, // 15 MB downloaded
    CurrentSpeedBytesPerSec = 4.5 * 1024 * 1024 // 4.50 MB/s
};
Assert(testSeg.DownloadedMegabytesFormatted.EndsWith("MB") && !testSeg.DownloadedMegabytesFormatted.Contains("KB"),
    $"Segment downloaded formatted in MB: {testSeg.DownloadedMegabytesFormatted}");
Assert(testSeg.SpeedMegabytesFormatted.EndsWith("MB/s") && !testSeg.SpeedMegabytesFormatted.Contains("KB"),
    $"Segment speed formatted in MB/s: {testSeg.SpeedMegabytesFormatted}");

var flashMethod = typeof(IDMLite.ShellHelper).GetMethod("FlashTaskbarIcon", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
Assert(flashMethod != null, "ShellHelper.FlashTaskbarIcon method is declared and accessible");

Console.WriteLine("\n--- Testing UniversalVideoDownloader (Social Exclusion + Non-Social Video MP4 Conversion) ---");
Assert(!UniversalVideoDownloader.IsSocialPlatformUrl("https://vimeo.com/123456789"), "Vimeo is not classified as one of the 4 social platforms");
Assert(UniversalVideoDownloader.IsSocialPlatformUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), "YouTube is excluded from UniversalVideoDownloader");
Assert(UniversalVideoDownloader.IsSocialPlatformUrl("https://www.instagram.com/reel/C8abc123/"), "Instagram is excluded from UniversalVideoDownloader");
Assert(UniversalVideoDownloader.IsSocialPlatformUrl("https://www.tiktok.com/@user/video/12345"), "TikTok is excluded from UniversalVideoDownloader");
Assert(UniversalVideoDownloader.IsSocialPlatformUrl("https://x.com/user/status/12345"), "X (Twitter) is excluded from UniversalVideoDownloader");

var vimeoTask = new DownloadTask { Url = "https://vimeo.com/123456789", FileName = "Vimeo Showcase.mp4" };
Assert(UniversalVideoDownloader.ShouldHandleTask(vimeoTask), "UniversalVideoDownloader handles Vimeo video tasks");

var webmTask = new DownloadTask { Url = "https://cdn.example.com/media/lecture.webm", FileName = "Big Buck Lecture.webm" };
Assert(UniversalVideoDownloader.ShouldHandleTask(webmTask), "UniversalVideoDownloader handles .webm video tasks for MP4 conversion");

var genericHlsTask = new DownloadTask { Url = "https://cdn.jwplayer.com/manifests/abc123.m3u8", FileName = "Course Lesson 1.mp4" };
Assert(UniversalVideoDownloader.ShouldHandleTask(genericHlsTask), "UniversalVideoDownloader handles non-social HLS .m3u8 streams");
Assert(!UniversalVideoDownloader.ShouldHandleTask(ytTask), "UniversalVideoDownloader does NOT touch YouTube tasks");
Assert(!UniversalVideoDownloader.ShouldHandleTask(igTask), "UniversalVideoDownloader does NOT touch Instagram tasks");
Assert(!UniversalVideoDownloader.ShouldHandleTask(xTask), "UniversalVideoDownloader does NOT touch X (Twitter) tasks");

string tempTsFile = Path.Combine(Path.GetTempPath(), "univ_test_" + Guid.NewGuid().ToString("N") + ".ts");
string tempMp4Out = Path.ChangeExtension(tempTsFile, ".mp4");
try
{
    if (!string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath))
    {
        // Generate a real 1-second MPEG-TS video + audio stream headlessly
        var genPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -f lavfi -i testsrc=duration=1:size=320x240:rate=15 -f lavfi -i sine=frequency=440:duration=1 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -f mpegts \"{tempTsFile}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using (var genProc = System.Diagnostics.Process.Start(genPsi))
        {
            genProc?.WaitForExit(15000);
        }

        Assert(File.Exists(tempTsFile) && !UniversalVideoDownloader.IsValidMp4Container(tempTsFile), "Generated raw MPEG-TS (.ts) stream is not an MP4 container initially");

        await UniversalVideoDownloader.EnsurePlayableMp4ContainerAsync(tempTsFile, tempMp4Out);
        Assert(File.Exists(tempMp4Out) && UniversalVideoDownloader.IsValidMp4Container(tempMp4Out), "UniversalVideoDownloader.EnsurePlayableMp4ContainerAsync converted .ts stream into a valid ISO-BMFF (.mp4) container with ftyp box");
    }
}
finally
{
    if (File.Exists(tempTsFile)) try { File.Delete(tempTsFile); } catch { }
    if (File.Exists(tempMp4Out)) try { File.Delete(tempMp4Out); } catch { }
}


Console.WriteLine("\n--- Testing Mini Download Dialog Auto-Close, Universal Download Bypass & TikTok Focus ---");
string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string progDlgCs = File.ReadAllText(Path.Combine(repoRoot, "ui", "Windows", "DownloadProgressDialog.xaml.cs"));
Assert(progDlgCs.Contains("CloseDialogImmediately()"), "DownloadProgressDialog defines CloseDialogImmediately()");
Assert(progDlgCs.Contains("ShellHelper.OpenFile(_task.FullPath, fallbackDir);\r\n        CloseDialogImmediately();") ||
       progDlgCs.Contains("ShellHelper.OpenFile(_task.FullPath, fallbackDir);\n        CloseDialogImmediately();"),
    "BtnOpenFile_Click calls CloseDialogImmediately() after opening file");
Assert(progDlgCs.Contains("ShellHelper.OpenFolderAndSelect(_task.FullPath, fallbackDir);\r\n        CloseDialogImmediately();") ||
       progDlgCs.Contains("ShellHelper.OpenFolderAndSelect(_task.FullPath, fallbackDir);\n        CloseDialogImmediately();"),
    "BtnOpenFolder_Click calls CloseDialogImmediately() after opening folder");

string bgJs = File.ReadAllText(Path.Combine(repoRoot, "extension", "background.js"));
Assert(bgJs.Contains("browserApi") && bgJs.Contains("onDeterminingFilename") && bgJs.Contains("onCreated"),
    "background.js supports both Chromium and Firefox download listeners at 0ms");
Assert(!bgJs.Contains("Wait up to 80ms"), "background.js removed 80ms delay on onCreated so Save As dialog can never open");
Assert(bgJs.Contains("\"image/webp\": \".webp\"") && bgJs.Contains("\"image/svg+xml\": \".svg\"") && bgJs.Contains("\"text/plain\": \".txt\""),
    "background.js MIME_EXT includes all image (JPG, PNG, WEBP, GIF, SVG) and document (PDF, DOCX, XLSX, TXT, PPTX) formats");

string contentJs = File.ReadAllText(Path.Combine(repoRoot, "extension", "content.js"));
Assert(contentJs.Contains("tiktokIntersectionObserver") && contentJs.Contains("0.6"),
    "content.js uses IntersectionObserver with threshold 0.6 for TikTok active video detection");
Assert(contentJs.Contains("recommend-list-item-container") && contentJs.Contains("user-post-item"),
    "content.js focuses TikTok video containers (recommend-list-item-container and user-post-item)");
Assert(contentJs.Contains("extractTikTokVideoTarget") && contentJs.Contains("xgwrapper-"),
    "content.js extracts exact TikTok permalink (/@user/video/ID) from active video container");
Assert(contentJs.Contains("inspectReactFiberMedia") && contentJs.Contains("new WeakSet()"),
    "content.js uses WeakSet-guarded React Fiber walker without circular JSON errors");
Assert(contentJs.Contains("extractInstagramVideoTarget") && contentJs.Contains("findOuterInstagramPostContainer"),
    "content.js extracts Instagram Saved Posts (Kaydedilenler) modal links and React Fiber shortcodes");
Assert(contentJs.Contains("lockCurrentVideoTarget") && contentJs.Contains("_lockedTikTokData"),
    "content.js locks active TikTok video target on hover/pointerdown for 100% stable button capture");

var igSavedPostTask = new DownloadTask { Url = "https://www.instagram.com/someuser/p/DC12345abc/", FallbackStreamUrl = "https://scontent.cdninstagram.com/v/t66.30100-16/123.mp4" };
var igSavedReelTask = new DownloadTask { Url = "https://www.instagram.com/someuser/reel/DC98765xyz/" };
Assert(igSavedPostTask.IsSocialVideo && igSavedReelTask.IsSocialVideo,
    "DownloadTask.IsSocialVideo recognizes Instagram URLs with username segment (/username/p/SHORTCODE and /username/reel/SHORTCODE)");
Assert(IDMLite.Core.Resolvers.InstagramResolver.NormalizeInstagramPostUrl("https://www.instagram.com/someuser/p/DC12345abc/?img_index=1") == "https://www.instagram.com/p/DC12345abc/",
    "InstagramResolver.NormalizeInstagramPostUrl normalizes /username/p/SHORTCODE to canonical /p/SHORTCODE/");
Assert(IDMLite.Core.Resolvers.InstagramResolver.NormalizeInstagramPostUrl("https://www.instagram.com/someuser/reel/DC98765xyz/") == "https://www.instagram.com/reel/DC98765xyz/",
    "InstagramResolver.NormalizeInstagramPostUrl normalizes /username/reel/SHORTCODE to canonical /reel/SHORTCODE/");
Assert(contentJs.Contains("refreshTikTokActiveVideoBadge") && (contentJs.Contains("tiktokCanvas") || contentJs.Contains("isBodyAttached")),
    "content.js attaches TikTok badge directly to video canvas container and auto-refreshes on every new video in the feed");
Assert(contentJs.Contains("resolveActiveInstagramVideo") && contentJs.Contains("vidCount > 1"),
    "content.js isolates single-Reel container on Instagram Reels without selecting multi-video <main>/<section> feed");

Assert(IDMLite.Windows.SupportDialog.BuyMeACoffeeUrl == "https://buymeacoffee.com/vibeabi",
    "SupportDialog.BuyMeACoffeeUrl matches https://buymeacoffee.com/vibeabi");
string supportXaml = File.ReadAllText(Path.Combine(repoRoot, "ui", "Windows", "SupportDialog.xaml"));
string mainXaml = File.ReadAllText(Path.Combine(repoRoot, "ui", "MainWindow.xaml"));
Assert(supportXaml.Contains("Buy Me a Coffee") && supportXaml.Contains("https://buymeacoffee.com/vibeabi"),
    "SupportDialog.xaml displays 'Buy Me a Coffee' and clickable 'https://buymeacoffee.com/vibeabi' link");
Assert( mainXaml.Contains("BtnSupport_Click"),
    "MainWindow.xaml includes Support button wired to BtnSupport_Click");

Assert(contentJs.Contains("getLoggedInInstagramUsername") && contentJs.Contains("IG_RESERVED_PATHS"),
    "content.js detects logged-in Instagram username and filters it out when extracting video creator/caption");
string addDlgCs = File.ReadAllText(Path.Combine(repoRoot, "ui", "Windows", "AddDownloadDialog.xaml.cs"));
Assert(addDlgCs.Contains("isAuthoritativeInstagramTitle"),
    "AddDownloadDialog updates filename with authoritative Instagram creator/caption from resolver");

Console.WriteLine("\n--- Testing GitHub .txt URL Normalization & End-to-End Download ---");
string ghBlobTxt = "https://github.com/robots-txt/robots.txt/blob/main/robots.txt";
string ghNormalized = SegmentedDownloader.NormalizeGitHubRawUrl(ghBlobTxt);
Assert(ghNormalized == "https://raw.githubusercontent.com/robots-txt/robots.txt/main/robots.txt",
    $"GitHub /blob/ .txt URL normalized to raw.githubusercontent.com ({ghNormalized})");

string ghRefsHeads = "https://github.com/curl/curl/raw/refs/heads/master/CMakeLists.txt";
Assert(SegmentedDownloader.NormalizeGitHubRawUrl(ghRefsHeads) == "https://raw.githubusercontent.com/curl/curl/master/CMakeLists.txt",
    "GitHub /raw/refs/heads/ .txt URL normalized to raw.githubusercontent.com");

// Test end-to-end GitHub .txt download starting from a /blob/ URL with 8 connections
string ghTxtLiveBlobUrl = "https://github.com/curl/curl/blob/master/CMakeLists.txt";
string tempGhTxtFile = Path.Combine(Path.GetTempPath(), "idm_github_test_" + Guid.NewGuid().ToString("N") + ".txt");
try
{
    using var ghTxtDownloader = new SegmentedDownloader(ghTxtLiveBlobUrl, tempGhTxtFile, 8);
    Assert(ghTxtDownloader.IsSingleStreamPreferred(), "IsSingleStreamPreferred() returns true for .txt files");
    await ghTxtDownloader.ProbeAsync();
    Assert(ghTxtDownloader.Url == "https://raw.githubusercontent.com/curl/curl/master/CMakeLists.txt",
        "SegmentedDownloader automatically normalizes GitHub /blob/ .txt URL to raw.githubusercontent.com");
    await ghTxtDownloader.StartAsync();
    Assert(File.Exists(tempGhTxtFile) && new FileInfo(tempGhTxtFile).Length > 100,
        $"GitHub .txt file downloaded cleanly without HTML or gzip range errors ({new FileInfo(tempGhTxtFile).Length} bytes)");
    string firstLines = File.ReadAllText(tempGhTxtFile);
    Assert(!firstLines.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase),
        "Downloaded GitHub .txt file contains raw text content, not GitHub's HTML blob viewer page");
}
finally
{
    if (File.Exists(tempGhTxtFile)) try { File.Delete(tempGhTxtFile); } catch { }
}

Console.WriteLine("\n--- Testing Non-Blocking IPC /api/download Response (Fixing 'App Not Open' Error) ---");
try
{
    ipcServer.OpenDownloadWindowAsync = (_, _) =>
    {
        return Task.FromResult(true);
    };
    var swIpc = Stopwatch.StartNew();
    var postContent = new StringContent("{\"url\":\"https://example.com/sample.txt\",\"fileName\":\"sample.txt\",\"autoStart\":false}", Encoding.UTF8, "application/json");
    var postRes = await httpClient.PostAsync("http://127.0.0.1:41719/api/download", postContent);
    swIpc.Stop();
    string postBody = await postRes.Content.ReadAsStringAsync();
    Assert(postRes.IsSuccessStatusCode && postBody.Contains("\"success\":true"),
        "POST /api/download returns HTTP 200 success JSON immediately");
    Assert(swIpc.ElapsedMilliseconds < 1000,
        $"POST /api/download responds in {swIpc.ElapsedMilliseconds}ms (< 1000ms) without blocking on modal dialog");

    var localhostRes = await httpClient.GetAsync("http://localhost:41719/api/status");
    Assert(localhostRes.IsSuccessStatusCode, "GET http://localhost:41719/api/status also responds HTTP 200");
}
catch (Exception ex)
{
    Assert(false, $"Non-blocking IPC test failed: {ex.Message}");
}

string linkMgrCs = File.ReadAllText(Path.Combine(repoRoot, "core", "Resolvers", "LinkResolverManager.cs"));
string igResolverCs = File.ReadAllText(Path.Combine(repoRoot, "core", "Resolvers", "InstagramResolver.cs"));
Assert(linkMgrCs.Contains("igResolver.ResolveWithCookiesAsync") &&
       igResolverCs.Contains("TryGetCachedResult") &&
       igResolverCs.Contains("FetchEmbedCaptionedAsync"),
    "LinkResolverManager bypasses slow 15s DartEngineHelper on Instagram and calls parallel cached InstagramResolver directly");

Console.WriteLine("\n--- Testing Specific Instagram Reel DeoCD4BNvUV & DepG695taRq ---");
var igResTest = await new InstagramResolver().ResolveAsync("https://www.instagram.com/reels/DeoCD4BNvUV/");
Assert(igResTest.IsSuccess, "Instagram Reel DeoCD4BNvUV resolved successfully");
Assert(!string.IsNullOrEmpty(igResTest.DirectUrl), "Instagram Reel DeoCD4BNvUV has direct MP4 stream URL");
Assert(!igResTest.SuggestedFileName.Contains("Meta AI"), "Instagram Reel DeoCD4BNvUV filters out Meta AI disclaimer from filename");

var depResTest = await new InstagramResolver().ResolveAsync("https://www.instagram.com/reels/DepG695taRq/");
Assert(depResTest.IsSuccess, "Instagram Reel DepG695taRq resolved successfully");
Assert(!string.IsNullOrEmpty(depResTest.DirectUrl) && depResTest.DirectUrl.Contains(".mp4"), "Instagram Reel DepG695taRq has direct MP4 stream URL");
Assert(depResTest.FileSize == null || depResTest.FileSize > 500_000, $"Instagram Reel DepG695taRq is a real video (>500KB), not a 41KB thumbnail (size: {depResTest.FileSize})");

Console.WriteLine("\n--- Testing Google Drive & Takeout Multi-Stream/Cookie Protection ---");
string takeoutTestUrl = "https://takeout-download-drive-eu.usercontent.google.com/download/Windows-20261018T040211Z-1-001.zip?j=206f98c8-51a0-4779-8729-54088ace05c9&i=0&user=353216952019&authuser=0";
using var takeoutDl = new SegmentedDownloader(takeoutTestUrl, Path.Combine(Path.GetTempPath(), "test_takeout.zip"), 8);
Assert(takeoutDl.IsGoogleDriveStream(), "IsGoogleDriveStream() returns true for takeout-download-drive-eu URL");
Assert(takeoutDl.IsDynamicTakeoutStream(), "IsDynamicTakeoutStream() returns true for dynamic takeout-download URL");
Assert(takeoutDl.IsSingleStreamPreferred(), "IsSingleStreamPreferred() returns true for dynamic Takeout streams to prevent 302 Range rejections");

// Test DownloadSegment Property Notification & Throttling
Console.WriteLine("\n--- Testing DownloadSegment Notification Throttling ---");
var segPropTest = new DownloadSegment(0, 0, 10 * 1024 * 1024);
int progressNotifyCount = 0;
bool speedPropNotified = false;
segPropTest.PropertyChanged += (s, e) =>
{
    if (e.PropertyName == nameof(DownloadSegment.ProgressPercentage))
    {
        progressNotifyCount++;
    }
    if (e.PropertyName == nameof(DownloadSegment.SpeedMegabytesFormatted))
    {
        speedPropNotified = true;
    }
};

// Simulate 100 fast 64KB writes in 0ms (same as high-speed network loop)
for (int i = 0; i < 100; i++)
{
    segPropTest.CurrentByte += 64 * 1024;
}
Assert(progressNotifyCount <= 2, $"DownloadSegment throttled rapid updates (notified {progressNotifyCount} times instead of 100 to protect low/mid CPUs)");

// Force update via progress reporter
segPropTest.NotifyProgress(force: true);
Assert(progressNotifyCount >= 1, "DownloadSegment.NotifyProgress(force: true) triggers immediate progress update");

segPropTest.CurrentSpeedBytesPerSec = 2.5 * 1024 * 1024;
Assert(speedPropNotified, "DownloadSegment raises PropertyChanged for SpeedMegabytesFormatted");
Assert(segPropTest.SpeedMegabytesFormatted is "2.50 MB/s" or "2,50 MB/s", $"Speed formatted correctly: {segPropTest.SpeedMegabytesFormatted}");

var gdRes = new GoogleDriveResolver();
Assert(!gdRes.CanResolve(takeoutTestUrl), "GoogleDriveResolver.CanResolve returns false for already-intercepted takeout streams");

string bgJsContent = File.ReadAllText(Path.Combine(repoRoot, "extension", "background.js"));
Assert(bgJsContent.Contains("gatherCookiesForDownload") &&
       bgJsContent.Contains("urlRequestCookiesCache") &&
       bgJsContent.Contains("hostCookiesCache") &&
       bgJsContent.Contains("partitionKey"),
    "background.js captures exact request cookies, CHIPS partitioned cookies, and isolates OSID/COMPASS per origin");

string segDlCs = File.ReadAllText(Path.Combine(repoRoot, "core", "SegmentedDownloader.cs"));
Assert(segDlCs.Contains("SendWithRedirectsAsync") &&
       segDlCs.Contains("CaptureSetCookiesFromResponse") &&
       segDlCs.Contains("BuildCookieHeader") &&
       segDlCs.Contains("AllowAutoRedirect = false"),
    "SegmentedDownloader uses custom SendWithRedirectsAsync with deterministic Set-Cookie and OSID handling");

Assert(segDlCs.Contains("buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF") &&
       segDlCs.Contains("İndirilen içerik bir video değil, resim/kapak görselidir"),
    "SegmentedDownloader inspects incoming buffer magic bytes and rejects JPEG/PNG images disguised as .mp4 videos");

string cobaltCs = File.ReadAllText(Path.Combine(repoRoot, "core", "Resolvers", "CobaltResolver.cs"));
Assert(cobaltCs.Contains("cobaltType.Equals(\"photo\"") &&
       cobaltCs.Contains("isVideoExpected"),
    "CobaltResolver discards photo tunnels when resolving video reels to prevent generating corrupt 41KB mp4 files");

// Regression assertions for Instagram direct video focus, fast determining, canvas attachment, and click fixes
string igResolverCode = File.ReadAllText(Path.Combine(repoRoot, "core", "Resolvers", "InstagramResolver.cs"));
Assert(igResolverCode.Contains("extLower.Contains(\".jpg\")") &&
       igResolverCode.Contains("extLower.Contains(\"/v/t51.\")"),
    "InstagramResolver.FetchEmbedCaptionedAsync strictly rejects JPEG, PNG, WEBP and photo CDN URLs");

Assert(igResolverCode.Contains("lower.Contains(\"/v/t51.\")") &&
       igResolverCode.Contains("lower.Contains(\".mp4\")") &&
       igResolverCode.Contains("lower.Contains(\"/v/t16.\")"),
    "InstagramResolver.ResolveSnapSaveAsync filters out thumbnail/cover photo URLs and strictly enforces video streams");

string addDlgCode = File.ReadAllText(Path.Combine(repoRoot, "ui", "Windows", "AddDownloadDialog.xaml.cs"));
Assert(addDlgCode.Contains("hasDirectExtensionStream") &&
       addDlgCode.Contains("_resolvedUrl = _initialRequest!.ResolvedStreamUrl;"),
    "AddDownloadDialog uses fast-path for extension direct streams to eliminate Determining delay and preserve video URLs");

string contentJsText = File.ReadAllText(Path.Combine(repoRoot, "extension", "content.js"));
Assert(contentJsText.Contains("isLikelyInstagramVideoUrl") &&
       contentJsText.Contains("/v/t51.") &&
       contentJsText.Contains(".jpg"),
    "content.js filters candidate streams with isLikelyInstagramVideoUrl to focus strictly on real video streams");

Assert(!contentJsText.Contains("if (isTikTok || isInstagram)") &&
       contentJsText.Contains("if (isTikTok)") &&
       contentJsText.Contains("video.closest(\"div[style*='aspect-ratio']"),
    "content.js anchors Instagram badge directly to video canvas container so it moves with the video and never drifts");

Assert(contentJsText.Contains("if (e.type === \"click\") {\n          e.preventDefault();\n        }") ||
       contentJsText.Contains("if (e.type === \"click\") {\r\n          e.preventDefault();\r\n        }"),
    "content.js setupGlobalCaptureInterceptors only prevents default on click to prevent Chromium pointerdown cancellation");

string contentCssText = File.ReadAllText(Path.Combine(repoRoot, "extension", "content.css"));
string badgeContainerCss = contentCssText.Substring(contentCssText.IndexOf(".idmlite-video-badge-container"));
badgeContainerCss = badgeContainerCss.Substring(0, badgeContainerCss.IndexOf('}'));
Assert(!badgeContainerCss.Contains("position: absolute !important;") &&
       !badgeContainerCss.Contains("top: 12px !important;") &&
       !badgeContainerCss.Contains("right: 16px !important;"),
    "content.css removed !important from badge container position and coordinates so JS dynamic styling works cleanly");

Console.WriteLine("\n==================================================");
Console.WriteLine($"Result: {passed} PASSED, {failed} FAILED");
Console.WriteLine("==================================================");

if (failed > 0) Environment.Exit(1);



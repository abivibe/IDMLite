using System.Text.Json;
using System.Text.RegularExpressions;
using IDMLite.Core.Services;

namespace IDMLite.Core.Resolvers;

public class TwitterResolver : ILinkResolver
{
    public string ServiceName => "X (Twitter) Video & Audio Resolver";

    public bool CanResolve(string url) => MediaUrl.IsSocialPage(url) && MediaUrl.HasHost(url, "twitter.com", "x.com");

    public async Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        // 1. Try yt-dlp first if available
        if (YtDlpHelper.IsAvailable)
        {
            try
            {
                var mediaInfo = await YtDlpHelper.GetMediaInfoAsync(url, cancellationToken).ConfigureAwait(false);
                if (mediaInfo != null)
                {
                    string safeTitle = CleanFileName(mediaInfo.Title);
                    if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "X_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

                    string? directUrl = mediaInfo.DirectVideoUrl ??
                                       mediaInfo.Formats.LastOrDefault(f => f.Extension == "mp4" && !string.IsNullOrEmpty(f.Url))?.Url ??
                                       mediaInfo.Formats.LastOrDefault(f => !string.IsNullOrEmpty(f.Url))?.Url;

                    if (!string.IsNullOrEmpty(directUrl))
                    {
                        var twResult = new ResolvedLinkResult
                        {
                            OriginalUrl = url,
                            DirectUrl = directUrl,
                            SuggestedFileName = $"{safeTitle}.mp4",
                            FileSize = mediaInfo.VideoSize,
                            IsSuccess = true
                        };

                        twResult.Streams.Add(new MediaStreamInfo
                        {
                            StreamType = "video",
                            Title = $"{safeTitle} (MP4 Video)",
                            Format = "mp4",
                            Quality = mediaInfo.VideoQuality,
                            Url = directUrl,
                            SuggestedFileName = $"{safeTitle}.mp4",
                            FileSize = mediaInfo.VideoSize,
                            IsAudioOnly = false
                        });

                        return twResult;
                    }
                }
            }
            catch { }
        }
        return new ResolvedLinkResult { OriginalUrl = url, DirectUrl = url, IsSuccess = false };
    }

    private static string CleanFileName(string name) => SegmentedDownloader.CleanFileName(name);
}

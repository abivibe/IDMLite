namespace IDMLite.Core.Resolvers;

public class LinkResolverManager
{
    private static readonly Lazy<LinkResolverManager> LazyInstance = new(() => new LinkResolverManager());
    public static LinkResolverManager Instance => LazyInstance.Value;

    private readonly List<ILinkResolver> _resolvers = new();
    private readonly Services.MediaProbeCache _probeCache = new();
    private readonly Services.MediaProbeCache _previewCache = new() { Budget = TimeSpan.FromSeconds(45) };

    public LinkResolverManager()
    {
        Register(new GoogleDriveResolver());
        Register(new MediaFireResolver());
        Register(new InstagramResolver());
        Register(new TwitterResolver());
        Register(new YouTubeResolver());
        Register(new TikTokResolver());
        Register(new CobaltResolver());
        Register(new CustomPlayerResolver());
    }

    public void Register(ILinkResolver resolver)
    {
        _resolvers.Add(resolver);
    }

    public bool HasResolverFor(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (Services.DartEngineHelper.CanResolve(url)) return true;
        return _resolvers.Any(r => r.CanResolve(url));
    }

    public Task<ResolvedLinkResult> ResolveLinkAsync(string url, CancellationToken cancellationToken = default, string? cookies = null)
    {
        if (Services.MediaUrl.IsSocialPage(url) || _resolvers.Any(r => r is CustomPlayerResolver && r.CanResolve(url)))
            return _probeCache.GetAsync(url, cookies, ct => ResolveCoreAsync(url, ct, cookies), cancellationToken);
        return ResolveCoreAsync(url, cancellationToken, cookies);
    }

    public Task<ResolvedLinkResult> ResolveMediaPreviewAsync(string url, CancellationToken cancellationToken = default, string? cookies = null)
        => _previewCache.GetAsync(url, cookies, ct => ResolveCoreAsync(url, ct, cookies), cancellationToken);

    private async Task<ResolvedLinkResult> ResolveCoreAsync(string url, CancellationToken cancellationToken, string? cookies)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ResolvedLinkResult { OriginalUrl = url, DirectUrl = url, IsSuccess = false };
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool isYouTube = Services.MediaUrl.HasHost(url, "youtube.com", "youtu.be");
        // Resolve through the same maintained extractor used for downloading.
        // Keep the page URL so signed CDN URLs can be refreshed at download time.
        if (Services.MediaUrl.IsSocialPage(url) && Services.YtDlpHelper.IsAvailable)
        {
            var info = await Services.YtDlpHelper.GetMediaInfoAsync(url, cancellationToken, cookies).ConfigureAwait(false);
            if (info != null)
            {
                string title = string.Concat(info.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                if (title.Length > 160) title = title[..160];
                var resolved = new ResolvedLinkResult { OriginalUrl = url, DirectUrl = url,
                    SuggestedFileName = title + ".mp4", FileSize = info.VideoSize, IsSuccess = true };
                resolved.Streams.Add(new MediaStreamInfo { Url = url, Title = title, Format = "mp4",
                    Quality = info.VideoQuality, SuggestedFileName = title + ".mp4", FileSize = info.VideoSize, FileSizeIsEstimated = info.VideoSizeIsEstimated });
                resolved.Streams.Add(new MediaStreamInfo { Url = url, Title = title, Format = "mp3", IsAudioOnly = true,
                    Quality = info.AudioQuality, SuggestedFileName = title + ".mp3", FileSize = info.AudioSize, FileSizeIsEstimated = true });
                return resolved;
            }
        }

        if (isYouTube && Services.DartEngineHelper.IsAvailable)
        {
            var alternate = await Services.DartEngineHelper.ProbeAsync(url, cancellationToken).ConfigureAwait(false);
            if (alternate?.IsSuccess == true) return alternate;
        }

        var matchingResolvers = _resolvers.Where(r => r.CanResolve(url)).ToList();
        ResolvedLinkResult? lastFailedResult = null;

        foreach (var resolver in matchingResolvers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = resolver is InstagramResolver igResolver
                    ? await igResolver.ResolveWithCookiesAsync(url, cookies, cancellationToken).ConfigureAwait(false)
                    : resolver is GoogleDriveResolver gdResolver
                        ? await gdResolver.ResolveWithCookiesAsync(url, cookies, cancellationToken).ConfigureAwait(false)
                        : await resolver.ResolveAsync(url, cancellationToken).ConfigureAwait(false);
                if (result != null && result.IsSuccess && !string.IsNullOrWhiteSpace(result.DirectUrl))
                {
                    if (isYouTube)
                    {
                        result.DirectUrl = url;
                    }
                    return result;
                }
                if (result != null)
                {
                    lastFailedResult = result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastFailedResult = new ResolvedLinkResult
                {
                    OriginalUrl = url,
                    DirectUrl = url,
                    IsSuccess = false,
                    ErrorMessage = $"{resolver.ServiceName} çözümleme hatası: {ex.Message}"
                };
            }
        }

        if (lastFailedResult != null)
        {
            return lastFailedResult;
        }

        // Return unchanged if no special resolver
        return new ResolvedLinkResult
        {
            OriginalUrl = url,
            DirectUrl = url,
            IsSuccess = true
        };
    }
}

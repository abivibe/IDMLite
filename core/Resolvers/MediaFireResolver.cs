using System.Net;
using System.Text.RegularExpressions;

namespace IDMLite.Core.Resolvers;

public class MediaFireResolver : ILinkResolver
{
    public string ServiceName => "MediaFire";

    private static readonly Regex MediaFireUrlRegex = new(
        @"mediafire\.com/(?:file|download)/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DirectLinkRegex = new(
        @"href=""(https?://download[0-9]*\.mediafire\.com/[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AriaDownloadRegex = new(
        @"id=""downloadButton""[^>]+href=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Services.MediaUrl.HasHost(url, "mediafire.com") &&
               Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Host.Equals("mediafire.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.mediafire.com", StringComparison.OrdinalIgnoreCase)) &&
               MediaFireUrlRegex.IsMatch(url);
    }

    public static string? ExtractDownloadUrl(string html)
    {
        // Attribute order and quote style vary across MediaFire page templates.
        foreach (Match tag in Regex.Matches(html, @"<a\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var href = Regex.Match(tag.Value, "\\bhref\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
            if (!href.Success) continue;
            string candidate = WebUtility.HtmlDecode(href.Groups[1].Value);
            if (candidate.StartsWith("//")) candidate = "https:" + candidate;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" &&
                Regex.IsMatch(uri.Host, @"^download[\w-]*\.mediafire\.com$", RegexOptions.IgnoreCase))
                return candidate;
        }
        return null;
    }

    public async Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMLite/1.0");

        try
        {
            string html = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

            string? directUrl = ExtractDownloadUrl(html);

            if (!string.IsNullOrEmpty(directUrl))
            {
                string fileName = Path.GetFileName(new Uri(directUrl).LocalPath);
                return new ResolvedLinkResult
                {
                    OriginalUrl = url,
                    DirectUrl = directUrl,
                    SuggestedFileName = WebUtility.UrlDecode(fileName),
                    Headers = new Dictionary<string, string> { ["Referer"] = url },
                    IsSuccess = true
                };
            }

            return new ResolvedLinkResult
            {
                OriginalUrl = url,
                DirectUrl = url,
                IsSuccess = false,
                ErrorMessage = "MediaFire doğrudan indirme bağlantısı sayfada bulunamadı."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new ResolvedLinkResult
            {
                OriginalUrl = url,
                DirectUrl = url,
                IsSuccess = false,
                ErrorMessage = $"MediaFire çözümleme hatası: {ex.Message}"
            };
        }
    }
}

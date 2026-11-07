using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace IDMLite.Core.Resolvers;

public class GoogleDriveResolver : ILinkResolver
{
    public string ServiceName => "Google Drive";

    private static readonly Regex FileIdRegex = new(
        @"(?:/file/d/|id=|/open\?id=|/uc\?id=)([a-zA-Z0-9_-]{15,60})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UcNameSizeRegex = new(
        @"class=""uc-name-size""[^>]*>\s*<a[^>]*>([^<]+)</a>(?:\s*\(([^)]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TitleRegex = new(
        @"<title>(?:Google Drive - )?([^<]+)</title>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // If the URL is already a confirmed direct stream with confirmation tokens or uuid,
        // or takeout zip stream, or direct CDN storage stream (/docs/securesc/), it doesn't need to be re-resolved from scratch.
        if (url.Contains("/docs/securesc/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("takeout-download", StringComparison.OrdinalIgnoreCase) ||
            (url.Contains("confirm=", StringComparison.OrdinalIgnoreCase) &&
             (url.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              url.Contains("usercontent.google.com", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("docs.google.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ResolvedLinkResult> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        return ResolveWithCookiesAsync(url, null, cancellationToken);
    }

    public async Task<ResolvedLinkResult> ResolveWithCookiesAsync(string url, string? cookies, CancellationToken cancellationToken = default)
    {
        // If already confirmed direct URL, takeout stream, or CDN storage stream, preserve it
        if (url.Contains("/docs/securesc/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("takeout-download", StringComparison.OrdinalIgnoreCase) ||
            (url.Contains("confirm=", StringComparison.OrdinalIgnoreCase) &&
             (url.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              url.Contains("usercontent.google.com", StringComparison.OrdinalIgnoreCase))))
        {
            return new ResolvedLinkResult
            {
                OriginalUrl = url,
                DirectUrl = url,
                Cookies = cookies,
                IsSuccess = true
            };
        }

        var match = FileIdRegex.Match(url);
        if (!match.Success)
        {
            // For folders, dynamic zips, or custom endpoints, preserve original direct URL
            return new ResolvedLinkResult
            {
                OriginalUrl = url,
                DirectUrl = url,
                Cookies = cookies,
                IsSuccess = true
            };
        }

        string fileId = match.Groups[1].Value;
        string initialDownloadUrl = $"https://drive.usercontent.google.com/download?id={fileId}&export=download";

        var cookieContainer = new CookieContainer();
        if (!string.IsNullOrWhiteSpace(cookies))
        {
            try
            {
                var cookieParts = cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var cp in cookieParts)
                {
                    int eq = cp.IndexOf('=');
                    if (eq > 0)
                    {
                        string name = cp.Substring(0, eq).Trim();
                        string val = cp.Substring(eq + 1).Trim();
                        // Do not inject foreign OSID to drive.usercontent.google.com
                        if (name.Equals("OSID", StringComparison.OrdinalIgnoreCase) || name.Equals("COMPASS", StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { cookieContainer.Add(new Uri(url), new Cookie(name, val, "/") { Secure = new Uri(url).Scheme == "https" }); } catch { }
                    }
                }
            }
            catch { }
        }

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        try
        {
            // 1. Initial Request (using ResponseHeadersRead to avoid buffering files)
            using var req1 = new HttpRequestMessage(HttpMethod.Get, initialDownloadUrl);
            var response = await client.SendAsync(req1, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            // Follow redirects if direct 301/302/303/307
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect)
            {
                var redirectUrl = response.Headers.Location?.ToString() ?? "";
                if (!string.IsNullOrEmpty(redirectUrl) && !redirectUrl.Contains("drive.google.com/file"))
                {
                    // If redirected to usercontent download or direct storage URL
                    if (redirectUrl.StartsWith("/"))
                    {
                        redirectUrl = "https://drive.usercontent.google.com" + redirectUrl;
                    }
                    initialDownloadUrl = redirectUrl;

                    response.Dispose();
                    using var redirectReq = new HttpRequestMessage(HttpMethod.Get, initialDownloadUrl);
                    response = await client.SendAsync(redirectReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                }
            }

            // Check Content-Disposition directly (for small files that don't trigger virus scan warning)
            string? directFileName = ExtractDispositionFileName(response);
            if (!string.IsNullOrEmpty(directFileName))
            {
                var res = CreateSuccessResult(url, initialDownloadUrl, response, cookieContainer, directFileName, null);
                response.Dispose();
                return res;
            }

            // If not direct download, read body (HTML warning page)
            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();

            // Extract file name and size from warning page HTML
            string? parsedFileName = null;
            long? parsedSize = null;

            var nameMatch = UcNameSizeRegex.Match(html);
            if (nameMatch.Success)
            {
                parsedFileName = WebUtility.HtmlDecode(nameMatch.Groups[1].Value.Trim());
                string sizeStr = nameMatch.Groups[2].Value.Trim();
                parsedSize = ParseHumanSize(sizeStr);
            }

            // Also check <title> if uc-name-size wasn't matched
            if (string.IsNullOrEmpty(parsedFileName))
            {
                var titleMatch = TitleRegex.Match(html);
                if (titleMatch.Success)
                {
                    string t = titleMatch.Groups[1].Value.Trim();
                    if (!t.Equals("Virus scan warning", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("Google Drive", StringComparison.OrdinalIgnoreCase))
                    {
                        parsedFileName = t;
                    }
                }
            }

            // Extract tokens from warning form
            string confirmToken = ExtractInputValue(html, "confirm");
            if (string.IsNullOrEmpty(confirmToken)) confirmToken = "t";

            string uuid = ExtractInputValue(html, "uuid");

            string formId = ExtractInputValue(html, "id");
            if (string.IsNullOrEmpty(formId)) formId = fileId;

            string formExport = ExtractInputValue(html, "export");
            if (string.IsNullOrEmpty(formExport)) formExport = "download";

            // Construct final download URL
            string confirmedUrl = $"https://drive.usercontent.google.com/download?id={formId}&export={formExport}&confirm={confirmToken}";
            if (!string.IsNullOrEmpty(uuid))
            {
                confirmedUrl += $"&uuid={uuid}";
            }

            // If we already parsed the file name and size from the warning page, return confirmedUrl directly.
            // Probing confirmedUrl with GET consumes single-use uuid tokens on Google Drive!
            if (!string.IsNullOrEmpty(parsedFileName) && parsedSize.HasValue && parsedSize.Value > 0)
            {
                string cookieHeader = GetAllCookiesHeader(cookieContainer, new Uri(confirmedUrl));
                return new ResolvedLinkResult
                {
                    OriginalUrl = url,
                    DirectUrl = confirmedUrl,
                    SuggestedFileName = parsedFileName,
                    FileSize = parsedSize,
                    Cookies = cookieHeader,
                    IsSuccess = true
                };
            }

            // Follow redirect chain from confirmedUrl to reach the true direct storage stream (doc-*-docs.googleusercontent.com)
            string directStreamUrl = confirmedUrl;
            HttpResponseMessage? directResponse = null;
            int redirectLimit = 6;

            for (int i = 0; i < redirectLimit; i++)
            {
                using var probeReq = new HttpRequestMessage(HttpMethod.Get, directStreamUrl);
                probeReq.Headers.Range = new RangeHeaderValue(0, 0);
                probeReq.Headers.Referrer = new Uri("https://drive.google.com/");

                var probeRes = await client.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (probeRes.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect)
                {
                    var loc = probeRes.Headers.Location?.ToString();
                    probeRes.Dispose();
                    if (!string.IsNullOrEmpty(loc))
                    {
                        if (loc.StartsWith("/"))
                        {
                            loc = "https://drive.usercontent.google.com" + loc;
                        }
                        directStreamUrl = loc;
                        continue;
                    }
                }

                directResponse = probeRes;
                break;
            }

            if (directResponse == null)
            {
                return new ResolvedLinkResult
                {
                    OriginalUrl = url,
                    DirectUrl = directStreamUrl,
                    SuggestedFileName = parsedFileName,
                    FileSize = parsedSize,
                    IsSuccess = true
                };
            }

            string? finalFileName = ExtractDispositionFileName(directResponse) ?? parsedFileName;
            long? finalSize = null;

            if (directResponse.Content.Headers.ContentRange?.Length.HasValue == true)
            {
                finalSize = directResponse.Content.Headers.ContentRange.Length.Value;
            }
            else if (directResponse.Content.Headers.ContentLength.HasValue && directResponse.Content.Headers.ContentLength.Value > 1)
            {
                finalSize = directResponse.Content.Headers.ContentLength.Value;
            }
            else if (parsedSize.HasValue)
            {
                finalSize = parsedSize.Value;
            }

            string? confMediaType = directResponse.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (confMediaType == "text/html")
            {
                // If direct response is still HTML, check if error page (e.g. quota exceeded)
                string errHtml = await directResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                directResponse.Dispose();

                string errorMsg = "Google Drive dosya erişimi veya indirme kotası engeli.";
                var subcaptionMatch = Regex.Match(errHtml, @"<p class=""uc-error-subcaption"">([^<]+)</p>", RegexOptions.IgnoreCase);
                if (subcaptionMatch.Success)
                {
                    errorMsg = WebUtility.HtmlDecode(subcaptionMatch.Groups[1].Value.Trim());
                }
                else if (errHtml.Contains("Download quota", StringComparison.OrdinalIgnoreCase))
                {
                    errorMsg = "Google Drive indirme kotası aşıldı. Lütfen daha sonra tekrar deneyin.";
                }

                return new ResolvedLinkResult
                {
                    OriginalUrl = url,
                    DirectUrl = url,
                    SuggestedFileName = parsedFileName,
                    FileSize = parsedSize,
                    IsSuccess = false,
                    ErrorMessage = errorMsg
                };
            }

            var result = CreateSuccessResult(url, directStreamUrl, directResponse, cookieContainer, finalFileName, finalSize);
            directResponse.Dispose();
            return result;
        }
        catch (Exception ex)
        {
            // Fallback confirmed URL
            string fallbackUrl = $"https://drive.usercontent.google.com/download?id={fileId}&export=download&confirm=t";
            return new ResolvedLinkResult
            {
                OriginalUrl = url,
                DirectUrl = fallbackUrl,
                IsSuccess = true,
                ErrorMessage = $"Google Drive otomatik çözümleme uyarısı: {ex.Message}"
            };
        }
    }

    private static string? ExtractDispositionFileName(HttpResponseMessage res)
    {
        string? name = res.Content.Headers.ContentDisposition?.FileNameStar 
                    ?? res.Content.Headers.ContentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(name)) return null;

        name = name.Trim('"', '\'', ' ');
        var rfc5987Match = Regex.Match(name, @"^(?:UTF-8|ISO-8859-1)''(.+)$", RegexOptions.IgnoreCase);
        if (rfc5987Match.Success)
        {
            try { name = Uri.UnescapeDataString(rfc5987Match.Groups[1].Value); } catch { }
        }
        else if (name.Contains('%'))
        {
            try { name = Uri.UnescapeDataString(name); } catch { }
        }
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static string ExtractInputValue(string html, string inputName)
    {
        var regex = new Regex($@"<input[^>]+name=""{inputName}""[^>]+value=""([^""]*)""", RegexOptions.IgnoreCase);
        var match = regex.Match(html);
        if (match.Success) return match.Groups[1].Value;

        // Try reverse attribute order: value="..." name="..."
        var reverseRegex = new Regex($@"<input[^>]+value=""([^""]*)""[^>]+name=""{inputName}""", RegexOptions.IgnoreCase);
        var reverseMatch = reverseRegex.Match(html);
        return reverseMatch.Success ? reverseMatch.Groups[1].Value : "";
    }

    private static long? ParseHumanSize(string sizeStr)
    {
        if (string.IsNullOrWhiteSpace(sizeStr)) return null;
        try
        {
            sizeStr = sizeStr.Trim();
            if (sizeStr.EndsWith("M", StringComparison.OrdinalIgnoreCase) || sizeStr.EndsWith("MB", StringComparison.OrdinalIgnoreCase))
            {
                string num = sizeStr.TrimEnd('M', 'm', 'B', 'b').Trim();
                if (double.TryParse(num, System.Globalization.CultureInfo.InvariantCulture, out double mb))
                    return (long)(mb * 1024 * 1024);
            }
            else if (sizeStr.EndsWith("G", StringComparison.OrdinalIgnoreCase) || sizeStr.EndsWith("GB", StringComparison.OrdinalIgnoreCase))
            {
                string num = sizeStr.TrimEnd('G', 'g', 'B', 'b').Trim();
                if (double.TryParse(num, System.Globalization.CultureInfo.InvariantCulture, out double gb))
                    return (long)(gb * 1024 * 1024 * 1024);
            }
            else if (sizeStr.EndsWith("K", StringComparison.OrdinalIgnoreCase) || sizeStr.EndsWith("KB", StringComparison.OrdinalIgnoreCase))
            {
                string num = sizeStr.TrimEnd('K', 'k', 'B', 'b').Trim();
                if (double.TryParse(num, System.Globalization.CultureInfo.InvariantCulture, out double kb))
                    return (long)(kb * 1024);
            }
        }
        catch { }
        return null;
    }

    private static string GetAllCookiesHeader(CookieContainer cookieContainer, Uri uri)
    {
        var cookieList = new List<string>();
        try
        {
            foreach (Cookie c in cookieContainer.GetCookies(uri))
            {
                string kv = $"{c.Name}={c.Value}";
                if (!cookieList.Contains(kv)) cookieList.Add(kv);
            }

        }
        catch { }
        return string.Join("; ", cookieList);
    }

    private static ResolvedLinkResult CreateSuccessResult(
        string originalUrl,
        string directUrl,
        HttpResponseMessage res,
        CookieContainer cookies,
        string? preferredFileName,
        long? preferredSize)
    {
        string? fileName = preferredFileName ?? ExtractDispositionFileName(res);
        long? size = preferredSize ?? res.Content.Headers.ContentLength;

        // Collect all cookies across all Google domains
        string cookieHeader = GetAllCookiesHeader(cookies, new Uri(directUrl));

        return new ResolvedLinkResult
        {
            OriginalUrl = originalUrl,
            DirectUrl = directUrl,
            SuggestedFileName = fileName,
            FileSize = size,
            Cookies = cookieHeader,
            IsSuccess = true
        };
    }
}

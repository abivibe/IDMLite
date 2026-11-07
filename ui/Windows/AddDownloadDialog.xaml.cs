using System.IO;
using System.Windows;
using System.Windows.Controls;
using IDMLite.Core;
using IDMLite.Core.Resolvers;
using IDMLite.Models;
using IDMLite.Services;
using Microsoft.Win32;

namespace IDMLite.Windows;

public partial class AddDownloadDialog : Window
{
    private readonly SettingsManager _settingsManager;
    private readonly DownloadRequest? _initialRequest;
    private CancellationTokenSource? _probeCts;
    private bool _closed;
    private string? _resolvedUrl;
    private string? _resolvedCookies;
    private List<MediaStreamInfo>? _probeStreams;
    private long _probedTotalBytes;
    private bool _mediaPreviewPending;
    private string? _lastSuggestedFileName;
    private bool _probedSizeIsEstimated;
    private readonly Func<string, CancellationToken, Task<ResolvedLinkResult>> _mediaPreviewResolver;

    public DownloadTask? CreatedTask { get; private set; }
    public bool AutoStart { get; private set; }

    public AddDownloadDialog(SettingsManager settingsManager, DownloadRequest? initialRequest = null,
        Func<string, CancellationToken, Task<ResolvedLinkResult>>? mediaPreviewResolver = null)
    {
        InitializeComponent();
        Closed += (_, _) => { _closed = true; _probeCts?.Cancel(); _probeCts?.Dispose(); _probeCts = null; };
        _settingsManager = settingsManager;
        _initialRequest = initialRequest;
        _mediaPreviewResolver = mediaPreviewResolver ?? ((url, ct) => Task.Run(() =>
            LinkResolverManager.Instance.ResolveMediaPreviewAsync(url, ct, url == _initialRequest?.Url ? _initialRequest.Cookies : null), ct));

        CmbCategory.SelectedIndex = 0;

        if (initialRequest != null)
        {
            if (!string.IsNullOrEmpty(initialRequest.ResolvedStreamUrl))
            {
                _resolvedUrl = initialRequest.ResolvedStreamUrl;
            }
            if (!string.IsNullOrEmpty(initialRequest.FileName))
            {
                TxtFileName.Text = initialRequest.FileName;
            }

            if (!string.IsNullOrEmpty(initialRequest.Category) &&
                Enum.TryParse<DownloadCategory>(initialRequest.Category, true, out var cat))
            {
                SelectCategory(cat);
            }

            if (initialRequest.FileSize.HasValue && initialRequest.FileSize.Value > 0)
            {
                LblFileSize.Text = DownloadItemViewModel.FormatBytes(initialRequest.FileSize.Value);
            }
            TxtUrl.Text = initialRequest.Url;
        }
        else
        {
            // Auto-check clipboard for URLs!
            try
            {
                if (Clipboard.ContainsText())
                {
                    string clip = Clipboard.GetText().Trim();
                    if (Uri.TryCreate(clip, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                    {
                        TxtUrl.Text = clip;
                    }
                }
            }
            catch { }
        }

        UpdateDirectoryForSelectedCategory();
    }

    private void SelectCategory(DownloadCategory cat)
    {
        foreach (ComboBoxItem item in CmbCategory.Items)
        {
            if (item.Tag?.ToString() == cat.ToString())
            {
                CmbCategory.SelectedItem = item;
                break;
            }
        }
    }

    private DownloadCategory GetSelectedCategory()
    {
        if (CmbCategory.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<DownloadCategory>(item.Tag?.ToString(), true, out var cat))
        {
            return cat;
        }
        return DownloadCategory.Other;
    }

    private void UpdateDirectoryForSelectedCategory()
    {
        var cat = GetSelectedCategory();
        TxtDirectory.Text = _settingsManager.GetDirectoryForCategory(cat);
    }

    private void CmbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateDirectoryForSelectedCategory();
        }
    }

    private async void TxtUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = null;
        _resolvedUrl = null;
        _resolvedCookies = null;
        _probeStreams = null;
        _probedTotalBytes = 0;
        _probedSizeIsEstimated = false;
        _mediaPreviewPending = false;
        _lastSuggestedFileName = null;
        PreviewNotice.Visibility = Visibility.Collapsed;
        PnlStreamSelection.Visibility = Visibility.Collapsed;
        LblFileSize.ToolTip = null;
        string url = TxtUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            LblFileSize.Text = "-";
            PnlStreamSelection.Visibility = Visibility.Collapsed;
            _probeStreams = null;
            _probedTotalBytes = 0;
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtFileName.Text))
        {
            TxtFileName.Text = SegmentedDownloader.ExtractFileNameFromUrl(url);
            var cat = CategoryDetector.FromExtension(Path.GetExtension(TxtFileName.Text));
            SelectCategory(cat);
        }

        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;
        var activeProbe = _probeCts;
        bool isSocialPreview = IDMLite.Core.Services.MediaUrl.IsSocialPage(url) || CustomPlayerResolver.IsGenericVideoHost(url);
        _mediaPreviewPending = isSocialPreview;
        _probeCts.CancelAfter(TimeSpan.FromSeconds(isSocialPreview ? 50 : 8));

        var loc = LocalizationManager.Instance;
        ct.Register(() => Dispatcher.BeginInvoke(() => {
            if (_closed || !ReferenceEquals(_probeCts, activeProbe)) return;
            if (isSocialPreview)
            {
                _mediaPreviewPending = false;
                if (LblFileSize.Text == "—") LblFileSize.Text = loc.Get("Str_SizeUnavailable", "Size unavailable");
                SetPreviewNotice("Str_PreviewDeferred");
            }
            else if (LblFileSize.Text == loc.Get("Str_Determining", "Belirleniyor..."))
                LblFileSize.Text = loc.Get("Str_Unknown", "Bilinmiyor");
        }));
        if (url == _initialRequest?.Url && _initialRequest.FileSize > 0)
        {
            LblFileSize.Text = FormatPreviewSize(_initialRequest.FileSize.Value, _initialRequest.FileSizeIsEstimated);
        }
        else
        {
            LblFileSize.Text = isSocialPreview ? "—" : loc.Get("Str_Determining", "Belirleniyor...");
        }

        if (_initialRequest?.Direct == true && url == _initialRequest.Url &&
            !string.IsNullOrWhiteSpace(_initialRequest.FileName) && _initialRequest.FileSize > 0 &&
            !IDMLite.Core.Services.MediaUrl.IsSocialPage(url))
        {
            _resolvedUrl = url;
            _resolvedCookies = _initialRequest.Cookies;
            _probedTotalBytes = _initialRequest.FileSize.Value;
            LblFileSize.Text = DownloadItemViewModel.FormatBytes(_probedTotalBytes);
            PnlStreamSelection.Visibility = Visibility.Collapsed;
            return;
        }

        // Show browser metadata immediately while the real preview resolves.
        if (isSocialPreview) SetPreviewNotice("Str_PreviewUpdating");
        if (IDMLite.Core.Services.MediaUrl.IsSocialPage(url))
        {
            _resolvedUrl = url;
            var hint = url == _initialRequest?.Url ? _initialRequest : null;
            _resolvedCookies = hint?.Cookies;
            _probedTotalBytes = hint?.FileSize ?? 0;
            _probedSizeIsEstimated = hint?.FileSizeIsEstimated == true;
            string title = Path.GetFileNameWithoutExtension(TxtFileName.Text);
            _probeStreams = new() {
                new MediaStreamInfo { Url = url, SuggestedFileName = title + ".mp4", Title = title, Format = "mp4", Quality = "", FileSize = hint?.FileSize, FileSizeIsEstimated = _probedSizeIsEstimated },
                new MediaStreamInfo { Url = url, SuggestedFileName = title + ".mp3", Title = title, Format = "mp3", Quality = "", IsAudioOnly = true }
            };
            var cachedPreview = GetInitialPreview(url);
            if (cachedPreview != null)
            {
                _probeStreams = cachedPreview.Streams;
                _probedTotalBytes = cachedPreview.FileSize ?? _probedTotalBytes;
            }
            PnlStreamSelection.Visibility = Visibility.Visible;
            UpdateSelectedStreamUI();
        }

        // Fast parallel title resolution for YouTube (150ms) to update filename immediately
        if (!(url == _initialRequest?.Url && !string.IsNullOrWhiteSpace(_initialRequest.FileName)) &&
            IDMLite.Core.Services.MediaUrl.HasHost(url, "youtube.com", "youtu.be"))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    string oembedUrl = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(url)}&format=json";
                    using var resp = await hc.GetAsync(oembedUrl, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        using var doc = await System.Text.Json.JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
                        if (doc.RootElement.TryGetProperty("title", out var titleProp))
                        {
                            string raw = titleProp.GetString() ?? "";
                            string clean = SegmentedDownloader.CleanFileName(raw);
                            if (!string.IsNullOrWhiteSpace(clean))
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (!ct.IsCancellationRequested)
                                    {
                                        bool isGeneric = string.IsNullOrWhiteSpace(TxtFileName.Text) ||
                                                         TxtFileName.Text.StartsWith("watch", StringComparison.OrdinalIgnoreCase) ||
                                                         TxtFileName.Text.StartsWith("download", StringComparison.OrdinalIgnoreCase) ||
                                                         TxtFileName.Text.StartsWith("videoplayback", StringComparison.OrdinalIgnoreCase) ||
                                                         string.IsNullOrEmpty(Path.GetExtension(TxtFileName.Text));
                                        if (isGeneric)
                                        {
                                            TxtFileName.Text = $"{clean}.mp4";
                                            SelectCategory(DownloadCategory.Video);
                                        }
                                    }
                                });
                            }
                        }
                    }
                }
                catch { }
            }, ct);
        }

        // Note: Instagram title + direct stream resolution is now handled in parallel (~250ms) inside InstagramResolver with in-memory caching.

        // Fast parallel title resolution for X (Twitter)
        if (url.Contains("twitter.com", StringComparison.OrdinalIgnoreCase) || url.Contains("x.com", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var matchStatus = System.Text.RegularExpressions.Regex.Match(url, @"status\/([0-9]+)");
                    if (matchStatus.Success)
                    {
                        string statusId = matchStatus.Groups[1].Value;
                        using var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                        hc.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                        hc.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                        string apiUrl = $"https://api.fxtwitter.com/i/status/{statusId}";
                        var resp = await hc.GetAsync(apiUrl, ct).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                            using var doc = await System.Text.Json.JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
                            if (doc.RootElement.TryGetProperty("tweet", out var tweet))
                            {
                                string text = tweet.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? "" : "";
                                string author = "";
                                if (tweet.TryGetProperty("author", out var authorProp) && authorProp.TryGetProperty("name", out var nameProp))
                                {
                                    author = nameProp.GetString() ?? "";
                                }
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    if (text.Length > 70) text = text.Substring(0, 70).Trim();
                                    string resolved = !string.IsNullOrWhiteSpace(author) ? $"{author} - {text}" : text;
                                    string safe = SegmentedDownloader.CleanFileName(resolved);
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (!ct.IsCancellationRequested)
                                        {
                                            bool isGeneric = string.IsNullOrWhiteSpace(TxtFileName.Text) ||
                                                             TxtFileName.Text.StartsWith("status", StringComparison.OrdinalIgnoreCase) ||
                                                             TxtFileName.Text.StartsWith("X_Video", StringComparison.OrdinalIgnoreCase) ||
                                                             TxtFileName.Text.StartsWith("download", StringComparison.OrdinalIgnoreCase);
                                            if (isGeneric)
                                            {
                                                TxtFileName.Text = $"{safe}.mp4";
                                                SelectCategory(DownloadCategory.Video);
                                            }
                                        }
                                    });
                                }
                            }
                        }
                    }
                }
                catch { }
            }, ct);
        }

        // Fast parallel title resolution for all OTHER generic web media players
        if (!UniversalVideoDownloader.IsSocialPlatformUrl(url))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string pageToScrape = !string.IsNullOrWhiteSpace(_initialRequest?.Referer) && _initialRequest.Referer.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? _initialRequest.Referer
                        : url;
                    var (scrapedTitle, _) = await CustomPlayerResolver.TryScrapePageMetadataAsync(pageToScrape, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(scrapedTitle))
                    {
                        string safe = CustomPlayerResolver.CleanFileName(scrapedTitle);
                        if (!string.IsNullOrWhiteSpace(safe))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (!ct.IsCancellationRequested)
                                {
                                    string curNoExt = Path.GetFileNameWithoutExtension(TxtFileName.Text);
                                    bool isGeneric = string.IsNullOrWhiteSpace(TxtFileName.Text) ||
                                                     CustomPlayerResolver.IsGenericPlaylistFileName(curNoExt) ||
                                                     curNoExt.StartsWith("HLS_Video_", StringComparison.OrdinalIgnoreCase) ||
                                                     curNoExt.StartsWith("Web_Media_", StringComparison.OrdinalIgnoreCase) ||
                                                     curNoExt.StartsWith("download_", StringComparison.OrdinalIgnoreCase);
                                    if (isGeneric)
                                    {
                                        TxtFileName.Text = $"{safe}.mp4";
                                        SelectCategory(DownloadCategory.Video);
                                    }
                                }
                            });
                        }
                    }
                }
                catch { }
            }, ct);
        }

        if (isSocialPreview)
        {
            string suggestedName = TxtFileName.Text;
            try
            {
                var preview = await _mediaPreviewResolver(url, ct);
                if (_closed || ct.IsCancellationRequested || !ReferenceEquals(_probeCts, activeProbe)) return;
                _mediaPreviewPending = false;
                if (preview.IsSuccess)
                {
                    _resolvedUrl = url;
                    _resolvedCookies = preview.Cookies ?? (url == _initialRequest?.Url ? _initialRequest.Cookies : null);
                    if (preview.FileSize > 0)
                    {
                        _probedTotalBytes = preview.FileSize.Value;
                        _probedSizeIsEstimated = preview.Streams.FirstOrDefault(stream => !stream.IsAudioOnly)?.FileSizeIsEstimated ?? true;
                    }
                    if (preview.Streams.Count > 0)
                    {
                        _probeStreams = preview.Streams;
                        PnlStreamSelection.Visibility = Visibility.Visible;
                        UpdateSelectedStreamUI(updateFileName: TxtFileName.Text == suggestedName || TxtFileName.Text == _lastSuggestedFileName);
                    }
                    else
                    {
                        if (TxtFileName.Text == suggestedName && !string.IsNullOrWhiteSpace(preview.SuggestedFileName))
                            TxtFileName.Text = preview.SuggestedFileName;
                        LblFileSize.Text = _probedTotalBytes > 0 ? FormatPreviewSize(_probedTotalBytes, _probedSizeIsEstimated)
                            : loc.Get("Str_SizeUnavailable", "Size unavailable");
                    }
                    PreviewNotice.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SetPreviewNotice("Str_PreviewDeferred");
                    if (LblFileSize.Text == "—") LblFileSize.Text = loc.Get("Str_SizeUnavailable", "Size unavailable");
                    LblFileSize.ToolTip = preview.ErrorMessage;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch
            {
                if (!_closed && !ct.IsCancellationRequested && ReferenceEquals(_probeCts, activeProbe))
                {
                    if (LblFileSize.Text == "—") LblFileSize.Text = loc.Get("Str_SizeUnavailable", "Size unavailable");
                    SetPreviewNotice("Str_PreviewDeferred");
                }
            }
            finally
            {
                if (ReferenceEquals(_probeCts, activeProbe)) _mediaPreviewPending = false;
            }
            return;
        }

        bool isDirectGoogleDrive = _initialRequest != null && _initialRequest.Direct && url == _initialRequest.Url &&
                                   (url.Contains("takeout-download", StringComparison.OrdinalIgnoreCase) ||
                                    url.Contains("usercontent.google.com", StringComparison.OrdinalIgnoreCase) ||
                                    url.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase));

        if (isDirectGoogleDrive && url == _initialRequest?.Url && _initialRequest.FileSize > 0)
        {
            _resolvedUrl = url;
            _resolvedCookies = _initialRequest.Cookies;
            _probedTotalBytes = _initialRequest.FileSize.Value;
            LblFileSize.Text = DownloadItemViewModel.FormatBytes(_initialRequest.FileSize.Value);
            PnlStreamSelection.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(_initialRequest.FileName))
            {
                TxtFileName.Text = _initialRequest.FileName;
                var cat = CategoryDetector.FromExtension(Path.GetExtension(_initialRequest.FileName));
                SelectCategory(cat);
            }
            return;
        }

        // Fast path for direct video streams passed from the browser extension (Instagram, TikTok, Twitter, generic)
        bool hasDirectExtensionStream = _initialRequest != null &&
                                        !string.IsNullOrWhiteSpace(_initialRequest.ResolvedStreamUrl) &&
                                        _initialRequest.ResolvedStreamUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                                        url == _initialRequest.Url;

        if (hasDirectExtensionStream)
        {
            _resolvedUrl = _initialRequest!.ResolvedStreamUrl;
            _resolvedCookies = _initialRequest.Cookies;
            PnlStreamSelection.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(_initialRequest.FileName))
            {
                TxtFileName.Text = _initialRequest.FileName;
                var cat = CategoryDetector.FromExtension(Path.GetExtension(_initialRequest.FileName));
                SelectCategory(cat);
            }

            if (_initialRequest.FileSize.HasValue && _initialRequest.FileSize.Value > 0)
            {
                _probedTotalBytes = _initialRequest.FileSize.Value;
                LblFileSize.Text = DownloadItemViewModel.FormatBytes(_probedTotalBytes);
                return;
            }

            // Fast parallel probe on the direct CDN stream URL (750ms timeout)
            _ = Task.Run(async () =>
            {
                try
                {
                    using var sizeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sizeCts.CancelAfter(TimeSpan.FromMilliseconds(750));
                    long? probedSize = await CobaltResolver.ProbeUrlSizeAsync(_resolvedUrl!, sizeCts.Token).ConfigureAwait(false);
                    if (probedSize.HasValue && probedSize.Value > 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (!ct.IsCancellationRequested)
                            {
                                _probedTotalBytes = probedSize.Value;
                                LblFileSize.Text = DownloadItemViewModel.FormatBytes(probedSize.Value);
                            }
                        });
                    }
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (!ct.IsCancellationRequested && LblFileSize.Text == loc.Get("Str_Determining", "Belirleniyor..."))
                            {
                                LblFileSize.Text = loc.Get("Str_Unknown", "Bilinmiyor");
                            }
                        });
                    }
                }
                catch
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!ct.IsCancellationRequested && LblFileSize.Text == loc.Get("Str_Determining", "Belirleniyor..."))
                        {
                            LblFileSize.Text = loc.Get("Str_Unknown", "Bilinmiyor");
                        }
                    });
                }
            }, ct);

            return;
        }

        try
        {
            using var downloader = new SegmentedDownloader(url, Path.Combine(Path.GetTempPath(), "dummy"));
            if (_initialRequest != null)
            {
                downloader.Referer = _initialRequest.Referer;
                downloader.UserAgent = _initialRequest.UserAgent;
                downloader.Cookies = _initialRequest.Cookies;
                downloader.SkipResolver = _initialRequest.Direct && url == _initialRequest.Url;
                if (_initialRequest.FileSize.HasValue && _initialRequest.FileSize.Value > 0)
                {
                    downloader.TotalBytes = _initialRequest.FileSize.Value;
                }
            }

            await downloader.ProbeAsync(ct);

            if (!ct.IsCancellationRequested)
            {
                if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                {
                    _resolvedUrl = url;
                }
                else if (!string.IsNullOrEmpty(_initialRequest?.ResolvedStreamUrl) &&
                         (url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase) ||
                          url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
                          url.Contains("twitter.com", StringComparison.OrdinalIgnoreCase) ||
                          url.Contains("x.com", StringComparison.OrdinalIgnoreCase)))
                {
                    _resolvedUrl = _initialRequest.ResolvedStreamUrl;
                }
                else if (downloader.Url != url && !string.IsNullOrEmpty(downloader.Url))
                {
                    _resolvedUrl = downloader.Url;
                }
                else
                {
                    _resolvedUrl = downloader.Url;
                }
                _resolvedCookies = downloader.Cookies ?? _initialRequest?.Cookies;
                _probeStreams = downloader.ResolvedStreams;
                _probedTotalBytes = downloader.TotalBytes;

                if (_probeStreams != null && _probeStreams.Count > 1)
                {
                    PnlStreamSelection.Visibility = Visibility.Visible;
                    UpdateSelectedStreamUI();
                }
                else
                {
                    PnlStreamSelection.Visibility = Visibility.Collapsed;
                    long finalSize = downloader.TotalBytes;
                    if (finalSize <= 0 && _initialRequest?.FileSize.HasValue == true && _initialRequest.FileSize.Value > 0)
                    {
                        finalSize = _initialRequest.FileSize.Value;
                    }
                    if (finalSize <= 0 && !string.IsNullOrWhiteSpace(_initialRequest?.ResolvedStreamUrl) &&
                        !string.Equals(_initialRequest.ResolvedStreamUrl, downloader.Url, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var streamProbe = new SegmentedDownloader(_initialRequest.ResolvedStreamUrl, Path.Combine(Path.GetTempPath(), "dummy"), 1)
                            {
                                Referer = _initialRequest?.Referer,
                                UserAgent = _initialRequest?.UserAgent,
                                Cookies = _resolvedCookies,
                                SkipResolver = true
                            };
                            await streamProbe.ProbeAsync(ct);
                            if (streamProbe.TotalBytes > 0)
                            {
                                finalSize = streamProbe.TotalBytes;
                                _probedTotalBytes = finalSize;
                            }
                        }
                        catch { }
                    }

                    if (finalSize > 0)
                    {
                        LblFileSize.Text = FormatPreviewSize(finalSize, _probeStreams?.FirstOrDefault()?.FileSizeIsEstimated == true);
                    }
                    else
                    {
                        LblFileSize.Text = loc.Get("Str_Unknown", "Bilinmiyor");
                    }

                    if (!string.IsNullOrWhiteSpace(downloader.SuggestedFileName))
                    {
                        bool isAuthoritativeInstagramTitle = url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase) &&
                                                             !downloader.SuggestedFileName.StartsWith("Instagram_Video_", StringComparison.OrdinalIgnoreCase) &&
                                                             !downloader.SuggestedFileName.Equals("Instagram Video.mp4", StringComparison.OrdinalIgnoreCase);

                        bool isGenericName = string.IsNullOrWhiteSpace(TxtFileName.Text) ||
                                             TxtFileName.Text.StartsWith("download_", StringComparison.OrdinalIgnoreCase) ||
                                             TxtFileName.Text.StartsWith("Instagram_Video", StringComparison.OrdinalIgnoreCase) ||
                                             TxtFileName.Text.Equals("Instagram Video.mp4", StringComparison.OrdinalIgnoreCase) ||
                                             TxtFileName.Text.Equals("Media.mp4", StringComparison.OrdinalIgnoreCase) ||
                                             TxtFileName.Text.Equals("view", StringComparison.OrdinalIgnoreCase) ||
                                             TxtFileName.Text.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                                             string.IsNullOrEmpty(Path.GetExtension(TxtFileName.Text));

                        if (isGenericName || isAuthoritativeInstagramTitle)
                        {
                            string safeName = downloader.SuggestedFileName;
                            if (safeName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                            {
                                safeName = Path.ChangeExtension(safeName, ".mp4");
                            }
                            TxtFileName.Text = safeName;
                            var cat = CategoryDetector.FromExtension(Path.GetExtension(safeName));
                            SelectCategory(cat);
                        }
                    }
                }
            }
        }
        catch
        {
            if (!ct.IsCancellationRequested)
            {
                if (_initialRequest?.FileSize.HasValue == true && _initialRequest.FileSize.Value > 0)
                {
                    LblFileSize.Text = DownloadItemViewModel.FormatBytes(_initialRequest.FileSize.Value);
                }
                else
                {
                    LblFileSize.Text = loc.Get("Str_Unknown", "Bilinmiyor");
                }
            }
        }
    }

    private void RbStream_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _probeStreams == null) return;
        UpdateSelectedStreamUI();
    }

    private void UpdateSelectedStreamUI(bool updateFileName = true)
    {
        if (_probeStreams == null || _probeStreams.Count == 0) return;

        bool isAudio = RbStreamAudio.IsChecked == true;
        MediaStreamInfo? stream = isAudio
            ? _probeStreams.FirstOrDefault(s => s.IsAudioOnly) ?? _probeStreams.LastOrDefault()
            : _probeStreams.FirstOrDefault(s => !s.IsAudioOnly) ?? _probeStreams.FirstOrDefault();

        if (stream != null)
        {
            string currentUrl = TxtUrl.Text.Trim();
            if (currentUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                currentUrl.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                _resolvedUrl = currentUrl;
            }
            else
            {
                _resolvedUrl = stream.Url;
            }
            if (updateFileName && !string.IsNullOrWhiteSpace(stream.SuggestedFileName))
            {
                string fn = stream.SuggestedFileName;
                if (fn.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                {
                    fn = Path.ChangeExtension(fn, ".mp4");
                }
                TxtFileName.Text = fn;
            }
            else if (updateFileName)
            {
                string ext = isAudio ? ".mp3" : ".mp4";
                string current = Path.GetFileNameWithoutExtension(TxtFileName.Text);
                TxtFileName.Text = $"{current}{ext}";
            }
            if (updateFileName) _lastSuggestedFileName = TxtFileName.Text;

            SelectCategory(isAudio ? DownloadCategory.Music : DownloadCategory.Video);

            long? size = stream.FileSize;
            if (!isAudio && (!size.HasValue || size.Value <= 0) && _probedTotalBytes > 0)
            {
                size = _probedTotalBytes;
            }
            if (!isAudio && (!size.HasValue || size.Value <= 0) && currentUrl == _initialRequest?.Url && _initialRequest.FileSize > 0)
            {
                size = _initialRequest.FileSize;
            }

            if (size.HasValue && size.Value > 0)
            {
                LblFileSize.Text = FormatPreviewSize(size.Value, stream.FileSizeIsEstimated || (!stream.FileSize.HasValue && _probedSizeIsEstimated));
            }
            else
            {
                LblFileSize.Text = _mediaPreviewPending
                    ? "—"
                    : LocalizationManager.Instance.Get("Str_SizeUnavailable", "Size unavailable");
            }
        }
        var loc = LocalizationManager.Instance;
        var video = _probeStreams.FirstOrDefault(s => !s.IsAudioOnly);
        var audio = _probeStreams.FirstOrDefault(s => s.IsAudioOnly);
        RbStreamVideo.Content = loc.Get("Str_VideoFormat", "Video") + (string.IsNullOrWhiteSpace(video?.Quality) ? "" : $" ({video.Quality})");
        RbStreamAudio.Content = loc.Get("Str_AudioFormat", "Audio") + (string.IsNullOrWhiteSpace(audio?.Quality) ? "" : $" ({audio.Quality})");
    }

    private void SetPreviewNotice(string key)
    {
        LblPreviewStatus.Text = LocalizationManager.Instance.Get(key);
        PreviewNotice.Visibility = Visibility.Visible;
    }

    private static string FormatPreviewSize(long size, bool estimated) => (estimated ? "≈ " : "") + DownloadItemViewModel.FormatBytes(size);

    private ResolvedLinkResult? GetInitialPreview(string url)
    {
        var preview = _initialRequest?.Preview;
        var age = DateTime.UtcNow - (_initialRequest?.PreviewCachedAtUtc.ToUniversalTime() ?? DateTime.MinValue);
        if (preview?.IsSuccess != true || preview.OriginalUrl != url || age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromMinutes(3) ||
            preview.Streams.Count == 0 || preview.Streams.Count > 32) return null;
        return new ResolvedLinkResult {
            OriginalUrl = url, DirectUrl = url, FileSize = preview.FileSize,
            Streams = preview.Streams.Select(stream => new MediaStreamInfo {
                Url = url, Title = stream.Title, Format = stream.IsAudioOnly ? "mp3" : "mp4",
                SuggestedFileName = SegmentedDownloader.CleanFileName(stream.SuggestedFileName ?? stream.Title),
                Quality = stream.Quality.Length > 64 ? stream.Quality[..64] : stream.Quality,
                IsAudioOnly = stream.IsAudioOnly, FileSize = stream.FileSize, FileSizeIsEstimated = stream.FileSizeIsEstimated
            }).ToList()
        };
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationManager.Instance.Get("Str_SaveDirectory", "İndirme Klasörünü Seçin"),
            InitialDirectory = Directory.Exists(TxtDirectory.Text) ? TxtDirectory.Text : _settingsManager.Current.DefaultDownloadDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            TxtDirectory.Text = dialog.FolderName;
        }
    }

    private void BtnDownloadNow_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateAndCreateTask())
        {
            AutoStart = true;
            DialogResult = true;
            Close();
        }
    }

    private void BtnDownloadLater_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateAndCreateTask())
        {
            AutoStart = false;
            DialogResult = true;
            Close();
        }
    }

    private bool ValidateAndCreateTask()
    {
        string url = TxtUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            MessageBox.Show(
                LocalizationManager.Instance.Get("Str_InvalidUrl", "Lütfen geçerli bir indirme URL'si girin."),
                "IDMLite",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            TxtUrl.Focus();
            return false;
        }

        string fileName = TxtFileName.Text.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = SegmentedDownloader.ExtractFileNameFromUrl(url);
        }
        if (fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Path.ChangeExtension(fileName, ".mp4");
        }

        string dir = TxtDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = _settingsManager.GetDirectoryForCategory(GetSelectedCategory());
        }

        long finalTaskSize = 0;
        if (LblFileSize.Text != "-" && LblFileSize.Text != LocalizationManager.Instance.Get("Str_Unknown", "Bilinmiyor"))
        {
            if (_probeStreams != null)
            {
                bool isAudio = RbStreamAudio.IsChecked == true;
                var stream = isAudio
                    ? _probeStreams.FirstOrDefault(s => s.IsAudioOnly) ?? _probeStreams.LastOrDefault()
                    : _probeStreams.FirstOrDefault(s => !s.IsAudioOnly) ?? _probeStreams.FirstOrDefault();
                if (stream?.FileSize.HasValue == true && stream.FileSize.Value > 0)
                {
                    finalTaskSize = stream.FileSize.Value;
                }
            }
            if (finalTaskSize <= 0 && _probedTotalBytes > 0)
            {
                finalTaskSize = _probedTotalBytes;
            }
            if (finalTaskSize <= 0 && _initialRequest?.FileSize.HasValue == true && _initialRequest.FileSize.Value > 0)
            {
                finalTaskSize = _initialRequest.FileSize.Value;
            }
        }

        var selectedCat = GetSelectedCategory();
        bool isVideo = selectedCat == DownloadCategory.Video ||
                       CategoryDetector.FromExtension(Path.GetExtension(fileName)) == DownloadCategory.Video ||
                       fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".flv", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

        if (isVideo)
        {
            selectedCat = DownloadCategory.Video;
            fileName = UniversalVideoDownloader.EnsureMp4Extension(fileName);
        }

        int connCount = _settingsManager.Current.MaxConnectionsPerDownload;

        string finalUrl = !string.IsNullOrWhiteSpace(_resolvedUrl) ? _resolvedUrl : url;
        if (IDMLite.Core.Services.MediaUrl.IsSocialPage(url))
        {
            finalUrl = url;
        }
        else if (finalUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrEmpty(_initialRequest?.Referer) &&
                 (_initialRequest.Referer.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
                  _initialRequest.Referer.Contains("youtube.com/shorts/", StringComparison.OrdinalIgnoreCase) ||
                  _initialRequest.Referer.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase)))
        {
            finalUrl = _initialRequest.Referer;
        }

        CreatedTask = new DownloadTask
        {
            Url = finalUrl,
            FallbackStreamUrl = _initialRequest?.ResolvedStreamUrl,
            FileName = fileName,
            SaveDirectory = dir,
            Category = selectedCat,
            TotalSize = finalTaskSize,
            Cookies = _resolvedCookies ?? _initialRequest?.Cookies,
            Referer = _initialRequest?.Referer,
            UserAgent = _initialRequest?.UserAgent,
            IsDirect = _initialRequest?.Direct == true && url == _initialRequest.Url,
            ConnectionCount = connCount,
            DateAdded = DateTime.Now
        };

        return true;
    }
}

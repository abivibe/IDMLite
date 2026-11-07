// IDMLite Background Service Worker (Manifest V3)

const browserApi = typeof browser !== "undefined" ? browser : chrome;

const DEFAULT_PORT = 41718;
const NATIVE_HOST_NAME = "com.idmlite.nativehost";

// In-memory status & settings cache for instantaneous synchronous responses
let isAutoIntercept = true;
let isIDMLiteOnline = true;
let currentIpcPort = DEFAULT_PORT;
let ipcToken = "";
let authPending = null;

async function authenticateIpc() {
  if (ipcToken) return ipcToken;
  if (!authPending) authPending = new Promise(resolve => {
    chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, { action: "getAuth" }, response => {
      if (!chrome.runtime.lastError && response?.success && typeof response.token === "string") {
        ipcToken = response.token;
        if (response.port) currentIpcPort = response.port;
      }
      resolve(ipcToken);
    });
  }).finally(() => { authPending = null; });
  return authPending;
}

// In-memory media sniffer cache per tab
const tabMediaStreams = new Map();
const previewCache = new Map();
const previewPending = new Map();
const previewTabGenerations = new Map();
let previewGeneration = 0;
const PREVIEW_TTL = 150000;

function previewPageUrl(raw) {
  try {
    const u = new URL(raw), host = u.hostname.toLowerCase();
    const matches = domain => host === domain || host.endsWith('.' + domain);
    if (matches('youtube.com') && u.pathname === '/watch' && u.searchParams.get('v'))
      return `https://www.youtube.com/watch?v=${encodeURIComponent(u.searchParams.get('v'))}`;
    if (matches('youtu.be') && u.pathname.length > 1)
      return `https://www.youtube.com/watch?v=${encodeURIComponent(u.pathname.slice(1))}`;
    if ((matches('youtube.com') && /^\/(shorts|live)\/[^/]+/.test(u.pathname)) ||
        (matches('instagram.com') && /^\/(reel|reels|p)\/[^/]+/.test(u.pathname)) ||
        ((matches('x.com') || matches('twitter.com')) && /\/status\/\d+/.test(u.pathname)) ||
        (matches('tiktok.com') && /\/video\/\d+/.test(u.pathname))) {
      u.search = ''; u.hash = ''; return u.href;
    }
  } catch {}
  return '';
}

function previewKey(tabId, url) { return `${tabId}:${previewPageUrl(url)}`; }
function savePreviews() {
  browserApi.storage.session?.set({ mediaPreviews: [...previewCache] })?.catch?.(() => {});
}
const previewRestoreGeneration = previewGeneration;
const previewsReady = browserApi.storage.session?.get('mediaPreviews').then(data => {
  if (previewGeneration !== previewRestoreGeneration) return;
  for (const [key, entry] of (data.mediaPreviews || []).slice(-64))
    if (entry.expires > Date.now()) previewCache.set(key, entry);
}).catch(() => {}) || Promise.resolve();
chrome.cookies.onChanged?.addListener(change => {
  const domain = (change?.cookie?.domain || '').replace(/^\./, '').toLowerCase();
  if (domain && !['youtube.com', 'youtu.be', 'google.com', 'instagram.com', 'x.com', 'twitter.com', 'tiktok.com']
      .some(platform => domain === platform || domain.endsWith('.' + platform))) return;
  previewGeneration++; previewCache.clear(); savePreviews();
});

async function warmMediaPreview(tabId, rawUrl) {
  const url = previewPageUrl(rawUrl);
  if (!url || !Number.isInteger(tabId) || !isIDMLiteOnline) return null;
  await previewsReady;
  const key = previewKey(tabId, url), cached = previewCache.get(key);
  if (cached?.expires > Date.now()) return cached;
  if (previewPending.has(key)) return previewPending.get(key);
  if (previewPending.size >= 2) return null;
  const generation = previewGeneration;
  const tabGeneration = previewTabGenerations.get(tabId) || {};
  previewTabGenerations.set(tabId, tabGeneration);
  const pending = (async () => {
    const token = await authenticateIpc();
    if (!token) return null;
    const cookies = await gatherCookiesForDownload(url);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 48000);
    try {
      const response = await fetch(`http://127.0.0.1:${currentIpcPort}/api/preview`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-IDMLite-Token': token },
        body: JSON.stringify({ url, cookies }), signal: controller.signal, cache: 'no-store'
      });
      if (!response.ok) return null;
      const result = await response.json();
      if (!result.success || generation !== previewGeneration || tabGeneration !== previewTabGenerations.get(tabId) || result.preview?.OriginalUrl !== url) return null;
      const entry = { preview: result.preview, expires: Date.now() + PREVIEW_TTL, cachedAt: new Date().toISOString() };
      if (previewCache.size >= 64) previewCache.delete(previewCache.keys().next().value);
      previewCache.set(key, entry); savePreviews(); return entry;
    } catch { return null; }
    finally { clearTimeout(timer); }
  })().catch(() => null).finally(() => previewPending.delete(key));
  previewPending.set(key, pending); return pending;
}

function completeMediaSize(details) {
  const headers = new Map((details.responseHeaders || []).map(h => [h.name.toLowerCase(), h.value || '']));
  const type = headers.get('content-type') || '';
  if (/mpegurl|dash\+xml/.test(type) || /\.(m3u8|mpd|m4s|ts)(?:\?|$)/i.test(details.url)) return 0;
  if (headers.get('content-encoding') && headers.get('content-encoding') !== 'identity') return 0;
  const range = /^bytes\s+\d+-\d+\/(\d+)$/i.exec(headers.get('content-range') || '');
  if (range) return Number.isSafeInteger(+range[1]) ? +range[1] : 0;
  try {
    const u = new URL(details.url);
    if (/(^|\.)googlevideo\.com$/i.test(u.hostname) && Number.isSafeInteger(+u.searchParams.get('clen')) && +u.searchParams.get('clen') > 0)
      return +u.searchParams.get('clen');
  } catch {}
  if (details.statusCode === 206) return 0;
  const size = +(headers.get('content-length') || 0);
  return Number.isSafeInteger(size) && size > 0 ? size : 0;
}

// Sets to track handled downloads and avoid duplicate requests or infinite loops
const interceptedDownloadIds = new Set();
const recentTriggeredUrls = new Map(); // url -> timestamp
const bypassedUrls = new Set(); // URLs that IDMLite failed to accept, let browser handle

// Cross-browser WebExtensions API compatibility (Chromium + Firefox)

// In-memory cache of HTTP response headers (Content-Disposition filename, Content-Type, Content-Length)
// Enables 0ms download cancellation in onCreated while still preserving server-provided filenames and sizes
const urlResponseCache = new Map(); // url -> { fileName, contentType, contentLength, timestamp }

function extractFilenameFromContentDisposition(disposition) {
  if (!disposition) return "";
  try {
    const utf8Match = disposition.match(/filename\*\s*=\s*(?:UTF-8''|utf-8'')([^;]+)/i);
    if (utf8Match && utf8Match[1]) {
      return decodeURIComponent(utf8Match[1].trim().replace(/^["']|["']$/g, ""));
    }
    const quotedMatch = disposition.match(/filename\s*=\s*"([^"]+)"/i);
    if (quotedMatch && quotedMatch[1]) {
      return decodeURIComponent(quotedMatch[1].trim());
    }
    const plainMatch = disposition.match(/filename\s*=\s*([^;]+)/i);
    if (plainMatch && plainMatch[1]) {
      return decodeURIComponent(plainMatch[1].trim().replace(/^["']|["']$/g, ""));
    }
  } catch (e) {}
  return "";
}

function applyDownloadUiSuppression(enabled = !isAutoIntercept) {
  try {
    if (typeof chrome.downloads?.setUiOptions === "function") {
      const result = chrome.downloads.setUiOptions({ enabled }, () => { void chrome.runtime.lastError; });
      result?.catch?.(() => {});
    }
  } catch (e) {}
}

// Suppress browser download shelf/bubble immediately on service worker start
applyDownloadUiSuppression();

// Initialize default settings and periodic heartbeat
chrome.runtime.onInstalled.addListener(() => {
  chrome.storage.local.get(["autoIntercept", "showVideoBar", "ipcPort"], (res) => {
    isAutoIntercept = res.autoIntercept ?? true;
    applyDownloadUiSuppression();
    chrome.storage.local.set({
      autoIntercept: isAutoIntercept,
      showVideoBar: res.showVideoBar ?? true,
      ipcPort: res.ipcPort || DEFAULT_PORT
    });
  });
});

// React immediately to storage setting changes (e.g. user toggles in popup)
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local") {
    if (changes.autoIntercept !== undefined) {
      isAutoIntercept = changes.autoIntercept.newValue;
      applyDownloadUiSuppression();
    }
    if (changes.ipcPort !== undefined) {
      currentIpcPort = changes.ipcPort.newValue;
    }
  }
});

async function refreshStatus() {
  try {
    const settings = await browserApi.storage.local.get(["autoIntercept", "ipcPort"]);
    if (settings.autoIntercept !== undefined) {
      isAutoIntercept = settings.autoIntercept;
      applyDownloadUiSuppression();
    }
    if (settings.ipcPort) currentIpcPort = settings.ipcPort;

    const status = await checkIDMLiteStatus(currentIpcPort);
    isIDMLiteOnline = status.online;
  } catch (e) {
    // Keep last known status on minor error
  }
}

// Initial status check & periodic keepalive
refreshStatus();
setInterval(refreshStatus, 6000);

// Throttling timers for mediaDetected IPC messages
const tabNotifyTimers = new Map();

function throttleNotifyTab(tabId, mediaItem) {
  if (tabNotifyTimers.has(tabId)) return;
  const timer = setTimeout(() => {
    tabNotifyTimers.delete(tabId);
    chrome.tabs.sendMessage(tabId, {
      action: "mediaDetected",
      media: mediaItem
    }).catch(() => {});
  }, 800);
  tabNotifyTimers.set(tabId, timer);
}

function normalizeGitHubRawUrl(rawUrl) {
  if (!rawUrl || typeof rawUrl !== "string") return rawUrl;
  try {
    const u = new URL(rawUrl);
    const host = u.hostname.toLowerCase();
    if (host === "github.com" || host === "www.github.com") {
      const m = u.pathname.match(/^\/([^\/]+)\/([^\/]+)\/(?:blob|raw)\/(.+)$/i);
      if (m) {
        const owner = m[1];
        const repo = m[2];
        let rest = m[3];
        if (rest.toLowerCase().startsWith("refs/heads/")) {
          rest = rest.substring("refs/heads/".length);
        } else if (rest.toLowerCase().startsWith("refs/tags/")) {
          rest = rest.substring("refs/tags/".length);
        }
        return `https://raw.githubusercontent.com/${owner}/${repo}/${rest}`;
      }
    }
  } catch (e) {}
  return rawUrl;
}

// 1. Sniff media streams & cache response metadata from web requests
const handleResponseStarted = (details) => {
  const url = details.url;
  if (!url || url.startsWith("chrome://") || url.startsWith("moz-extension://") || url.startsWith("blob:") || url.startsWith("data:")) return;

  const headers = details.responseHeaders || [];
  let contentType = "";
  let contentLength = 0;
  let contentDisposition = "";
  let contentEncoding = "";


  for (const h of headers) {
    const name = h.name.toLowerCase();
    if (name === "content-type" && h.value) contentType = h.value.toLowerCase();
    else if (name === "content-length" && h.value) contentLength = parseInt(h.value, 10) || 0;
    else if (name === "content-disposition" && h.value) contentDisposition = h.value;
    else if (name === "content-encoding" && h.value) contentEncoding = h.value.toLowerCase();
  }

  // Do not trust Content-Length when response is HTTP-compressed (e.g. GitHub raw .txt with gzip/br),
  // because compressed byte length differs from uncompressed file size
  if (contentEncoding && contentEncoding !== "identity") {
    contentLength = 0;
  }
  contentLength = completeMediaSize(details);

  // Cache response metadata for 0ms download interception
  if (contentType || contentLength > 0 || contentDisposition) {
    if (urlResponseCache.size >= 300) {
      const oldestKey = urlResponseCache.keys().next().value;
      urlResponseCache.delete(oldestKey);
    }
    urlResponseCache.set(url, {
      fileName: extractFilenameFromContentDisposition(contentDisposition),
      contentType: contentType,
      contentLength: contentLength,
      timestamp: Date.now()
    });
  }

  if (!details.tabId || details.tabId < 0) return;

  const isTikTokStream =
    (url.includes("tiktokcdn.com") ||
     url.includes("webapp-prime.tiktok.com") ||
     url.includes("tiktok.com/aweme") ||
     url.includes("ibytedtos.com") ||
     url.includes("byteoversea.com") ||
     url.includes("tikcdn.io")) &&
    (contentType.includes("video/") || url.includes("mime_type=video") || url.includes("/video/tos/"));

  const isMedia = 
    contentType.includes("video/") ||
    contentType.includes("mpegurl") ||
    contentType.includes("dash+xml") ||
    url.match(/\.(mp4|m3u8|mpd|webm|mkv|flv|avi|mov)(\?.*)?$/i) ||
    (url.includes("cdninstagram.com") && (url.includes(".mp4") || url.includes("bytestart="))) ||
    (url.includes("fbcdn.net") && (url.includes(".mp4") || url.includes("bytestart="))) ||
    (url.includes("twimg.com") && (url.includes(".mp4") || url.includes(".m3u8"))) ||
    isTikTokStream;

  if (isMedia) {
    if (!tabMediaStreams.has(details.tabId)) {
      tabMediaStreams.set(details.tabId, []);
    }

    const list = tabMediaStreams.get(details.tabId);

    // YouTube googlevideo streams: deduplicate by itag so we don't spam for every 1-second range chunk
    if (url.includes("googlevideo.com/videoplayback")) {
      const itagMatch = url.match(/[?&]itag=(\d+)/);
      const itag = itagMatch ? itagMatch[1] : null;
      if (itag) {
        const existing = list.find(item => item.itag === itag);
        if (existing) {
          if (contentLength > (existing.size || 0)) {
            existing.size = contentLength;
          }
          return;
        }
      }
    }

    if (!list.some(item => item.url === url)) {
      // Enforce maximum of 35 media items per tab to prevent memory leaks
      if (list.length >= 35) {
        list.shift();
      }

      const itagMatch = url.match(/[?&]itag=(\d+)/);
      const mediaItem = {
        url: url,
        contentType: contentType,
        size: contentLength,
        timestamp: Date.now(),
        tabId: details.tabId,
        itag: itagMatch ? itagMatch[1] : undefined
      };

      list.push(mediaItem);
      throttleNotifyTab(details.tabId, mediaItem);
    }
  }
};

try {
  chrome.webRequest.onResponseStarted.addListener(
    handleResponseStarted,
    { urls: ["<all_urls>"] },
    ["responseHeaders", "extraHeaders"]
  );
} catch (e) {
  chrome.webRequest.onResponseStarted.addListener(
    handleResponseStarted,
    { urls: ["<all_urls>"] },
    ["responseHeaders"]
  );
}

// Clean up tab media on tab close or top-level navigation
chrome.tabs.onRemoved.addListener((tabId) => {
  previewTabGenerations.delete(tabId);
  for (const key of previewCache.keys()) if (key.startsWith(`${tabId}:`)) previewCache.delete(key);
  savePreviews();
  tabMediaStreams.delete(tabId);
  const timer = tabNotifyTimers.get(tabId);
  if (timer) {
    clearTimeout(timer);
    tabNotifyTimers.delete(tabId);
  }
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "loading" && changeInfo.url) {
    previewTabGenerations.delete(tabId);
    for (const key of previewCache.keys()) if (key.startsWith(`${tabId}:`)) previewCache.delete(key);
    savePreviews();
    tabMediaStreams.delete(tabId);
    const timer = tabNotifyTimers.get(tabId);
    if (timer) {
      clearTimeout(timer);
      tabNotifyTimers.delete(tabId);
    }
  }
});

async function browserDownload(id) {
  return (await browserApi.downloads.search({ id }))[0];
}

async function forceKillBrowserDownload(downloadId) {
  if ((await browserDownload(downloadId))?.state !== "in_progress") return false;
  await browserApi.downloads.cancel(downloadId);
  const item = await browserDownload(downloadId);
  if (item?.state === "interrupted" && item.error === "USER_CANCELED") {
    try { await browserApi.downloads.erase({ id: downloadId }); } catch {}
  }
  return true;
}

const pendingTransfers = new Map();
const handoffPrefix = "downloadHandoff:";

function crushAndIntercept(downloadItem, suggest, saved = null) {
  let url = downloadItem?.finalUrl || downloadItem?.url || "";
  if (url.startsWith("blob:https://github.com/") && downloadItem.referrer) {
    url = normalizeGitHubRawUrl(downloadItem.referrer);
  } else if (/^https?:\/\//i.test(url)) {
    url = normalizeGitHubRawUrl(url);
  }
  if (!/^https?:\/\//i.test(url) || downloadItem.byExtensionId === chrome.runtime.id ||
      downloadItem.state !== "in_progress" || (downloadItem.paused && !saved) ||
      (downloadItem.danger && !["safe", "accepted", "allowlistedByPolicy"].includes(downloadItem.danger))) {
    applyDownloadUiSuppression(true);
    if (typeof suggest === "function") suggest();
    return false;
  }
  let transfer = pendingTransfers.get(downloadItem.id);
  if (!transfer) {
    transfer = (async () => {
      const key = handoffPrefix + downloadItem.id;
      const storage = browserApi.storage.session;
      let paused = !!saved && !!downloadItem.paused;
      try {
        const settings = await browserApi.storage.local.get(["autoIntercept"]);
        if (!(settings.autoIntercept ?? isAutoIntercept)) {
          applyDownloadUiSuppression(true);
          return;
        }
        const requestId = saved?.requestId || crypto.randomUUID();
        if (storage) await storage.set({ [key]: { requestId, startTime: downloadItem.startTime, created: saved?.created || Date.now() } });
        applyDownloadUiSuppression(false);
        if (!paused) {
          await browserApi.downloads.pause(downloadItem.id);
          paused = true;
        }
        const current = await browserDownload(downloadItem.id);
        if (!current || current.state !== "in_progress" || !current.paused) return;
        const result = await handleInterceptedDownload(downloadItem, url, requestId);
        if (result?.success && await forceKillBrowserDownload(downloadItem.id)) {
          paused = false;
          return;
        }
      } catch {}
      finally {
        if (paused) {
          applyDownloadUiSuppression(true);
          try { await browserApi.downloads.resume(downloadItem.id); } catch {}
        }
        try { await storage?.remove(key); } catch {}
      }
    })();
    pendingTransfers.set(downloadItem.id, transfer);
    transfer.finally(() => pendingTransfers.delete(downloadItem.id));
  }
  if (typeof suggest === "function") {
    transfer.finally(() => { try { suggest(); } catch {} });
    return true;
  }
  return false;
}

// A restarted worker replays only transfers it paused, with their original ID.
browserApi.storage.session?.get(null).then(async entries => {
  for (const [key, saved] of Object.entries(entries)) {
    if (!key.startsWith(handoffPrefix)) continue;
    try {
      const item = await browserDownload(Number(key.slice(handoffPrefix.length)));
      if (item?.state === "in_progress" && item.paused && item.startTime === saved.startTime) {
        if (Date.now() - saved.created < 120000) {
          crushAndIntercept(item, null, saved);
          continue;
        }
        applyDownloadUiSuppression(true);
        await browserApi.downloads.resume(item.id);
      }
      await browserApi.storage.session.remove(key);
    } catch {}
  }
}).catch(() => {});

const MIME_EXT = {
  // Archives & Disk Images
  "application/zip": ".zip", "application/x-zip-compressed": ".zip",
  "application/x-rar-compressed": ".rar", "application/vnd.rar": ".rar",
  "application/x-7z-compressed": ".7z", "application/gzip": ".gz", "application/x-gzip": ".gz",
  "application/x-tar": ".tar", "application/x-bzip2": ".bz2", "application/x-xz": ".xz",
  "application/x-iso9660-image": ".iso", "application/x-bittorrent": ".torrent",
  // Executables & Packages
  "application/x-msdownload": ".exe", "application/x-msdos-program": ".exe",
  "application/x-msi": ".msi", "application/vnd.android.package-archive": ".apk",
  // Documents & Data (PDF, Office, Text, Data)
  "application/pdf": ".pdf",
  "text/plain": ".txt", "text/csv": ".csv", "text/markdown": ".md",
  "application/rtf": ".rtf", "text/rtf": ".rtf",
  "application/msword": ".doc",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document": ".docx",
  "application/vnd.ms-excel": ".xls",
  "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": ".xlsx",
  "application/vnd.ms-powerpoint": ".ppt",
  "application/vnd.openxmlformats-officedocument.presentationml.presentation": ".pptx",
  "application/vnd.oasis.opendocument.text": ".odt",
  "application/vnd.oasis.opendocument.spreadsheet": ".ods",
  "application/vnd.oasis.opendocument.presentation": ".odp",
  "application/epub+zip": ".epub",
  "application/json": ".json", "application/xml": ".xml", "text/xml": ".xml",
  // Images (JPG, PNG, WEBP, GIF, SVG, BMP, ICO, AVIF, TIFF, HEIC)
  "image/jpeg": ".jpg", "image/jpg": ".jpg", "image/pjpeg": ".jpg",
  "image/png": ".png", "image/webp": ".webp", "image/gif": ".gif",
  "image/svg+xml": ".svg", "image/bmp": ".bmp", "image/x-ms-bmp": ".bmp",
  "image/vnd.microsoft.icon": ".ico", "image/x-icon": ".ico",
  "image/avif": ".avif", "image/tiff": ".tiff", "image/heic": ".heic", "image/heif": ".heif",
  // Video & Audio
  "video/mp4": ".mp4", "video/webm": ".webm", "video/x-matroska": ".mkv",
  "video/quicktime": ".mov", "video/x-msvideo": ".avi", "video/x-flv": ".flv",
  "audio/mpeg": ".mp3", "audio/mp3": ".mp3", "audio/mp4": ".m4a", "audio/x-m4a": ".m4a",
  "audio/ogg": ".ogg", "audio/wav": ".wav", "audio/x-wav": ".wav", "audio/flac": ".flac", "audio/aac": ".aac"
};

// Chromium provides a filename gate before Save As. Firefox uses onCreated.
if (chrome.downloads?.onDeterminingFilename) {
  chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) =>
    crushAndIntercept(downloadItem, suggest));
} else if (browserApi?.downloads?.onCreated) {
  browserApi.downloads.onCreated.addListener(downloadItem => crushAndIntercept(downloadItem));
}

async function getCookiesForUrlSafe(targetUrl, refererUrl = "") {
  const results = [];
  if (!targetUrl || !/^https?:\/\//i.test(targetUrl)) return results;
  try {
    const standard = await browserApi.cookies.getAll({ url: targetUrl });
    if (Array.isArray(standard)) results.push(...standard);
  } catch (e) {}
  try {
    // Never query every partition. Use the browser's actual top-level frame key.
    if (refererUrl && typeof chrome.cookies.getPartitionKey === "function" && chrome.tabs.query) {
      const tabs = await chrome.tabs.query({ url: new URL(refererUrl).origin + "/*" });
      const tab = tabs.find(item => item.url === refererUrl);
      if (tab) {
        const { partitionKey } = await chrome.cookies.getPartitionKey({ tabId: tab.id, frameId: 0 });
        if (partitionKey?.topLevelSite) {
          const partitioned = await browserApi.cookies.getAll({ url: targetUrl, partitionKey });
          if (Array.isArray(partitioned)) results.push(...partitioned);
        }
      }
    }
  } catch (e) {}
  return results;
}

async function gatherCookiesForDownload(targetUrl, initialUrl = "", refererUrl = "") {
  // The browser cookie API applies Domain, Path, Secure and partition rules.
  // Referer and redirect-source URLs never authorize cookies for a new target.
  let target;
  try { target = new URL(targetUrl); } catch { return ""; }
  if (!["http:", "https:"].includes(target.protocol)) return "";
  const cookieMap = new Map();
  for (const cookie of await getCookiesForUrlSafe(target.href, refererUrl)) {
    const domain = (cookie.domain || "").replace(/^\./, "").toLowerCase();
    if (domain && target.hostname !== domain && (cookie.hostOnly || !target.hostname.endsWith("." + domain))) continue;
    if (cookie.secure && target.protocol !== "https:") continue;
    const path = cookie.path || "/";
    if (target.pathname !== path && !(target.pathname.startsWith(path) && (path.endsWith("/") || target.pathname[path.length] === "/"))) continue;
    if (!cookieMap.has(cookie.name)) cookieMap.set(cookie.name, cookie.value);
  }
  return Array.from(cookieMap, ([name, value]) => `${name}=${value}`).join("; ");
}
async function handleInterceptedDownload(downloadItem, url, requestId) {
  url = normalizeGitHubRawUrl(url);
  const cookieHeader = await gatherCookiesForDownload(url, downloadItem?.url || "", downloadItem?.referrer || "");

  const cachedMeta = urlResponseCache.get(url) || (downloadItem?.url ? urlResponseCache.get(downloadItem.url) : null);

  let rawFileName = downloadItem?.filename ? downloadItem.filename.split(/[\\/]/).pop() : undefined;
  if (rawFileName === "idmlite_intercepted.tmp") {
    rawFileName = undefined;
  }
  if (!rawFileName && cachedMeta?.fileName) {
    rawFileName = cachedMeta.fileName;
  }
  if (!rawFileName && url) {
    try {
      const u = new URL(url);
      const seg = u.pathname.split("/").filter(Boolean).pop();
      if (seg) {
        const decoded = decodeURIComponent(seg).replace(/[/\\?%*:|"<>]/g, "_").trim();
        if (decoded && decoded.length <= 180) {
          rawFileName = decoded;
        }
      }
    } catch (e) {}
  }

  const effectiveMime = (downloadItem?.mime || cachedMeta?.contentType || "").toLowerCase().split(";")[0].trim();
  if (rawFileName && !/\.[A-Za-z0-9]{1,6}$/.test(rawFileName) && effectiveMime) {
    const ext = MIME_EXT[effectiveMime];
    if (ext) rawFileName += ext;
  } else if (!rawFileName && effectiveMime && MIME_EXT[effectiveMime]) {
    rawFileName = `download_${Date.now()}${MIME_EXT[effectiveMime]}`;
  }

  const isTextLikeFile = rawFileName && /\.(txt|md|markdown|csv|json|xml|log|ini|cfg|yaml|yml|rtf)$/i.test(rawFileName);
  const effectiveSize = isTextLikeFile
    ? undefined
    : downloadItem?.totalBytes > 0
      ? downloadItem.totalBytes
      : downloadItem?.fileSize > 0
      ? downloadItem.fileSize
      : cachedMeta?.contentLength > 0
      ? cachedMeta.contentLength
      : undefined;

  const payload = {
    requestId,
    url: url,
    fileName: rawFileName,
    fileSize: effectiveSize,
    referer: downloadItem?.referrer || "",
    userAgent: navigator.userAgent,
    cookies: cookieHeader,
    direct: true, // real browser download URL: the app must not re-resolve it
    autoStart: false
  };

  return await sendToIDMLite(payload, currentIpcPort);
}

// 3. Messaging Bridge (Content Scripts & Popup <-> IDMLite)
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.action === 'warmPreview') {
    let page = sender?.tab?.url || sender?.url || '';
    try {
      if (/(^|\.)tiktok\.com$/i.test(new URL(page).hostname) &&
          /(^|\.)tiktok\.com$/i.test(new URL(message.url).hostname) && previewPageUrl(message.url)) page = message.url;
    } catch {}
    warmMediaPreview(sender?.tab?.id, page).then(entry => sendResponse({ ready: !!entry }));
    return true;
  }
  if (message.action === "checkStatus") {
    checkIDMLiteStatus(currentIpcPort).then(status => {
      isIDMLiteOnline = status.online;
      sendResponse(status);
    });
    return true;
  }

  if (message.action === "downloadUrl") {
    if (message.payload && message.payload.url) {
      message.payload.url = normalizeGitHubRawUrl(message.payload.url);
      recentTriggeredUrls.set(message.payload.url, Date.now());
      setTimeout(() => recentTriggeredUrls.delete(message.payload.url), 8000);
    }
    (async () => {
      const refUrl = message.payload.referer || "";
      let targetUrl = previewPageUrl(message.payload.url) || message.payload.url || "";
      try {
        if (/(^|\.)googlevideo\.com$/i.test(new URL(targetUrl).hostname) && previewPageUrl(refUrl))
          targetUrl = previewPageUrl(refUrl);
      } catch {}
      const cookieHeader = await gatherCookiesForDownload(targetUrl, targetUrl, refUrl);

      const isYouTubePage = /^https:\/\/([^/]+\.)?youtube\.com\//i.test(targetUrl);
      let fileSize = isYouTubePage ? undefined : message.payload.fileSize;
      if (!fileSize || fileSize <= 0) {
        const tabId = sender?.tab?.id;
        const mediaList = tabMediaStreams.get(tabId) || [];
        const match = mediaList.find(m => m.url === targetUrl);
        if (match && match.size > 0) {
          fileSize = match.size;
        }
      }

      const payload = {
        ...message.payload,
        url: targetUrl,
        fileSize: fileSize > 0 ? fileSize : undefined,
        userAgent: navigator.userAgent,
        cookies: cookieHeader
      };
      await previewsReady;
      const cached = previewCache.get(previewKey(sender?.tab?.id, targetUrl));
      if (cached?.expires > Date.now()) {
        payload.preview = cached.preview;
        payload.previewCachedAtUtc = cached.cachedAt;
        const video = cached.preview.Streams?.find(stream => !stream.IsAudioOnly);
        if (video?.FileSize > 0) { payload.fileSize = video.FileSize; payload.fileSizeIsEstimated = !!video.FileSizeIsEstimated; }
      }

      const result = await sendToIDMLite(payload, currentIpcPort);
      sendResponse(result);
    })().catch(() => sendResponse({ success: false, error: "IDMLite indirme isteği işlenemedi." }));
    return true;
  }

  if (message.action === "getTabMedia") {
    const tabId = message.tabId || sender?.tab?.id;
    const media = tabMediaStreams.get(tabId) || [];
    sendResponse({ media: media });
    return true;
  }
});

function buildCandidateEndpoints(port, path) {
  const activePort = Number(port) || DEFAULT_PORT;
  const endpoints = [
    `http://127.0.0.1:${activePort}${path}`,
    `http://localhost:${activePort}${path}`
  ];
  if (activePort !== DEFAULT_PORT) {
    endpoints.push(`http://127.0.0.1:${DEFAULT_PORT}${path}`);
    endpoints.push(`http://localhost:${DEFAULT_PORT}${path}`);
  }
  return endpoints;
}

// Helper: Check if IDMLite is online
async function checkIDMLiteStatus(port) {
  await authenticateIpc();
  if (ipcToken) port = currentIpcPort;
  const endpoints = buildCandidateEndpoints(port, "/api/status");
  for (const endpoint of endpoints) {
    try {
      const controller = new AbortController();
      const id = setTimeout(() => controller.abort(), 1500);
      const res = await fetch(endpoint, {
        method: "GET",
        headers: ipcToken ? { "X-IDMLite-Token": ipcToken } : {},
        cache: "no-store",
        signal: controller.signal
      });
      clearTimeout(id);
      if (res.ok || [400, 409, 413, 503].includes(res.status)) {
        const data = await res.json();
        return { online: true, version: data.version, activeCount: data.activeCount };
      }
    } catch (e) {}
  }

  // Fallback: Check via Native Messaging Host if HTTP loopback was blocked
  try {
    const nativeStatus = await new Promise((resolve) => {
      chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, { action: "checkStatus" }, (response) => {
        if (chrome.runtime.lastError || !response) {
          resolve(null);
        } else {
          resolve(response);
        }
      });
    });
    if (nativeStatus && (nativeStatus.online || nativeStatus.status === "ok")) {
      return {
        online: true,
        version: nativeStatus.version || "1.0.1",
        activeCount: nativeStatus.activeCount || 0
      };
    }
  } catch (e) {}

  return { online: false };
}

// Helper: Send payload to IDMLite via HTTP IPC or Native Messaging
async function sendToIDMLite(payload, port) {
  payload = { ...payload, requestId: payload.requestId || crypto.randomUUID() };
  await authenticateIpc();
  if (ipcToken) port = currentIpcPort;
  const endpoints = ipcToken ? [`http://127.0.0.1:${port}/api/download`] : [];

  // Primary attempt: Local HTTP REST Bridge (127.0.0.1 + localhost)
  for (const endpoint of endpoints) {
    try {
      const controller = new AbortController();
      const id = setTimeout(() => controller.abort(), 5000);
      const res = await fetch(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-IDMLite-Token": ipcToken },
        body: JSON.stringify(payload),
        cache: "no-store",
        signal: controller.signal
      });
      clearTimeout(id);
      if (res.status === 401) ipcToken = "";
      if (res.ok) {
        const data = await res.json();
        isIDMLiteOnline = true;
        if (data.success === true) return { success: true, via: "http", data: data };
        return { success: false, error: data.error || "Download rejected" };
      }
    } catch (httpError) {}
  }

  // Secondary attempt: Native Messaging Host
  try {
    return await new Promise((resolve) => {
      chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload, (response) => {
        if (chrome.runtime.lastError || !response || response.success !== true) {
          resolve({ success: false, error: chrome.runtime.lastError?.message || response?.error || response?.message || "Native host rejected download" });
        } else {
          isIDMLiteOnline = true;
          resolve({ success: true, via: "native", data: response });
        }
      });
    });
  } catch (nativeError) {
    return { success: false, error: "IDMLite masaüstü uygulamasına ulaşılamadı." };
  }
}

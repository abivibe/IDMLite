// IDMLite Content Script - Video Sniffer & Floating Download Bar

const IDMLITE_BROWSER_TRIGGER_HOSTS = /(^|\.)((drive|docs)\.google\.com|drive\.usercontent\.google\.com|googleusercontent\.com|mediafire\.com)$/i;

(function () {
  // MediaFire owns its download controls. Do not inject overlays or any pointer
  // listeners here; the background downloads API handles the resulting file.
  if (/(^|\.)mediafire\.com$/i.test(window.location.hostname)) return;

  // Guard ad frames and tiny tracking iframes
  if (window !== window.top) {
    const host = window.location.hostname || "";
    if (
      host.includes("doubleclick") ||
      host.includes("googleadservices") ||
      host.includes("googlesyndication") ||
      host.includes("adnxs") ||
      host.includes("amazon-adsystem")
    ) {
      return;
    }
    if (window.innerWidth < 260 || window.innerHeight < 180) {
      return;
    }
  }

  let isVideoBarEnabled = true;
  let isAutoIntercept = true;
  const detectedStreams = new Map(); // url -> mediaItem
  const videoBadgeMap = new WeakMap();
  const hookedVideos = new WeakSet();
  let lastPreviewPage = '', lastPreviewAt = 0;

  // Load preferences
  chrome.storage.local.get(["showVideoBar", "autoIntercept"], (res) => {
    if (res.showVideoBar !== undefined) isVideoBarEnabled = res.showVideoBar;
    if (res.autoIntercept !== undefined) isAutoIntercept = res.autoIntercept;
  });

  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === "local") {
      if (changes.showVideoBar !== undefined) isVideoBarEnabled = changes.showVideoBar.newValue;
      if (changes.autoIntercept !== undefined) isAutoIntercept = changes.autoIntercept.newValue;
    }
  });

  function updateAllVideoBadges() {
    const openDropdown = document.querySelector(".idmlite-dropdown-menu.idmlite-show");
    if (!openDropdown) return;
    for (const video of document.querySelectorAll("video")) {
      const entry = videoBadgeMap.get(video);
      if (entry && entry.dropdown === openDropdown) {
        populateDropdown(video, openDropdown);
        break;
      }
    }
  }

  // Listen for media detected from background webRequest
  chrome.runtime.onMessage.addListener((msg) => {
    if (msg.action === "mediaDetected" && msg.media && msg.media.url) {
      if (detectedStreams.size >= 40) {
        const firstKey = detectedStreams.keys().next().value;
        detectedStreams.delete(firstKey);
      }
      detectedStreams.set(msg.media.url, msg.media);
      updateAllVideoBadges();
    }
  });

  function fetchTabMedia() {
    try {
      chrome.runtime.sendMessage({ action: "getTabMedia" }, (res) => {
        if (res && res.media && Array.isArray(res.media)) {
          res.media.forEach(m => {
            if (m && m.url) detectedStreams.set(m.url, m);
          });
          updateAllVideoBadges();
        }
      });
    } catch (e) {}
  }
  fetchTabMedia();

  // Listen for YouTube SPA navigation events
  window.addEventListener("yt-navigate-finish", () => {
    fetchTabMedia();
    scheduleScan();
  });
  window.addEventListener("spfdone", () => {
    fetchTabMedia();
    scheduleScan();
  });

  // Debounced scan scheduler to prevent CPU thrashing & tab freezing
  let scanTimer = null;
  function scheduleScan() {
    if (scanTimer) return;
    scanTimer = setTimeout(() => {
      scanTimer = null;
      scanForVideos();
    }, 400);
  }

  const activeBodyBadges = new Set();
  let lastRefreshedTikTokVideo = null;

  function tikTokPaintedRect(video) {
    const box = video.getBoundingClientRect();
    let left = box.left, top = box.top, width = box.width, height = box.height;
    const style = getComputedStyle(video);
    if (video.videoWidth > 0 && video.videoHeight > 0 && /^(contain|scale-down)$/.test(style.objectFit)) {
      let scale = Math.min(width / video.videoWidth, height / video.videoHeight);
      if (style.objectFit === 'scale-down') scale = Math.min(1, scale);
      const paintedWidth = video.videoWidth * scale, paintedHeight = video.videoHeight * scale;
      const position = style.objectPosition.split(/\s+/);
      const offset = (value, spare) => value?.endsWith('%') ? spare * parseFloat(value) / 100 : value?.endsWith('px') ? parseFloat(value) : spare / 2;
      left += offset(position[0], width - paintedWidth); top += offset(position[1], height - paintedHeight);
      width = paintedWidth; height = paintedHeight;
    }
    let right = Math.min(window.innerWidth, left + width), bottom = Math.min(window.innerHeight, top + height);
    left = Math.max(0, left); top = Math.max(60, top);
    for (let parent = video.parentElement; parent && parent !== document.body; parent = parent.parentElement) {
      const css = getComputedStyle(parent), rect = parent.getBoundingClientRect();
      if (/(hidden|clip|auto|scroll)/.test(css.overflowX)) { left = Math.max(left, rect.left); right = Math.min(right, rect.right); }
      if (/(hidden|clip|auto|scroll)/.test(css.overflowY)) { top = Math.max(top, rect.top); bottom = Math.min(bottom, rect.bottom); }
    }
    return { left, top, right, bottom, width: right - left, height: bottom - top };
  }

  function tikTokDomIdentity(video) {
    const root = video.closest('[data-e2e="recommend-list-item-container"], [data-e2e="user-post-item"], [data-e2e="search-card-video-container"], article') || video.parentElement;
    return (video.closest('[id*="xgwrapper-"]')?.id || '') + '|' + (root?.querySelector('a[href*="/video/"]')?.getAttribute('href') || '') + '|' + (video.currentSrc || video.src || '');
  }

  function syncAllBadgePositions() {
    const isTikTok = window.location.hostname.includes("tiktok.com");
    for (const entry of activeBodyBadges) {
      const video = entry.video;
      const wrapper = entry.wrapper;
      if (!video || !video.isConnected || !document.body.contains(video)) {
        wrapper.remove();
        activeBodyBadges.delete(entry);
        continue;
      }
      const gridVideo = video.closest('[data-e2e="user-post-item"], [data-e2e="search-card-video-container"]');
      if (isTikTok && !gridVideo && activeTikTokVideo && video !== activeTikTokVideo) {
        wrapper.style.display = 'none'; entry.dropdown.classList.remove('idmlite-show'); continue;
      }
      const rect = isTikTok ? tikTokPaintedRect(video) : video.getBoundingClientRect();
      const isVisible = rect.width >= 100 && rect.height >= 80 &&
                        rect.bottom > 60 && rect.top < window.innerHeight - 60 &&
                        rect.right > 50 && rect.left < window.innerWidth - 50 &&
                        (!isTikTok || gridVideo || !activeTikTokVideo || video === activeTikTokVideo);
      if (!isVisible) {
        wrapper.style.display = "none";
        if (entry.dropdown) entry.dropdown.classList.remove("idmlite-show");
        continue;
      }
      wrapper.style.display = "block";
      wrapper.style.visibility = "visible";
      wrapper.style.opacity = "1";
      wrapper.style.position = "fixed";
      wrapper.style.zIndex = "2147483647";
      // On TikTok, keep top below the 60px top header bar so the button is never covered
      const topPos = isTikTok ? Math.max(66, Math.round(rect.top + 14)) : Math.max(12, Math.round(rect.top + 12));
      wrapper.style.top = `${topPos}px`;
      const rightPos = window.innerWidth - rect.right + 16;
      wrapper.style.right = `${Math.max(16, Math.round(rightPos))}px`;
      wrapper.style.left = "auto";
      if (isTikTok) {
        wrapper.style.maxWidth = `${Math.max(0, rect.width - 24)}px`;
        const identity = tikTokDomIdentity(video);
        if (entry.identity && entry.identity !== identity) {
          entry.dropdown.classList.remove('idmlite-show'); wrapper._lockedTikTokData = null;
        }
        entry.identity = identity;
        if (entry.dropdown.classList.contains('idmlite-show')) {
          entry.dropdown.style.setProperty('min-width', `${Math.min(230, window.innerWidth - 24)}px`, 'important');
          entry.dropdown.style.setProperty('max-width', `${Math.min(320, window.innerWidth - 24)}px`, 'important');
          entry.dropdown.style.setProperty('right', '0px', 'important');
          const menu = entry.dropdown.getBoundingClientRect();
          if (menu.left < 12) entry.dropdown.style.setProperty('right', `${menu.left - 12}px`, 'important');
        }
      }
    }
  }

  window.addEventListener("scroll", syncAllBadgePositions, { passive: true, capture: true });
  window.addEventListener("resize", syncAllBadgePositions, { passive: true });

  let rafRunning = false;
  function startBadgeRafLoop() {
    if (rafRunning) return;
    rafRunning = true;
    let lastSync = 0;
    function loop(now) {
      if (activeBodyBadges.size === 0) { rafRunning = false; return; }
      if (activeBodyBadges.size > 0 && !document.hidden && now - lastSync >= 32) {
        lastSync = now;
        syncAllBadgePositions();
      }
      requestAnimationFrame(loop);
    }
    requestAnimationFrame(loop);
  }

  function setupGlobalCaptureInterceptors() {
    function findIdmLiteTargetAt(x, y) {
      // 1. Check open dropdown menus first (highest priority)
      const openDropdowns = document.querySelectorAll(".idmlite-dropdown-menu.idmlite-show");
      for (const d of openDropdowns) {
        const rect = d.getBoundingClientRect();
        if (rect.width > 0 && rect.height > 0 && x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom) {
          const items = d.querySelectorAll(".idmlite-stream-item, .idmlite-dropdown-item, button, a");
          for (const item of items) {
            const r = item.getBoundingClientRect();
            if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) {
              return item;
            }
          }
          return d;
        }
      }

      // 2. Check badge buttons
      const badgeBtns = document.querySelectorAll(".idmlite-badge-btn");
      for (const b of badgeBtns) {
        const rect = b.getBoundingClientRect();
        if (rect.width > 0 && rect.height > 0 && x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom) {
          return b;
        }
      }

      return null;
    }

    const interceptEvent = (e) => {
      const x = e.clientX;
      const y = e.clientY;

      let idmTarget = null;
      if (e.target && e.target.closest) {
        idmTarget = e.target.closest(".idmlite-stream-item, .idmlite-dropdown-item, .idmlite-badge-btn");
      }
      if (!idmTarget && x !== undefined && y !== undefined) {
        idmTarget = findIdmLiteTargetAt(x, y);
      }

      if (idmTarget) {
        // Stop propagation so Instagram/TikTok never receives pointerdown/mousedown/click
        if (e.type === "click") {
          e.preventDefault();
        }
        e.stopPropagation();
        e.stopImmediatePropagation();

        if (e.type === "click") {
          if (typeof idmTarget._triggerHandler === "function") {
            idmTarget._triggerHandler();
          } else if (typeof idmTarget._downloadHandler === "function") {
            idmTarget._downloadHandler();
          } else if (typeof idmTarget.click === "function") {
            idmTarget.click();
          }
        }
      }
    };

    window.addEventListener("pointerdown", interceptEvent, true);
    window.addEventListener("mousedown", interceptEvent, true);
    window.addEventListener("mouseup", interceptEvent, true);
    window.addEventListener("click", interceptEvent, true);
  }

  // Dedicated TikTok active video tracker & IntersectionObserver (threshold: 0.6)
  let activeTikTokVideo = null;
  let tiktokIntersectionObserver = null;
  const observedTikTokNodes = new WeakSet();

  function refreshTikTokActiveVideoBadge(targetVid) {
    if (!targetVid || !targetVid.isConnected) return;
    activeTikTokVideo = targetVid;
    attachVideoBadge(targetVid);

    const existing = videoBadgeMap.get(targetVid);
    if (existing && existing.wrapper) {
      existing.wrapper._currentVideo = targetVid;
      existing.wrapper._lockedVideo = targetVid;
      existing.wrapper._lockedTikTokData = extractTikTokVideoTarget(targetVid);
      const target = existing.wrapper._lockedTikTokData;
      if (target.videoId && (existing.previewUrl !== target.permalink || Date.now() - (existing.previewAt || 0) > 30000)) {
        existing.previewUrl = target.permalink; existing.previewAt = Date.now();
        chrome.runtime.sendMessage({ action: 'warmPreview', url: target.permalink }, () => { void chrome.runtime.lastError; });
      }
      existing.wrapper.style.display = "block";
      existing.wrapper.style.zIndex = "2147483647";
    }
    lastRefreshedTikTokVideo = targetVid;
  }

  function initTikTokActiveVideoObserver() {
    if (!window.location.hostname.includes("tiktok.com")) return;
    if (tiktokIntersectionObserver) return;

    try {
      tiktokIntersectionObserver = new IntersectionObserver(
        (entries) => {
          let bestCandidate = null;
          let bestRatio = 0;

          for (const entry of entries) {
            const vid = entry.target.tagName === "VIDEO" ? entry.target : entry.target.querySelector?.("video");
            if (!vid) continue;

            if (entry.isIntersecting && entry.intersectionRatio >= 0.6) {
              if (entry.intersectionRatio >= bestRatio) {
                bestRatio = entry.intersectionRatio;
                bestCandidate = vid;
              }
            }
          }

          const centered = findViewportCenterTikTokVideo();
          if (centered || bestCandidate) {
            refreshTikTokActiveVideoBadge(centered || bestCandidate);
          } else {
            const centered = findViewportCenterTikTokVideo();
            if (centered) {
              refreshTikTokActiveVideoBadge(centered);
            }
          }
        },
        { threshold: [0.4, 0.6, 0.75, 0.9] }
      );
    } catch (e) {}
  }

  function findViewportCenterTikTokVideo() {
    const videos = Array.from(document.querySelectorAll("video"));
    if (videos.length === 0) return null;

    const vpCenterX = window.innerWidth / 2;
    const vpCenterY = window.innerHeight / 2;
    let bestVideo = null;
    let bestScore = -Infinity;

    for (const v of videos) {
      if (!v.isConnected) continue;
      const rect = v.getBoundingClientRect();
      if (rect.width < 100 || rect.height < 100) continue;

      const visibleTop = Math.max(0, rect.top);
      const visibleBottom = Math.min(window.innerHeight, rect.bottom);
      const visibleHeight = Math.max(0, visibleBottom - visibleTop);
      const ratio = visibleHeight / Math.max(1, rect.height);
      if (ratio < 0.45) continue;

      const centerX = rect.left + rect.width / 2;
      const centerY = rect.top + rect.height / 2;
      const dist = Math.hypot(centerX - vpCenterX, centerY - vpCenterY);

      // Prioritize playing video with >= 0.6 visibility and closest to viewport center
      let score = ratio * 1000 - dist;
      if (!v.paused) score += 500;
      if (ratio >= 0.6) score += 300;

      if (score > bestScore) {
        bestScore = score;
        bestVideo = v;
      }
    }

    return bestVideo;
  }

  function resolveActiveTikTokVideo(referenceVideo) {
    if (referenceVideo && referenceVideo.isConnected) {
      // If user clicked badge on a specific profile grid item, keep that exact video
      if (referenceVideo.closest('div[data-e2e="user-post-item"], div[data-e2e="search-card-video-container"]')) {
        return referenceVideo;
      }
      const rect = referenceVideo.getBoundingClientRect();
      const visibleHeight = Math.max(0, Math.min(window.innerHeight, rect.bottom) - Math.max(0, rect.top));
      if (rect.height > 0 && visibleHeight / rect.height >= 0.6) {
        return referenceVideo;
      }
    }
    return findViewportCenterTikTokVideo() || activeTikTokVideo || referenceVideo;
  }

  function findViewportCenterVideo() {
    const videos = Array.from(document.querySelectorAll("video")).filter(v => v.isConnected);
    if (videos.length === 0) return null;
    if (videos.length === 1) return videos[0];

    const vpCenterX = window.innerWidth / 2;
    const vpCenterY = window.innerHeight / 2;

    let bestVideo = null;
    let bestScore = -1;

    for (const v of videos) {
      const rect = v.getBoundingClientRect();
      if (rect.width < 100 || rect.height < 80) continue;
      const visibleHeight = Math.max(0, Math.min(window.innerHeight, rect.bottom) - Math.max(0, rect.top));
      const ratio = visibleHeight / Math.max(1, rect.height);
      if (ratio < 0.4) continue;

      const centerX = rect.left + rect.width / 2;
      const centerY = rect.top + rect.height / 2;
      const dist = Math.hypot(centerX - vpCenterX, centerY - vpCenterY);

      let score = ratio * 1000 - dist;
      if (!v.paused) score += 500;
      if (ratio >= 0.6) score += 300;

      if (score > bestScore) {
        bestScore = score;
        bestVideo = v;
      }
    }
    return bestVideo || videos[0];
  }

  function resolveActiveInstagramVideo(referenceVideo) {
    if (referenceVideo && referenceVideo.isConnected) {
      const rect = referenceVideo.getBoundingClientRect();
      const visibleHeight = Math.max(0, Math.min(window.innerHeight, rect.bottom) - Math.max(0, rect.top));
      if (rect.height > 0 && visibleHeight / rect.height >= 0.5) {
        return referenceVideo;
      }
    }
    const centered = findViewportCenterVideo();
    return centered || referenceVideo;
  }

  // Highly responsive and low-overhead observer for video elements across sites (including TikTok & Instagram reels/modals)
  function initVideoObserver() {
    setupGlobalCaptureInterceptors();
    initTikTokActiveVideoObserver();
    scanForVideos();

    // Instant reaction when any video starts playing (TikTok For You scroll, Instagram reels, full-view canvas, etc.)
    document.addEventListener("play", (e) => {
      if (e.target && e.target.tagName === "VIDEO") {
        if (!hookedVideos.has(e.target)) {
          hookedVideos.add(e.target);
        }
        if (window.location.hostname.includes("tiktok.com")) {
          refreshTikTokActiveVideoBadge(e.target);
        } else {
          attachVideoBadge(e.target);
        }
      }
    }, true);

    document.addEventListener("playing", (e) => {
      if (e.target && e.target.tagName === "VIDEO") {
        if (window.location.hostname.includes("tiktok.com")) {
          refreshTikTokActiveVideoBadge(e.target);
        } else {
          attachVideoBadge(e.target);
        }
      }
    }, true);

    document.addEventListener("timeupdate", (e) => {
      if (e.target && e.target.tagName === "VIDEO" && window.location.hostname.includes("tiktok.com")) {
        const existing = videoBadgeMap.get(e.target);
        if (!existing || !existing.wrapper || !document.body.contains(existing.wrapper)) {
          refreshTikTokActiveVideoBadge(e.target);
        }
      }
    }, true);

    document.addEventListener("loadeddata", (e) => {
      if (e.target && e.target.tagName === "VIDEO") {
        if (window.location.hostname.includes("tiktok.com")) {
          refreshTikTokActiveVideoBadge(e.target);
        } else {
          attachVideoBadge(e.target);
        }
      }
    }, true);

    // Fast mutation detection: triggers scheduleScan when video elements, TikTok virtual scroll cards, or dialogs enter/update the DOM
    try {
      const mo = new MutationObserver((mutations) => {
        let shouldRescan = false;
        const isTikTok = window.location.hostname.includes("tiktok.com");
        for (const m of mutations) {
          if (m.type === "attributes" && isTikTok) {
            if (m.target.tagName === "VIDEO" || (m.target.id && m.target.id.startsWith("xgwrapper-"))) {
              shouldRescan = true;
              break;
            }
          }
          for (const node of m.addedNodes) {
            if (node.nodeType === 1) {
              if (
                node.tagName === "VIDEO" ||
                (node.querySelector && node.querySelector("video")) ||
                (isTikTok && node.matches && node.matches('div[data-e2e="recommend-list-item-container"], div[data-e2e="user-post-item"], div[data-e2e="feed-video"], div[data-e2e="browse-video"]'))
              ) {
                shouldRescan = true;
                break;
              }
            }
          }
          if (shouldRescan) break;
        }
        if (shouldRescan) {
          scheduleScan();
        }
      });
      mo.observe(document.body || document.documentElement, {
        childList: true,
        subtree: true,
        attributes: window.location.hostname.includes("tiktok.com"),
        attributeFilter: ["src", "id", "data-e2e"]
      });
    } catch (e) {}

    if (window.location.hostname.includes("tiktok.com")) {
      window.addEventListener("scroll", () => scheduleScan(), { passive: true, capture: true });
      window.addEventListener("wheel", () => scheduleScan(), { passive: true });
      window.addEventListener("keyup", (e) => {
        if (e.key === "ArrowDown" || e.key === "ArrowUp" || e.key === "PageDown" || e.key === "PageUp") {
          scheduleScan();
        }
      }, { passive: true });
    }

    setInterval(() => {
      if (document.hidden) return;
      if (document.querySelector("video")) scanForVideos();
    }, 1200);

    document.addEventListener("visibilitychange", () => {
      if (!document.hidden) scheduleScan();
    }, { passive: true });
  }

  function isEligibleVideo(elem) {
    if (!elem) return false;
    // Don't inject badge on miniplayers or feed preview thumbnail cards
    if (elem.closest("ytd-miniplayer, [mini-player], #inline-preview-player, ytd-thumbnail")) {
      return false;
    }
    const w = elem.offsetWidth || elem.videoWidth || elem.clientWidth || 0;
    const h = elem.offsetHeight || elem.videoHeight || elem.clientHeight || 0;
    // If the video is playing or already has a source stream, consider it eligible immediately
    if (elem.isConnected && (elem.currentSrc || elem.src || !elem.paused || elem.readyState > 0)) {
      if (w === 0 && h === 0) return true; // Newly mounted, layout in progress
    }
    return w >= 120 && h >= 80;
  }

  function scanForVideos() {
    if (!isVideoBarEnabled) return;
    if (window === window.top && document.querySelector('video') &&
        (lastPreviewPage !== window.location.href || Date.now() - lastPreviewAt > 30000)) {
      lastPreviewPage = window.location.href; lastPreviewAt = Date.now();
      chrome.runtime.sendMessage({ action: 'warmPreview' }, () => { void chrome.runtime.lastError; });
    }

    const isTikTok = window.location.hostname.includes("tiktok.com");
    if (isTikTok) {
      initTikTokActiveVideoObserver();
      const centered = findViewportCenterTikTokVideo();
      if (centered) {
        refreshTikTokActiveVideoBadge(centered);
      }
      if (tiktokIntersectionObserver) {
        document.querySelectorAll('div[data-e2e="recommend-list-item-container"], article[data-e2e="recommend-list-item-container"], div[data-e2e="user-post-item"], div[data-e2e="feed-video"], div[data-e2e="browse-video"]').forEach((c) => {
          if (!observedTikTokNodes.has(c)) {
            observedTikTokNodes.add(c);
            try { tiktokIntersectionObserver.observe(c); } catch (e) {}
          }
        });
      }
    }

    const videos = document.querySelectorAll("video");
    videos.forEach((video) => {
      if (!hookedVideos.has(video)) {
        hookedVideos.add(video);
        video.addEventListener("play", () => {
          if (isTikTok) refreshTikTokActiveVideoBadge(video);
          else attachVideoBadge(video);
        }, { passive: true });
        video.addEventListener("playing", () => {
          if (isTikTok) refreshTikTokActiveVideoBadge(video);
          else attachVideoBadge(video);
        }, { passive: true });
        video.addEventListener("loadedmetadata", () => {
          if (isTikTok) refreshTikTokActiveVideoBadge(video);
          else attachVideoBadge(video);
        }, { passive: true });
      }

      if (isTikTok && tiktokIntersectionObserver && !observedTikTokNodes.has(video)) {
        observedTikTokNodes.add(video);
        try { tiktokIntersectionObserver.observe(video); } catch (e) {}
      }

      if (isEligibleVideo(video)) {
        attachVideoBadge(video);
      }
    });
  }

  function findBestPlayerContainer(video) {
    // 1. YouTube specific: Closest player container
    if (window.location.hostname.includes("youtube.com")) {
      const ytPlayer = video.closest("#movie_player, .html5-video-player, ytd-player, ytd-shorts, #player");
      if (ytPlayer) return ytPlayer;
    }

    // 2. Instagram specific: Direct canvas or reel/post viewport container
    if (window.location.hostname.includes("instagram.com")) {
      const igCanvas = video.closest("div[style*='aspect-ratio'], div._ab6-, div._aato, div.x1ey2m1c, div[class*='canvas']") || video.parentElement;
      return igCanvas || video.parentElement || video;
    }

    // 3. TikTok specific: Video canvas player wrapper (inside green rectangle)
    if (window.location.hostname.includes("tiktok.com")) {
      const tiktokCanvas =
        video.closest('.xgplayer-container, .xgplayer, div[class*="DivVideoWrapper"], div[class*="DivBasicPlayerWrapper"], div[class*="DivPlayerContainer"], div[data-e2e="feed-video"], div[data-e2e="browse-video"], div[data-e2e="user-post-item"]') ||
        video.parentElement;
      return tiktokCanvas || video.parentElement || video;
    }

    // 4. Generic video players: Outermost player container
    let container = video.closest(
      "#movie_player, .html5-video-player, [id*='player'], [class*='player-container'], [class*='video-container'], [class*='player_container'], .video-js, .vjs-tech"
    );

    if (!container || container === video) {
      container = video.parentElement;
    }

    // Avoid inner video canvas surfaces like .html5-video-container
    if (container && container.classList.contains("html5-video-container")) {
      container = container.parentElement || container;
    }

    return container || video.parentElement;
  }

  function attachVideoBadge(video) {
    if (!video || !video.isConnected) return;
    const isTikTok = window.location.hostname.includes("tiktok.com");
    const isInstagram = window.location.hostname.includes("instagram.com");

    let container = findBestPlayerContainer(video);
    if (!container) return;

    // Check if container already has our badge (if virtual scroll recycled the container, update _currentVideo!)
    const existingBadgeInContainer = !isTikTok && container.querySelector(":scope > .idmlite-video-badge-container");
    if (existingBadgeInContainer) {
      existingBadgeInContainer._currentVideo = video;
      existingBadgeInContainer.style.display = "block";
      existingBadgeInContainer.style.zIndex = "2147483647";
      return;
    }

    if (videoBadgeMap.has(video)) {
      const existing = videoBadgeMap.get(video);
      if (existing && existing.container && existing.wrapper && (existing.isBodyAttached ? document.body.contains(existing.wrapper) : existing.container.contains(existing.wrapper))) {
        existing.wrapper._currentVideo = video;
        existing.wrapper.style.display = "block";
        return;
      }
    }

    if (container !== document.body && container !== document.documentElement) {
      if (!container.style.position || container.style.position === "static") {
        try {
          const comp = window.getComputedStyle(container);
          if (comp.position === "static") {
            container.style.position = "relative";
          }
        } catch (e) {}
      }
    }

    const badgeWrapper = document.createElement("div");
    badgeWrapper.className = "idmlite-video-badge-container";
    if (isTikTok) badgeWrapper.classList.add('idmlite-tiktok-badge');
    badgeWrapper._currentVideo = video;
    badgeWrapper.style.position = "absolute";
    badgeWrapper.style.top = "12px";
    badgeWrapper.style.right = "16px";
    badgeWrapper.style.zIndex = "2147483647";
    badgeWrapper.style.pointerEvents = "auto";

    const badgeBtn = document.createElement("button");
    badgeBtn.type = "button";
    badgeBtn.className = "idmlite-badge-btn";
    badgeBtn.innerHTML = `<span class="idmlite-badge-icon">⬇️</span><span>Bu Videoyu İndir</span><span class="idmlite-badge-sub">IDM</span>`;

    const dropdown = document.createElement("div");
    dropdown.className = "idmlite-dropdown-menu";

    // Lock the exact active video on hover/pointerdown so virtual scroll or player state changes never lose it
    const lockCurrentVideoTarget = () => {
      const liveVid =
        (badgeWrapper._currentVideo && badgeWrapper._currentVideo.isConnected ? badgeWrapper._currentVideo : null) ||
        (container ? container.querySelector("video") : null) ||
        video;
      badgeWrapper._lockedVideo = liveVid;
      if (isTikTok) {
        badgeWrapper._lockedTikTokData = extractTikTokVideoTarget(liveVid);
      } else if (isInstagram) {
        badgeWrapper._lockedInstagramData = extractInstagramVideoTarget(liveVid);
      }
    };

    badgeBtn.addEventListener("mouseenter", lockCurrentVideoTarget, { passive: true });

    let lastTriggerTime = 0;
    const badgeTrigger = () => {
      const now = Date.now();
      if (now - lastTriggerTime < 250) return;
      lastTriggerTime = now;

      // Close all other open dropdowns first to prevent stale dropdown interactions
      document.querySelectorAll(".idmlite-dropdown-menu.idmlite-show").forEach((d) => {
        if (d !== dropdown) d.classList.remove("idmlite-show");
      });
      lockCurrentVideoTarget();
      const currentVid = badgeWrapper._lockedVideo || badgeWrapper._currentVideo || (container ? container.querySelector("video") : null) || video;
      populateDropdown(currentVid, dropdown, badgeWrapper._lockedTikTokData || badgeWrapper._lockedInstagramData);
      dropdown.classList.toggle("idmlite-show");
    };
    badgeBtn._triggerHandler = badgeTrigger;

    // Use capture phase and stop immediate propagation so TikTok/Instagram/canvas overlays cannot steal the click
    const handleBadgeClick = (e) => {
      e.preventDefault();
      e.stopPropagation();
      e.stopImmediatePropagation();
      badgeTrigger();
    };

    badgeBtn.addEventListener("click", handleBadgeClick, true);
    badgeBtn.addEventListener("pointerdown", (e) => {
      lockCurrentVideoTarget();
      e.stopPropagation();
      e.stopImmediatePropagation();
    }, true);
    badgeBtn.addEventListener("mousedown", (e) => {
      lockCurrentVideoTarget();
      e.stopPropagation();
      e.stopImmediatePropagation();
    }, true);

    badgeWrapper.appendChild(badgeBtn);
    badgeWrapper.appendChild(dropdown);

    if (isTikTok) {
      document.body.appendChild(badgeWrapper);
      const entry = { video, wrapper: badgeWrapper, dropdown, container, isBodyAttached: true };
      videoBadgeMap.set(video, entry); activeBodyBadges.add(entry);
      syncAllBadgePositions(); startBadgeRafLoop();
    } else {
      container.appendChild(badgeWrapper);
      videoBadgeMap.set(video, { wrapper: badgeWrapper, dropdown: dropdown, container: container, isBodyAttached: false });
    }
  }

  // Single global click handler to close open dropdowns without multiplying event listeners
  document.addEventListener("click", (e) => {
    if (!e.target.closest(".idmlite-video-badge-container")) {
      document.querySelectorAll(".idmlite-dropdown-menu.idmlite-show").forEach((d) => {
        d.classList.remove("idmlite-show");
      });
    }
  }, { passive: true });

  // Safe, WeakSet-guarded React Fiber & Props walker (never throws on circular references)
  function inspectReactFiberMedia(startElement, stopAtMultiVideoContainer = false) {
    const result = {
      videoId: "",
      shortcode: "",
      author: "",
      caption: "",
      directStreamUrl: "",
      directStreamSize: 0
    };
    if (!startElement) return result;

    try {
      const visited = new WeakSet();
      let curEl = startElement;

      for (let domDepth = 0; domDepth < 10 && curEl; domDepth++) {
        // Never walk into a feed list container that holds multiple videos!
        if (stopAtMultiVideoContainer && domDepth > 0 && curEl.querySelectorAll) {
          if (curEl.querySelectorAll("video").length > 1) {
            break;
          }
        }

        let keys = [];
        try {
          keys = Object.keys(curEl);
        } catch (e) {}

        for (const k of keys) {
          if (!k.startsWith("__reactFiber$") && !k.startsWith("__reactProps$") && !k.startsWith("__reactInternalInstance$")) {
            continue;
          }
          const rootObj = curEl[k];
          if (!rootObj || typeof rootObj !== "object") continue;

          // Walk fiber chain (.return) and props object graph with bounded BFS
          let fiberNode = rootObj;
          const maxFiberSteps = stopAtMultiVideoContainer ? 4 : 10;
          for (let fStep = 0; fStep < maxFiberSteps && fiberNode; fStep++) {
            const candidates = [fiberNode.memoizedProps, fiberNode.pendingProps, fiberNode.memoizedState, fiberNode];
            for (const cand of candidates) {
              if (!cand || typeof cand !== "object") continue;
              const queue = [{ obj: cand, depth: 0 }];
              let inspectedCount = 0;

              while (queue.length > 0 && inspectedCount < 350) {
                const { obj, depth } = queue.shift();
                if (!obj || typeof obj !== "object" || visited.has(obj)) continue;
                if (obj.nodeType || obj === window || obj === document) continue;
                visited.add(obj);
                inspectedCount++;

                // 1. Check TikTok itemStruct / video properties
                const itemId = (obj.video && typeof obj.video === 'object') ||
                    (typeof obj.desc === 'string' && obj.author) ? obj.id : undefined;
                const rawId = obj.itemId || obj.awemeId || obj.videoId || obj.aweme_id || itemId;
                if (!result.videoId && (typeof rawId === "string" || (typeof rawId === "number" && Number.isSafeInteger(rawId)))) {
                  const sId = String(rawId).trim();
                  if (/^\d{17,21}$/.test(sId)) {
                    result.videoId = sId;
                  }
                }

                // 2. Check Instagram shortcode / code
                const rawCode = obj.shortcode || obj.code;
                if (!result.shortcode && typeof rawCode === "string" && /^[A-Za-z0-9_-]{6,16}$/.test(rawCode)) {
                  result.shortcode = rawCode;
                }

                // 3. Check Author username
                if (!result.author) {
                  if (typeof obj.uniqueId === "string" && obj.uniqueId.length > 1) {
                    result.author = obj.uniqueId;
                  } else if (obj.author && typeof obj.author.uniqueId === "string") {
                    result.author = obj.author.uniqueId;
                  } else if (obj.user && typeof obj.user.username === "string") {
                    result.author = obj.user.username;
                  } else if (obj.owner && typeof obj.owner.username === "string") {
                    result.author = obj.owner.username;
                  }
                }

                // 4. Check Caption / Description
                if (!result.caption) {
                  if (typeof obj.desc === "string" && obj.desc.trim().length > 1) {
                    result.caption = obj.desc.trim();
                  } else if (obj.caption && typeof obj.caption.text === "string" && obj.caption.text.trim().length > 1) {
                    result.caption = obj.caption.text.trim();
                  }
                }

                // 5. Check TikTok video playAddr / downloadAddr / bitrateInfo or Instagram video_versions / video_url
                if (!result.directStreamUrl) {
                  if (Array.isArray(obj.video_versions) && obj.video_versions.length > 0 && typeof obj.video_versions[0]?.url === "string") {
                    const igUrl = obj.video_versions[0].url;
                    if (/^https?:\/\//i.test(igUrl)) {
                      result.directStreamUrl = cleanPlaybackUrl(igUrl);
                      result.directStreamSize = extractSizeFromUrl(igUrl);
                    }
                  } else if (typeof obj.video_url === "string" && /^https?:\/\//i.test(obj.video_url)) {
                    result.directStreamUrl = cleanPlaybackUrl(obj.video_url);
                    result.directStreamSize = extractSizeFromUrl(obj.video_url);
                  } else if (obj.video && typeof obj.video === "object") {
                    const vObj = obj.video;
                    const tryExtractAddr = (addr) => {
                      if (!addr) return "";
                      if (typeof addr === "string" && addr.startsWith("http")) return addr;
                      if (Array.isArray(addr.UrlList) && addr.UrlList.length > 0) return addr.UrlList[0];
                      if (Array.isArray(addr.urlList) && addr.urlList.length > 0) return addr.urlList[0];
                      if (typeof addr.url === "string" && addr.url.startsWith("http")) return addr.url;
                      return "";
                    };
                    const foundUrl =
                      tryExtractAddr(vObj.playAddr) ||
                      tryExtractAddr(vObj.downloadAddr) ||
                      (Array.isArray(vObj.bitrateInfo) && vObj.bitrateInfo.length > 0
                        ? tryExtractAddr(vObj.bitrateInfo[0].PlayAddr || vObj.bitrateInfo[0].playAddr)
                        : "");
                    if (foundUrl && /^https?:\/\//i.test(foundUrl)) {
                      result.directStreamUrl = cleanPlaybackUrl(foundUrl);
                      const sz = Number(vObj.playAddr?.dataSize || vObj.downloadAddr?.dataSize || vObj.size || 0);
                      if (sz > 0) result.directStreamSize = sz;
                    }
                  }
                }

                if (depth < 4) {
                  let childKeys = [];
                  try {
                    childKeys = Object.keys(obj);
                  } catch (e) {}
                  for (const ck of childKeys) {
                    if (ck === "return" || ck === "alternate" || ck === "_owner" || ck === "stateNode" || ck === "dep") continue;
                    let val;
                    try {
                      val = obj[ck];
                    } catch (e) {
                      continue;
                    }
                    if (val && typeof val === "object") {
                      queue.push({ obj: val, depth: depth + 1 });
                    }
                  }
                }
              }
            }
            fiberNode = fiberNode.return;
          }
        }
        curEl = curEl.parentElement;
      }
    } catch (e) {}

    return result;
  }

  function findOuterInstagramPostContainer(video) {
    if (!video) return document;
    // 1. If inside a feed <article> or modal <div role="dialog"> (Home Feed, Profile Modal, "Kaydedilenler" Saved Modal), return that exact post container
    const dialogOrArticle = video.closest("article, div[role='dialog']");
    if (dialogOrArticle) {
      return dialogOrArticle;
    }

    // 2. On Instagram "/reels/" vertical scroll tab, there is NO <article> or <div role="dialog">.
    // Walk up ONLY while the ancestor contains this single <video> element (NEVER select <main> or <section> which hold the entire Reels feed!)
    let singleReelContainer = video.parentElement || video;
    let cur = video.parentElement;
    for (let i = 0; i < 10 && cur && cur !== document.body; i++) {
      if (cur.tagName === "MAIN" || cur.tagName === "SECTION") break;
      const vidCount = cur.querySelectorAll("video").length;
      if (vidCount > 1) break;
      singleReelContainer = cur;
      cur = cur.parentElement;
    }
    return singleReelContainer;
  }

  function isLikelyInstagramVideoUrl(url) {
    if (!url || typeof url !== "string" || !url.startsWith("http")) return false;
    const lower = url.toLowerCase();
    if (lower.includes(".jpg") || lower.includes(".jpeg") || lower.includes(".png") || lower.includes(".webp") || lower.includes(".gif")) return false;
    if (lower.includes("/v/t51.")) return false; // Instagram photos CDN
    if (lower.includes("/s150x150/") || lower.includes("/s320x320/")) return false;
    return true;
  }

  function inspectInstagramVideoFiber(video) {
    const info = {
      shortcode: "",
      directStreamUrl: "",
      directStreamSize: 0,
      author: "",
      caption: ""
    };
    if (!video) return info;

    const elementsToTry = [video];
    let parentCur = video.parentElement;
    for (let i = 0; i < 6 && parentCur && parentCur !== document.body; i++) {
      elementsToTry.push(parentCur);
      parentCur = parentCur.parentElement;
    }

    try {
      for (const el of elementsToTry) {
        let fiberKey = "";
        for (const k of Object.keys(el)) {
          if (k.startsWith("__reactFiber$") || k.startsWith("__reactInternalInstance$")) {
            fiberKey = k;
            break;
          }
        }
        if (!fiberKey) continue;

        let fiber = el[fiberKey];
        // Walk UP the React component tree (up to 30 steps) through .return
        // This strictly traverses the ancestors of THIS video element ONLY!
        for (let step = 0; fiber && step < 30; step++, fiber = fiber.return) {
          const propsCandidates = [fiber.memoizedProps, fiber.pendingProps, fiber.memoizedState];
          for (const p of propsCandidates) {
            if (!p || typeof p !== "object") continue;

            const objectsToCheck = [
              p,
              p.media,
              p.post,
              p.item,
              p.clip,
              p.reel,
              p.video,
              p.feedItem,
              p.exploreItem,
              p.items?.[0],
              p.carousel_media?.[0],
              p.clips_metadata
            ];

            for (const obj of objectsToCheck) {
              if (!obj || typeof obj !== "object") continue;

              // 1. Shortcode
              if (!info.shortcode) {
                const code = obj.shortcode || obj.code;
                if (typeof code === "string" && /^[A-Za-z0-9_-]{7,25}$/.test(code)) {
                  info.shortcode = code;
                }
              }

              // 2. Direct video stream URL from video_versions
              if (!info.directStreamUrl) {
                const candidateVersions = Array.isArray(obj.video_versions) ? obj.video_versions :
                                          (Array.isArray(obj.clips_metadata?.video_versions) ? obj.clips_metadata.video_versions : null);
                if (candidateVersions && candidateVersions.length > 0) {
                  let bestStream = null;
                  for (const v of candidateVersions) {
                    if (v && v.url && isLikelyInstagramVideoUrl(v.url)) {
                      const w = Number(v.width || 0);
                      const bestW = Number(bestStream?.width || 0);
                      if (!bestStream || w >= bestW) {
                        bestStream = v;
                      }
                    }
                  }
                  if (bestStream && isLikelyInstagramVideoUrl(bestStream.url)) {
                    info.directStreamUrl = cleanPlaybackUrl(bestStream.url);
                    info.directStreamSize = extractSizeFromUrl(bestStream.url);
                  }
                } else if (typeof obj.video_url === "string" && isLikelyInstagramVideoUrl(obj.video_url)) {
                  info.directStreamUrl = cleanPlaybackUrl(obj.video_url);
                  info.directStreamSize = extractSizeFromUrl(obj.video_url);
                } else if (typeof obj.playback_url === "string" && isLikelyInstagramVideoUrl(obj.playback_url)) {
                  info.directStreamUrl = cleanPlaybackUrl(obj.playback_url);
                  info.directStreamSize = extractSizeFromUrl(obj.playback_url);
                }
              }

              // 3. Author
              if (!info.author) {
                const userObj = obj.user || obj.owner || obj.author;
                if (userObj && typeof userObj.username === "string") {
                  info.author = userObj.username;
                } else if (typeof obj.username === "string") {
                  info.author = obj.username;
                }
              }

              // 4. Caption
              if (!info.caption) {
                if (obj.caption && typeof obj.caption.text === "string") {
                  info.caption = obj.caption.text.trim();
                } else if (typeof obj.caption === "string") {
                  info.caption = obj.caption.trim();
                } else if (typeof obj.title === "string") {
                  info.caption = obj.title.trim();
                }
              }

              if (info.shortcode && info.directStreamUrl) {
                return info;
              }
            }
          }
        }
      }
    } catch (e) {}

    return info;
  }

  function extractInstagramVideoTarget(rawVideo) {
    const video = resolveActiveInstagramVideo(rawVideo) || rawVideo;
    const outerRoot = findOuterInstagramPostContainer(video);
    let targetPostUrl = "";
    let shortcode = "";
    let directStreamUrl = "";
    let directStreamSize = 0;
    let author = "";
    let caption = "";

    try {
      // 1. Primary: Direct React Fiber inspection of THIS video (instant, exact, and works on the 1st reel where address bar has no code)
      const fiberInfo = inspectInstagramVideoFiber(video);
      if (fiberInfo.shortcode) {
        shortcode = fiberInfo.shortcode;
        targetPostUrl = `https://www.instagram.com/reel/${shortcode}/`;
      }
      if (fiberInfo.directStreamUrl) {
        directStreamUrl = fiberInfo.directStreamUrl;
        directStreamSize = fiberInfo.directStreamSize;
      }
      if (fiberInfo.author) author = fiberInfo.author;
      if (fiberInfo.caption) caption = fiberInfo.caption;

      // 2. Browser Address Bar URL (Only if address bar already contains an explicit shortcode!)
      if (!shortcode) {
        const locMatch = window.location.pathname.match(/^(?:\/[^\/]+)?\/(p|reel|reels|tv)\/([A-Za-z0-9_-]{7,25})/i);
        if (locMatch && locMatch[2]) {
          const isReel = locMatch[1].toLowerCase().startsWith("reel");
          shortcode = locMatch[2];
          targetPostUrl = `https://www.instagram.com/${isReel ? "reel" : "p"}/${shortcode}/`;
        }
      }

      // 3. Search inside post container or slide ancestors for permalink anchors
      if (!shortcode) {
        let pSearch = outerRoot || video.parentElement;
        for (let i = 0; i < 10 && pSearch && pSearch !== document.body; i++) {
          const links = Array.from(pSearch.querySelectorAll("a[href*='/reel/'], a[href*='/reels/'], a[href*='/p/'], a[href*='/tv/'], a[href*='/share/reel/']"));
          for (const a of links) {
            const href = a.getAttribute("href") || a.href || "";
            const m = href.match(/(?:\/(?:p|reel|reels|tv|share\/reel)\/)([A-Za-z0-9_-]{7,25})/i);
            if (m && m[1]) {
              shortcode = m[1];
              targetPostUrl = `https://www.instagram.com/reel/${shortcode}/`;
              break;
            }
          }
          if (shortcode) break;
          pSearch = pSearch.parentElement;
        }
      }

      // 4. Check canonical or og:url meta tags in document.head
      if (!shortcode) {
        const ogUrl = document.querySelector("meta[property='og:url']")?.content ||
                      document.querySelector("link[rel='canonical']")?.href || "";
        const mOg = ogUrl.match(/(?:\/(?:p|reel|reels|tv)\/)([A-Za-z0-9_-]{7,25})/i);
        if (mOg && mOg[1]) {
          shortcode = mOg[1];
          targetPostUrl = `https://www.instagram.com/reel/${shortcode}/`;
        }
      }

      // 5. Fallback BFS Fiber search
      if (!shortcode) {
        const fallbackFiber = inspectReactFiberMedia(video, false);
        if (fallbackFiber.shortcode) {
          shortcode = fallbackFiber.shortcode;
          targetPostUrl = `https://www.instagram.com/reel/${shortcode}/`;
        }
        if (!directStreamUrl && fallbackFiber.directStreamUrl) {
          directStreamUrl = fallbackFiber.directStreamUrl;
          directStreamSize = fallbackFiber.directStreamSize;
        }
        if (!author && fallbackFiber.author) author = fallbackFiber.author;
        if (!caption && fallbackFiber.caption) caption = fallbackFiber.caption;
      }
    } catch (e) {}

    // Check direct video element src attribute (if non-blob and is actual video stream)
    if (!directStreamUrl && video) {
      const src = video.currentSrc || video.src || "";
      if (src && !src.startsWith("blob:") && !src.startsWith("data:") && isLikelyInstagramVideoUrl(src)) {
        directStreamUrl = cleanPlaybackUrl(src);
        directStreamSize = extractSizeFromUrl(src);
      }
    }

    if (!targetPostUrl || targetPostUrl === "https://www.instagram.com/" || targetPostUrl === "https://www.instagram.com/reels/") {
      if (shortcode) {
        targetPostUrl = `https://www.instagram.com/reel/${shortcode}/`;
      } else if (directStreamUrl) {
        targetPostUrl = directStreamUrl;
      } else {
        targetPostUrl = window.location.href;
      }
    }

    const title = cleanInstagramTitle(video, shortcode, outerRoot, author, caption);

    return {
      targetPostUrl,
      shortcode,
      title,
      directStreamUrl,
      directStreamSize
    };
  }

  const IG_RESERVED_PATHS = new Set([
    "p", "reel", "reels", "tv", "stories", "explore", "direct", "accounts",
    "saved", "audio", "tags", "locations", "about", "legal", "privacy",
    "terms", "session", "challenge", " developer", "directory", "lite"
  ]);

  function getLoggedInInstagramUsername() {
    try {
      // 1. If on /<my_username>/saved/, that segment is always the logged-in user
      const mSaved = window.location.pathname.match(/^\/([A-Za-z0-9._]{2,30})\/saved(?:\/|$)/i);
      if (mSaved && mSaved[1] && !IG_RESERVED_PATHS.has(mSaved[1].toLowerCase())) {
        return mSaved[1].toLowerCase();
      }
      // 2. Check global left/bottom navigation sidebar profile link
      const navLinks = document.querySelectorAll("nav a[href^='/'], div[role='navigation'] a[href^='/']");
      for (const a of navLinks) {
        const href = a.getAttribute("href") || "";
        const m = href.match(/^\/([A-Za-z0-9._]{2,30})\/$/);
        if (m && m[1] && !IG_RESERVED_PATHS.has(m[1].toLowerCase())) {
          // Profile link in nav usually contains profile picture img or "Profil"/"Profile" label
          if (a.querySelector("img") || /profil|profile/i.test(a.textContent || "")) {
            return m[1].toLowerCase();
          }
        }
      }
    } catch (e) {}
    return "";
  }

  function cleanInstagramTitle(video, shortcode = "", precomputedRoot = null, precomputedAuthor = "", precomputedCaption = "") {
    let author = precomputedAuthor || "";
    let caption = precomputedCaption || "";
    const loggedInUser = getLoggedInInstagramUsername();

    try {
      if (author && caption) {
        // Already fully resolved from direct React Fiber
      } else {
        const root = precomputedRoot || (video ? findOuterInstagramPostContainer(video) : null);

      if (root && root !== document && root.querySelectorAll) {
        // 1. Extract the video uploader/creator username from post header or Reel overlay links
        // Note: The first <a href="/creator/"> in Instagram's DOM is often the avatar <img> link (with empty textContent),
        // so we inspect both textContent AND the href="/<username>/" path!
        const candidateLinks = Array.from(
          root.querySelectorAll("header a[href^='/'], a[role='link'][href^='/'], a[href^='/']")
        );

        let fallbackHrefAuthor = "";
        for (const a of candidateLinks) {
          // Ignore links inside global navigation, comment forms, or "Liked by" sections
          if (a.closest("nav, [role='navigation'], form, footer")) continue;

          const href = a.getAttribute("href") || "";
          const mUser = href.match(/^\/([A-Za-z0-9._]{2,30})\/(?:reels\/)?(?:\?.*)?$/i);
          if (!mUser || !mUser[1]) continue;

          const handleFromHref = mUser[1].trim();
          if (IG_RESERVED_PATHS.has(handleFromHref.toLowerCase())) continue;

          // If this link is the logged-in user's own handle (e.g. comment box avatar or saved collection owner header), skip unless inside post's own <header>
          if (loggedInUser && handleFromHref.toLowerCase() === loggedInUser && !a.closest("article header, div[role='dialog'] header")) {
            continue;
          }

          const linkText = (a.textContent || "").replace(/[\r\n\t•]+/g, " ").trim().replace(/^@/, "");
          if (linkText && linkText.toLowerCase() === handleFromHref.toLowerCase()) {
            author = handleFromHref;
            break;
          }
          if (!fallbackHrefAuthor) {
            fallbackHrefAuthor = handleFromHref;
          }
        }

        if (!author && fallbackHrefAuthor) {
          author = fallbackHrefAuthor;
        }

        // 2. Also check single-video React Fiber for the post owner's username & caption if needed
        if (!author || !caption) {
          const fiber = inspectReactFiberMedia(video || root, true);
          if (!author && fiber.author && fiber.author.toLowerCase() !== loggedInUser) {
            author = fiber.author.replace(/^@/, "");
          }
          if (!caption && fiber.caption) {
            caption = fiber.caption;
          }
        }

        // 3. Extract the original video caption from the post/Reel container
        if (!caption) {
          const captionNodes = Array.from(
            root.querySelectorAll(
              "h1._ap3a, ul li h1, div._a9zs span[dir='auto'], div[data-pressable-container='true'] span[dir='auto'], h1, span[dir='auto']"
            )
          );
          const uiNoiseRegex = /^(takip et|takip ediliyor|follow|following|orijinal ses|original audio|ses|audio|reels|instagram|kaydedildi|saved|kaydedilenler|beğen|like|yorum yap|comment|paylaş|share|daha fazla|more|devamı|\.\.\.|•|\d+\s*[smhdwyg]|\d+\s*(saniye|dakika|saat|gün|hafta|ay|yıl|beğenme|likes?|views?|görüntüleme|yorum|comments?).*)$/i;

          for (const node of captionNodes) {
            if (node.closest("header, nav, [role='navigation'], form, button")) continue;
            // Skip if node wraps an author link and only contains the username
            const txt = (node.textContent || "").replace(/[\r\n\t]+/g, " ").trim();
            if (!txt || txt.length < 2) continue;
            if (author && txt.toLowerCase() === author.toLowerCase()) continue;
            if (loggedInUser && txt.toLowerCase().includes(loggedInUser)) continue;
            if (uiNoiseRegex.test(txt)) continue;
            // Skip audio marquee text ("username • Orijinal ses")
            if (/•\s*(orijinal ses|original audio)/i.test(txt)) continue;

            caption = txt;
            break;
          }
        }
      }
      }
    } catch (e) {}

    // 4. Only use <meta property="og:title"> if og:url explicitly matches the active video's shortcode
    // (In Instagram's SPA, og:title and document.title stay stuck on the initial page—such as the user's own Profile or Saved Posts "@my_username"!)
    if (!caption && shortcode) {
      const ogUrl = document.querySelector("meta[property='og:url']")?.content || "";
      if (ogUrl && ogUrl.includes(shortcode)) {
        const ogTitle = document.querySelector("meta[property='og:title']")?.content || "";
        const ogDesc = document.querySelector("meta[property='og:description']")?.content || "";
        const raw = ogTitle || ogDesc || "";

        if (!author) {
          const mUser = raw.match(/\(@([A-Za-z0-9._]{2,30})\)/) || raw.match(/^([^•("@]+?)\s+(?:on Instagram|Instagram'da|Instagramda)/i);
          if (mUser && mUser[1] && mUser[1].trim().toLowerCase() !== loggedInUser) {
            author = mUser[1].trim().replace(/^@/, "");
          }
        }

        const mCap = raw.match(/(?:on Instagram|Instagram'da|Instagramda):\s*["“']?(.*?)(?:["”']?\s*$)/i);
        if (mCap && mCap[1]) {
          caption = mCap[1].trim();
        }
      }
    }

    caption = caption.replace(/[\r\n\t]+/g, " ").trim();
    if (caption.length > 70) {
      caption = caption.substring(0, 70).trim();
    }

    if (caption && caption.toLowerCase() !== "instagram" && caption.toLowerCase() !== "video" && caption.toLowerCase() !== "saved") {
      if (author && !caption.toLowerCase().includes(author.toLowerCase())) {
        return `${author} - ${caption}`;
      }
      return caption;
    }

    if (author) {
      return shortcode ? `${author} - ${shortcode}` : author;
    }
    return shortcode ? `Instagram_Video_${shortcode}` : "Instagram Video";
  }

  function cleanTwitterTitle(video) {
    let author = "";
    let text = "";

    try {
      const article = video ? video.closest("article") : document.querySelector("article");
      const root = article || document;

      const userEl = root.querySelector("div[data-testid='User-Name']");
      if (userEl) {
        author = userEl.textContent.split("@")[0].trim();
      }
      const tweetEl = root.querySelector("div[data-testid='tweetText']");
      if (tweetEl) {
        text = tweetEl.textContent.trim();
      }
    } catch (e) {}

    if (!text) {
      const ogTitle = document.querySelector("meta[property='og:title']")?.content || "";
      const raw = ogTitle || document.title || "";
      const m = raw.match(/^(?:.*?(?:on X|on Twitter):\s*["“']?)(.*?)(?:["”']?\s*(?:\/\s*X)?$)/i);
      if (m && m[1]) {
        text = m[1].trim();
      } else {
        text = raw.replace(/\s*\/\s*X$/i, "").replace(/on X:?.*$/i, "").trim();
      }
    }

    text = text.replace(/[\r\n\t]+/g, " ").trim();
    if (text.length > 70) text = text.substring(0, 70).trim();

    if (text && text.toLowerCase() !== "x" && text.toLowerCase() !== "twitter") {
      if (author && !text.toLowerCase().includes(author.toLowerCase())) {
        return `${author} - ${text}`;
      }
      return text;
    }

    if (author) return `${author} - X Video`;
    return "X Video";
  }

  function extractTikTokVideoTarget(rawVideo) {
    const video = rawVideo?.isConnected ? rawVideo : resolveActiveTikTokVideo(rawVideo);
    let author = "";
    let caption = "";
    let videoId = "";
    let permalink = "";
    let directStreamUrl = "";
    let directStreamSize = 0;

    try {
      const itemContainer = video
        ? video.closest(
            'div[data-e2e="recommend-list-item-container"], article[data-e2e="recommend-list-item-container"], div[data-e2e="user-post-item"], div[data-e2e="search-card-video-container"], div[data-e2e="browse-video"], [role="dialog"], div[class*="DivItemContainer"], div[class*="DivContentContainer"], article'
          ) || findBestPlayerContainer(video) || video.parentElement
        : document;
      const root = itemContainer || document;

      // 1. Extract Author Handle (@username) from the active container
      const authorEl = root.querySelector(
        '[data-e2e="video-author-uniqueid"], a[data-e2e="video-author-avatar"], [data-e2e="browse-username"], a[href^="/@"]'
      );
      if (authorEl) {
        const href = authorEl.getAttribute?.("href") || authorEl.closest?.("a")?.getAttribute?.("href") || "";
        const mHandle = href.match(/\/@([^/?#]+)/);
        if (mHandle && mHandle[1]) {
          author = mHandle[1].trim();
        } else if (authorEl.textContent.trim()) {
          author = authorEl.textContent.trim().replace(/^@/, "").split(/\s+/)[0];
        }
      }
      if (!author) {
        const anyUserLink = root.querySelector('a[href*="/@"]');
        if (anyUserLink) {
          const m = (anyUserLink.getAttribute("href") || "").match(/\/@([^/?#]+)/);
          if (m && m[1]) author = m[1].trim();
        }
      }

      // 2. Extract Video Description / Caption from the active container
      const descEl = root.querySelector(
        '[data-e2e="video-desc"], [data-e2e="browse-video-desc"], [data-e2e="new-desc-span"], div[class*="DivVideoInfoContainer"] span, h1[data-e2e="browse-video-desc"]'
      );
      if (descEl && descEl.textContent.trim()) {
        caption = descEl.textContent.replace(/[\r\n\t]+/g, " ").trim();
      }

      // 3. Look for direct permalink <a href*="/video/"> inside the active container
      const videoLinkEl = root.querySelector('a[href*="/video/"], a[href*="/photo/"]');
      if (videoLinkEl && videoLinkEl.href) {
        const mUrl = videoLinkEl.href.match(/(https?:\/\/(?:www\.)?tiktok\.com\/@[^/?#]+\/(?:video|photo)\/(\d{15,22}))/i);
        if (mUrl) {
          permalink = mUrl[1];
          videoId = mUrl[2];
        }
      }

      // 4. Extract 19-digit TikTok Video ID from xgplayer wrapper (id="xgwrapper-0-7381234567890123456")
      if (!videoId && video) {
        const xgWrapper = video.closest('[id*="xgwrapper-"]') || root.querySelector?.('[id*="xgwrapper-"]');
        if (xgWrapper && xgWrapper.id) {
          const mXg = xgWrapper.id.match(/xgwrapper-\d+-(\d{15,22})/);
          if (mXg && mXg[1]) {
            videoId = mXg[1];
          }
        }
      }

      // 5. Check data attributes / IDs inside the player wrapper or container
      if (!videoId && root && root.querySelectorAll) {
        const idNodes = [video, video?.parentElement, root, ...Array.from(root.querySelectorAll("[id], [data-id], [data-video-id], [data-vid]"))];
        for (const node of idNodes) {
          if (!node || !node.getAttribute) continue;
          const candidateAttrs = [
            node.getAttribute("id"),
            node.getAttribute("data-id"),
            node.getAttribute("data-video-id"),
            node.getAttribute("data-vid")
          ];
          for (const val of candidateAttrs) {
            if (!val) continue;
            const mId = val.match(/(?:^|\D)(\d{18,20})(?:\D|$)/);
            if (mId && mId[1]) {
              videoId = mId[1];
              break;
            }
          }
          if (videoId) break;
        }
      }

      // 6. Safely inspect React Fiber tree (WeakSet-guarded, extracts videoId, author, caption, and playAddr/downloadAddr!)
      const fiberInfo = inspectReactFiberMedia(video || root, true);
      if (!videoId && fiberInfo.videoId) videoId = fiberInfo.videoId;
      if (!author && fiberInfo.author) author = fiberInfo.author;
      if (!caption && fiberInfo.caption) caption = fiberInfo.caption;
      if (fiberInfo.directStreamUrl) {
        directStreamUrl = fiberInfo.directStreamUrl;
        directStreamSize = fiberInfo.directStreamSize || extractSizeFromUrl(fiberInfo.directStreamUrl);
      }

      // 7. Check browser address bar if on a video detail URL (/@user/video/123456...)
      const locMatch = window.location.href.match(/(https?:\/\/(?:www\.)?tiktok\.com\/@([^/?#]+)\/(?:video|photo)\/(\d{15,22}))/i);
      if (locMatch) {
        if (!author) author = locMatch[2];
        if (!videoId) {
          videoId = locMatch[3];
          permalink = locMatch[1];
        }
      }

      if (!permalink && videoId) {
        const handle = author || "tiktok";
        permalink = `https://www.tiktok.com/@${handle}/video/${videoId}`;
      }

      // 8. Check direct HTTP stream attached to THIS <video> element
      if (video) {
        const candidateSrcs = [];
        if (video.currentSrc) candidateSrcs.push(video.currentSrc);
        if (video.src) candidateSrcs.push(video.src);
        video.querySelectorAll("source").forEach((s) => {
          if (s.src) candidateSrcs.push(s.src);
        });
        for (const sUrl of candidateSrcs) {
          if (sUrl && !sUrl.startsWith("blob:") && !sUrl.startsWith("data:") && /^https?:\/\//i.test(sUrl)) {
            directStreamUrl = cleanPlaybackUrl(sUrl);
            directStreamSize = extractSizeFromUrl(sUrl) || directStreamSize;
            break;
          }
        }
      }
    } catch (e) {}

    if (caption.length > 70) {
      caption = caption.substring(0, 70).trim();
    }

    let cleanTitle = "TikTok Video";
    if (caption && author) {
      cleanTitle = caption.toLowerCase().includes(author.toLowerCase()) ? caption : `${author} - ${caption}`;
    } else if (caption) {
      cleanTitle = caption;
    } else if (author) {
      cleanTitle = `${author} - TikTok Video${videoId ? " " + videoId.slice(-6) : ""}`;
    } else if (videoId) {
      cleanTitle = `TikTok Video ${videoId}`;
    }

    return {
      video,
      author,
      videoId,
      permalink: permalink || window.location.href,
      directStreamUrl,
      directStreamSize,
      title: cleanTitle
    };
  }

  function getMediaPageTitle(video) {
    const host = window.location.hostname || "";
    if (host.includes("youtube.com")) {
      const h1 = document.querySelector("h1.ytd-watch-metadata, #title h1, ytd-watch-metadata h1");
      if (h1 && h1.textContent.trim()) {
        return h1.textContent.trim();
      }
      const title = document.title || "YouTube Video";
      return title.replace(/- YouTube$/i, "").trim();
    }

    if (host.includes("instagram.com")) {
      return cleanInstagramTitle(video);
    }

    if (host.includes("twitter.com") || host.includes("x.com")) {
      return cleanTwitterTitle(video);
    }

    if (host.includes("tiktok.com")) {
      return extractTikTokVideoTarget(video).title;
    }

    // Generic Web Media Player Title Extraction (Vimeo, Dailymotion, Twitch, Kick, JWPlayer, Video.js, HTML5, etc.)
    try {
      if (video) {
        const vTitle = video.getAttribute("title") || video.getAttribute("aria-label") || video.dataset?.title;
        if (vTitle && vTitle.trim().length > 2 && !/^(video|player|media)$/i.test(vTitle.trim())) {
          return vTitle.trim();
        }
        const playerContainer = video.closest("article, figure, section, main, [class*='player'], [class*='video']");
        if (playerContainer) {
          const heading = playerContainer.querySelector("h1, h2, .video-title, [class*='video-title'], [class*='title']");
          if (heading && heading.textContent.trim().length > 2 && heading.textContent.trim().length < 140) {
            return heading.textContent.replace(/[\r\n\t]+/g, " ").trim();
          }
        }
      }
      const ogTitle = document.querySelector("meta[property='og:title'], meta[name='twitter:title']")?.content;
      if (ogTitle && ogTitle.trim().length > 2) {
        return ogTitle.replace(/\s*[-|–—]\s*(Vimeo|Dailymotion|Twitch|Kick|Streamable|Rumble|Bilibili|Watch|Video).*$/i, "").trim();
      }
      const h1 = document.querySelector("h1");
      if (h1 && h1.textContent.trim().length > 2 && h1.textContent.trim().length < 140) {
        return h1.textContent.replace(/[\r\n\t]+/g, " ").trim();
      }
    } catch (e) {}

    const title = document.title || "Video";
    return title
      .replace(/\s*[-|–—]\s*(YouTube|Instagram|X|Twitter|TikTok|Vimeo|Dailymotion|Twitch|Kick|Streamable|Rumble|Bilibili|Watch).*$/i, "")
      .trim();
  }

  function cleanPlaybackUrl(url) {
    try {
      const u = new URL(url);
      u.searchParams.delete("range");
      u.searchParams.delete("rn");
      u.searchParams.delete("rbuf");
      u.searchParams.delete("bytestart");
      u.searchParams.delete("byteend");
      return u.toString();
    } catch {
      return url.replace(/([?&])(?:range=\d+-\d+|bytestart=\d+|byteend=\d+)(&?)/g, "$1").trim();
    }
  }

  function extractSizeFromUrl(url) {
    if (!url) return 0;
    try {
      const u = new URL(url);
      const clen = u.searchParams.get("clen") || u.searchParams.get("content_length") || u.searchParams.get("size");
      if (clen && parseInt(clen, 10) > 0) {
        return parseInt(clen, 10);
      }
    } catch (e) {
      const m = url.match(/[?&](?:clen|content_length|size)=(\d+)/);
      if (m && parseInt(m[1], 10) > 0) {
        return parseInt(m[1], 10);
      }
    }
    return 0;
  }

  function populateDropdown(video, dropdown, lockedTikTokData = null) {
    const pageTitle = getMediaPageTitle(video);
    dropdown.innerHTML = `<div class="idmlite-dropdown-header">🎬 Medya İndir (IDMLite)</div>`;

    const isYouTube = window.location.hostname.includes("youtube.com") || window.location.href.includes("youtu.be");
    if (isYouTube) {
      // 1. YouTube Dedicated High Quality Video Option
      let bestVideoUrl = window.location.href;
      try {
        const u = new URL(window.location.href);
        const vParam = u.searchParams.get("v");
        if (vParam) {
          bestVideoUrl = `https://www.youtube.com/watch?v=${vParam}`;
        } else if (!u.pathname.startsWith("/shorts/") && !u.pathname.startsWith("/watch")) {
          const ytCard = video.closest("ytd-rich-item-renderer, ytd-video-renderer, ytd-grid-video-renderer, ytd-compact-video-renderer, ytd-reel-item-renderer, ytm-media-item");
          const ytLink = ytCard ? ytCard.querySelector("a[href*='/watch?v='], a[href*='/shorts/']") : null;
          if (ytLink && ytLink.href) {
            const linkUrl = new URL(ytLink.href, window.location.origin);
            const lv = linkUrl.searchParams.get("v");
            bestVideoUrl = lv ? `https://www.youtube.com/watch?v=${lv}` : linkUrl.href;
          }
        }
      } catch (e) {}

      const itemVideo = document.createElement("div");
      itemVideo.className = "idmlite-stream-item";
      itemVideo.innerHTML = `
        <span class="idmlite-stream-title">🎬 En Yüksek Kalite Video İndir (MP4 HD)</span>
        <span class="idmlite-stream-badge">1080p MP4</span>
      `;
      itemVideo._downloadHandler = () => {
        dropdown.classList.remove("idmlite-show");
        triggerDownload(bestVideoUrl, "video", pageTitle, 0);
      };
      itemVideo.addEventListener("click", (e) => {
        e.preventDefault();
        e.stopPropagation();
        itemVideo._downloadHandler();
      }, true);
      dropdown.appendChild(itemVideo);
      return;
    }

    const isInstagram = window.location.hostname.includes("instagram.com");
    if (isInstagram) {
      const igData = lockedTikTokData || extractInstagramVideoTarget(video);

      const itemVideo = document.createElement("button");
      itemVideo.type = "button";
      itemVideo.className = "idmlite-stream-item";
      const vidBadge = igData.directStreamSize > 0 ? formatBytes(igData.directStreamSize) : "Reels MP4";
      itemVideo.innerHTML = `
        <span class="idmlite-stream-title">🎬 Instagram Video İndir (MP4 HD)</span>
        <span class="idmlite-stream-badge">${vidBadge}</span>
      `;
      itemVideo._downloadHandler = () => {
        dropdown.classList.remove("idmlite-show");
        let freshIg = null;
        try { freshIg = extractInstagramVideoTarget(video); } catch { }
        const resolvedData = (freshIg && (freshIg.shortcode || freshIg.directStreamUrl)) ? freshIg : igData;

        let targetUrl = "";
        if (resolvedData.shortcode) {
          targetUrl = `https://www.instagram.com/reel/${resolvedData.shortcode}/`;
        } else if (resolvedData.targetPostUrl && /\/(?:reel|reels|p|tv|share\/reel|share\/p)\/([A-Za-z0-9_-]{7,25})/i.test(resolvedData.targetPostUrl)) {
          targetUrl = resolvedData.targetPostUrl;
        } else if (resolvedData.directStreamUrl) {
          targetUrl = resolvedData.directStreamUrl;
        } else {
          targetUrl = window.location.href;
        }

        const finalStreamUrl = resolvedData.directStreamUrl || undefined;
        const finalSize = resolvedData.directStreamSize || 0;
        const finalTitle = resolvedData.title && resolvedData.title !== "Instagram Video" ? resolvedData.title : pageTitle;

        triggerDownload(targetUrl, "video", finalTitle, finalSize, finalStreamUrl);
      };
      itemVideo.addEventListener("click", (e) => {
        e.preventDefault();
        e.stopPropagation();
        itemVideo._downloadHandler();
      }, true);
      dropdown.appendChild(itemVideo);
      return;
    }

    const isTwitter = window.location.hostname.includes("x.com") || window.location.hostname.includes("twitter.com");
    if (isTwitter) {
      // Find the specific tweet article enclosing THIS video
      const tweetArticle = video.closest("article");
      const statusLink = tweetArticle ? tweetArticle.querySelector("a[href*='/status/']") : null;
      let tweetUrl = window.location.href;
      if (statusLink && statusLink.href && statusLink.href.includes("/status/")) {
        tweetUrl = statusLink.href;
      }

      const realTitle = cleanTwitterTitle(video);

      let directStreamUrl = "";
      let directStreamSize = 0;

      // Check if video element itself has a direct progressive MP4 source
      const src = video.currentSrc || video.src;
      if (src && !src.startsWith("blob:") && !src.startsWith("data:") && src.includes(".mp4")) {
        directStreamUrl = cleanPlaybackUrl(src);
        directStreamSize = extractSizeFromUrl(src);
      }

      const itemVideo = document.createElement("div");
      itemVideo.className = "idmlite-stream-item";
      const vidBadge = directStreamSize > 0 ? formatBytes(directStreamSize) : "X Video MP4";
      itemVideo.innerHTML = `
        <span class="idmlite-stream-title">🎬 X (Twitter) Video İndir (MP4)</span>
        <span class="idmlite-stream-badge">${vidBadge}</span>
      `;
      itemVideo._downloadHandler = () => {
        dropdown.classList.remove("idmlite-show");
        triggerDownload(tweetUrl, "video", realTitle, directStreamSize, directStreamUrl);
      };
      itemVideo.addEventListener("click", (e) => {
        e.preventDefault();
        e.stopPropagation();
        itemVideo._downloadHandler();
      }, true);
      dropdown.appendChild(itemVideo);
      return;
    }

    const isTikTok = window.location.hostname.includes("tiktok.com");
    if (isTikTok) {
      const liveVid = (video && video.isConnected) ? video : (document.querySelector("video") || video);
      const ttData = lockedTikTokData && (lockedTikTokData.videoId || lockedTikTokData.directStreamUrl)
        ? lockedTikTokData
        : extractTikTokVideoTarget(liveVid);

      const itemVideo = document.createElement("button");
      itemVideo.type = 'button';
      itemVideo.className = "idmlite-stream-item";
      const vidBadge = ttData.directStreamSize > 0 ? formatBytes(ttData.directStreamSize) : "TikTok MP4";
      itemVideo.innerHTML = `
        <span class="idmlite-stream-title">🎬 En Yüksek Kalitede İndir (TikTok MP4 HD)</span>
        <span class="idmlite-stream-badge">${vidBadge}</span>
      `;
      itemVideo._downloadHandler = () => {
        dropdown.classList.remove("idmlite-show");
        const freshData = extractTikTokVideoTarget(liveVid);
        if (ttData.videoId && freshData.videoId && ttData.videoId !== freshData.videoId) {
          showToast('Video değişti. Lütfen indirme menüsünü yeniden açın.'); return;
        }
        const chosen = (freshData.videoId || freshData.directStreamUrl) ? freshData : ttData;
        if (!/\/(?:video|photo)\/\d{15,22}/.test(chosen.permalink || '') && !/^https?:\/\//i.test(chosen.directStreamUrl || '')) {
          showToast('Video bağlantısı belirlenemedi. Videoyu ayrı sayfada açıp tekrar deneyin.'); return;
        }
        const finalUrl = chosen.permalink.includes("/video/") || chosen.permalink.includes("/photo/")
          ? chosen.permalink
          : (chosen.directStreamUrl || chosen.permalink || window.location.href);
        triggerDownload(
          finalUrl,
          "video",
          chosen.title,
          chosen.directStreamSize,
          chosen.directStreamUrl || undefined
        );
      };
      itemVideo.addEventListener("click", (e) => {
        e.preventDefault();
        e.stopPropagation();
        itemVideo._downloadHandler();
      }, true);
      dropdown.appendChild(itemVideo);
      return;
    }

    // Generic Website: Find the SINGLE best video stream
    const candidateStreams = [];

    // Direct video element sources
    const src = video.currentSrc || video.src;
    if (src && !src.startsWith("blob:") && !src.startsWith("data:")) {
      candidateStreams.push({ url: src, type: "direct", size: extractSizeFromUrl(src) });
    }

    // Source tags inside video
    const sourceElements = video.querySelectorAll("source");
    sourceElements.forEach((s) => {
      const sUrl = s.src;
      if (sUrl && !candidateStreams.some((x) => x.url === sUrl) && !sUrl.startsWith("blob:")) {
        candidateStreams.push({ url: sUrl, type: "source", size: extractSizeFromUrl(sUrl) });
      }
    });

    // Sniffed streams from background (HLS m3u8, DASH mpd, MP4, etc.)
    detectedStreams.forEach((sniffed) => {
      const cleaned = cleanPlaybackUrl(sniffed.url);
      if (!candidateStreams.some((x) => x.url === cleaned)) {
        candidateStreams.push({ url: cleaned, type: "sniffed", size: sniffed.size || extractSizeFromUrl(sniffed.url) });
      }
    });

    // Active network resources (critical for blob: players)
    if (window.performance && typeof window.performance.getEntriesByType === "function") {
      try {
        const perfEntries = window.performance.getEntriesByType("resource");
        for (let i = perfEntries.length - 1; i >= 0; i--) {
          const entry = perfEntries[i];
          const eUrl = entry.name;
          if (!eUrl || eUrl.startsWith("blob:") || eUrl.startsWith("data:")) continue;

          // Generic MP4 / M3U8 / WebM / MKV / TS / MPD video resources
          if (eUrl.match(/\.(mp4|m3u8|mpd|webm|mkv|ts|flv|mov)(\?.*)?$/i)) {
            const cleaned = cleanPlaybackUrl(eUrl);
            if (!candidateStreams.some((x) => x.url === cleaned)) {
              candidateStreams.push({ url: cleaned, type: "perf_resource", size: extractSizeFromUrl(eUrl) });
            }
          }
        }
      } catch (e) {}
    }

    // Check OpenGraph meta video tags as reliable fallback
    const metaVideo = document.querySelector("meta[property='og:video'], meta[property='og:video:secure_url'], meta[property='og:video:url'], meta[name='twitter:player:stream']");
    if (metaVideo && metaVideo.content && !candidateStreams.some((x) => x.url === metaVideo.content)) {
      candidateStreams.push({ url: metaVideo.content, type: "meta_video", size: 0 });
    }

    // Select the single highest quality video URL
    let bestVideo = null;
    const directVideoStreams = candidateStreams.filter(s =>
      s.url.includes(".mp4") || s.url.includes(".webm") || s.url.includes(".mkv") || s.url.includes(".mov") || s.url.includes(".flv")
    );
    if (directVideoStreams.length > 0) {
      directVideoStreams.sort((a, b) => (b.size || 0) - (a.size || 0));
      bestVideo = directVideoStreams[0];
    }

    if (!bestVideo) {
      const hlsStream = candidateStreams.find(s => s.url.includes(".m3u8") || s.url.includes(".mpd"));
      if (hlsStream) {
        bestVideo = hlsStream;
      }
    }

    if (!bestVideo && candidateStreams.length > 0) {
      bestVideo = candidateStreams[0];
    }

    const targetVideoUrl = bestVideo ? bestVideo.url : window.location.href;
    const targetVideoSize = bestVideo ? (bestVideo.size || extractSizeFromUrl(bestVideo.url)) : 0;

    // Single Best Video Option
    const rowVideo = document.createElement("div");
    rowVideo.className = "idmlite-stream-item";
    const videoBadgeText = targetVideoSize > 0 ? formatBytes(targetVideoSize) : "MP4 HD";
    rowVideo.innerHTML = `
      <span class="idmlite-stream-title">🎬 Video Download (MP4)</span>
      <span class="idmlite-stream-badge">${videoBadgeText}</span>
    `;
    rowVideo._downloadHandler = () => {
      dropdown.classList.remove("idmlite-show");
      triggerDownload(targetVideoUrl, "video", pageTitle, targetVideoSize);
    };
    rowVideo.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      rowVideo._downloadHandler();
    }, true);
    dropdown.appendChild(rowVideo);
  }

  function triggerDownload(url, type = "video", title = "", knownSize = 0, resolvedStreamUrl = null) {
    const rawTitle = title || getMediaPageTitle() || "Media";
    const cleanTitle = rawTitle.replace(/[/\\?%*:|"<>]/g, "_").trim();
    
    // Always use .mp4 for video/stream downloads (never .ts or .mp3)
    let fileName = cleanTitle + ".mp4";

    let calculatedSize = knownSize || extractSizeFromUrl(url);
    if (!calculatedSize || calculatedSize <= 0) {
      detectedStreams.forEach((s) => {
        if (s.size > 0 && (s.url === url || url.includes(s.url) || s.url.includes(url))) {
          calculatedSize = s.size;
        }
      });
    }

    chrome.runtime.sendMessage({
      action: "downloadUrl",
      payload: {
        url: url,
        resolvedStreamUrl: resolvedStreamUrl || undefined,
        fileName: fileName,
        fileSize: calculatedSize > 0 ? calculatedSize : undefined,
        referer: window.location.href,
        category: "Video",
        autoStart: false
      }
    }, (res) => {
      if (res && res.success) {
        showToast("🚀 Video IDMLite indirme penceresine aktarıldı!");
      } else {
        showToast("⚠️ IDMLite masaüstü uygulamasına bağlanılamadı. Uygulamanın açık olduğundan emin olun.");
      }
    });
  }

  function showToast(text) {
    const existing = document.querySelector(".idmlite-toast");
    if (existing) existing.remove();

    const toast = document.createElement("div");
    toast.className = "idmlite-toast";
    toast.innerHTML = `<span>${text}</span>`;
    document.body.appendChild(toast);

    setTimeout(() => {
      toast.style.transition = "opacity 0.4s ease";
      toast.style.opacity = "0";
      setTimeout(() => toast.remove(), 400);
    }, 3500);
  }

  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return "";
    const units = ["B", "KB", "MB", "GB"];
    let i = 0;
    while (bytes >= 1024 && i < units.length - 1) {
      bytes /= 1024;
      i++;
    }
    return bytes.toFixed(1) + " " + units[i];
  }

  // Downloadable file extensions regex (Archives, Executables, Audio, Video, Documents, Images)
  const DOWNLOAD_EXTENSIONS_REGEX = /\.(zip|rar|7z|tar|gz|tgz|bz2|tbz2|xz|txz|iso|dmg|bin|cue|img|vhd|vhdx|qcow2|exe|msi|apk|deb|rpm|appx|msix|pkg|jar|mp4|mkv|avi|mov|wmv|flv|webm|m4v|ts|mp3|wav|flac|aac|m4a|ogg|opus|wma|pdf|epub|mobi|doc|docx|xls|xlsx|ppt|pptx|txt|csv|rtf|odt|ods|odp|torrent|crx|xpi|jpg|jpeg|png|webp|gif|svg|bmp|ico|avif|tiff|tif|heic|heif|psd|ai|eps)(\?.*)?$/i;
  const DOWNLOAD_PARAM_REGEX = /(?:^|[?&])(?:download|export=download|response-content-disposition=attachment|action=download|dl=1)(?:[&=]|$)/i;

  function normalizeGitHubRawUrl(rawUrl) {
    if (!rawUrl || typeof rawUrl !== "string") return rawUrl;
    try {
      const u = new URL(rawUrl, window.location.origin);
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

  function handleGlobalLinkClick(e) {
    if (!isAutoIntercept) return;
    // Download links may be landing pages, POST forms or single-use redirects.
    // Let the browser resolve them; background.js captures the real download.
    // GitHub's blob-backed raw button is the only DOM interception exception.
    if (!/^(www\.)?github\.com$/i.test(window.location.hostname)) return;
    // Only capture primary left clicks
    if (e.button !== 0) return;
    // Allow Alt key to bypass IDMLite (standard downloader shortcut)
    if (e.altKey) return;

    const host = (window.location.hostname || "").toLowerCase();
    const isGitHubHost = host === "github.com" || host === "www.github.com";

    // Special handling for GitHub's "Download raw file" button (<button data-testid="download-raw-button"> or <a data-testid="raw-button">)
    if (isGitHubHost) {
      const rawBtn = e.target.closest(
        '[data-testid="download-raw-button"], [aria-label="Download raw file"], a[data-testid="raw-button"]'
      );
      if (rawBtn) {
        const rawCandidate = rawBtn.href || window.location.href;
        const normalizedRaw = normalizeGitHubRawUrl(rawCandidate);
        if (normalizedRaw && normalizedRaw.includes("raw.githubusercontent.com")) {
          e.preventDefault();
          e.stopPropagation();
          e.stopImmediatePropagation();

          let fileName = "";
          try {
            const u = new URL(normalizedRaw);
            const seg = u.pathname.split("/").filter(Boolean).pop();
            if (seg) fileName = decodeURIComponent(seg);
          } catch (err) {}

          showToast("🚀 IDMLite İndirme Penceresi Açılıyor...");
          chrome.runtime.sendMessage({
            action: "downloadUrl",
            payload: {
              url: normalizedRaw,
              fileName: fileName || undefined,
              referer: window.location.href,
              direct: true,
              autoStart: false
            }
          }, (res) => {
            if (!res || !res.success) {
              showToast("⚠️ IDMLite masaüstü uygulamasına bağlanılamadı. Uygulamanın açık olduğundan emin olun.");
            }
          });
          return;
        }
      }
    }

    const anchor = e.target.closest("a");
    if (!anchor || !anchor.href) return;

    // Ignore clicks inside IDMLite's own floating UI
    if (anchor.closest(".idmlite-video-badge-container, .idmlite-dropdown-menu")) return;

    let href = anchor.href.trim();
    if (!href || href.startsWith("javascript:") || href.startsWith("#") || href.startsWith("blob:") || href.startsWith("data:") || href.startsWith("mailto:") || href.startsWith("tel:")) {
      return;
    }

    try {
      const urlObj = new URL(href);
      if (urlObj.protocol !== "http:" && urlObj.protocol !== "https:") return;

      // Google Drive: let the browser start the real download; the background download
      // trigger then forwards the final URL + exact name/size/type to IDMLite.
      if (IDMLITE_BROWSER_TRIGGER_HOSTS.test(urlObj.hostname)) return;

      const hasDownloadAttr = anchor.hasAttribute("download");
      const isDownloadExt = DOWNLOAD_EXTENSIONS_REGEX.test(urlObj.pathname);
      const isDownloadParam = DOWNLOAD_PARAM_REGEX.test(urlObj.search) || /[?&]raw=(?:1|true)(?:&|$)/i.test(urlObj.search);

      const urlHost = urlObj.hostname.toLowerCase();
      if (urlHost === "github.com" || urlHost === "www.github.com") {
        const isBlobPageLink = /^\/[^\/]+\/[^\/]+\/blob\//i.test(urlObj.pathname);
        const isRawPageLink = /^\/[^\/]+\/[^\/]+\/raw\//i.test(urlObj.pathname);

        // On GitHub repository file listings, normal clicks on `/blob/.../file.txt` navigate to the code viewer page.
        // Only intercept if it's a `/raw/` link, has `download` attribute, or has `?raw=1` / `?download=1`.
        if (isBlobPageLink && !hasDownloadAttr && !isDownloadParam) {
          return;
        }
        if (isBlobPageLink || isRawPageLink) {
          href = normalizeGitHubRawUrl(href);
        }
      }

      if (hasDownloadAttr || isDownloadExt || isDownloadParam) {
        // Crush the browser's download trigger immediately!
        e.preventDefault();
        e.stopPropagation();
        e.stopImmediatePropagation();

        let fileName = anchor.getAttribute("download") || "";
        if (!fileName) {
          const seg = new URL(href).pathname.split("/").pop();
          if (seg && seg.includes(".")) {
            fileName = decodeURIComponent(seg);
          }
        }

        showToast("🚀 IDMLite İndirme Penceresi Açılıyor...");

        chrome.runtime.sendMessage({
          action: "downloadUrl",
          payload: {
            url: href,
            fileName: fileName || undefined,
            referer: window.location.href,
            direct: true,
            autoStart: false
          }
        }, (res) => {
          if (!res || !res.success) {
            showToast("⚠️ IDMLite masaüstü uygulamasına bağlanılamadı. Uygulamanın açık olduğundan emin olun.");
          }
        });
      }
    } catch (err) {
      // Ignore URL parsing errors
    }
  }

  // Register capturing event listener on document
  document.addEventListener("click", handleGlobalLinkClick, true);

  // Start after document loads
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initVideoObserver);
  } else {
    initVideoObserver();
  }
})();

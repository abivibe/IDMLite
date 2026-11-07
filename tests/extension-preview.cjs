const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const source = fs.readFileSync(require('node:path').join(__dirname, '../extension/background.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
function fixture(session = {}) {
  const listeners = {}, calls = { previews: 0, downloads: [], previewUrls: [] }, clock = { now: Date.now() };
  let release, hold = true;
  const event = name => ({ addListener(fn) { listeners[name] = fn; } });
  class ClockDate extends Date { constructor(...args) { super(...(args.length ? args : [clock.now])); } static now() { return clock.now; } }
  const chrome = {
    runtime: { onInstalled: event('install'), onMessage: event('message'), sendNativeMessage(n, m, cb) { cb({ success: true, token: 'DUMMY_AUTH', port: 49111 }); } },
    storage: { onChanged: event('storage'), local: { get: async () => ({}), set() {} }, session: { get: async () => structuredClone(session), set: async data => Object.assign(session, structuredClone(data)) } },
    cookies: { getAll: async () => [], onChanged: event('cookies') },
    tabs: { onRemoved: event('removed'), onUpdated: event('updated'), sendMessage: async () => {} },
    webRequest: { onResponseStarted: event('headers') }, downloads: { onDeterminingFilename: event('filename'), onCreated: event('created'), setUiOptions() {} }
  };
  const context = vm.createContext({ console, URL, Date: ClockDate, AbortController, crypto: require('node:crypto').webcrypto,
    navigator: { userAgent: 'fixture' }, chrome, setTimeout: () => 1, clearTimeout() {}, setInterval() {},
    fetch: async (url, options) => {
      if (url.endsWith('/api/status')) return { ok: true, json: async () => ({ online: true }) };
      if (url.endsWith('/api/preview')) {
        calls.previews++; const request = JSON.parse(options.body);
        calls.previewUrls.push(request.url);
        assert.equal(options.headers['X-IDMLite-Token'], 'DUMMY_AUTH');
        if (hold) await new Promise(resolve => { release = resolve; });
        return { ok: true, json: async () => ({ success: true, preview: { IsSuccess: true, OriginalUrl: request.url, FileSize: 90000,
          Streams: [{ Url: request.url, IsAudioOnly: false, FileSize: 90000, FileSizeIsEstimated: true, Quality: '1080p' }] } }) };
      }
      calls.downloads.push(JSON.parse(options.body)); return { ok: true, json: async () => ({ success: true }) };
    }
  });
  vm.runInContext(source, context);
  const page = 'https://www.youtube.com/watch?v=fixture';
  return { context, calls, session, clock, listeners, page,
    warm: () => context.warmMediaPreview(7, page),
    release() { hold = false; release(); },
    download: () => new Promise(resolve => listeners.message({ action: 'downloadUrl', payload: { url: page, fileName: 'fixture.mp4' } }, { tab: { id: 7, url: page } }, resolve))
  };
}
(async () => {
  let passed = 0;
  const check = (condition, name) => { assert.ok(condition, name); console.log('PASS: ' + name); passed++; };
  const f = fixture(); await tick();
  const first = f.warm(), second = f.warm(); await tick();
  check(f.calls.previews === 1, 'concurrent warm requests share one authenticated metadata job');
  await f.download();
  check(f.calls.downloads.length === 1 && !f.calls.downloads[0].preview, 'download handoff does not wait for pending prefetch');
  f.release(); await Promise.all([first, second]);
  await f.download();
  const ready = f.calls.downloads.at(-1);
  check(ready.fileSize === 90000 && ready.fileSizeIsEstimated && ready.preview && ready.previewCachedAtUtc, 'ready metadata and estimated-size provenance travel with download request');
  await f.warm(); check(f.calls.previews === 1, 'warm cache avoids another extractor request');
  const restored = fixture(f.session); await tick(); await restored.download();
  check(!!restored.calls.downloads[0].preview && restored.calls.previews === 0, 'session metadata survives MV3 worker restart');
  f.clock.now += 151000; await f.warm(); check(f.calls.previews === 2, 'expired metadata is refreshed');
  f.listeners.cookies({}); await f.download();
  check(!f.calls.downloads.at(-1).preview, 'cookie changes invalidate cached account metadata');
  await f.context.warmMediaPreview(7, 'https://youtube.com.evil.test/watch?v=fixture');
  check(f.calls.previews === 2, 'untrusted lookalike page does not trigger extraction');
  await f.warm(); f.listeners.updated(7, { status: 'loading', url: 'https://www.youtube.com/watch?v=second' }); await f.download();
  check(!f.calls.downloads.at(-1).preview, 'navigation removes ready metadata for the old tab page');
  const moving = fixture(); await tick(); const oldRequest = moving.warm(); await tick();
  moving.listeners.updated(7, { status: 'loading', url: 'https://www.youtube.com/watch?v=second' }); moving.release(); await oldRequest; await moving.download();
  check(!moving.calls.downloads.at(-1).preview, 'late response after navigation cannot repopulate stale metadata');
  const feed = fixture(); await tick();
  const feedResult = new Promise(resolve => feed.listeners.message({ action: 'warmPreview', url: 'https://www.tiktok.com/@alice/video/7381234567890123456' }, { tab: { id: 9, url: 'https://www.tiktok.com/foryou' } }, resolve));
  await tick(); feed.release(); await feedResult;
  check(feed.calls.previewUrls[0] === 'https://www.tiktok.com/@alice/video/7381234567890123456', 'TikTok feed warms the visible video rather than the generic feed URL');
  const rejected = fixture(); await tick();
  await new Promise(resolve => rejected.listeners.message({ action: 'warmPreview', url: 'https://www.tiktok.com/@alice/video/7381234567890123456' }, { tab: { id: 9, url: 'https://evil.example.test/' } }, resolve));
  check(rejected.calls.previews === 0, 'foreign page cannot supply a TikTok warm target');
  const headers = (statusCode, url, values) => f.context.completeMediaSize({ statusCode, url, responseHeaders: Object.entries(values).map(([name,value]) => ({ name, value })) });
  check(headers(206, 'https://cdn.example/file.mp4', { 'Content-Length': '1000', 'Content-Range': 'bytes 0-999/123456' }) === 123456, 'Content-Range total replaces partial response length');
  check(headers(206, 'https://cdn.example/file.mp4', { 'Content-Length': '1000' }) === 0, 'partial length without entity total is never a full size');
  check(headers(200, 'https://cdn.example/master.m3u8', { 'Content-Length': '2000' }) === 0, 'manifest byte length is never video size');
  check(headers(200, 'https://cdn.example/part.ts', { 'Content-Length': '3000' }) === 0, 'single HLS fragment byte length is never video size');
  check(headers(206, 'https://x.googlevideo.com/videoplayback?clen=7654321', { 'Content-Length': '1000' }) === 7654321, 'Google track length is read independently of downloaded range');
  check(headers(200, 'https://cdn.example/file.mp4', { 'Content-Length': '7000', 'Content-Encoding': 'gzip' }) === 0, 'compressed response length is not accepted as file length');
  console.log(`RESULT: ${passed} passed`);
})().catch(error => { console.error(error); process.exitCode = 1; });

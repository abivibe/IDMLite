// Real Chromium extension integration in an isolated profile. Requires Chrome
// supporting the DevTools Extensions.loadUnpacked testing API.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(check, label) {
  for (let i = 0; i < 100; i++) { if (await check()) return; await pause(100); }
  throw new Error('Timed out: ' + label);
}
class Protocol {
  constructor(process) {
    this.process = process; this.next = 0; this.pending = new Map(); this.buffer = Buffer.alloc(0);
    process.stdio[4].on('data', data => {
      this.buffer = Buffer.concat([this.buffer, data]);
      let end;
      while ((end = this.buffer.indexOf(0)) !== -1) {
        const item = JSON.parse(this.buffer.subarray(0, end).toString()); this.buffer = this.buffer.subarray(end + 1);
        if (item.id && this.pending.has(item.id)) {
          const { resolve, reject, timer } = this.pending.get(item.id); clearTimeout(timer); this.pending.delete(item.id);
          item.error ? reject(new Error(JSON.stringify(item.error))) : resolve(item.result);
        }
        else if (item.method && this.onEvent) this.onEvent(item);
      }
    });
  }
  send(method, params = {}, sessionId) {
    const id = ++this.next;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('Protocol timeout: ' + method)); }, 15000);
      this.pending.set(id, { resolve, reject, timer });
      this.process.stdio[3].write(JSON.stringify({ id, method, params, sessionId }) + '\0');
    });
  }
  async evaluate(session, expression) {
    const reply = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, session);
    if (reply.exceptionDetails) throw new Error(JSON.stringify(reply.exceptionDetails));
    return reply.result.value;
  }
}
module.exports = { Protocol };
if (require.main === module) (async () => {
  let accepted = false, posts = 0, previewPosts = 0, downloadRequest;
  const server = http.createServer(async (request, response) => {
    response.setHeader('Access-Control-Allow-Origin', '*');
    response.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-IDMLite-Token');
    response.setHeader('Access-Control-Allow-Private-Network', 'true');
    if (request.method === 'OPTIONS') { response.writeHead(204); response.end(); return; }
    if (request.url === '/api/status') { response.end(JSON.stringify({ online: true, appName: 'IDMLite', version: 'fixture' })); return; }
    if (request.url === '/api/preview') {
      assert.equal(request.headers['x-idmlite-token'], 'DUMMY_BROWSER_AUTH');
      let body = ''; for await (const part of request) body += part;
      const input = JSON.parse(body); previewPosts++;
      response.end(JSON.stringify({ success: true, preview: { IsSuccess: true, OriginalUrl: input.url, FileSize: 123456,
        Streams: [{ Url: input.url, FileSize: 123456, FileSizeIsEstimated: true, Quality: '1080p' }] } })); return;
    }
    if (request.url === '/api/download') {
      if (request.headers['x-idmlite-token'] !== 'DUMMY_BROWSER_AUTH') { response.writeHead(401); response.end('{}'); return; }
      let body = ''; for await (const part of request) body += part;
      downloadRequest = JSON.parse(body); posts++;
      await until(() => accepted, 'fixture acceptance');
      response.end(JSON.stringify({ success: true })); return;
    }
    if (request.url === '/redirect') {
      response.writeHead(302, { Location: '/file?signature=a%2Bb%2Fc&token=d%3D' }); response.end(); return;
    }
    if (request.url === '/file?signature=a%2Bb%2Fc&token=d%3D') {
      response.setHeader('Content-Type', 'application/octet-stream');
      response.setHeader('Content-Disposition', 'attachment; filename="idmlite-browser-test.bin"');
      response.end(Buffer.alloc(256 * 1024, 42)); return;
    }
    response.setHeader('Content-Type', 'text/html');
    response.end('<!doctype html><a id="file" href="/redirect">Download fixture</a>');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  const profile = path.join(root, 'installer/verification/browser-profile-' + Date.now());
  const downloads = path.join(profile, 'downloads');
  fs.mkdirSync(path.join(profile, 'Default'), { recursive: true }); fs.mkdirSync(downloads);
  fs.writeFileSync(path.join(profile, 'Default/Preferences'), JSON.stringify({ download: { default_directory: downloads, prompt_for_download: true } }));
  const extensionCopy = path.join(profile, 'extension-fixture');
  fs.cpSync(path.join(root, 'extension'), extensionCopy, { recursive: true });
  const background = path.join(extensionCopy, 'background.js');
  // Never contact the user's installed native host or their running downloader.
  fs.writeFileSync(background, `chrome.runtime.sendNativeMessage = (name, message, callback) => callback(message.action === 'getAuth' ? { success: true, token: 'DUMMY_BROWSER_AUTH', port: ${port} } : { success: false });\n` + fs.readFileSync(background, 'utf8'));
  const brave = process.argv.includes('--brave');
  const executable = brave ? 'C:/Program Files/BraveSoftware/Brave-Browser/Application/brave.exe' : 'C:/Program Files/Google/Chrome/Application/chrome.exe';
  const browser = spawn(executable, [
    '--headless=new', '--no-first-run', '--no-default-browser-check', '--enable-unsafe-extension-debugging',
    '--remote-debugging-pipe', '--user-data-dir=' + profile, 'about:blank'
  ], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe', 'pipe', 'pipe'] });
  browser.stderr.on('data', () => {});
  const cdp = new Protocol(browser);
  try {
    const version = await cdp.send('Browser.getVersion');
    const { id } = await cdp.send('Extensions.loadUnpacked', { path: extensionCopy });
    assert.equal(id, 'gdcnkipjepgidojhonbcfpgagchjcdci');
    let worker;
    await until(async () => { worker = (await cdp.send('Target.getTargets')).targetInfos.find(t => t.type === 'service_worker' && t.url.startsWith('chrome-extension://' + id + '/')); return !!worker; }, 'extension worker');
    const workerSession = (await cdp.send('Target.attachToTarget', { targetId: worker.targetId, flatten: true })).sessionId;
    await pause(500);
    await cdp.evaluate(workerSession, `(async () => {
      await chrome.storage.local.set({ autoIntercept: true, ipcPort: ${port} });
      currentIpcPort = ${port}; isAutoIntercept = true;
      globalThis.__ui = []; globalThis.__filenameReleases = 0;
      const originalUI = chrome.downloads.setUiOptions.bind(chrome.downloads);
      chrome.downloads.setUiOptions = (...args) => { __ui.push(args[0].enabled); return originalUI(...args); };
      const originalCapture = crushAndIntercept;
      crushAndIntercept = (item, suggest) => originalCapture(item, suggest && ((...args) => { __filenameReleases++; return suggest(...args); }));
      applyDownloadUiSuppression();
    })()`);
    await cdp.evaluate(workerSession, `warmMediaPreview(77, 'https://www.youtube.com/watch?v=fixture')`);
    await cdp.evaluate(workerSession, `warmMediaPreview(77, 'https://www.youtube.com/watch?v=fixture')`);
    assert.equal(previewPosts, 1);
    assert.equal(await cdp.evaluate(workerSession, `chrome.storage.session.get('mediaPreviews').then(data => data.mediaPreviews[0][1].preview.Streams[0].FileSize)`), 123456);
    const target = await cdp.send('Target.createTarget', { url: `http://127.0.0.1:${port}/page` });
    const page = (await cdp.send('Target.attachToTarget', { targetId: target.targetId, flatten: true })).sessionId;
    await until(async () => cdp.evaluate(page, "!!document.querySelector('#file')"), 'fixture page');
    await cdp.send('Runtime.evaluate', { expression: "document.querySelector('#file').click()", userGesture: true }, page);
    await until(() => posts > 0, 'desktop handoff');
    assert.equal(await cdp.evaluate(workerSession, '__filenameReleases'), 0, 'Filename gate released before desktop accepted');
    assert.equal(await cdp.evaluate(workerSession, '__ui.at(-1)'), false);
    assert.equal(fs.existsSync(path.join(downloads, 'idmlite-browser-test.bin')), false);
    accepted = true;
    await until(async () => (await cdp.evaluate(workerSession, '__filenameReleases')) === 1, 'filename release');
    await until(async () => (await cdp.evaluate(workerSession, 'chrome.downloads.search({})')).length === 0, 'browser history cleanup');
    assert.equal(posts, 1);
    assert.equal(downloadRequest.url, `http://127.0.0.1:${port}/file?signature=a%2Bb%2Fc&token=d%3D`);
    assert.equal(downloadRequest.direct, true);
    console.log('PASS: redirect handoff preserves signed query bytes and sends the final URL');
    assert.equal(await cdp.evaluate(workerSession, '__ui.at(-1)'), false);
    assert.equal(fs.existsSync(path.join(downloads, 'idmlite-browser-test.bin')), false);
    const maliciousUrl = 'https://example.test/file"><img id="fixture-injected">.mp4';
    const popup = await cdp.send('Target.createTarget', { url: 'about:blank', background: true });
    await cdp.evaluate(workerSession, `chrome.tabs.query({}).then(tabs => tabs.forEach(tab => tabMediaStreams.set(tab.id, [{ url: ${JSON.stringify(maliciousUrl)}, contentType: 'video/<b id="fixture-sub">dummy</b>', size: 0 }])))`);
    const popupSession = (await cdp.send('Target.attachToTarget', { targetId: popup.targetId, flatten: true })).sessionId;
    await cdp.send('Page.enable', {}, popupSession);
    await cdp.send('Page.navigate', { url: 'chrome-extension://' + id + '/popup.html' }, popupSession);
    await until(() => cdp.evaluate(popupSession, `!!document.querySelector('.media-title')`), 'popup media fixture');
    assert.equal(await cdp.evaluate(popupSession, `document.querySelector('.media-title').title`), maliciousUrl);
    assert.equal(await cdp.evaluate(popupSession, `document.querySelectorAll('#fixture-injected, #fixture-sub').length`), 0);
    fs.writeFileSync(path.join(root, brave ? 'installer/verification/brave-download-1.0.1.json' : 'installer/verification/browser-download-1.0.1.json'), JSON.stringify({ browser: version.product, capturedOnce: true, filenameHeldUntilAcceptance: true, browserUiDisabled: true, noBrowserFile: true, browserEntryErased: true, authenticatedDummyNativeHandoff: true, popupHtmlInjectionRejected: true }, null, 2));
    console.log('PASS: real Chromium extension authenticates handoff, holds filename, hides browser UI, transfers once and cancels/erases browser download');
    console.log('PASS: real Chromium popup displays hostile URL and content type as text without injecting elements');
    console.log('PASS: real Chromium authenticates background prefetch and keeps ready metadata in session cache');
  } finally {
    try { await cdp.send('Browser.close'); } catch { browser.kill(); }
    server.closeAllConnections(); server.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });


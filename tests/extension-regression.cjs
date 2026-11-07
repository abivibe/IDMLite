const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { randomUUID } = require('node:crypto');
const source = fs.readFileSync(require('node:path').join(__dirname, '../extension/background.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
const event = () => ({ addListener() {} });
function fixture({ holdCancellation = false, saved = null, alreadyComplete = false } = {}) {
  const calls = { pause: [], resume: [], cancel: [], posts: [], native: 0, ui: [] };
  let finishCancellation;
  let accept;
  let mode = 'pending';
  let state = alreadyComplete ? "complete" : "in_progress", paused = !!saved;
  const items = saved ? { "downloadHandoff:1": saved } : {};
  const context = vm.createContext({
    console, URL, AbortController, navigator: { userAgent: 'fixture' }, crypto: { randomUUID },
    setTimeout: () => 1, clearTimeout() {}, setInterval() {},
    chrome: {
      runtime: { id: 'extension-id', onInstalled: event(), onMessage: event(), sendNativeMessage(name, message, cb) { calls.native++; cb(message.action === "getAuth" ? { success: true, token: "dummy" } : { success: false, online: false }); } },
      storage: { onChanged: event(), local: { get: async () => ({}), set() {} }, session: {
        get: async () => ({ ...items }), set: async updates => Object.assign(items, updates), remove: async key => delete items[key]
      } },
      cookies: { getAll: async () => [] },
      tabs: { onRemoved: event(), onUpdated: event(), sendMessage() {} },
      webRequest: { onSendHeaders: event(), onResponseStarted: event() },
      downloads: {
        onDeterminingFilename: event(), onCreated: event(), setUiOptions(options) { calls.ui.push(options.enabled); },
        pause: async id => { calls.pause.push(id); paused = true; }, resume: async id => { calls.resume.push(id); paused = false; },
        async cancel(id) { calls.cancel.push(id); if (holdCancellation) await new Promise(resolve => { finishCancellation = resolve; }); state = "interrupted"; },
        async search({id}) { return [{id, state, paused, error: "USER_CANCELED", startTime: saved?.startTime, url: "https://example.test/fixture.bin"}]; },
        async erase() {}, async removeFile() {}
      }
    },
    fetch: async (url, opts) => {
      if (url.endsWith('/status')) return { ok: true, json: async () => ({ online: true }) };
      calls.posts.push(JSON.parse(opts.body));
      if (mode === 'pending') await new Promise(resolve => { accept = resolve; });
      return { ok: mode !== 'failure', json: async () => ({ success: mode !== 'failure' }) };
    }
  });
  vm.runInContext(source, context);
  return { context, calls, accept() { mode = 'success'; accept(); }, fail() { mode = 'failure'; }, finishCancellation() { finishCancellation(); } };
}
(async () => {
  let f = fixture();
  const item = { id: 1, state: 'in_progress', url: 'https://download123.mediafire.com/token/file.zip', filename: 'file.zip' };
  f.context.crushAndIntercept(item); await tick();
  assert.deepEqual(f.calls.pause, [1]); assert.equal(f.calls.cancel.length, 0);
  f.context.crushAndIntercept(item); await tick(); assert.equal(f.calls.posts.length, 1);
  f.accept(); await tick(); assert.deepEqual(f.calls.cancel, [1]); assert.equal(f.calls.resume.length, 0);
  assert.equal(f.calls.ui.at(-1), false);
  console.log('PASS: pause first, cancel only after acceptance, duplicate events send once');
  f = fixture(); f.fail(); f.context.crushAndIntercept(item); await tick(); await tick();
  assert.deepEqual(f.calls.resume, [1]); assert.equal(f.calls.cancel.length, 0);
  assert.equal(f.calls.ui.at(-1), true);
  assert.equal(new Set(f.calls.posts.map(p => p.requestId)).size, 1);
  assert.ok(f.calls.posts[0].requestId);
  console.log('PASS: HTTP and native rejection resume original request and share retry ID');
  let suggestions = 0;
  f.context.crushAndIntercept({ id: 2, url: 'blob:https://example.com/id' }, () => suggestions++);
  assert.equal(suggestions, 1); assert.deepEqual(f.calls.pause, [1]);
  console.log('PASS: blob download stays in browser and filename callback resolves');
  let content = fs.readFileSync(require('node:path').join(__dirname, '../extension/content.js'), 'utf8');
  content = content.slice(content.indexOf('  function handleGlobalLinkClick(e)'), content.indexOf('  // Register capturing event listener'));
  const dom = vm.createContext({ isAutoIntercept: true, window: { location: { hostname: 'www.mediafire.com' } } });
  vm.runInContext(content, dom);
  let prevented = false;
  dom.handleGlobalLinkClick({ button: 0, preventDefault() { prevented = true; } });
  assert.equal(prevented, false);
  console.log('PASS: MediaFire page clicks reach site download handlers');
  const mediafirePage = vm.createContext({ window: { location: { hostname: 'www.mediafire.com' } } });
  vm.runInContext(fs.readFileSync(require('node:path').join(__dirname, '../extension/content.js'), 'utf8'), mediafirePage);
  console.log('PASS: entire MediaFire content script exits before DOM hooks, overlays or browser calls');
  const saved = { requestId: randomUUID(), startTime: 'same item', created: Date.now() };
  f = fixture({ saved }); await tick(); await tick();
  assert.equal(f.calls.posts[0].requestId, saved.requestId);
  assert.deepEqual(f.calls.pause, []);
  f.accept(); await tick(); assert.deepEqual(f.calls.cancel, [1]);
  console.log('PASS: restarted worker retries paused browser handoff with the original ID');
  f = fixture({ saved, alreadyComplete: true }); await tick();
  assert.equal(f.calls.posts.length + f.calls.cancel.length, 0);
  console.log('PASS: restarted worker does not touch a completed browser download');
  f = fixture({ holdCancellation: true });
  let filenameReleased = 0;
  assert.equal(f.context.crushAndIntercept(item, () => filenameReleased++), true);
  await tick();
  assert.equal(filenameReleased, 0, 'Save As must wait during desktop handoff');
  f.accept(); await tick();
  assert.equal(filenameReleased, 0, 'Save As must wait until cancel completes');
  f.finishCancellation(); await tick();
  assert.equal(filenameReleased, 1);
  assert.equal(f.calls.ui.at(-1), false);
  console.log('PASS: filename gate holds Save As until cancellation completes and browser UI stays hidden');
  f = fixture(); f.fail(); filenameReleased = 0;
  assert.equal(f.context.crushAndIntercept(item, () => filenameReleased++), true);
  await tick(); await tick();
  assert.equal(filenameReleased, 1); assert.equal(f.calls.ui.at(-1), true);
  assert.deepEqual(f.calls.resume, [1]);
  console.log('PASS: failed handoff releases filename gate once and restores original browser UI');
})().catch(error => { console.error(error); process.exitCode = 1; });

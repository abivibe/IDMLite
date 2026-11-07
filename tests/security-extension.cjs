const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../extension/background.js'), 'utf8');
const event = () => ({ addListener() {} });
const cookies = [
  { name: 'sessionid', value: 'DUMMY_INSTAGRAM', domain: '.instagram.com', path: '/', secure: true },
  { name: 'SID', value: 'DUMMY_GOOGLE', domain: '.google.com', path: '/', secure: true },
  { name: 'private', value: 'DUMMY_PATH', domain: 'files.example.test', path: '/private', secure: true },
  { name: 'normal', value: 'DUMMY_TARGET', domain: 'files.example.test', path: '/', secure: true }
];
const context = vm.createContext({
  console, URL, AbortController, crypto: require('node:crypto').webcrypto,
  setTimeout: () => 1, clearTimeout() {}, setInterval() {},
  fetch: async () => ({ ok: false }),
  chrome: {
    runtime: { onInstalled: event(), onMessage: event(), sendNativeMessage(n, m, cb) { cb({ success: false }); } },
    storage: { onChanged: event(), local: { get: async () => ({}), set() {} } },
    tabs: { onRemoved: event(), onUpdated: event(), sendMessage: async () => {} },
    webRequest: { onSendHeaders: event(), onResponseStarted: event() },
    downloads: { onDeterminingFilename: event(), onCreated: event(), setUiOptions() {} },
    cookies: { getAll: async query => cookies.filter(c => {
      if (query.domain) return c.domain.replace(/^\./, '') === query.domain.replace(/^\./, '');
      const u = new URL(query.url), d = c.domain.replace(/^\./, '');
      return (u.hostname === d || (c.domain.startsWith('.') && u.hostname.endsWith('.' + d))) && u.pathname.startsWith(c.path);
    }) }
  }
});
vm.runInContext(source, context);
(async () => {
  let passed = 0, failed = 0;
  async function check(name, url, initial, referer, expected) {
    const actual = await context.gatherCookiesForDownload(url, initial, referer);
    try { assert.equal(actual, expected); console.log('PASS: ' + name); passed++; }
    catch { console.log('FAIL: ' + name + ' (dummy cookie scope mismatch)'); failed++; }
  }
  await check('query-string platform name cannot collect social session', 'https://evil.example.test/file?instagram.com', '', '', '');
  await check('spoofed domain cannot collect social session', 'https://instagram.com.evil.example.test/file', '', '', '');
  await check('social referer cannot authorize foreign destination', 'https://evil.example.test/file', '', 'https://instagram.com/reel/x', '');
  await check('Google referer cannot authorize foreign destination', 'https://evil.example.test/file', '', 'https://drive.google.com/file/x', '');
  await check('redirect source cannot lend its cookies to destination', 'https://evil.example.test/file', 'https://instagram.com/file', '', '');
  await check('legitimate target retains matching session', 'https://instagram.com/reel/x', '', '', 'sessionid=DUMMY_INSTAGRAM');
  await check('cookie paths remain scoped', 'https://files.example.test/public/file', '', '', 'normal=DUMMY_TARGET');
  await check('HTTPS-only cookies are not sent over HTTP', 'http://files.example.test/public/file', '', '', '');
  await check('path prefix alone does not authorize a cookie', 'https://files.example.test/private-other/file', '', '', 'normal=DUMMY_TARGET');
  console.log(`RESULT: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
})().catch(error => { console.error(error); process.exitCode = 1; });

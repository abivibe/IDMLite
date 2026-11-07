const { spawn } = require('node:child_process');
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const executable = path.resolve(__dirname, '../installer/dist_app/IDMLite.exe');
const net = require('node:net');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
(async () => {
  const socket = net.createServer();
  await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  const endpoint = `http://127.0.0.1:${port}/api/status`;
  const dataDirectory = path.resolve(__dirname, '../installer/verification/app-profile-' + Date.now());
  fs.mkdirSync(dataDirectory, { recursive: true });
  fs.writeFileSync(path.join(dataDirectory, 'settings.json'), JSON.stringify({ IpcPort: port }));
  const env = { ...process.env, IDMLITE_DATA_DIR: dataDirectory };
  const app = spawn(executable, ['--background'], { windowsHide: true, stdio: 'ignore', env });
  try {
    let status;
    for (let i = 0; i < 40; i++) {
      await pause(250);
      try { const response = await fetch(endpoint, { signal: AbortSignal.timeout(1000) }); if (response.ok) { status = await response.json(); break; } } catch {}
    }
    assert.equal(status?.appName, 'IDMLite'); assert.equal(status.version, '1.0.1');
    console.log('PASS: published WPF app starts and serves IPC 1.0.1');
    async function nativeCall(action, payload = {}) {
    const native = spawn(executable, ['chrome-extension://gdcnkipjepgidojhonbcfpgagchjcdci/'], { windowsHide: true, env });
    const message = Buffer.from(JSON.stringify({ action, ...payload }));
    const header = Buffer.alloc(4); header.writeUInt32LE(message.length);
    let output = Buffer.alloc(0), error = '';
    native.stdout.on('data', data => { output = Buffer.concat([output, data]); });
    native.stderr.on('data', data => { error += data; });
    const timer = setTimeout(() => native.kill(), 15000);
    native.stdin.end(Buffer.concat([header, message]));
    await new Promise(resolve => native.on('close', resolve)); clearTimeout(timer);
    assert.ok(output.length >= 4, 'Native host produced no framed reply: ' + error);
    const size = output.readUInt32LE(0); const reply = JSON.parse(output.subarray(4, 4 + size).toString('utf8'));
    return reply;
    }
    const reply = await nativeCall('checkStatus');
    assert.equal(reply.online, true); assert.equal(reply.version, '1.0.1');
    console.log('PASS: native host recognizes browser origin and returns valid framed JSON');
    const auth = await nativeCall('getAuth');
    assert.equal(auth.success, true); assert.equal(auth.port, port); assert.equal(typeof auth.token, 'string');
    const queueUrl = endpoint.replace('/status', '/downloads');
    const anonymous = await fetch(queueUrl); assert.equal(anonymous.status, 401);
    const owner = await fetch(queueUrl, { headers: { 'X-IDMLite-Token': auth.token } }); assert.equal(owner.status, 200);
    console.log('PASS: published native broker supplies working owner token; anonymous queue access is denied');
    if (process.argv.includes('--handoff')) {
      const payload = { url: "https://example.test/test.bin", fileName: "test.bin",
        requestId: require('node:crypto').randomUUID(), direct: true, fileSize: 1024 };
      const [httpAck, nativeAck] = await Promise.all([
        fetch(endpoint.replace('/status', '/download'), {
          method: 'POST', headers: { 'X-IDMLite-Token': auth.token, 'Content-Type': 'application/json' },
          body: JSON.stringify(payload), signal: AbortSignal.timeout(5000)
        }).then(r => r.json()),
        nativeCall('download', payload)
      ]);
      assert.deepEqual(httpAck, nativeAck);
      assert.equal(httpAck.action, 'dialog_opened'); assert.equal(httpAck.requestId, payload.requestId);
      console.log('PASS: published WPF dialog renders and HTTP/native retries share its acknowledgement');
    }
    if (process.argv.includes('--preview')) {
      const previewUrl = endpoint.replace('/status', '/preview');
      const body = JSON.stringify({ url: 'https://www.youtube.com/watch?v=jNQXAC9IVRw' });
      const headers = { 'X-IDMLite-Token': auth.token, 'Content-Type': 'application/json' };
      let start = Date.now();
      const first = await fetch(previewUrl, { method: 'POST', headers, body, signal: AbortSignal.timeout(50000) });
      const initial = await first.json(); const coldMs = Date.now() - start;
      assert.equal(initial.success, true); assert.ok(initial.preview.Streams.some(stream => stream.FileSize > 0));
      start = Date.now();
      const second = await fetch(previewUrl, { method: 'POST', headers, body, signal: AbortSignal.timeout(5000) });
      const warm = await second.json(); const warmMs = Date.now() - start;
      assert.equal(warm.success, true); assert.ok(warmMs < 1000, 'Warm metadata should not run extraction again');
      assert.equal(warm.preview.Cookies, null); assert.ok(warm.preview.Streams.every(stream => stream.Url === JSON.parse(body).url));
      fs.writeFileSync(path.resolve(__dirname, '../installer/verification/preview-live-1.0.1.json'), JSON.stringify({ coldMs, warmMs, warmSize: warm.preview.FileSize, noCredentialsInBrowserCache: true }, null, 2));
      console.log(`PASS: published preview endpoint warms public YouTube metadata (${coldMs} ms cold, ${warmMs} ms warm)`);
    }
    fs.writeFileSync(path.resolve(__dirname, '../installer/verification/release-smoke.json'), JSON.stringify({ appStart: true, ipcVersion: status.version, nativeFraming: true, tokenAuthentication: true }, null, 2));
  } finally { app.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

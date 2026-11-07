const fs = require('node:fs'), net = require('node:net'), path = require('node:path');
const assert = require('node:assert/strict'), { spawn } = require('node:child_process');
const wait = ms => new Promise(r => setTimeout(r, ms));
(async () => {
  const server = net.createServer();
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;
  await new Promise(r => server.close(r));
  const dir = fs.mkdtempSync(path.resolve('installer/verification/firefox-fixture-'));
  fs.writeFileSync(path.join(dir, 'user.js'),
    'user_pref("marionette.port", ' + port + ');\nuser_pref("browser.shell.checkDefaultBrowser", false);\n');
  const profile = path.resolve('installer/dist_app/extension_firefox');
  const firefox = spawn('C:/Program Files/Mozilla Firefox/firefox.exe',
    ['--headless','--no-remote','--marionette','--profile',dir,'about:blank'],
    { windowsHide: true, stdio: 'ignore' });
  let socket;
  try {
    for (let i=0; i<100 && !socket; i++) {
      socket = await new Promise(resolve => {
        const candidate = net.createConnection({ host:'127.0.0.1', port });
        candidate.once('connect', () => resolve(candidate));
        candidate.once('error', () => resolve(null));
      });
      if (!socket) await wait(125);
    }
    assert.ok(socket, 'Marionette did not start');
    let buffered = Buffer.alloc(0);
    const waiting = new Map();
    let seq = 0;
    socket.on('data', part => {
      buffered = Buffer.concat([buffered, part]);
      for (;;) {
        const colon = buffered.indexOf(58);
        if (colon < 0) return;
        const size = Number(buffered.subarray(0,colon).toString());
        if (buffered.length < colon+1+size) return;
        const message = JSON.parse(buffered.subarray(colon+1,colon+1+size).toString());
        buffered = buffered.subarray(colon+1+size);
        if (!Array.isArray(message)) continue;
        const slot = waiting.get(message[1]);
        if (slot) { waiting.delete(message[1]); clearTimeout(slot.timer); message[2] ? slot.reject(Error(JSON.stringify(message[2]))) : slot.resolve(message[3]); }
      }
    });
    function send(name, args) {
      const id = ++seq;
      return new Promise((resolve,reject) => {
        const body = JSON.stringify([0,id,name,args]);
        const timer = setTimeout(() => { waiting.delete(id); reject(Error('Timeout ' + name)); }, 9000);
        waiting.set(id,{ resolve,reject,timer });
        socket.write(Buffer.byteLength(body) + ':' + body);
      });
    }
    await send('WebDriver:NewSession', { capabilities: { alwaysMatch: {} } });
    const result = await send('Addon:Install', { path: profile, temporary: true });
    assert.equal(typeof result === 'string' ? result : result.value,'idmlite@extension');
    console.log('PASS: installed the staged Gecko package as a temporary Firefox add-on in a fresh profile');
    await send('Addon:Uninstall', { id: 'idmlite@extension' });
  } finally {
    socket?.destroy();
    firefox.kill();
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });

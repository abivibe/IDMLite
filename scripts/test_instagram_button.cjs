const { Protocol } = require('./test_browser_download.cjs');
const fs = require('node:fs'), path = require('node:path'), http = require('node:http');
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(check, name) { for (let i=0;i<100;i++) { if (await check()) return; await pause(100); } throw new Error('Timed out: '+name); }
(async () => {
  const received = [], errors = [], requests = [];
  const server = http.createServer(async (req,res) => {
    requests.push(req.url);
    res.setHeader('Access-Control-Allow-Origin','*'); res.setHeader('Access-Control-Allow-Headers','Content-Type, X-IDMLite-Token'); res.setHeader('Access-Control-Allow-Private-Network','true');
    if (req.method === 'OPTIONS') { res.writeHead(204);res.end();return; }
    if (req.url === '/api/status') { res.end(JSON.stringify({ online:true }));return; }
    let body='';for await (const chunk of req) body+=chunk;
    if (req.url === '/api/download') { received.push(JSON.parse(body));res.end(JSON.stringify({ success:true }));return; }
    res.end(JSON.stringify({ success:false }));
  });
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve)); const port=server.address().port;
  const profile=path.join(root,'installer/verification/instagram-profile-'+Date.now());fs.mkdirSync(profile,{recursive:true});
  const extension=path.join(profile,'extension');fs.cpSync(path.join(root,'extension'),extension,{recursive:true});
  const background=path.join(extension,'background.js');
  fs.writeFileSync(background,`chrome.runtime.sendNativeMessage=(n,m,cb)=>cb(m.action==='getAuth'?{success:true,token:'DUMMY_INSTAGRAM',port:${port}}:{success:false});\n`+fs.readFileSync(background,'utf8'));
  const browser=spawn('C:/Program Files/Google/Chrome/Application/chrome.exe',['--headless=new','--no-first-run','--no-default-browser-check','--enable-unsafe-extension-debugging','--remote-debugging-pipe','--user-data-dir='+profile,'about:blank'],{windowsHide:true,stdio:['ignore','ignore','ignore','pipe','pipe']});
  const cdp=new Protocol(browser);
  cdp.onEvent=event=>{
    if(event.method==='Runtime.exceptionThrown') errors.push(event.params.exceptionDetails.exception?.description||event.params.exceptionDetails.text);
    if(event.method==='Fetch.requestPaused') {
      const page=event.params.request.url;
      if(page.startsWith('http://127.0.0.1:')||page.startsWith('http://localhost:')) { cdp.send('Fetch.continueRequest',{requestId:event.params.requestId},event.sessionId).catch(()=>{}); return; }
      const siteCapture=process.argv.includes('--capture')?`<script>window.addEventListener('click',e=>{if(e.target.closest('article')&&!e.target.closest('button,a,input'))e.stopImmediatePropagation();},true)</script>`:'';
      const html=`<!doctype html>${siteCapture}<style>body{margin:0;padding:35px;background:#111;color:white}article{width:360px;height:640px;position:relative}video{width:360px;height:640px}a{color:white}</style><article><div style="aspect-ratio:9/16"><video></video><a href="/reel/Cop84x6u7CP/">post</a><a href="/fixture_creator/">fixture_creator</a><span>Fixture caption</span></div></article>`;
      cdp.send('Fetch.fulfillRequest',{requestId:event.params.requestId,responseCode:page.startsWith('https://www.instagram.com/')?200:404,responseHeaders:[{name:'Content-Type',value:'text/html'}],body:Buffer.from(html).toString('base64')},event.sessionId).catch(error=>errors.push(error.message));
    }
  };
  try {
    const { id }=await cdp.send('Extensions.loadUnpacked',{path:extension});
    let worker;
    await until(async()=>{worker=(await cdp.send('Target.getTargets')).targetInfos.find(t=>t.type==='service_worker'&&t.url.startsWith('chrome-extension://'+id+'/'));return !!worker;},'worker');
    const workerSession=(await cdp.send('Target.attachToTarget',{targetId:worker.targetId,flatten:true})).sessionId;
    await cdp.send('Runtime.enable',{},workerSession);
    await pause(400);
    await cdp.evaluate(workerSession,`(async()=>{await chrome.storage.local.set({ipcPort:${port}});currentIpcPort=${port};isIDMLiteOnline=true;})()`);
    console.log('WORKER STATUS:',await cdp.evaluate(workerSession,`checkIDMLiteStatus(${port})`));
    const target=await cdp.send('Target.createTarget',{url:'about:blank'});
    const session=(await cdp.send('Target.attachToTarget',{targetId:target.targetId,flatten:true})).sessionId;
    await cdp.send('Runtime.enable',{},session);await cdp.send('Page.enable',{},session);await cdp.send('Fetch.enable',{patterns:[{urlPattern:'*'}]},session);
    await cdp.send('Page.navigate',{url:'https://www.instagram.com/reels/'},session);
    await until(()=>cdp.evaluate(session,`!!document.querySelector('.idmlite-badge-btn')`),'Instagram badge');
    async function click(selector) {
      const point=await cdp.evaluate(session,`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()`);
      await cdp.send('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount:1},session);
      await cdp.send('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount:1},session);
    }
    await click('.idmlite-badge-btn');
    await until(()=>cdp.evaluate(session,`!!document.querySelector('.idmlite-dropdown-menu.idmlite-show .idmlite-stream-item')`),'Instagram menu');
    if(process.argv.includes('--keyboard')) {
      await cdp.send('Page.bringToFront',{},session); await pause(200);
      await cdp.evaluate(session,`document.querySelector('.idmlite-stream-item').focus()`);
      console.log('KEY TARGET:',await cdp.evaluate(session,`({tag:document.activeElement.tagName,focus:document.hasFocus()})`));
      await cdp.send('Input.dispatchKeyEvent',{type:'keyDown',key:'Enter',code:'Enter',text:'\r',unmodifiedText:'\r',windowsVirtualKeyCode:13},session);
      await cdp.send('Input.dispatchKeyEvent',{type:'keyUp',key:'Enter',code:'Enter',windowsVirtualKeyCode:13},session);
    } else await click('.idmlite-stream-item');
    await pause(400);
    console.log('CLICK STATE:', await cdp.evaluate(session, `JSON.stringify({open:!!document.querySelector('.idmlite-dropdown-menu.idmlite-show'),toast:document.querySelector('.idmlite-toast')?.textContent,rect:document.querySelector('.idmlite-stream-item').getBoundingClientRect().toJSON()})`));
    console.log('FIXTURE REQUESTS:',requests);
    await until(()=>received.length===1,'Instagram desktop handoff; errors='+errors.join(' | '));
    assert.equal(received[0].url,'https://www.instagram.com/reel/Cop84x6u7CP/');
    assert.equal(errors.length,0,errors.join('\n'));
    console.log('PASS: real Chrome Instagram '+(process.argv.includes('--keyboard')?'keyboard':process.argv.includes('--capture')?'page-captured click':'menu click')+' transfers the enclosing reel exactly once');
  } finally {
    if(errors.length) console.error('BROWSER ERRORS:',errors.join('\n'));
    try{await cdp.send('Browser.close');}catch{browser.kill();}
    server.closeAllConnections();server.close();
  }
})().catch(error=>{console.error(error);process.exitCode=1;});

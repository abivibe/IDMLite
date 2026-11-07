const { Protocol } = require('./test_browser_download.cjs');
const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),assert=require('node:assert/strict');
const { spawn,execFileSync }=require('node:child_process');
const root=path.resolve(__dirname,'..'),pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(check,name){for(let i=0;i<100;i++){if(await check())return;await pause(100);}throw new Error('Timed out: '+name);}
(async()=>{
 const requests=[],errors=[];
 const server=http.createServer(async(req,res)=>{
  res.setHeader('Access-Control-Allow-Origin','*');res.setHeader('Access-Control-Allow-Headers','Content-Type, X-IDMLite-Token');res.setHeader('Access-Control-Allow-Private-Network','true');
  if(req.method==='OPTIONS'){res.writeHead(204);res.end();return;}
  if(req.url==='/api/status'){res.end(JSON.stringify({online:true}));return;}
  let body='';for await(const part of req)body+=part;
  if(req.url==='/api/download'){requests.push(JSON.parse(body));res.end(JSON.stringify({success:true}));return;}
  res.end(JSON.stringify({success:false}));
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const port=server.address().port;
 const profile=path.join(root,'installer/verification/tiktok-profile-'+Date.now());fs.mkdirSync(profile,{recursive:true});
 const media=path.join(profile,'portrait.mp4');
 execFileSync(path.join(root,'ui/ffmpeg.exe'),['-v','error','-f','lavfi','-i','color=c=blue:s=180x320:d=1','-c:v','libx264','-pix_fmt','yuv420p','-movflags','+faststart',media],{windowsHide:true});
 const extension=path.join(profile,'extension');fs.cpSync(path.join(root,'extension'),extension,{recursive:true});
 const background=path.join(extension,'background.js');fs.writeFileSync(background,`chrome.runtime.sendNativeMessage=(n,m,cb)=>cb(m.action==='getAuth'?{success:true,token:'DUMMY_TIKTOK',port:${port}}:{success:false});\n`+fs.readFileSync(background,'utf8'));
 const browser=spawn('C:/Program Files/Google/Chrome/Application/chrome.exe',['--headless=new','--no-first-run','--no-default-browser-check','--enable-unsafe-extension-debugging','--remote-debugging-pipe','--user-data-dir='+profile,'about:blank'],{windowsHide:true,stdio:['ignore','ignore','ignore','pipe','pipe']});
 const cdp=new Protocol(browser);
 cdp.onEvent=event=>{
  if(event.method==='Runtime.exceptionThrown')errors.push(event.params.exceptionDetails.exception?.description||event.params.exceptionDetails.text);
  if(event.method!=='Fetch.requestPaused')return;
  const url=event.params.request.url;
  if(url.startsWith('http://127.0.0.1:')||url.startsWith('http://localhost:')){cdp.send('Fetch.continueRequest',{requestId:event.params.requestId},event.sessionId).catch(()=>{});return;}
  const html=`<!doctype html><style>body{margin:0;background:#111;color:white}header{height:60px}#feed{height:calc(100vh - 60px);overflow:auto}article{height:740px;width:900px;position:relative;transform:translateX(12px)}.DivVideoWrapper{width:880px;overflow:hidden;position:relative;transform:translateZ(0)}video{display:block;width:min(640px,calc(100vw - 40px));height:600px;object-fit:contain;background:black}a{color:white}</style><header>TikTok fixture</header><main id=feed><article data-e2e=recommend-list-item-container><div class=DivVideoWrapper id=xgwrapper-0-7381234567890123456><video preload=auto muted src=/portrait.mp4></video><a id=first href=/@alice/video/7381234567890123456>Alice</a><span data-e2e=video-desc>First fixture</span></div></article><article data-e2e=recommend-list-item-container><div class=DivVideoWrapper id=xgwrapper-1-7381234567890123457><video preload=auto muted src=/portrait.mp4></video><a href=/@bob/video/7381234567890123457>Bob</a><span data-e2e=video-desc>Second fixture</span></div></article></main>`;
  const isMedia=url.endsWith('/portrait.mp4');
  cdp.send('Fetch.fulfillRequest',{requestId:event.params.requestId,responseCode:200,responseHeaders:[{name:'Content-Type',value:isMedia?'video/mp4':'text/html'}],body:(isMedia?fs.readFileSync(media):Buffer.from(html)).toString('base64')},event.sessionId).catch(e=>errors.push(e.message));
 };
 let passed=0;const check=(result,name)=>{assert.ok(result,name);console.log('PASS: '+name);passed++;};
 try{
  const {id}=await cdp.send('Extensions.loadUnpacked',{path:extension});let worker;
  await until(async()=>{worker=(await cdp.send('Target.getTargets')).targetInfos.find(t=>t.type==='service_worker'&&t.url.startsWith('chrome-extension://'+id+'/'));return !!worker;},'worker');
  const ws=(await cdp.send('Target.attachToTarget',{targetId:worker.targetId,flatten:true})).sessionId;await pause(400);
  await cdp.evaluate(ws,`(async()=>{await chrome.storage.local.set({ipcPort:${port}});currentIpcPort=${port};isIDMLiteOnline=true;})()`);
  const target=await cdp.send('Target.createTarget',{url:'about:blank'}),s=(await cdp.send('Target.attachToTarget',{targetId:target.targetId,flatten:true})).sessionId;
  await cdp.send('Runtime.enable',{},s);await cdp.send('Page.enable',{},s);await cdp.send('Emulation.setFocusEmulationEnabled',{enabled:true},s);await cdp.send('Page.bringToFront',{},s);await cdp.send('Fetch.enable',{patterns:[{urlPattern:'*'}]},s);await cdp.send('Page.navigate',{url:'https://www.tiktok.com/foryou'},s);
  await until(()=>cdp.evaluate(s,`[...document.querySelectorAll('.idmlite-badge-btn')].some(b=>b.getBoundingClientRect().width>0)&&document.querySelector('video').videoWidth>0`),'portrait video badge');
  const geometry=()=>cdp.evaluate(s,`(()=>{const v=[...document.querySelectorAll('video')].find(v=>v.getBoundingClientRect().top>=0&&v.getBoundingClientRect().top<200),b=[...document.querySelectorAll('.idmlite-badge-btn')].find(b=>b.getBoundingClientRect().width&&getComputedStyle(b.parentElement).display!=='none'),r=v.getBoundingClientRect(),br=b.getBoundingClientRect(),scale=Math.min(r.width/v.videoWidth,r.height/v.videoHeight),w=v.videoWidth*scale;return {portal:b.parentElement.parentElement===document.body,left:br.left,right:br.right,top:br.top,paintLeft:r.left+(r.width-w)/2,paintRight:r.right-(r.width-w)/2}})()`);
  await pause(250);
  let g=await geometry();check(g.portal,'TikTok badge escapes transformed and clipped player ancestors');
  check(g.left>=g.paintLeft&&g.right<=g.paintRight&&g.top>=66,'badge fits the painted portrait frame rather than outer canvas');
  async function click(selector){const p=await cdp.evaluate(s,`(()=>{const e=[...document.querySelectorAll(${JSON.stringify(selector)})].find(e=>e.getBoundingClientRect().width&&getComputedStyle(e.parentElement).display!=='none'),r=e.getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2}})()`);await cdp.send('Input.dispatchMouseEvent',{type:'mousePressed',...p,button:'left',clickCount:1},s);await cdp.send('Input.dispatchMouseEvent',{type:'mouseReleased',...p,button:'left',clickCount:1},s);}
  await click('.idmlite-badge-btn');await until(()=>cdp.evaluate(s,`!!document.querySelector('.idmlite-show .idmlite-stream-item')`),'menu');await click('.idmlite-show .idmlite-stream-item');await until(()=>requests.length===1,'first handoff');
  check(requests[0].url==='https://www.tiktok.com/@alice/video/7381234567890123456','clicked canvas transfers its own video identity');
  await cdp.send('Emulation.setDeviceMetricsOverride',{width:390,height:740,deviceScaleFactor:1,mobile:false},s);await pause(250);g=await geometry();
  check(g.left>=g.paintLeft&&g.right<=g.paintRight,'badge remains inside painted frame on narrow viewport');
  await cdp.evaluate(s,`document.querySelector('#feed').scrollTop=740`);await pause(700);
  await click('.idmlite-badge-btn');await until(()=>cdp.evaluate(s,`!!document.querySelector('.idmlite-show .idmlite-stream-item')`),'second menu');await click('.idmlite-show .idmlite-stream-item');await until(()=>requests.length===2,'second handoff');
  check(requests[1].url==='https://www.tiktok.com/@bob/video/7381234567890123457','scroll switches badge and handoff to the visible video');
  await pause(300);await click('.idmlite-badge-btn');await until(()=>cdp.evaluate(s,`!!document.querySelector('.idmlite-show .idmlite-stream-item')`),'recycled menu');
  check(await cdp.evaluate(s,`(()=>{const r=document.querySelector('.idmlite-show').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth})()`),'dropdown remains inside narrow viewport');
  await cdp.evaluate(s,`(()=>{const root=document.querySelectorAll('article')[1];root.querySelector('.DivVideoWrapper').id='xgwrapper-1-7381234567890123458';root.querySelector('a').href='/@bob/video/7381234567890123458';document.querySelector('.idmlite-show .idmlite-stream-item').click()})()`);
  await pause(200);check(requests.length===2,'a recycled card cannot download a different video from a stale open menu');
  check(await cdp.evaluate(s,`!document.querySelector('.idmlite-show')`),'changing video identity closes the old menu');
  await pause(300);await click('.idmlite-badge-btn');await until(()=>cdp.evaluate(s,`!!document.querySelector('.idmlite-show .idmlite-stream-item')`),'fresh recycled menu');await click('.idmlite-show .idmlite-stream-item');await until(()=>requests.length===3,'recycled handoff');
  check(requests[2].url==='https://www.tiktok.com/@bob/video/7381234567890123458','reopening after virtual reuse transfers the new identity exactly once');
  await cdp.evaluate(s,`(()=>{const root=document.querySelectorAll('article')[1];root.querySelector('.DivVideoWrapper').removeAttribute('id');root.querySelector('a').href='/@bob';root.querySelector('video').replaceWith(document.createElement('video'))})()`);
  await pause(1000);await click('.idmlite-badge-btn');await until(()=>cdp.evaluate(s,`!!document.querySelector('.idmlite-show .idmlite-stream-item')`),'unknown identity menu');await click('.idmlite-show .idmlite-stream-item');await pause(150);
  check(requests.length===3&&await cdp.evaluate(s,`document.querySelector('.idmlite-toast')?.textContent.includes('belirlenemedi')`),'unidentified feed item never submits a homepage as a video download');
  await cdp.evaluate(s,`document.querySelectorAll('article')[1].remove()`);
  await until(()=>cdp.evaluate(s,`document.querySelectorAll('.idmlite-tiktok-badge').length===1`),'detached video badge cleanup');
  check(true,'removing a canvas cleans up its detached portal button');
  check(errors.length===0,'TikTok fixture has no content script exception');
  console.log(`RESULT: ${passed} passed`);
 }finally{if(errors.length)console.error(errors.join('\n'));try{await cdp.send('Browser.close');}catch{browser.kill();}server.closeAllConnections();server.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});

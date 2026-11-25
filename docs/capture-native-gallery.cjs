// Synthetic fixtures only: this never reads a real Command profile or connects a radio.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const root = path.resolve(__dirname, '..');
const web = path.join(root, 'NeonPocket/examples/companion_radio/webui');
const esbuild = require(path.join(web, 'node_modules/esbuild'));
const { chromium } = require(process.env.MCC_PLAYWRIGHT_PATH || path.resolve(path.dirname(process.execPath), '../node_modules/playwright'));

(async () => {
 const version = fs.readFileSync(path.join(root,'MeshCore Windows/NativeVersion.props'),'utf8').match(/<MccVersion>([^<]+)<\/MccVersion>/)[1];
 const code = fs.readFileSync(path.join(web,'src/app.js'),'utf8') + '\nwindow.__gallery={state,showView,renderMessages,renderHome,selectTarget,stop:()=>connection.leavePage()};';
 const built = await esbuild.build({stdin:{contents:code,resolveDir:path.join(web,'src'),sourcefile:'gallery.js'},bundle:true,write:false,format:'iife',define:{__APP_VERSION__:JSON.stringify(version),__MESHCORE_JS_VERSION__:JSON.stringify('1.14.0')}});
 const css = fs.readFileSync(path.join(web,'node_modules/leaflet/dist/leaflet.css'),'utf8')+'\n'+fs.readFileSync(path.join(web,'src/styles.css'),'utf8');
 let html = fs.readFileSync(path.join(web,'src/index.html'),'utf8');
 const icon=fs.readFileSync(path.join(root,'MeshCore Windows/assets/meshcore-command-small.png')).toString('base64');
 html=html.replace('<span class="app-build" id="desktop-build" hidden></span>',`<span class="app-build" id="desktop-build">Version ${version}</span>`).replaceAll('NeonPocketMC','MeshCore Command').replaceAll('<svg class="brand-mark" aria-hidden="true"><use href="#brand-pocket"/></svg>',`<img class="brand-mark" alt="" src="data:image/png;base64,${icon}">`).replace('/*__RCC6_CSS__*/',()=>css+'\n#boot,#toasts{display:none!important}').replace('/*__RCC6_JS__*/',()=>built.outputFiles[0].text);
 let shell=fs.readFileSync(path.join(root,'MeshCore Windows/assets/multi_board_shell.html'),'utf8');
 shell=shell.replace('<body>','<body><div style="height:36px;background:#900;color:white;text-align:center;padding:10px;font:12px Segoe UI">EARLY NATIVE CANDIDATE — separate profile and credentials; changes are not merged</div>').replace('#views{position:absolute;inset:46px 0 0}', '#views{position:absolute;inset:82px 0 0}');
 shell=shell.replace('<script>','<script>window.pywebview={api:{board_list:async()=>({boards:[{id:"00000000000000000000000000000001",name:"Example board",open:true}]}),board_disconnect:async()=>({ok:true}),board_rename:async()=>({ok:true}),exit_app:async()=>({ok:true}),minimize:async()=>({ok:true}),toggle_fullscreen:async()=>({ok:true})}};window.addEventListener("DOMContentLoaded",()=>window.dispatchEvent(new Event("pywebviewready")));</script><script>');
 const server=http.createServer((req,res)=>{if(req.url==='/'){res.writeHead(200,{'Content-Type':'text/html'});res.end(shell)}else if(req.url.startsWith('/board/')&&!req.url.includes('/api/')){res.writeHead(200,{'Content-Type':'text/html'});res.end(html)}else {res.writeHead(404);res.end()}});
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true,executablePath:process.env.MCC_CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 try {
  const page=await browser.newPage({viewport:{width:1600,height:1000},deviceScaleFactor:1});
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.waitForSelector('iframe');
  const frame=page.frames().find(f=>f.url().includes('/board/'));
  await frame.waitForFunction(()=>!!window.__gallery);
  await frame.evaluate(()=>{
   document.getElementById('boot')?.classList.add('hidden');
   window.__gallery.stop();
   const {state}=window.__gallery, now=Math.floor(Date.now()/1000);
   Object.defineProperty(state,'connected',{get:()=>true,set:()=>{},configurable:true});
   state.self={publicKey:new Uint8Array(32).fill(1),name:'Example station',radioFreq:869618,radioBw:62500,radioSf:8,radioCr:8,txPower:22,advLat:55960000,advLon:-4190000,manualAddContacts:0,reserved:new Uint8Array(3)};
   state.battery=4050;state.linkStatus='connected';
   state.contacts=[{type:1,publicKey:new Uint8Array(32).fill(2),advName:'Example contact',lastAdvert:now,flags:0,outPathLen:255,outPath:new Uint8Array(64),advLat:55980000,advLon:-4200000},{type:2,publicKey:new Uint8Array(32).fill(3),advName:'Example repeater',lastAdvert:now,flags:0,outPathLen:255,outPath:new Uint8Array(64),advLat:56000000,advLon:-4250000}];
   state.channels=[{channelIdx:0,name:'Public'},{channelIdx:1,name:'#local'}];
   state.messages=[{id:'1',key:'ch:0',text:'Example contact: Hello from the mesh!',timestamp:now-180,direction:'in',status:'received',snr:8,pathLen:1},{id:'2',key:'ch:0',text:'Hello! Radio check received.',timestamp:now-90,direction:'out',status:'queued'}];
   state.selected='ch:0';state.radio={lastRssi:-84,lastSnr:8,noiseFloor:-105};state.rfSamples=[-95,-91,-87,-90,-86,-84,-87,-83];state.packets={recv:120,sent:31,nRecvDirect:12,nRecvFlood:108,nRecvErrors:0};
   const own='01'.repeat(32),repeater='03'.repeat(32),time=Date.now();
   localStorage.setItem(`meshcore-monitor-v1:${own}`,JSON.stringify([{key:repeater,interval:30,enabled:{telemetry:true},results:{telemetry:{text:'Latest reading',time,data:{values:[{channel:1,type:116,number:4.08}]}}},voltage:Array.from({length:8},(_,i)=>[time-(7-i)*12*3600000,4.15-i*.01])}]));
   localStorage.setItem(`meshcore-situation-v1:${own}`,JSON.stringify([{owner:own,peer:'',id:'010203040506',name:'Example point',note:'',lat:56.05,lon:-4.35,symbol:0,revision:time,deleted:false}]));
  });
  const out=path.join(root,'docs/images/native');fs.mkdirSync(out,{recursive:true});
  const views=[['overview','home'],['messages','messages'],['contacts','contacts'],['contact-map','contactmap'],['situation-map','situation'],['secure-coms','secure'],['secure-manage','secure-manage'],['word-alerts','watch'],['monitoring','monitoring'],['repeaters','repeaters'],['terrain-tools','terrain'],['radio','radio'],['settings','ultimate']];
  for(const [name,view] of views){
   await frame.evaluate(view=>{window.__gallery.showView(view);document.getElementById('boot')?.classList.add('hidden');if(view==='terrain')document.getElementById('terrain-elevation-section').open=true;if(view==='secure'){document.getElementById('secure-title').textContent='Secure conversation · Example contact';const select=document.getElementById('secure-message-contact');select.add(new Option('Example contact','fixture'));select.value='fixture';document.getElementById('secure-messages').innerHTML='<article class="bubble in"><p>Example secure message</p><small>10:30</small></article><article class="bubble out"><p>Message received.</p><small>10:31 · radio acknowledged</small></article>';document.getElementById('secure-input').disabled=false;document.getElementById('secure-send').disabled=false}},view);
   await page.waitForTimeout(view.includes('map')||view==='situation'||view==='terrain'?1800:350);
   await frame.evaluate(()=>{for(const id of ['link-label','side-status','hero-link'])document.getElementById(id).textContent='Connected';document.getElementById('connect-button').className='link-chip connected';document.getElementById('side-dot').className='status-dot connected';document.getElementById('toasts').replaceChildren()});
   await page.screenshot({path:path.join(out,name+'.png')});
   if(view==='messages') { await frame.locator('.bubble.in').first().click({button:'right'}); await frame.evaluate(()=>{for(const id of ['link-label','side-status'])document.getElementById(id).textContent='Connected';document.getElementById('connect-button').className='link-chip connected';document.getElementById('side-dot').className='status-dot connected';document.getElementById('toasts').replaceChildren()}); await page.screenshot({path:path.join(out,'message-actions.png')}); }
   if(view==='secure') { await frame.locator('#secure-input').fill('A longer secure message needs to be split into two shorter messages for sending.'); await page.screenshot({path:path.join(out,'secure-length-warning.png')}); }
   console.log('Captured '+name);
  }
 } finally {await browser.close();server.close()}
})().catch(error=>{console.error(error);process.exitCode=1});

// Capture the released APK's HTML with a simulated bridge and synthetic messages.
const {chromium}=require(process.env.MCC_PLAYWRIGHT_PATH || require('node:path').resolve(require('node:path').dirname(process.execPath),'../node_modules/playwright'));
const fs=require('node:fs'),http=require('node:http'),path=require('node:path');
const input=process.argv[2]; if(!input) throw Error('Pass the extracted assets/index.html from the verified released APK.');
const html=fs.readFileSync(input),out=path.resolve(__dirname,'images');
const server=http.createServer((req,res)=>{res.setHeader('Content-Type','text/html');res.end(html)});
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true,executablePath:process.env.MCC_CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 try{
  const page=await browser.newPage({viewport:{width:393,height:852},deviceScaleFactor:1,isMobile:true,hasTouch:true});
  await page.addInitScript(()=>{window.AndroidBridge={readFrame:()=>'{}',writeFrame:()=>true,credential:()=>'{}',repeaterCredential:()=>'{}',notifyMessage(){},copyText:()=>true};});
  await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);await page.waitForTimeout(1500);
  await page.evaluate(()=>{document.querySelector('#boot')?.classList.add('hidden');const style=document.createElement('style');style.textContent='#toasts{display:none!important}';document.head.append(style);document.querySelector('[data-view="messages"]').click();document.getElementById('messages-channels').click();document.getElementById('history-storage-status').textContent='Message history is saved on this device.';const list=document.getElementById('thread-list');list.replaceChildren();for(const [name,preview] of [['#Public','Alex: Morning everyone'],['#local','Sam: Radio check'],['#events','Alex: Event starting soon']]){const b=document.createElement('button');b.className='thread';const avatar=document.createElement('span');avatar.className='avatar';avatar.textContent='#';const copy=document.createElement('span');copy.className='thread-copy';const title=document.createElement('strong');title.textContent=name;const small=document.createElement('small');small.textContent=preview;copy.append(title,small);const time=document.createElement('time');time.className='thread-time';time.textContent='10:24';b.append(avatar,copy,time);list.append(b)}});
  await page.screenshot({path:path.join(out,'android-0.2.15-list.png')});
  await page.locator('.thread').first().click();
  await page.evaluate(()=>{document.querySelector('#conversation-title').textContent='#Public';const list=document.querySelector('#message-list');list.replaceChildren();for(let i=0;i<4;i++){const bubble=document.createElement('div');bubble.className='bubble'+(i%2?' out':'');const p=document.createElement('p');p.textContent=i%2?'Good morning!':'Alex: Morning everyone, how is the signal?';const small=document.createElement('small');small.textContent=`10:${20+i} · 2 hops`;bubble.append(p,small);list.append(bubble)}document.querySelector('#message-input').disabled=false});
  await page.screenshot({path:path.join(out,'android-0.2.15-chat.png')});
  if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Android screenshot layout overflows horizontally.');
  console.log('Captured released Android 0.2.15 list and conversation with synthetic data.');
 }finally{await browser.close();server.close()}
})().catch(error=>{console.error(error);process.exitCode=1});

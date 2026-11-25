// Exercise the actual native WebView bridge script and board shell using a simulated host.
const fs=require('node:fs'), path=require('node:path'), http=require('node:http');
const {chromium}=require(process.env.MCC_PLAYWRIGHT_PATH || path.resolve(path.dirname(process.execPath),'../node_modules/playwright'));
const dir=__dirname, source=fs.readFileSync(path.join(dir,'MeshCore.Windows.Preview/Program.cs'),'utf8');
const match=source.match(/AddScriptToExecuteOnDocumentCreatedAsync\("""\s*\r?\n([\s\S]*?)\r?\n\s*"""\);/);
if(!match)throw Error('Native WebView bridge script not found.');
const shell=fs.readFileSync(path.join(dir,'assets/multi_board_shell.html'),'utf8');
const server=http.createServer((req,res)=>{res.writeHead(200,{'Content-Type':'text/html'});res.end(req.url==='/'?shell:'<!doctype html><title>Simulated board</title>')});
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true,executablePath:process.env.MCC_CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 try{
  const page=await browser.newPage();
  await page.addInitScript(()=>{
   if(window!==window.top)return;
   const id='a'.repeat(32),boards=[{id,name:'Example board',address:'192.0.2.4',kind:'wifi',open:false}],calls=[];let listener;
   window.__test={boards,calls};
   window.chrome.webview={addEventListener:(type,fn)=>{if(type==='message')listener=fn},postMessage:request=>{
    calls.push({method:request.method,args:request.args});let value={ok:true};
    if(request.method==='board_list')value={boards:boards.map(x=>({...x}))};
    if(request.method==='board_connect_wifi'){const b=boards.find(x=>x.address===request.args[0]);if(b){b.open=true;value={id:b.id}}else value={error:'Unknown test address'};}
    if(request.method==='board_forget'){const i=boards.findIndex(x=>x.id===request.args[0]);if(i>=0&&boards[i].open)value={error:'Disconnect the board before removing its saved connection.'};else if(i>=0)boards.splice(i,1);}
    queueMicrotask(()=>listener({data:{type:'result',id:request.id,value}}));
   }};
  });
  await page.addInitScript({content:match[1]});
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.locator('#add').click();
  await page.locator('#native-connect-saved-wifi').click();
  if(await page.evaluate(()=>window.__test.calls.some(c=>c.method==='board_connect_wifi')))throw Error('Connect ran with no board selected.');
  await page.locator('#native-forget-wifi').click();
  if(await page.evaluate(()=>window.__test.calls.some(c=>c.method==='board_forget')))throw Error('Remove ran with no board selected.');
  await page.selectOption('#native-saved-wifi','a'.repeat(32));
  if(process.env.MCC_SCREENSHOT)await page.locator('#connect').screenshot({path:process.env.MCC_SCREENSHOT});
  const order=await page.evaluate(()=>{const a=document.getElementById('native-saved-wifi'),b=document.getElementById('native-connect-saved-wifi'),c=document.getElementById('wifi-address');return !!(a.compareDocumentPosition(b)&Node.DOCUMENT_POSITION_FOLLOWING)&&!!(b.compareDocumentPosition(c)&Node.DOCUMENT_POSITION_FOLLOWING)});
  if(!order)throw Error('Saved Connect is not between the saved selector and manual IP field.');
  await page.locator('#native-connect-saved-wifi').click();await page.waitForFunction(()=>window.__test.calls.some(c=>c.method==='board_connect_wifi'));
  if(await page.evaluate(()=>window.__test.calls.find(c=>c.method==='board_connect_wifi').args[0])!=='192.0.2.4')throw Error('Saved Connect used the wrong address.');
  await page.evaluate(()=>{window.__test.boards[0].open=false});
  await page.locator('#add').click();await page.waitForTimeout(100);await page.selectOption('#native-saved-wifi','a'.repeat(32));
  page.once('dialog',dialog=>dialog.dismiss());await page.locator('#native-forget-wifi').click();
  if(await page.evaluate(()=>window.__test.calls.filter(c=>c.method==='board_forget').length)!==0)throw Error('Dismissing confirmation removed a saved board.');
  page.once('dialog',dialog=>dialog.accept());await page.locator('#native-forget-wifi').click();await page.waitForFunction(()=>window.__test.calls.some(c=>c.method==='board_forget'));
  if(await page.evaluate(()=>window.__test.calls.filter(c=>c.method==='board_forget').length)!==1)throw Error('Confirmation did not remove exactly one board.');
  await page.locator('#wifi-address').fill('192.0.2.9');await page.locator('#wifi-connect').click();await page.waitForFunction(()=>window.__test.calls.some(c=>c.method==='board_connect_wifi'&&c.args[0]==='192.0.2.9'));
  console.log('Saved Connect, cancel/confirm removal and manual address verified.');
 }finally{await browser.close();server.close()}
})().catch(error=>{console.error(error);server.close();process.exitCode=1});

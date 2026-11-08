const {chromium}=require(process.env.PLAYWRIGHT_DIR),fs=require('fs'),http=require('http'),path=require('path');
const html=fs.readFileSync(path.join(__dirname,'../app/src/main/assets/index.html'));
const server=http.createServer((req,res)=>{res.setHeader('Content-Type','text/html');res.end(html)});
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({headless:true,executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
for(const [width,height] of [[393,852],[852,393],[640,360]]){
 const page=await browser.newPage({viewport:{width,height},isMobile:true,hasTouch:true});
 await page.addInitScript(()=>{window.AndroidBridge={readFrame:()=>'{}',writeFrame:()=>true,credential:()=>'{}',repeaterCredential:()=>'{}',notifyMessage(){},copyText:()=>true}});
 await page.goto(`http://127.0.0.1:${server.address().port}/`);await page.waitForTimeout(1200);await page.evaluate(()=>document.querySelector('#boot')?.classList.add('hidden'));
 const secure=page.locator('.nav [data-view="secure"]');if(!await secure.isVisible())throw Error(`Secure Chat missing ${width}x${height}`);await secure.click();if(!await page.locator('[data-screen="secure"].active').isVisible())throw Error('Secure chat did not open');
 await page.locator('.nav [data-view="situation"]').click();await page.waitForTimeout(400);
 if(width===393){await page.setViewportSize({width:852,height:393});await page.waitForTimeout(400);const rotated=await page.locator('#situation-map').boundingBox();if(!rotated||rotated.width<850||rotated.height<391)throw Error('Map did not expand after rotation: '+JSON.stringify(rotated));await page.setViewportSize({width,height});await page.waitForTimeout(200);}
 if(width>height){const box=await page.locator('#situation-map').boundingBox();if(!box||box.width<width-2||box.height<height-2)throw Error('Map not full screen: '+JSON.stringify(box));
  for(const id of ['situation-new','situation-tool-area','situation-tool-share','situation-tool-items','situation-settings-toggle'])if(!await page.locator('#'+id).isVisible())throw Error(id+' missing');
  await page.locator('#situation-new').click();if(!await page.locator('#situation-panel').isVisible())throw Error('Marker editor not visible');await page.locator('#situation-panel-close').click();
  await page.locator('.mobile-map-back').click();if(!await page.locator('[data-screen="messages"].active').isVisible())throw Error('Cannot leave map');
 }
 console.log(`${width}x${height}: secure chat and map navigation passed`);await page.close();}
await browser.close();server.close()})().catch(e=>{console.error(e);process.exit(1)});

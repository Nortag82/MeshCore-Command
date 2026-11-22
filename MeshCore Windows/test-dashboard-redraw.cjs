const { chromium } = require(process.env.MCC_PLAYWRIGHT_PATH);
const esbuild = require(process.env.MCC_ESBUILD_PATH);
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');

(async () => {
  const web = path.resolve(__dirname, '../NeonPocket/examples/companion_radio/webui');
  const source = fs.readFileSync(path.join(web, 'src/app.js'), 'utf8') + '\nwindow.__dashboardTest={state,renderHome,renderMessages,showView};\n';
  const script = await esbuild.build({ stdin: { contents: source, resolveDir: path.join(web, 'src'), sourcefile: 'dashboard-test.js' }, bundle: true, write: false, format: 'iife',
    define: { __APP_VERSION__: JSON.stringify('test'), __MESHCORE_JS_VERSION__: JSON.stringify('test') } });
  const css = fs.readFileSync(path.join(web, 'node_modules/leaflet/dist/leaflet.css'), 'utf8') + '\n' + fs.readFileSync(path.join(web, 'src/styles.css'), 'utf8');
  const html = fs.readFileSync(path.join(web, 'src/index.html'), 'utf8').replace('/*__RCC6_CSS__*/', () => css).replace('/*__RCC6_JS__*/', () => script.outputFiles[0].text);
  const server = http.createServer((req, res) => { if (req.url === '/') { res.writeHead(200, {'Content-Type':'text/html'}); res.end(html); } else { res.writeHead(req.method === 'GET' && req.url.startsWith('/api/frame') ? 204 : 200); res.end(); } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  try {
    const page = await browser.newPage();
    await page.addInitScript(() => {
      window.__canvasWrites = 0;
      const desc = Object.getOwnPropertyDescriptor(HTMLCanvasElement.prototype, 'width');
      Object.defineProperty(HTMLCanvasElement.prototype, 'width', { ...desc, set(value) { if (this.id === 'dashboard-activity-chart') window.__canvasWrites++; desc.set.call(this, value); } });
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    await page.waitForFunction(() => !!window.__dashboardTest);
    const result = await page.evaluate(() => {
      const {state, renderHome, showView} = window.__dashboardTest;
      showView('home');
      const initial = window.__canvasWrites;
      showView('messages');
      state.messages.push({id:'synthetic', key:'ch:0', text:'test', timestamp:1, direction:'in', unread:true});
      for (let i = 0; i < 10; i++) renderHome();
      const hidden = window.__canvasWrites;
      const badge = document.getElementById('nav-unread').textContent;
      showView('home');
      return {initial, hidden, badge, restored: document.getElementById('message-preview').textContent};
    });
    if (result.initial < 1 || result.hidden !== result.initial || result.badge !== '1' || !result.restored.includes('test'))
      throw Error('Hidden dashboard redraw or unread behaviour failed: ' + JSON.stringify(result));
    const messages = await page.evaluate(() => {
      const {state, renderMessages, showView} = window.__dashboardTest;
      state.channels = [{channelIdx:0, name:'Test'}]; state.selected = 'ch:0';
      showView('messages'); showView('home');
      const before = document.getElementById('message-list').innerHTML;
      state.messages.push({id:'synthetic-2', key:'ch:0', text:'new while hidden', timestamp:2, direction:'in', unread:true});
      renderMessages();
      const unchanged = document.getElementById('message-list').innerHTML === before;
      const badge = document.getElementById('unread-channels').textContent;
      showView('messages');
      return {unchanged, badge, restored: document.getElementById('message-list').textContent};
    });
    if (!messages.unchanged || messages.badge !== '1' || !messages.restored.includes('new while hidden'))
      throw Error('Hidden message rendering or unread behaviour failed: ' + JSON.stringify(messages));
    const history = await page.evaluate(() => {
      const {state, renderMessages} = window.__dashboardTest;
      state.messages = Array.from({length:250}, (_, i) => ({id:`history-${i}`, key:'ch:0', text:`message ${i}`, timestamp:i+100, direction:'in'}));
      renderMessages();
      const list = document.getElementById('message-list');
      const count = () => list.querySelectorAll('.bubble').length;
      const initial = count();
      list.querySelector('.load-older-messages').click();
      const middle = count();
      list.querySelector('.load-older-messages').click();
      return {initial, middle, final:count(), oldest:list.textContent.includes('message 0'), stored:state.messages.length, button:!!list.querySelector('.load-older-messages')};
    });
    if (history.initial !== 100 || history.middle !== 200 || history.final !== 250 || !history.oldest || history.stored !== 250 || history.button)
      throw Error('Message history paging failed: ' + JSON.stringify(history));
    const large = await page.evaluate(() => {
      const {state, renderMessages} = window.__dashboardTest;
      state.selected = 'ch:1';
      state.channels.push({channelIdx:1, name:'Large history'});
      state.messages = Array.from({length:5000}, (_, i) => ({id:`large-${i}`, key:'ch:1', text:`message ${i}`, timestamp:i+100, direction:'in'}));
      renderMessages();
      return {rendered:document.querySelectorAll('#message-list .bubble').length, stored:state.messages.length, last:document.getElementById('message-list').textContent.includes('message 4999')};
    });
    if (large.rendered !== 100 || large.stored !== 5000 || !large.last)
      throw Error('Large message history rendering failed: ' + JSON.stringify(large));
    console.log('Hidden dashboard and message lists avoid redraws, unread badges remain current, and views refresh when opened.');
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

(() => {
  try { localStorage.removeItem('rcc6-radio-owner'); } catch (_) {}
  const nativeFetch = window.fetch.bind(window);
  const empty = () => new Response(null, {status: 204});
  const json = (value, status=200) => new Response(JSON.stringify(value), {status, headers:{'Content-Type':'application/json'}});
  try { Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async value=>{if(!AndroidBridge.copyText(String(value)))throw new Error('Clipboard unavailable');}}}); } catch (_) {}
  window.fetch = async (input, options={}) => {
    const url = new URL(typeof input === 'string' ? input : input.url, location.href);
    if (url.origin !== location.origin || !url.pathname.startsWith('/api/')) return nativeFetch(input,options);
    const method=(options.method || 'GET').toUpperCase();
    try {
      if (url.pathname === '/api/frame' && method === 'GET') {
        const headers = new Headers(options.headers || {}), raw=AndroidBridge.readFrame(headers.get('X-RCC6-Ack') || '');
        const packet=JSON.parse(raw);
        if (packet.error) return new Response(null,{status:503});
        if (!packet.data) return empty();
        const binary=atob(packet.data), bytes=Uint8Array.from(binary,x=>x.charCodeAt(0));
        return new Response(bytes,{status:200,headers:{'Content-Type':'application/octet-stream','X-RCC6-Seq':String(packet.seq)}});
      }
      if (url.pathname === '/api/frame' && method === 'POST') {
        const frame=options.body instanceof Uint8Array ? options.body : new Uint8Array(options.body);
        const value=btoa(String.fromCharCode(...frame));
        return AndroidBridge.writeFrame(value) ? new Response(null,{status:200}) : new Response(null,{status:503});
      }
      if (url.pathname === '/api/secure-credential' && method === 'POST') {
        const result=JSON.parse(AndroidBridge.credential(String(options.body || '')));
        return json(result,result.error?503:200);
      }
      if (url.pathname === '/api/repeater-credential' && method === 'POST') {
        const result=JSON.parse(AndroidBridge.repeaterCredential(String(options.body || '')));
        return json(result,result.error?503:200);
      }
      if (url.pathname === '/api/notify' && method === 'POST') {
        AndroidBridge.notifyMessage();return json({queued:true});
      }
      if (url.pathname === '/api/offline-map' && method === 'GET') return json({available:false});
      return new Response(null,{status:404});
    } catch (error) { return new Response(null,{status:503}); }
  };
  window.addEventListener('DOMContentLoaded',()=>{
    const primaryViews=new Set(['messages','contacts','situation','secure']);
    const secondaryViews=[['home','Overview'],['secure','Secure Chat'],['secure-manage','Manage Secure Coms'],['contactmap','Contact map'],['monitoring','Monitoring'],['repeaters','Repeaters'],['radio','Radio and adverts'],['watch','Word alerts'],['ultimate','Radio settings']];
    const nav=document.querySelector('.nav');
    for(const button of nav.querySelectorAll('[data-view]'))if(!primaryViews.has(button.dataset.view))button.hidden=true;
    const more=document.createElement('button');more.type='button';more.className='mobile-more';more.setAttribute('aria-expanded','false');more.innerHTML='<svg aria-hidden="true"><use href="#i-more"></use></svg><span>More</span>';nav.append(more);
    const sheet=document.createElement('section');sheet.className='mobile-menu';sheet.hidden=true;sheet.setAttribute('aria-label','More tools');sheet.innerHTML='<div class="mobile-menu-head"><strong>More tools</strong><button type="button" aria-label="Close menu">Close</button></div><div class="mobile-menu-grid"></div><p>Monitoring and alerts run while the app is open and connected.</p>';
    const close=()=>{sheet.hidden=true;more.setAttribute('aria-expanded','false')};sheet.querySelector('.mobile-menu-head button').onclick=close;more.onclick=()=>{sheet.hidden=!sheet.hidden;more.setAttribute('aria-expanded',String(!sheet.hidden))};
    for(const [view,label] of secondaryViews){const original=nav.querySelector(`[data-view="${view}"]`);if(!original)continue;const button=document.createElement('button');button.type='button';button.textContent=label;button.onclick=()=>{close();original.click()};sheet.querySelector('.mobile-menu-grid').append(button);}
    document.body.append(sheet);for(const original of nav.querySelectorAll('[data-view]'))original.addEventListener('click',close);
    for(const id of ['secure-backup-card','network-setup-button']){const el=document.getElementById(id);if(el)el.hidden=true;}
    for(const cls of ['ultimate-hero','ultimate-metrics','ultimate-grid','ultimate-settings-grid'])for(const el of document.querySelectorAll('.'+cls))el.hidden=true;
    for(const id of ['situation-import','offline-select','offline-download','offline-download-status','situation-offline-status']){const el=document.getElementById(id);if(el)el.hidden=true;}
    document.getElementById('offline-zoom')?.closest('label')?.setAttribute('hidden','');
    document.querySelector('#situation-basemap option[value="offline"]')?.remove();
    try{if(localStorage.getItem('meshcore-situation-basemap')==='offline')localStorage.setItem('meshcore-situation-basemap','online')}catch(_){}
    const hint=document.getElementById('situation-status');if(hint&&hint.textContent.includes('Right-click'))hint.textContent='Tap Marker, then tap the map to place it.';
    const labels={messages:'Messages',contacts:'Contacts',situation:'Map',secure:'Secure Chat', 'secure-manage':'Manage'};
    for(const [view,label] of Object.entries(labels)){const span=document.querySelector(`[data-view="${view}"] span`);if(span)span.textContent=label;}
    const region=document.getElementById('region-select'),regionLabel=document.querySelector('label[for="region-select"]'),regionActions=document.querySelector('.region-actions'),custom=document.getElementById('custom-region');
    if(region&&regionLabel&&regionActions&&custom){const details=document.createElement('details');details.className='mobile-region';const summary=document.createElement('summary');summary.textContent='Flood region and routing';details.append(summary);regionLabel.before(details);details.append(regionLabel,region,regionActions,custom);}
    // The radio's conversation list and chat are separate phone screens.
    const messages=document.querySelector('.messages-layout'),threads=document.getElementById('thread-list'),head=document.querySelector('.conversation-head');
    if(messages&&threads&&head){
      const back=document.createElement('button');back.type='button';back.className='mobile-chat-back';back.setAttribute('aria-label','Back to conversations');back.textContent='Back';head.prepend(back);
      const showList=()=>{messages.classList.remove('mobile-chat-open');back.setAttribute('aria-hidden','true')};
      const showChat=()=>{messages.classList.add('mobile-chat-open');back.removeAttribute('aria-hidden');document.getElementById('message-list')?.scrollTo({top:document.getElementById('message-list').scrollHeight})};
      back.onclick=showList;showList();
      threads.addEventListener('click',event=>{if(event.target.closest('button.thread'))showChat()});
      document.getElementById('target-select')?.addEventListener('change',event=>{if(event.target.value)showChat()});
      document.getElementById('contact-message')?.addEventListener('click',()=>setTimeout(()=>{if(document.querySelector('[data-screen="messages"]')?.classList.contains('active'))showChat()},0));
      const menu=document.createElement('details');menu.className='mobile-chat-actions';const summary=document.createElement('summary');summary.textContent='Options';menu.append(summary);
      for(const id of ['channel-mute','conversation-edit','conversation-delete']){const action=document.getElementById(id);if(action)menu.append(action)}head.append(menu);
      menu.addEventListener('click',event=>{if(event.target.closest('button'))menu.open=false});
    }
    const share=document.getElementById('situation-share-panel'),confirmations=document.getElementById('situation-deliveries'),panel=document.getElementById('situation-panel'),layout=document.querySelector('.situation-layout');
    if(layout){const back=document.createElement('button');back.type='button';back.className='mobile-map-back';back.textContent='← Back';back.setAttribute('aria-label','Leave full-screen map');back.onclick=()=>document.querySelector('[data-view="messages"]')?.click();layout.append(back);
      const toggle=document.createElement('button');toggle.type='button';toggle.id='mobile-map-contacts';toggle.className='mobile-map-contacts';toggle.setAttribute('aria-label','Show known contact locations');toggle.setAttribute('aria-pressed',localStorage.getItem('mcc-mobile-map-contacts')==='1'?'true':'false');const label=()=>{toggle.textContent=toggle.getAttribute('aria-pressed')==='true'?'Contacts on':'Contacts off'};label();toggle.onclick=()=>{const enabled=toggle.getAttribute('aria-pressed')!=='true';toggle.setAttribute('aria-pressed',String(enabled));try{localStorage.setItem('mcc-mobile-map-contacts',enabled?'1':'0')}catch{}label();window.dispatchEvent(new Event('mcc-map-contacts-change'))};layout.append(toggle);document.querySelector('[data-view="situation"]')?.addEventListener('click',()=>setTimeout(()=>window.dispatchEvent(new Event('mcc-map-contacts-change')),0));}
    const contactsLayout=document.querySelector('.contacts-layout'),contactsList=document.getElementById('contacts-list'),contactDetail=document.querySelector('.contact-detail');
    if(contactsLayout&&contactsList&&contactDetail){const back=document.createElement('button');back.type='button';back.className='mobile-contact-back secondary-button';back.textContent='← Contacts';back.onclick=()=>contactsLayout.classList.remove('mobile-contact-open');contactDetail.prepend(back);contactsList.addEventListener('click',e=>{if(e.target.closest('.contact-row'))contactsLayout.classList.add('mobile-contact-open')});document.querySelector('[data-view="contacts"]')?.addEventListener('click',()=>contactsLayout.classList.remove('mobile-contact-open'));}
    if(share&&confirmations&&panel&&layout){share.open=false;confirmations.open=false;document.getElementById('situation-tool-share')?.setAttribute('aria-expanded','false');const sync=()=>{layout.classList.toggle('mobile-panel-open',!panel.hidden);layout.classList.toggle('mobile-sharing',share.open);layout.classList.toggle('mobile-confirmations',confirmations.open)};share.addEventListener('toggle',()=>{if(share.open)confirmations.open=false;sync()});confirmations.addEventListener('toggle',()=>{if(confirmations.open){share.open=false;panel.hidden=true;document.getElementById('situation-tool-share')?.setAttribute('aria-expanded','false')}sync()});new MutationObserver(()=>{if(!panel.hidden){share.open=false;confirmations.open=false}sync()}).observe(panel,{attributes:true,attributeFilter:['hidden']});sync();}
    const home=document.querySelector('[data-view="messages"]');if(home)setTimeout(()=>home.click(),100);
  });
})();

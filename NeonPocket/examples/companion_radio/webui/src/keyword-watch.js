export function parseTerms(value) {
  const seen=new Set(),result=[];
  for(const raw of value.split(/\r?\n/)){
    const term=raw.trim();if(!term)continue;
    if(term.length>64)throw Error('Each phrase must be 64 characters or fewer.');
    const key=term.toLocaleLowerCase();if(seen.has(key))continue;seen.add(key);result.push(term);
    if(result.length>50)throw Error('Use at most 50 words or phrases.');
  }
  return result;
}
export function findMatches(message,terms) {
  const text=String(message).toLocaleLowerCase();
  return terms.filter(term=>text.includes(term.toLocaleLowerCase()));
}
export function createKeywordWatch({toast,targetInfo}) {
  const $=id=>document.getElementById(id),configKey='meshcore-keyword-watch-v1',eventsKey='meshcore-keyword-events-v1';
  let config={enabled:true,preview:false,terms:[]},events=[];
  try {const c=JSON.parse(localStorage.getItem(configKey)||'null');if(c&&Array.isArray(c.terms)){config={enabled:!!c.enabled,preview:!!c.preview,terms:parseTerms(c.terms.filter(x=>typeof x==='string').join('\n'))};}}catch{}
  try {const v=JSON.parse(localStorage.getItem(eventsKey)||'[]');if(Array.isArray(v))events=v.filter(x=>x&&typeof x.id==='string'&&typeof x.message==='string').slice(0,50);}catch{}
  $('watch-enabled').checked=config.enabled;$('watch-preview').checked=config.preview;$('watch-terms').value=config.terms.join('\n');
  function render(){const root=$('watch-events');root.replaceChildren();if(!events.length){const p=document.createElement('p');p.textContent='No matches yet.';root.append(p);return;}for(const event of events){const row=document.createElement('article');row.className='watch-event';const title=document.createElement('strong');title.textContent=`${event.source||'Message'} · ${event.matches||'Match'}`;const time=document.createElement('small');time.textContent=' · '+new Date(event.time).toLocaleString();const body=document.createElement('p');body.textContent=event.message;row.append(title,time,body);root.append(row);}}
  const alertBox=document.createElement('aside');alertBox.className='word-alert-banner';alertBox.hidden=true;alertBox.setAttribute('role','alert');
  const alertTitle=document.createElement('strong'),alertBody=document.createElement('span'),dismiss=document.createElement('button');dismiss.type='button';dismiss.textContent='Dismiss';dismiss.setAttribute('aria-label','Dismiss word alert');dismiss.onclick=()=>{alertBox.hidden=true};alertBox.append(alertTitle,alertBody,dismiss);document.body.append(alertBox);
  async function desktop(title,body){
    alertTitle.textContent=title;alertBody.textContent=body;alertBox.hidden=false;
    try{const r=await fetch('/api/notify',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({title,body})});if(!r.ok)throw Error('Windows notification unavailable');$('watch-status').textContent='Alert shown in the app; Windows notification requested. Windows controls desktop pop-ups and sound.';}
    catch{$('watch-status').textContent='Alert shown in the app, but the Windows notification service is unavailable.';}
  }
  $('watch-form').addEventListener('submit',event=>{event.preventDefault();try{const terms=parseTerms($('watch-terms').value);const next={enabled:$('watch-enabled').checked,preview:$('watch-preview').checked,terms};localStorage.setItem(configKey,JSON.stringify(next));config=next;$('watch-terms').value=terms.join('\n');$('watch-status').textContent=terms.length?`Watching ${terms.length} word or phrase${terms.length===1?'':'s'}.`:'No words configured.';toast('Watch list saved');}catch(e){$('watch-status').textContent=e.message||'Could not save the watch list.';}});
  $('watch-test').addEventListener('click',()=>desktop('MeshCore word alert','This is a test notification from MeshCore Command.'));
  $('watch-clear').addEventListener('click',()=>{events=[];try{localStorage.removeItem(eventsKey)}catch{}render();});
  function check(item, {muted=false} = {}){
    if(!config.enabled||!config.terms.length||item.direction!=='in'||events.some(e=>e.id===item.id))return;
    const found=findMatches(item.text,config.terms);if(!found.length)return;
    const target=targetInfo(item.key);const source=target?.name|| (item.key.startsWith('ch:')?`Channel ${item.key.slice(3)}`:'Direct message');
    const entry={id:item.id,source:source+(muted?' (muted)':''),matches:found.join(', '),message:item.text,time:Date.now()};events.unshift(entry);events=events.slice(0,50);
    try{localStorage.setItem(eventsKey,JSON.stringify(events));}catch{}render();if(muted)return;toast(`Word match: ${found[0]} · ${source}`,'warn');
    const body=config.preview?`${source}: ${item.text}`:`${source} matched “${found[0]}”. Open MeshCore to read it.`;
    desktop('MeshCore word alert',body);
  }
  render();return {check};
}

import {decodeTelemetry,renderTelemetry,renderStatus} from './telemetry.js';
export function formatContactRole(type){return ({1:'Chat',2:'Repeater',3:'Room',4:'Sensor'})[type]||`Node ${type||'?'}`;}
export function matchesContact(contact,query,hex){const q=query.trim().toLocaleLowerCase();return !q||`${contact.advName||''} ${hex(contact.publicKey)} ${formatContactRole(contact.type)}`.toLocaleLowerCase().includes(q);}
export function matchesConversation(target,query){const q=query.trim().toLocaleLowerCase();return !q||`${target.name} ${target.key} ${target.subtitle}`.toLocaleLowerCase().includes(q);}
export function tracePathFor(contact){
 if(!Number.isInteger(contact.outPathLen))throw Error('No saved route for this contact.');
 const packed=Number(contact.outPathLen)&255;if(packed===255)throw Error('No saved route. Message the contact or reset its path first.');
 const hops=packed&63,size=((packed>>6)&3)+1;
 if(size!==1)throw Error('Tracing multi-byte paths is not supported by this version.');
 if(!contact.outPath||hops>contact.outPath.length)throw Error('Saved path is incomplete.');
 const path=Array.from(contact.outPath.slice(0,hops));
 if(contact.type===2)path.push(contact.publicKey[0]);
 if(!path.length)throw Error('This is a direct route with no repeaters to trace. Use Ping instead.');
 return new Uint8Array(path);
}
export function createContactsTools({state,getConnection,exclusive,toast,hex,refreshContacts,selectTarget,showView,addMessage,Constants,favorites}){
 const $=id=>document.getElementById(id);let selected='', scan=null;
 const log=value=>{$('contact-results').hidden=false;$('contact-telemetry-view').hidden=true;$('contact-results').textContent=String(value);};
 const getSelected=()=>state.contacts.find(c=>hex(c.publicKey)===selected);
 const requireNode=()=>{const c=getSelected();if(!c)throw Error('Choose a contact first.');if(!state.connected)throw Error('Connect the radio first.');return c;};
 function render(){const list=$('contacts-list'),filter=$('contacts-search').value,contacts=state.contacts.filter(c=>matchesContact(c,filter,hex)).sort((a,b)=>Number(favorites.has(hex(b.publicKey)))-Number(favorites.has(hex(a.publicKey))));list.replaceChildren();$('contacts-count').textContent=`${contacts.length} of ${state.contacts.length} contacts`;if(!contacts.length){const p=document.createElement('p');p.className='empty-state';p.textContent=state.contacts.length?'No contacts match your search.':'No contacts yet. Nearby contacts appear after adverts are received.';list.append(p)}for(const c of contacts){let key=hex(c.publicKey),button=document.createElement('button');button.type='button';button.className='contact-row'+(key===selected?' active':'');const name=document.createElement('strong');name.textContent=c.advName||'Unnamed contact';const info=document.createElement('small');info.textContent=`${formatContactRole(c.type)} · ${key.slice(0,12)}…`;button.append(name,info);button.onclick=()=>{selected=key;log('Choose a tool.');render()};const row=document.createElement('div'),star=document.createElement('button');row.className='contact-item';star.type='button';star.className='favorite-star';star.textContent=favorites.has(key)?'★':'☆';star.setAttribute('aria-label',`${favorites.has(key)?'Remove':'Add'} ${c.advName||'contact'} ${favorites.has(key)?'from':'to'} favourites`);star.setAttribute('aria-pressed',String(favorites.has(key)));star.title=star.getAttribute('aria-label');star.onclick=()=>{try{favorites.toggle(key)}catch(e){toast(e.message,'error')}};row.append(button,star);list.append(row)}
  const c=getSelected();$('contact-title').textContent=c?.advName||'Choose a contact';$('contact-key').textContent=c?hex(c.publicKey):'';
  if(c){const p=Number(c.outPathLen)&255;const route=p===255?'Route unknown':(p&63)===0?'Direct route':`${p&63} repeater hop(s)`;$('contact-meta').textContent=`${formatContactRole(c.type)} · ${route} · Last advert ${c.lastAdvert?new Date(c.lastAdvert*1000).toLocaleString():'unknown'}`;}else $('contact-meta').textContent='Select a contact to see its details and tools.';
  for(const id of ['contact-message','contact-ping','contact-trace','contact-status','contact-telemetry','contact-reset','contact-copy','contact-remove'])$(id).disabled=!c;
 }
 async function run(fn){try{await fn()}catch(e){log(`Error: ${e?.message||String(e)||'Request failed'}`);toast('Contact request failed','error')}}
 function renderDiscovery(){
  const list=$('contacts-discovery-results');list.replaceChildren();if(!scan)return;
  const query=$('contacts-search').value.trim().toLocaleLowerCase();
  for(const result of scan.results.values()){
   const key=hex(result.key),known=state.contacts.find(c=>hex(c.publicKey)===key),name=known?.advName||`${formatContactRole(result.type)} ${key.slice(0,8)}`;
   if(query&&!`${name} ${key} ${formatContactRole(result.type)}`.toLocaleLowerCase().includes(query))continue;
   const row=document.createElement('div');row.className='discovery-contact';const label=document.createElement('span');label.textContent=`${name} · ${result.snr.toFixed(1)} dB`;row.append(label);
   if(known){const tag=document.createElement('small');tag.textContent='In contacts';row.append(tag)}else{const button=document.createElement('button');button.type='button';button.className='secondary-button';button.textContent='Add';button.onclick=()=>run(async()=>{if(!state.connected)throw Error('Connect the radio first.');if(state.contacts.some(c=>hex(c.publicKey)===key)){renderDiscovery();return}button.disabled=true;try{await exclusive(async()=>{if(state.contacts.some(c=>hex(c.publicKey)===key))return;await getConnection().addOrUpdateContact(result.key,result.type,0,255,new Uint8Array(64),`${formatContactRole(result.type)} ${key.slice(0,8)}`,0,0,0);await refreshContacts()});render();renderDiscovery();toast('Contact added. Its name will update when it advertises.')}finally{button.disabled=false}});row.append(button)}list.append(row);
  }
 }
 $('contacts-search').addEventListener('input',()=>{render();renderDiscovery()});
 $('contacts-discover').onclick=()=>run(async()=>{
  if(!state.connected)throw Error('Connect the radio first.');if(scan?.active)return;
  const radio=getConnection(),tag=crypto.getRandomValues(new Uint8Array(4));scan={active:true,radio,tag,results:new Map()};const current=scan,button=$('contacts-discover'),status=$('contacts-discovery-status');button.disabled=true;$('contacts-discovery').hidden=false;status.textContent='Starting nearby discovery…';renderDiscovery();
  const stop=()=>{radio.off('rx',onFrame);current.active=false;button.disabled=false;};
  const onFrame=frame=>{if(!current.active||!state.connected||getConnection()!==radio||!(frame instanceof Uint8Array)||frame[0]!==0x8e||frame.length<42)return;const type=frame[4]&15;if((frame[4]&0xf0)!==0x90||(type!==2&&type!==4)||!tag.every((b,i)=>frame[6+i]===b))return;const key=frame.slice(10,42);if(key.every(b=>b===0)||state.self?.publicKey&&hex(key)===hex(state.self.publicKey))return;current.results.set(hex(key),{key,type,snr:(frame[1]>127?frame[1]-256:frame[1])/4});status.textContent=`Listening for nearby replies… ${current.results.size} found.`;renderDiscovery();};
  radio.on('rx',onFrame);
  try{await exclusive(()=>new Promise((resolve,reject)=>{let timer;const done=(error)=>{clearTimeout(timer);radio.off('rx',ack);error?reject(error):resolve()};const ack=frame=>{if(frame[0]===0)done();else if(frame[0]===1)done(Error('This radio does not support nearby discovery.'))};radio.on('rx',ack);timer=setTimeout(()=>done(Error('Radio did not respond to the discovery command.')),10000);radio.sendToRadioFrame(new Uint8Array([55,0x80,0x14,...tag,0,0,0,0])).catch(done);}));
   status.textContent='Listening for nearby replies for 20 seconds…';setTimeout(()=>{if(scan!==current)return;const connected=state.connected&&getConnection()===radio;stop();status.textContent=connected?`Discovery complete: ${current.results.size} nearby node(s) replied.`:'Discovery stopped because the radio disconnected.';},20000);
  }catch(error){stop();status.textContent=`Discovery failed: ${error?.message||String(error)}`;throw error}
 });
 $('contacts-refresh').onclick=()=>run(async()=>{if(!state.connected)throw Error('Connect the radio first.');log('Refreshing contacts…');await exclusive(refreshContacts);render();log('Contacts refreshed.');});
 $('contact-message').onclick=()=>run(async()=>{const c=getSelected();if(!c)throw Error('Choose a contact first.');if(c.type!==1)throw Error('This node is not a chat contact. Use repeater tools for management.');selectTarget('dm:'+hex(c.publicKey));showView('messages');});
 $('contact-copy').onclick=()=>run(async()=>{const c=getSelected();if(!c)throw Error('Choose a contact first.');await navigator.clipboard.writeText(hex(c.publicKey));toast('Public key copied.');});
 $('contact-remove').onclick=()=>run(async()=>{const c=requireNode();if(!confirm(`Remove ${c.advName||'this contact'} from the radio?`))return;await exclusive(()=>getConnection().removeContact(c.publicKey));selected='';await exclusive(refreshContacts);log('Contact removed.');render();});
 $('contact-reset').onclick=()=>run(async()=>{const c=requireNode();await exclusive(()=>getConnection().resetPath(c.publicKey));await exclusive(refreshContacts);log('Saved path cleared. The next message will use flooding.');render();});
 $('contact-status').onclick=()=>run(async()=>{const c=requireNode();if(c.type!==2&&c.type!==4)throw Error('Status requests are supported by compatible repeaters and sensors.');log('Requesting status…');const response=await exclusive(()=>getConnection().getStatus(c.publicKey,10000));if(getSelected()!==c)return;$('contact-results').hidden=true;$('contact-telemetry-view').hidden=false;renderStatus($('contact-telemetry-view'),response);});
 $('contact-telemetry').onclick=()=>run(async()=>{const c=requireNode();log('Requesting telemetry…');const response=await exclusive(()=>getConnection().getTelemetry(c.publicKey,10000));$('contact-results').hidden=true;$('contact-telemetry-view').hidden=false;renderTelemetry($('contact-telemetry-view'),decodeTelemetry(response.lppSensorData));});
 $('contact-trace').onclick=()=>run(async()=>{const c=requireNode(),path=tracePathFor(c);log(`Tracing ${path.length} repeater hash(es); waiting for a response…`);const r=await exclusive(()=>getConnection().tracePath(path,10000));const hashes=Array.from(r.pathHashes||[]).map(v=>v.toString(16).padStart(2,'0'));const snrs=Array.from(r.pathSnrs||[]).map(v=>((v>127?v-256:v)/4).toFixed(1));log(`Trace response received. Hashes are abbreviated and may collide.\n${hashes.map((h,i)=>`${i+1}. ${h} · SNR ${snrs[i]??'?'} dB`).join('\n')}\nLast signal SNR: ${r.lastSnr} dB\nA trace does not confirm delivery to the final chat contact.`);});
 $('contact-ping').onclick=()=>run(async()=>{const c=requireNode(),conn=getConnection();if(c.type===2){log('Pinging repeater directly; waiting for trace response…');const start=performance.now(),r=await exclusive(()=>conn.tracePath(new Uint8Array([c.publicKey[0]]),10000));log(`Direct repeater response received in ${Math.round(performance.now()-start)} ms · last SNR ${r.lastSnr} dB. One-byte repeater hashes can collide.`);return;}
  if(c.type!==1)throw Error('Ping is available for chat contacts and direct repeaters.');
  let expected=null,timer=null,done=false,early=[];const started=performance.now();
  const cleanup=()=>{conn.off(Constants.PushCodes.SendConfirmed,onAck);if(timer)clearTimeout(timer)};
  const checkAck=ack=>{if(done||expected===null||ack.ackCode!==expected)return;done=true;cleanup();log(`Delivery acknowledgement received after ${Math.round(performance.now()-started)} ms${Number.isFinite(ack.roundTrip)?` (radio reported ${ack.roundTrip} ms)`:''}.`);};
  const onAck=ack=>{if(expected===null){if(early.length<20)early.push(ack);}else checkAck(ack);};
  conn.on(Constants.PushCodes.SendConfirmed,onAck);
  try{log('Sending “ping” as a direct message…');const r=await exclusive(()=>conn.sendTextMessage(c.publicKey,'ping'));if(!Number.isInteger(r?.expectedAckCrc)){cleanup();log('Ping queued, but no acknowledgement ID was provided.');return;}expected=r.expectedAckCrc;addMessage({key:'dm:'+hex(c.publicKey),text:'ping',timestamp:Math.floor(Date.now()/1000),direction:'out',status:'queued',unread:false,expectedAckCrc:expected});log('Ping queued; waiting for delivery acknowledgement…');for(const ack of early)checkAck(ack);if(!done)timer=setTimeout(()=>{if(done)return;done=true;cleanup();log('No delivery acknowledgement within the wait period. The contact may be unreachable or an acknowledgement may arrive later.');},Math.min(45000,Math.max(5000,(Number(r.estTimeout)||10000)+3000)));}catch(e){cleanup();throw e;}
 });
 return {render};
}

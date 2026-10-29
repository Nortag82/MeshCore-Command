export function createCommandDiscovery({state,getConnection,exclusive,hex,refreshContacts,secureComs}) {
 const $=id=>document.getElementById(id),KEY='meshcore-command-discovery-response-v1',WAIT=60000;
 let active=null,lastBroadcast=0,lastResponse=0,pendingResponse=null,timer=null,results=new Map();
 const own=()=>state.self?.publicKey?hex(state.self.publicKey):'';
 const publicChannel=()=>state.channels.find(c=>typeof c.name==='string'&&c.name.replace(/^#/,'').toLowerCase()==='public');
 const say=text=>{$('command-discovery-status').textContent=text};
 const checkbox=$('command-discovery-respond');
 checkbox.checked=localStorage.getItem(KEY)!=='off';
 checkbox.onchange=()=>{localStorage.setItem(KEY,checkbox.checked?'on':'off');if(!checkbox.checked&&pendingResponse){clearTimeout(pendingResponse);pendingResponse=null}};
 function render(){const box=$('command-discovery-results');box.replaceChildren();for(const result of results.values()){
  const row=document.createElement('div');row.className='command-discovery-result';
  const text=document.createElement('span'),known=state.contacts.find(c=>hex(c.publicKey)===result.key);
  text.textContent=`${known?.advName||result.name||'Unknown name'} · ${result.key.slice(0,12)}… · Unverified`;
  const button=document.createElement('button');button.type='button';button.className='secondary-button';button.textContent=known?'Set up':'Add & set up';
  button.onclick=async()=>{try{if(!state.connected)throw Error('Connect the radio first.');button.disabled=true;
   const existing=state.contacts.find(c=>hex(c.publicKey)===result.key);
   if(existing&&existing.type!==1)throw Error('This key belongs to a non-chat contact.');
   if(!existing){const radio=getConnection(),bytes=Uint8Array.from(result.key.match(/../g),x=>parseInt(x,16));await exclusive(async()=>{if(!state.connected||radio!==getConnection())throw Error('Radio disconnected.');await radio.addOrUpdateContact(bytes,1,0,255,new Uint8Array(64),`Command ${result.key.slice(0,8)}`,0,0,0);await refreshContacts()})}
   secureComs.selectContact(result.key);say('Contact selected. Exchange a secret privately and verify the fingerprint before trusting it.');render();
  }catch(e){say(e?.message||'Could not add contact.')}finally{button.disabled=false}};
  row.append(text,button);box.append(row);
 }if(!box.children.length){const p=document.createElement('p');p.className='quiet';p.textContent='No replies yet.';box.append(p)}}
 $('command-discovery-start').onclick=async()=>{try{
  if(!state.connected||!own())throw Error('Connect a radio first.');const channel=publicChannel();if(!channel)throw Error('Add the Public channel to this radio before discovering.');
  if(active)throw Error('Discovery is already running.');if(Date.now()-lastBroadcast<WAIT)throw Error('Wait one minute between discovery broadcasts.');
  const token=hex(crypto.getRandomValues(new Uint8Array(6))),radio=getConnection(),identity=own();results=new Map();render();
  active={token,identity,channel:channel.channelIdx,until:Date.now()+WAIT};$('command-discovery-start').disabled=true;say('Broadcasting on Public using the radio default flood scope…');
  try{await exclusive(async()=>{if(!state.connected||radio!==getConnection()||own()!==identity)throw Error('Radio disconnected.');await radio.sendChannelTextMessage(channel.channelIdx,`MCCD1|Q|${token}|${identity}`)});lastBroadcast=Date.now();say('Listening for replies for 60 seconds. Discovery replies are unverified.');timer=setTimeout(()=>{active=null;$('command-discovery-start').disabled=false;say(`Discovery complete: ${results.size} unverified installation(s) replied.`)},WAIT)}
  catch(e){active=null;$('command-discovery-start').disabled=false;throw e}
 }catch(e){say(e?.message||'Discovery failed.')}};
 function ingest(message){
  if(!message||typeof message.text!=='string')return false;
  const match=message.text.match(/^(?:(.{1,80}): )?(MCCD1\|([QR])\|([0-9a-f]{12})\|([0-9a-f]{64}))$/);
  if(!match)return false;
  const channel=publicChannel();if(!channel||message.channelIdx!==channel.channelIdx)return false;
  const kind=match[3],token=match[4],key=match[5],identity=own(),now=Date.now(),sent=Number(message.senderTimestamp)*1000;
  if(!identity||key===identity||!Number.isFinite(sent)||Math.abs(now-sent)>120000)return true;
  if(kind==='R'){
   if(active&&now<=active.until&&active.token===token&&active.identity===identity&&active.channel===message.channelIdx&&results.size<100){
    results.set(key,{key,name:match[1]||''});render();say(`Listening for replies… ${results.size} unverified installation(s) found.`)
   }
  }else if(checkbox.checked&&state.connected&&!pendingResponse&&now-lastResponse>=WAIT){
   const radio=getConnection(),channelIdx=channel.channelIdx;lastResponse=now;
   const jitter=1000+(crypto.getRandomValues(new Uint16Array(1))[0]%5000);
   pendingResponse=setTimeout(()=>{pendingResponse=null;if(!checkbox.checked||!state.connected||radio!==getConnection()||own()!==identity)return;void exclusive(async()=>{if(!state.connected||radio!==getConnection()||own()!==identity||!checkbox.checked)return;await radio.sendChannelTextMessage(channelIdx,`MCCD1|R|${token}|${identity}`)}).catch(()=>{})},jitter);
  }
  return true;
 }
 render();return {ingest};
}

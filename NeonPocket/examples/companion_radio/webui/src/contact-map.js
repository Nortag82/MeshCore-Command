import L from 'leaflet';

// Companion push 0x88 contains the received wire packet. Reject truncated paths.
export function parseObservedPacket(raw) {
 if (!(raw instanceof Uint8Array) || raw.length < 2) return null;
 const header=raw[0],route=header&3,offset=(route===0||route===3)?5:1;
 if(raw.length<=offset) return null;
 const packed=raw[offset],size=(packed>>6)+1,count=packed&63,end=offset+1+size*count;
 if(size>3||size*count>64||end>=raw.length) return null;
 const hashes=[];for(let i=0;i<count;i++)hashes.push(raw.slice(offset+1+i*size,offset+1+(i+1)*size));
 return {route,flood:route===0||route===1,type:(header>>2)&15,hashes};
}
export function createContactMap({state,hex}) {
 const $=id=>document.getElementById(id), names=['Request','Response','Direct message','Acknowledgement','Advert','Channel message','Channel data','Anonymous request','Path','Trace','Multipart','Discovery'];
 let map,nodes,paths,packets=[],nextId=0,selectedId=null,queued=false,fitted=false,pulseTimer=null;
 const location=c=>{if(!Number.isFinite(c?.advLat)||!Number.isFinite(c?.advLon))return null;const a=c.advLat/1e6,b=c.advLon/1e6;return (!a&&!b)||Math.abs(a)>90||Math.abs(b)>180?null:[a,b]};
 const label=value=>{const el=document.createElement('span');el.textContent=value;return el};
 function init(){if(map)return;map=L.map('contacts-map').setView([56,-4],6);L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:18,attribution:'© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'}).addTo(map);nodes=L.layerGroup().addTo(map);paths=L.layerGroup().addTo(map)}
 function visible(){init();render();setTimeout(()=>map.invalidateSize(),100)}
 function schedule(){if(queued||state.view!=='contactmap')return;queued=true;setTimeout(()=>{queued=false;if(state.view==='contactmap')render()},100)}
 function ingest(sample){const packet=parseObservedPacket(sample?.raw);if(!packet)return;packets.unshift({id:++nextId,packet,time:Date.now(),snr:sample.lastSnr,rssi:sample.lastRssi});if(packets.length>100)packets.length=100;if($('map-follow').checked)selectedId=packets[0].id;else if(!packets.some(p=>p.id===selectedId))selectedId=null;if(pulseTimer)clearTimeout(pulseTimer);pulseTimer=setTimeout(()=>{pulseTimer=null;schedule()},8000);schedule()}
 function render(){if(!map)return;nodes.clearLayers();paths.clearLayers();const liveStatus=$('packet-live-status');liveStatus.textContent=state.connected?'● Live · listening for received packets':'○ Radio disconnected · packet display paused';liveStatus.classList.toggle('online',!!state.connected);const query=$('map-contact-search').value.trim().toLowerCase();let count=0,missing=0,coords=[];
  for(const c of state.contacts){const pos=location(c);if(!pos){missing++;continue}if(query&&!`${c.advName||''} ${hex(c.publicKey)}`.toLowerCase().includes(query))continue;count++;coords.push(pos);const role=({1:'Contact',2:'Repeater',3:'Room',4:'Sensor'})[c.type]||'Node';L.circleMarker(pos,{radius:7,color:'#10212d',weight:2,fillColor:c.type===2?'#ffb52e':'#38d695',fillOpacity:1}).addTo(nodes).bindTooltip(label(`${c.advName||'Unnamed'} · ${role}`));}
  const own=location(state.self);if(own){coords.push(own);L.circleMarker(own,{radius:9,color:'#10212d',weight:2,fillColor:'#42a5ff',fillOpacity:1}).addTo(nodes).bindTooltip(label('Your radio'));}
  $('contact-map-count').textContent=`${count} contacts shown · ${missing} without a location`;
  const list=$('packet-map-list');list.replaceChildren();if(!packets.length){const el=document.createElement('p');el.textContent='Waiting for packets received by your radio.';list.append(el)}
  for(const item of packets){const button=document.createElement('button');button.type='button';button.className='packet-map-item'+(item.id===selectedId?' active':'');const type=names[item.packet.type]||`Packet type ${item.packet.type}`;button.textContent=`${new Date(item.time).toLocaleTimeString()} · ${type} · ${item.packet.flood?'flood':'direct'} · ${item.packet.hashes.length} path hashes${Number.isFinite(item.snr)?` · SNR ${item.snr} dB`:''}`;button.onclick=()=>{selectedId=item.id;$('map-follow').checked=false;render()};list.append(button)}
  const item=packets.find(p=>p.id===selectedId);if(item){const fresh=state.connected&&$('map-follow').checked&&Date.now()-item.time<8000;if(fresh&&own)L.circleMarker(own,{radius:14,color:'#42a5ff',weight:4,fillOpacity:0,className:'packet-live-node'}).addTo(paths);const packet=item.packet;if(!packet.flood){$('packet-map-detail').textContent='Direct packets carry a remaining route, not a recorded journey. No travelled path is drawn.';}else if(!packet.hashes.length){$('packet-map-detail').textContent='This flood packet reports no repeater hashes. Its source and route are not identified.';}else{let resolved=0,ambiguous=0;const matched=packet.hashes.map(hash=>{const prefix=hex(hash),matches=state.contacts.filter(c=>hex(c.publicKey).startsWith(prefix));if(matches.length!==1){ambiguous++;return null}const pos=location(matches[0]);if(!pos){ambiguous++;return null}resolved++;return {pos,name:matches[0].advName||prefix}});for(let i=1;i<matched.length;i++)if(matched[i-1]&&matched[i])L.polyline([matched[i-1].pos,matched[i].pos],{color:'#fa8e3c',weight:4,opacity:.95,dashArray:'9 6',className:fresh?'packet-live-path':''}).addTo(paths);if(own&&matched.at(-1))L.polyline([matched.at(-1).pos,own],{color:'#fa8e3c',weight:4,opacity:.95,dashArray:'9 6',className:fresh?'packet-live-path':''}).addTo(paths);matched.forEach((node,i)=>{if(node)L.circleMarker(node.pos,{radius:9,color:'#fc8e3c',weight:3,fillColor:'#fff',fillOpacity:1,className:fresh?'packet-live-node':''}).addTo(paths).bindTooltip(label(`${i+1}. ${node.name}`),{permanent:true,direction:'top',className:'neighbour-name'})});$('packet-map-detail').textContent=`Packet-reported flood path: ${resolved} of ${packet.hashes.length} hashes matched to a located contact; ${ambiguous} unknown, ambiguous or unlocated. Dashed lines show only consecutive matches and the receiving radio.`;}}else $('packet-map-detail').textContent='Select a packet to show its reported route.';
  if(!fitted&&coords.length){if(coords.length>1)map.fitBounds(coords,{padding:[25,25],maxZoom:12});else map.setView(coords[0],10);fitted=true}
 }
 $('map-contact-search').addEventListener('input',render);
 $('map-fit').onclick=()=>{fitted=false;render()};
 $('map-clear-packets').onclick=()=>{packets=[];selectedId=null;render()};
 $('map-follow').onchange=()=>{if($('map-follow').checked&&packets.length)selectedId=packets[0].id;render()};
 return {visible,render,schedule,ingest};
}

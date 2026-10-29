export function decodeTelemetry(input) {
 const data=input instanceof Uint8Array?input:new Uint8Array(input||[]), values=[];let i=0,warning='';
 const types={0:['Digital input',1,1,false,''],1:['Digital output',1,1,false,''],2:['Analog input',2,100,true,''],3:['Analog output',2,100,true,''],100:['Generic sensor',4,1,false,''],101:['Light',2,1,false,'lux'],102:['Presence',1,1,false,''],103:['Temperature',2,10,true,'°C'],104:['Humidity',1,2,false,'%'],115:['Pressure',2,10,false,'hPa'],116:['Voltage',2,100,false,'V'],117:['Current',2,1000,true,'A'],118:['Frequency',4,1,false,'Hz'],120:['Percentage',1,1,false,'%'],121:['Altitude',2,1,true,'m'],125:['Concentration',2,1,false,'ppm'],128:['Power',2,1,false,'W'],130:['Distance',4,1000,false,'m'],131:['Energy',4,1000,false,'kWh'],132:['Direction',2,1,false,'°'],133:['Time',4,1,false,''],142:['Switch',1,1,false,'']};
 const read=(offset,n,signed)=>{let v=0;for(let j=0;j<n;j++)v=v*256+data[offset+j];if(signed&&v>=2**(n*8-1))v-=2**(n*8);return v};
 while(i<data.length){const channel=data[i++];if(channel===0)break;if(i>=data.length){warning='Incomplete telemetry header.';break}const type=data[i++];
  if(type===136){if(i+9>data.length){warning='Incomplete GPS measurement.';break}const lat=read(i,3,true)/10000,lon=read(i+3,3,true)/10000,alt=read(i+6,3,true)/100;i+=9;values.push({channel,name:'Position',value:`${lat.toFixed(4)}, ${lon.toFixed(4)} · ${alt.toFixed(2)} m`});continue}
  if(type===113||type===134){if(i+6>data.length){warning='Incomplete motion measurement.';break}const scale=type===113?1000:100,unit=type===113?'g':'°/s';const axes=[0,2,4].map(n=>read(i+n,2,true)/scale);i+=6;values.push({channel,name:type===113?'Acceleration':'Rotation',value:`${axes.join(', ')} ${unit}`});continue}
  const spec=types[type];if(!spec){warning=`Unknown telemetry type ${type}; remaining data cannot be decoded safely.`;break}const [name,size,scale,signed,unit]=spec;if(i+size>data.length){warning=`Incomplete ${name.toLowerCase()} measurement.`;break}const n=read(i,size,signed)/scale;i+=size;let value=type===133?new Date(n*1000).toLocaleString():`${n}${unit?' '+unit:''}`;if(type===102||type===142)value=n?'On':'Off';values.push({channel,name,value,type,number:n});
 }
 return {values,warning,raw:[...data].map(v=>v.toString(16).padStart(2,'0')).join('')};
}
// Approximate open-circuit state of charge for a single Li-ion cell.
export function estimateSingleCell(voltage) {
 if (!Number.isFinite(voltage) || voltage < 2.5 || voltage > 4.3) return null;
 const points=[[3.0,0],[3.3,5],[3.5,10],[3.6,15],[3.7,25],[3.75,35],[3.8,45],[3.85,55],[3.9,65],[4.0,75],[4.1,90],[4.2,100]];
 if(voltage<=points[0][0])return 0;
 for(let i=1;i<points.length;i++){const [v,p]=points[i], [prevV,prevP]=points[i-1];if(voltage<=v)return Math.round(prevP+(voltage-prevV)*(p-prevP)/(v-prevV));}
 return 100;
}
function batteryCard(v) {
 const n=Number.isFinite(v.number)?v.number:Number.parseFloat(v.value),percent=estimateSingleCell(n);
 const card=document.createElement('div');card.className='telemetry-item battery-card';
 const name=document.createElement('small');name.textContent='Device voltage';
 const row=document.createElement('div');row.className='battery-row';
 const icon=document.createElement('div');icon.className='battery-icon';icon.setAttribute('aria-hidden','true');
 const fill=document.createElement('span');fill.className='battery-fill';fill.style.width=`${percent??0}%`;if(percent!==null&&percent<=20)fill.classList.add('low');icon.append(fill);
 const text=document.createElement('strong');text.textContent=percent===null?'No estimate':`~${percent}%`;
 row.append(icon,text);const voltage=document.createElement('span');voltage.className='battery-voltage';voltage.textContent=v.value;
 const note=document.createElement('small');note.textContent=percent===null?'Outside the expected range for a 1-cell Li-ion estimate.':'Estimated for 1-cell Li-ion; device voltage may not be a battery.';
 card.append(name,row,voltage,note);return card;
}
export function renderTelemetry(container, decoded) {
 container.replaceChildren();if(!decoded.values.length){const p=document.createElement('p');p.textContent='No supported measurements reported.';container.append(p)}
 const grid=document.createElement('div');grid.className='telemetry-grid';for(const v of decoded.values){if(v.channel===1&&(v.type===116||v.name==='Voltage')){grid.append(batteryCard(v));continue}const card=document.createElement('div');card.className='telemetry-item';const name=document.createElement('small');name.textContent=`${v.name} · ${v.channel===1?'Device':`Channel ${v.channel}`}`;const value=document.createElement('strong');value.textContent=v.value;card.append(name,value);grid.append(card)}container.append(grid);if(decoded.warning){const p=document.createElement('p');p.className='quiet';p.textContent=decoded.warning;container.append(p)}const details=document.createElement('details'),summary=document.createElement('summary'),raw=document.createElement('code');summary.textContent='Raw telemetry';raw.textContent=decoded.raw||'(empty)';details.append(summary,raw);container.append(details);
}

// MeshCore status reports the remote node's latest radio measurements; SNR
// is a signed quarter-dB value, and durations are in seconds.
export function renderStatus(container, status) {
 container.replaceChildren();
 const title=document.createElement('h4');title.textContent='Node status';container.append(title);
 const note=document.createElement('p');note.className='quiet';note.textContent='Signal measurements are the last packet heard by this node; they may not describe this connection.';container.append(note);
 const grid=document.createElement('div');grid.className='telemetry-grid';
 const value=(key,format)=>Number.isFinite(status?.[key])?format(status[key]):'Not reported';
 if(Number.isFinite(status?.batt_milli_volts)&&status.batt_milli_volts>0){const voltage=status.batt_milli_volts/1000;grid.append(batteryCard({number:voltage,value:`${voltage.toFixed(2)} V`}));}
 const duration=n=>{n=Math.max(0,Math.floor(n));const days=Math.floor(n/86400),hours=Math.floor(n%86400/3600),minutes=Math.floor(n%3600/60);return days?`${days}d ${hours}h ${minutes}m`:hours?`${hours}h ${minutes}m`:`${minutes}m ${n%60}s`;};
 const count=n=>n.toLocaleString();
 const items=[['Battery voltage','batt_milli_volts',n=>n>0?`${(n/1000).toFixed(2)} V`:'Not reported'],['Uptime','total_up_time_secs',duration],['Last received signal','last_rssi',n=>`${n} dBm`],['Last received SNR','last_snr',n=>`${(n/4).toFixed(2)} dB`],['Noise floor','noise_floor',n=>`${n} dBm`],['Transmit queue','curr_tx_queue_len',count],['Packets received','n_packets_recv',count],['Packets sent','n_packets_sent',count],['Flood received','n_recv_flood',count],['Direct received','n_recv_direct',count],['Flood sent','n_sent_flood',count],['Direct sent','n_sent_direct',count],['Flood duplicates','n_flood_dups',count],['Direct duplicates','n_direct_dups',count],['Transmit airtime','total_air_time_secs',duration],['Error flags','err_events',n=>`0x${n.toString(16).padStart(4,'0')}`]];
 for(const [label,key,format] of items){if(key==='batt_milli_volts'&&Number.isFinite(status?.[key])&&status[key]>0)continue;const card=document.createElement('div');card.className='telemetry-item';const name=document.createElement('small');name.textContent=label;const data=document.createElement('strong');data.textContent=value(key,format);card.append(name,data);grid.append(card)}
 container.append(grid);const details=document.createElement('details'),summary=document.createElement('summary'),raw=document.createElement('pre');summary.textContent='Raw status';raw.textContent=JSON.stringify(status,null,2);details.append(summary,raw);container.append(details);
}

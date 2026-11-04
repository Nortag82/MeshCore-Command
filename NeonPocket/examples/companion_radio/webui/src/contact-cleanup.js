export const CLEANUP_SETTING = 'meshcore-auto-contact-cleanup-v1';
const keyPattern=/^[0-9a-f]{64}$/;
function read(storage,key,fallback,valid){const raw=storage.getItem(key);if(raw===null)return fallback;const value=JSON.parse(raw);if(!valid(value))throw Error('Saved contact protection data is invalid.');return value}
export function protectedContacts(storage,own,favorites){
 if(!keyPattern.test(own))throw Error('Radio identity is unavailable.');
 const object=x=>!!x&&typeof x==='object'&&!Array.isArray(x);
 const savedFavorites=read(storage,'meshcore-favourites-v1',[],Array.isArray);
 const secure=read(storage,`meshcore-secure-v1:${own}`,{contacts:{}},x=>object(x)&&object(x.contacts));
 const monitored=read(storage,`meshcore-monitor-v1:${own}`,[],Array.isArray);
 const rooms=read(storage,`meshcore-rooms-v1:${own}`,[],Array.isArray);
 const saved=read(storage,'meshcore-saved-repeaters-v1',[],Array.isArray);
 const keys=new Set([...savedFavorites,...Object.keys(secure.contacts),...monitored.map(x=>x?.key),...rooms,...saved.map(x=>x?.key)].filter(x=>typeof x==='string'));
 return key=>keys.has(key)||favorites.has(key);
}
export function oldestContact(contacts,messages,protectedKey,hex,now=Math.floor(Date.now()/1000),selected=''){
 const recent=new Map();for(const m of messages||[]){if(m?.direction!=='in'||typeof m.key!=='string'||!m.key.startsWith('dm:')||!Number.isFinite(m.timestamp)||m.timestamp<=0)continue;const key=m.key.slice(3);recent.set(key,Math.max(recent.get(key)||0,m.timestamp))}
 return contacts.filter(c=>{const k=hex(c.publicKey);return keyPattern.test(k)&&!protectedKey(k)&&!(Number(c.flags)&1)&&selected!==`dm:${k}`&&selected!==`room:${k}`&&Number.isFinite(c.lastAdvert)&&c.lastAdvert>0&&c.lastAdvert<=now+300&&(recent.get(k)||0)<=now+300}).sort((a,b)=>Math.max(a.lastAdvert,recent.get(hex(a.publicKey))||0)-Math.max(b.lastAdvert,recent.get(hex(b.publicKey))||0))[0]||null;
}
export function contactArguments(c){if(!(c?.publicKey instanceof Uint8Array)||c.publicKey.length!==32||!(c.outPath instanceof Uint8Array)||c.outPath.length!==64||!Number.isInteger(c.type)||!Number.isInteger(c.flags)||!Number.isInteger(c.outPathLen)||typeof c.advName!=='string'||!Number.isInteger(c.lastAdvert)||!Number.isInteger(c.advLat)||!Number.isInteger(c.advLon))throw Error('Contact details are incomplete; no contact was removed.');return [c.publicKey,c.type,c.flags,c.outPathLen&255,c.outPath,c.advName,c.lastAdvert,c.advLat,c.advLon]}

export async function replaceContact(radio,candidate,incoming,canContinue=()=>true){
 const next=contactArguments(incoming),original=contactArguments(candidate);
 if(!canContinue())throw Error('Radio connection changed; no contact was removed.');
 await radio.removeContact(candidate.publicKey);
 try{if(!canContinue())throw Error('Radio disconnected');await radio.addOrUpdateContact(...next)}
 catch(error){let restored=false;try{if(canContinue()){await radio.addOrUpdateContact(...original);restored=true}}catch{}throw Error(restored?'New contact could not be added; the previous contact was restored.':`Could not add the new contact or restore ${candidate.advName||'the previous contact'}. It may need to advertise again.`)}
}

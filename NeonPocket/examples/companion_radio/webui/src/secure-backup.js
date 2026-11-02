const enc = new TextEncoder();
const dec = new TextDecoder('utf-8', {fatal:true});
const idPattern = /^[0-9a-f]{64}$/;
const to64 = bytes => btoa(Array.from(bytes, b => String.fromCharCode(b)).join('')).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
function from64(s) {
  if (typeof s !== 'string' || !/^[A-Za-z0-9_-]+$/.test(s)) throw Error('Invalid backup encoding.');
  const value=atob(s.replace(/-/g,'+').replace(/_/g,'/')+'='.repeat((4-s.length%4)%4));
  const bytes=Uint8Array.from(value,c=>c.charCodeAt(0));
  if(to64(bytes)!==s) throw Error('Invalid backup encoding.');
  return bytes;
}
async function keyFor(password,salt) {
  const base=await crypto.subtle.importKey('raw',enc.encode(password),'PBKDF2',false,['deriveKey']);
  return crypto.subtle.deriveKey({name:'PBKDF2',hash:'SHA-256',salt,iterations:600000},base,{name:'AES-GCM',length:256},false,['encrypt','decrypt']);
}
async function credentialId(own,peer) {
  const hash=await crypto.subtle.digest('SHA-256',enc.encode(`secure-v1|${own}|${peer}`));
  return Array.from(new Uint8Array(hash),v=>v.toString(16).padStart(2,'0')).join('');
}
async function credential(action,own,peer,password) {
  const response=await fetch('/api/secure-credential',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action,key:await credentialId(own,peer),password})});
  if(!response.ok) throw Error('Secure credential storage is unavailable.');
  return response.json();
}
function validDb(db) {
  return db && typeof db==='object' && ['contacts','history','seen'].every(k=>db[k] && typeof db[k]==='object' && !Array.isArray(db[k]));
}
function validateSecret(value) {
  if(typeof value!=='string' || !/^MCC1-[A-Za-z0-9_-]{43}$/.test(value) || from64(value.slice(5)).length!==32) throw Error('Backup has an invalid shared secret.');
}
async function fingerprint(value,own,peer) {
  const bytes=from64(value.slice(5)), suffix=enc.encode([own,peer].sort().join('|')), all=new Uint8Array(bytes.length+suffix.length);
  all.set(bytes);all.set(suffix,bytes.length);
  const hash=new Uint8Array(await crypto.subtle.digest('SHA-256',all));
  return Array.from(hash,v=>v.toString(16).padStart(2,'0')).join('').slice(0,24).match(/.{4}/g).join(' ');
}
async function collect() {
  const radios=[];
  for(let i=0;i<localStorage.length;i++) {
    const key=localStorage.key(i), match=/^meshcore-secure-v1:([0-9a-f]{64})$/.exec(key||'');
    if(!match) continue;
    const db=JSON.parse(localStorage.getItem(key)); if(!validDb(db)) throw Error('Secure Coms state cannot be read. No backup was created.');
    const secrets={};
    for(const peer of Object.keys(db.contacts)) {
      if(!idPattern.test(peer)) throw Error('Invalid secure contact. No backup was created.');
      const value=(await credential('get',match[1],peer)).password;
      if(!value) throw Error('A saved contact has no shared secret. Re-enter or remove it before exporting.');
      validateSecret(value);
      if(await fingerprint(value,match[1],peer)!==db.contacts[peer]?.fingerprint) throw Error('A contact fingerprint does not match its secret. Check it before exporting.');
      secrets[peer]=value;
    }
    const groupsRaw=localStorage.getItem(`meshcore-secure-groups-v1:${match[1]}`);
    let groupsState=null;if(groupsRaw){groupsState=JSON.parse(groupsRaw);validateGroups(groupsState)}
    radios.push({own:match[1],db,secrets,groupsState});
  }
  if(!radios.some(r=>Object.keys(r.secrets).length)) throw Error('No saved Secure Coms secrets found for this Windows profile.');
  return {version:1,radios};
}
async function encryptBackup(data,password) {
  const salt=crypto.getRandomValues(new Uint8Array(16)),iv=crypto.getRandomValues(new Uint8Array(12));
  const ciphertext=await crypto.subtle.encrypt({name:'AES-GCM',iv},await keyFor(password,salt),enc.encode(JSON.stringify(data)));
  return JSON.stringify({format:'MeshCoreCommandSecureBackup',version:1,kdf:'PBKDF2-SHA256',iterations:600000,salt:to64(salt),iv:to64(iv),ciphertext:to64(new Uint8Array(ciphertext))});
}
async function decryptBackup(contents,password) {
  const e=JSON.parse(contents);
  if(e?.format!=='MeshCoreCommandSecureBackup'||e.version!==1||e.kdf!=='PBKDF2-SHA256'||e.iterations!==600000) throw Error('Unsupported backup format.');
  const salt=from64(e.salt),iv=from64(e.iv),cipher=from64(e.ciphertext);
  if(salt.length!==16||iv.length!==12||cipher.length>6000000) throw Error('Invalid backup.');
  let plain;try{plain=await crypto.subtle.decrypt({name:'AES-GCM',iv},await keyFor(password,salt),cipher)}catch{throw Error('Wrong password or damaged backup. Nothing was changed.')}
  return JSON.parse(dec.decode(plain));
}
function validateGroups(value) {
  if(!value||typeof value!=='object'||!value.groups||typeof value.groups!=='object'||Array.isArray(value.groups)||!value.history||typeof value.history!=='object'||Array.isArray(value.history)) throw Error('Invalid secure group backup.');
  for(const [id,g] of Object.entries(value.groups)) if(!/^[A-Za-z0-9_-]{8}$/.test(id)||!g||typeof g.name!=='string'||g.name.length>32||!Array.isArray(g.members)||g.members.some(peer=>!idPattern.test(peer))) throw Error('Invalid secure group backup.');
  for(const [id,history] of Object.entries(value.history)) if(!/^[A-Za-z0-9_-]{8}$/.test(id)||!Array.isArray(history)||history.length>120||history.some(item=>!item||!idPattern.test(item.peer)||typeof item.wire!=='string')) throw Error('Invalid secure group history.');
}
async function restore(data) {
  if(data?.version!==1||!Array.isArray(data.radios)||data.radios.length>100) throw Error('Invalid backup contents.');
  const plans=[],writes=[],owners=new Set();
  for(const entry of data.radios) {
    const {own,db,secrets}=entry||{};
    if(!idPattern.test(own)||owners.has(own)||!validDb(db)||!secrets||typeof secrets!=='object'||Array.isArray(secrets)) throw Error('Invalid backup contents.');
    owners.add(own);
    if(Object.keys(db.contacts).length!==Object.keys(secrets).length) throw Error('Backup contact information is incomplete.');
    const key=`meshcore-secure-v1:${own}`,prior=localStorage.getItem(key),current=prior?JSON.parse(prior):{contacts:{},history:{},seen:{}};
    if(!validDb(current)) throw Error('Existing Secure Coms state cannot be read. Nothing was changed.');
    const merged=structuredClone(current);
    for(const [peer,secret] of Object.entries(secrets)) {
      if(!idPattern.test(peer)||!Object.hasOwn(db.contacts,peer)) throw Error('Invalid backup contact.');
      validateSecret(secret);
      const fp=await fingerprint(secret,own,peer);
      if(db.contacts[peer]?.fingerprint!==fp) throw Error('Backup fingerprint does not match the secret.');
      const existing=(await credential('get',own,peer)).password;
      if(existing && existing!==secret) throw Error('A contact already has a different secret. Nothing was changed; resolve the conflict first.');
      if(merged.contacts[peer] && merged.contacts[peer].fingerprint!==fp) throw Error('A contact has a different fingerprint. Nothing was changed.');
      if(!existing) writes.push({own,peer,secret});
      if(!merged.contacts[peer]) merged.contacts[peer]={fingerprint:fp,verified:false};
      if(!merged.history[peer] && Array.isArray(db.history[peer])) merged.history[peer]=db.history[peer];
    }
    for(const [fp,seen] of Object.entries(db.seen)) {
      if(!/^(?:[a-f0-9]{4} ){5}[a-f0-9]{4}$/.test(fp)||!Array.isArray(seen)||seen.some(n=>typeof n!=='string')) throw Error('Invalid replay history.');
      const existing=merged.seen[fp]||[];if(!Array.isArray(existing)) throw Error('Existing replay history is invalid.');
      const union=[...new Set([...existing,...seen])];if(union.length>20000) throw Error('Replay history is full. Exchange a new shared secret.');merged.seen[fp]=union;
    }
    plans.push({key,prior,value:JSON.stringify(merged)});
    if(entry.groupsState){
      validateGroups(entry.groupsState);const groupKey=`meshcore-secure-groups-v1:${own}`,groupPrior=localStorage.getItem(groupKey),currentGroups=groupPrior?JSON.parse(groupPrior):{groups:{},history:{}};validateGroups(currentGroups);
      for(const [id,group] of Object.entries(entry.groupsState.groups)) if(!Object.hasOwn(currentGroups.groups,id)) currentGroups.groups[id]=group;
      for(const [id,history] of Object.entries(entry.groupsState.history)) if(!Object.hasOwn(currentGroups.history,id)) currentGroups.history[id]=history;
      plans.push({key:groupKey,prior:groupPrior,value:JSON.stringify(currentGroups)});
    }
  }
  if(!writes.length && !plans.length) throw Error('This backup contains no Secure Coms data.');
  if(!confirm(`Restore Secure Coms for ${owners.size} radio(s)? Existing different secrets will not be replaced. Imported contacts require fingerprint verification. Secure Coms will refresh.`)) return false;
  const saved=[],changed=[];
  try {
    for(const item of writes){if((await credential('get',item.own,item.peer)).password) throw Error('Secure credentials changed during restore.');await credential('save',item.own,item.peer,item.secret);saved.push(item)}
    for(const item of plans) if(localStorage.getItem(item.key)!==item.prior) throw Error('Secure state changed during restore.');
    for(const item of plans){localStorage.setItem(item.key,item.value);changed.push(item)}
  } catch(error) {
    let failed=false;
    for(const item of changed.reverse()) try{if(item.prior===null)localStorage.removeItem(item.key);else localStorage.setItem(item.key,item.prior)}catch{failed=true}
    for(const item of saved.reverse()) try{await credential('delete',item.own,item.peer)}catch{failed=true}
    throw Error(failed?'Restore failed and rollback was incomplete. Check Secure Coms before use.':'Restore failed. Changes were rolled back.');
  }
  return true;
}
export function setupSecureBackup(onRestore=()=>{}) {
  const $=id=>document.getElementById(id),status=$('backup-status'),password=$('backup-password'),buttons=[$('backup-export'),$('backup-import')];
  async function run(task){buttons.forEach(b=>b.disabled=true);try{if(password.value.length<12)throw Error('Enter a backup password of at least 12 characters.');if(!window.pywebview?.api?.save_secure_backup)throw Error('This feature requires the updated desktop app.');await task(password.value)}catch(e){status.textContent=e?.message||'Backup operation failed.'}finally{password.value='';buttons.forEach(b=>b.disabled=false)}}
  buttons[0].onclick=()=>run(async pass=>{status.textContent='Encrypting backup…';const contents=await encryptBackup(await collect(),pass);const result=await window.pywebview.api.save_secure_backup(contents);status.textContent=result.error|| (result.cancelled?'Export cancelled.':'Encrypted Secure Coms backup saved.');});
  buttons[1].onclick=()=>run(async pass=>{const result=await window.pywebview.api.open_secure_backup();if(result.error)throw Error(result.error);if(result.cancelled){status.textContent='Import cancelled.';return}status.textContent='Checking backup…';if(await restore(await decryptBackup(result.contents,pass))){onRestore();status.textContent='Secure Coms restored. Compare fingerprints for imported contacts.'}else status.textContent='Import cancelled.'});
}

export {encryptBackup, decryptBackup, restore};

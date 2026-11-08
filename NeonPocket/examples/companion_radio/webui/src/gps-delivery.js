// A confirmation applies only to the exact marker revision acknowledged by a peer.
export function latestGpsDelivery(items, owner, contacts, hex){
 const local=items.filter(x=>x.owner===owner&&x.peer===''&&!x.deleted&&x.name==='My position'&&x.note==='GPS'&&Number.isSafeInteger(x.revision)).sort((a,b)=>b.revision-a.revision)[0];
 if(!local)return null;
 const peers=items.filter(x=>x.owner===owner&&x.id===local.id&&typeof x.peer==='string'&&/^[0-9a-f]{64}$/.test(x.peer));
 return {revision:local.revision,recipients:peers.map(x=>({name:contacts.find(c=>hex(c.publicKey)===x.peer)?.advName||x.peer.slice(0,8),peer:x.peer,confirmed:x.revision===local.revision&&x.ackRevision===local.revision,status:x.revision!==local.revision?'not sent':x.status||'not confirmed'}))};
}

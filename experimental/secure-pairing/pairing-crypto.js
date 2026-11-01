// Prototype primitives for Command pairing. Not wired into the app.
// Pairing approval, secure persistence, transport and group membership are required.
const E = new TextEncoder(), D = new TextDecoder('utf-8', {fatal:true});
const utf8 = x => E.encode(x);
const to64 = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
const from64 = (value, size) => { if(typeof value!=='string'||!/^[A-Za-z0-9_-]+$/.test(value))throw Error('Invalid key encoding'); const out=Uint8Array.from(atob(value.replace(/-/g,'+').replace(/_/g,'/')+'='.repeat((4-value.length%4)%4)),c=>c.charCodeAt(0));if(to64(out)!==value||(size!==undefined&&out.length!==size))throw Error('Invalid key length');return out; };
const hex = bytes => [...new Uint8Array(bytes)].map(x=>x.toString(16).padStart(2,'0')).join('');
const digest = async text => new Uint8Array(await crypto.subtle.digest('SHA-256',utf8(text)));
const requireId = id => {if(typeof id!=='string'||!/^[0-9a-f]{64}$/.test(id))throw Error('Invalid radio identity');return id;};
const canonical = (node,sign,exchange) => `meshcore-command-invite-v1|${requireId(node)}|${sign}|${exchange}`;

export async function createIdentity(node) {
 requireId(node);
 const signing=await crypto.subtle.generateKey({name:'Ed25519'},true,['sign','verify']);
 const exchange=await crypto.subtle.generateKey({name:'X25519'},true,['deriveBits']);
 const sign=to64(await crypto.subtle.exportKey('raw',signing.publicKey));
 const dh=to64(await crypto.subtle.exportKey('raw',exchange.publicKey));
 const signature=to64(await crypto.subtle.sign('Ed25519',signing.privateKey,utf8(canonical(node,sign,dh))));
 return {invitation:{version:1,node,sign,dh,signature},privateIdentity:{signing:await crypto.subtle.exportKey('jwk',signing.privateKey),exchange:await crypto.subtle.exportKey('jwk',exchange.privateKey)}};
}
export async function validateInvitation(invite) {
 if(!invite||typeof invite!=='object'||Array.isArray(invite)||invite.version!==1||Object.keys(invite).sort().join(',')!=='dh,node,sign,signature,version')throw Error('Invalid invitation');
 requireId(invite.node);const sign=from64(invite.sign,32);from64(invite.dh,32);const signature=from64(invite.signature,64);
 const key=await crypto.subtle.importKey('raw',sign,{name:'Ed25519'},false,['verify']);
 if(!await crypto.subtle.verify('Ed25519',key,signature,utf8(canonical(invite.node,invite.sign,invite.dh))))throw Error('Invitation signature is invalid');
 return {version:1,node:invite.node,sign:invite.sign,dh:invite.dh,signature:invite.signature};
}
export async function comparisonCode(a,b){a=await validateInvitation(a);b=await validateInvitation(b);if(a.node===b.node)throw Error('Cannot pair with the same radio identity');const ordered=[canonical(a.node,a.sign,a.dh),canonical(b.node,b.sign,b.dh)].sort();const code=hex(await digest(`meshcore-command-pairing-v1|${ordered.join('|')}`)).slice(0,32);return code.match(/.{4}/g).join(' ');}
export async function pairingKey(own,privateIdentity,peer){
 own=await validateInvitation(own);peer=await validateInvitation(peer);await comparisonCode(own,peer);
 if(!privateIdentity?.exchange||privateIdentity.exchange.x!==own.dh)throw Error('Private key does not match this invitation');
 const priv=await crypto.subtle.importKey('jwk',privateIdentity.exchange,{name:'X25519'},false,['deriveBits']);
 const peerKey=await crypto.subtle.importKey('raw',from64(peer.dh,32),{name:'X25519'},false,[]);
 const shared=await crypto.subtle.deriveBits({name:'X25519',public:peerKey},priv,256);
 const base=await crypto.subtle.importKey('raw',shared,'HKDF',false,['deriveKey']);
 const salt=await digest(`meshcore-command-pairing-salt-v1|${[canonical(own.node,own.sign,own.dh),canonical(peer.node,peer.sign,peer.dh)].sort().join('|')}`);
 return crypto.subtle.deriveKey({name:'HKDF',hash:'SHA-256',salt,info:utf8('MeshCore Command pairing v1')},base,{name:'AES-GCM',length:256},false,['encrypt','decrypt']);
}
export async function sealGroup(keyBytes,signingPrivate,group,sender,sequence,plain){
 if(!/^[0-9a-f]{32}$/.test(group)||!Number.isSafeInteger(sequence)||sequence<1)throw Error('Invalid group envelope');requireId(sender);
 const key=await crypto.subtle.importKey('raw',from64(keyBytes,32),'AES-GCM',false,['encrypt']);const iv=crypto.getRandomValues(new Uint8Array(12)),nonce=to64(iv),context=`MCG1|${group}|${sender}|${sequence}|${nonce}`;
 const ciphertext=to64(await crypto.subtle.encrypt({name:'AES-GCM',iv,additionalData:utf8(context),tagLength:128},key,utf8(plain)));
 const sign=await crypto.subtle.importKey('jwk',signingPrivate,{name:'Ed25519'},false,['sign']);const signature=to64(await crypto.subtle.sign('Ed25519',sign,utf8(`${context}|${ciphertext}`)));
 return {version:1,group,sender,sequence,nonce,ciphertext,signature};
}
export async function openGroup(keyBytes,approvedSigningKey,envelope){
 if(!envelope||envelope.version!==1||!/^[0-9a-f]{32}$/.test(envelope.group)||!Number.isSafeInteger(envelope.sequence)||envelope.sequence<1)throw Error('Invalid group envelope');requireId(envelope.sender);
 const iv=from64(envelope.nonce,12),cipher=from64(envelope.ciphertext),signature=from64(envelope.signature,64),context=`MCG1|${envelope.group}|${envelope.sender}|${envelope.sequence}|${envelope.nonce}`;
 const verify=await crypto.subtle.importKey('raw',from64(approvedSigningKey,32),{name:'Ed25519'},false,['verify']);
 if(!await crypto.subtle.verify('Ed25519',verify,signature,utf8(`${context}|${envelope.ciphertext}`)))throw Error('Unverified sender or altered message');
 const key=await crypto.subtle.importKey('raw',from64(keyBytes,32),'AES-GCM',false,['decrypt']);
 return D.decode(await crypto.subtle.decrypt({name:'AES-GCM',iv,additionalData:utf8(context),tagLength:128},key,cipher));
}

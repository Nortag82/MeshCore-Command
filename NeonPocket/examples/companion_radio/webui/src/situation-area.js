const enc=new TextEncoder(),dec=new TextDecoder('utf-8',{fatal:true});
const b64=s=>btoa(String.fromCharCode(...enc.encode(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
function unb64(s){if(!/^[A-Za-z0-9_-]+$/.test(s))throw Error('Invalid area name.');const raw=atob(s.replace(/-/g,'+').replace(/_/g,'/')+'='.repeat((4-s.length%4)%4));const value=dec.decode(Uint8Array.from(raw,c=>c.charCodeAt(0)));if(b64(value)!==s)throw Error('Invalid area name.');return value}
function n36(s){if(!/^-?[0-9a-z]+$/.test(s))throw Error('Invalid area coordinate.');const n=parseInt(s,36);if(!Number.isSafeInteger(n)||n.toString(36)!==s)throw Error('Invalid area coordinate.');return n}
function validate(area){if(!/^[0-9a-f]{12}$/.test(area.id)||!Number.isSafeInteger(area.revision)||area.revision<1)throw Error('Invalid area identity.');if(area.deleted)return;if(!['area','out'].includes(area.type)||typeof area.name!=='string'||!area.name.trim()||enc.encode(area.name).length>12||/[\x00-\x1f\x7f]/.test(area.name))throw Error('Area name must be 1–12 UTF-8 bytes.');if(!Array.isArray(area.points)||area.points.length<3||area.points.length>8)throw Error('An area needs 3–8 points.');const points=area.points.map(p=>{if(!Array.isArray(p)||p.length!==2||!p.every(Number.isFinite)||Math.abs(p[0])>85||Math.abs(p[1])>180)throw Error('Invalid area coordinates.');return p.map(x=>Math.round(x*100000))});let twice=0;for(let i=0;i<points.length;i++){const a=points[i],b=points[(i+1)%points.length];twice+=a[0]*b[1]-b[0]*a[1]}if(!twice)throw Error('Area points must enclose an area.');}
export function encodeArea(area){validate(area);if(area.deleted)return `AX:${area.id}:${area.revision.toString(36)}`;const points=area.points.map(p=>p.map(x=>Math.round(x*100000))),first=points[0];const coords=points.flatMap((p,i)=>i?[p[0]-first[0],p[1]-first[1]]:p).map(n=>n.toString(36)).join(',');const text=`A2:${area.id}:${area.revision.toString(36)}:${area.type==='out'?'x':'a'}:${coords}:${b64(area.name)}`;return text}
export function decodeArea(text,{allowLong=false}={}){if(typeof text!=='string'||enc.encode(text).length>(allowLong?512:74))throw Error('Invalid area update.');const v=text.split(':');if(v[0]==='AA'||v[0]==='AX'){if(v.length!==3||!/^[0-9a-f]{12}$/.test(v[1]))throw Error('Invalid area update.');const revision=n36(v[2]);if(revision<1)throw Error('Invalid area revision.');return {kind:v[0]==='AA'?'ack':'delete',id:v[1],revision,deleted:v[0]==='AX'}}if(v[0]!=='A2'||v.length!==6||!['a','x'].includes(v[3]))throw Error('Invalid area update.');const coords=v[4].split(',').map(n36);if(coords.length<6||coords.length>16||coords.length%2)throw Error('Invalid area points.');const points=[];for(let i=0;i<coords.length;i+=2)points.push([(coords[i]+(i?coords[0]:0))/100000,(coords[i+1]+(i?coords[1]:0))/100000]);const area={kind:'area',id:v[1],revision:n36(v[2]),type:v[3]==='x'?'out':'area',points,name:unb64(v[5]),deleted:false};validate(area);return area}

// Each encrypted fragment is acknowledged independently; partial polygons are never displayed.
export function encodeAreaPackets(area){
 const raw=encodeArea(area);if(enc.encode(raw).length<=74)return [raw];
 const encoded=b64(raw),chunks=encoded.match(/.{1,32}/g)||[];
 if(chunks.length<2||chunks.length>8)throw Error('Area is too large to share. Use fewer corners or a shorter name.');
 return chunks.map((chunk,index)=>{const frame=`A3:${area.id}:${area.revision.toString(36)}:${index.toString(36)}:${chunks.length.toString(36)}:${chunk}`;if(enc.encode(frame).length>74)throw Error('Area fragment exceeds the secure packet limit.');return frame});
}
export function decodeAreaFragment(text){
 if(typeof text!=='string'||enc.encode(text).length>74)throw Error('Invalid area fragment.');const f=text.split(':');
 if(f[0]==='AF'){if(f.length!==4||!/^[0-9a-f]{12}$/.test(f[1]))throw Error('Invalid fragment acknowledgement.');const revision=n36(f[2]),index=n36(f[3]);if(revision<1||index<0||index>=8)throw Error('Invalid fragment acknowledgement.');return {kind:'fragment-ack',id:f[1],revision,index}}
 if(f[0]!=='A3'||f.length!==6||!/^[0-9a-f]{12}$/.test(f[1])||!/^[A-Za-z0-9_-]{1,32}$/.test(f[5]))throw Error('Invalid area fragment.');const revision=n36(f[2]),index=n36(f[3]),total=n36(f[4]);if(revision<1||total<2||total>8||index<0||index>=total)throw Error('Invalid area fragment.');return {kind:'fragment',id:f[1],revision,index,total,chunk:f[5]};
}
export function combineAreaFragments(parts, fragment){
 const next=parts?{total:parts.total,chunks:[...parts.chunks]}:{total:fragment.total,chunks:Array(fragment.total).fill(null)};
 if(next.total!==fragment.total||next.chunks.length!==fragment.total||(next.chunks[fragment.index]!==null&&next.chunks[fragment.index]!==fragment.chunk))throw Error('Conflicting area fragments.');
 next.chunks[fragment.index]=fragment.chunk;
 if(next.chunks.some(x=>x===null))return {pending:next,area:null};
 const area=decodeArea(unb64(next.chunks.join('')),{allowLong:true});if(area.kind!=='area'||area.id!==fragment.id||area.revision!==fragment.revision)throw Error('Area fragments do not match.');return {pending:next,area};
}

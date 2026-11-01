// Prototype bounded framing only. Receiving a frame never establishes trust.
// Verify the full signed envelope and persist replay state before applying it.
const MAGIC=[0x4d,0x43,0x47,0x31], HEADER=14, MAX_FRAME=165, MAX_CHUNKS=16, MAX_ASSEMBLIES=12, TTL=120000;
const idHex=a=>Array.from(a,x=>x.toString(16).padStart(2,'0')).join('');
export function fragmentMessage(bytes){
 if(!(bytes instanceof Uint8Array)||!bytes.length||bytes.length>(MAX_FRAME-HEADER)*MAX_CHUNKS)throw Error('Message is too large for group framing');
 const id=crypto.getRandomValues(new Uint8Array(8)),count=Math.ceil(bytes.length/(MAX_FRAME-HEADER)),out=[];
 for(let i=0;i<count;i++){const body=bytes.slice(i*(MAX_FRAME-HEADER),(i+1)*(MAX_FRAME-HEADER)),frame=new Uint8Array(HEADER+body.length);frame.set(MAGIC);frame.set(id,4);frame[12]=count;frame[13]=i;frame.set(body,HEADER);out.push(frame)}return out;
}
export class FragmentCollector {
 constructor(now=()=>Date.now()){this.now=now;this.pending=new Map()}
 accept(frame){
  if(!(frame instanceof Uint8Array)||frame.length<=HEADER||frame.length>MAX_FRAME||!MAGIC.every((x,i)=>frame[i]===x))throw Error('Invalid group frame');
  const count=frame[12],index=frame[13];if(count<1||count>MAX_CHUNKS||index>=count)throw Error('Invalid fragment count');
  const now=this.now();for(const [id,entry] of this.pending)if(now-entry.first>TTL)this.pending.delete(id);
  const id=idHex(frame.slice(4,12));let entry=this.pending.get(id);
  if(!entry){if(this.pending.size>=MAX_ASSEMBLIES)throw Error('Too many incomplete group messages');entry={first:now,count,parts:new Map()};this.pending.set(id,entry)}
  if(entry.count!==count){this.pending.delete(id);throw Error('Conflicting fragment count')}
  const part=frame.slice(HEADER),previous=entry.parts.get(index);if(previous){if(previous.length!==part.length||!previous.every((b,i)=>b===part[i])){this.pending.delete(id);throw Error('Conflicting fragment')}return null}
  entry.parts.set(index,part);if(entry.parts.size<count)return null;
  const out=new Uint8Array([...entry.parts.values()].reduce((n,v)=>n+v.length,0));let pos=0;for(let i=0;i<count;i++){const item=entry.parts.get(i);if(!item)throw Error('Missing fragment');out.set(item,pos);pos+=item.length}this.pending.delete(id);return out;
 }
}

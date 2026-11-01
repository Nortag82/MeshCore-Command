import assert from 'node:assert/strict';
import {fragmentMessage,FragmentCollector} from './pairing-framing.js';
let t=0;const c=new FragmentCollector(()=>t),data=crypto.getRandomValues(new Uint8Array(570)),frames=fragmentMessage(data);assert(frames.length>1&&frames.every(f=>f.length<=165));assert.equal(c.accept(frames[1]),null);assert.equal(c.accept(frames[1]),null);let result;for(const f of frames.filter((_,i)=>i!==1))result=c.accept(f);assert.deepEqual(result,data);
const altered=frames[0].slice();const e=new FragmentCollector();e.accept(altered);altered[14]^=1;assert.throws(()=>e.accept(altered),/Conflicting/);assert.throws(()=>fragmentMessage(new Uint8Array(3000)),/too large/);
const old=fragmentMessage(data),z=new FragmentCollector(()=>t);z.accept(old[0]);t=120001;z.accept(frames[0]);assert.equal(z.pending.size,1);
console.log('PASS group framing bounds, out-of-order delivery, duplicate handling, expiry and conflict rejection');

import assert from 'node:assert/strict';
import {encodeArea,decodeArea} from './situation-area.js';
for(const type of ['area','out']){const area={id:'012345abcdef',revision:1800000000000,name:'Zone',type,points:[[56.1,-4.7],[56.101,-4.7],[56.101,-4.701],[56.1,-4.701]]};const wire=encodeArea(area);assert(new TextEncoder().encode(wire).length<=74);const decoded=decodeArea(wire);assert.equal(decoded.type,type);assert.deepEqual(decoded.points,area.points);assert.equal(decoded.name,area.name);assert.equal(decodeArea('AA:'+area.id+':'+area.revision.toString(36)).kind,'ack');assert.equal(decodeArea(encodeArea({...area,deleted:true})).kind,'delete')}
assert.throws(()=>encodeArea({id:'012345abcdef',revision:1,name:'Bad',type:'out',points:[[1,1],[2,2],[3,3]]}),/enclose/);
assert.throws(()=>decodeArea('A2:malformed'));
console.log('Area packets round-trip normal and out-of-bounds polygons, acknowledgements and deletions.');

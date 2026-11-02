import assert from 'node:assert/strict';
import {symbols,encodeSymbol,decodeSymbol,symbolSvg} from './situation-symbols.js';
assert.equal(symbols.length,29);
for(let i=0;i<symbols.length;i++)for(const forwarded of [false,true]){const wire=encodeSymbol(i,forwarded),decoded=decodeSymbol(wire);assert.deepEqual(decoded,{index:i,forwarded});if(i<8)assert.equal(wire,i+(forwarded?8:0));else assert(wire>=16);}
assert.throws(()=>decodeSymbol(63));assert.throws(()=>encodeSymbol(999));assert(symbolSvg(10,26,{}).includes('SAR'));
console.log('Marker symbols preserve legacy codes and round-trip new civilian and emergency symbols.');

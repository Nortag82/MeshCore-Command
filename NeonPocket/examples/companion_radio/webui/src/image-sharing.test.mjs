import test from 'node:test';
import assert from 'node:assert/strict';
import {imageFrames,parseImageFrame} from './image-sharing.js';
const encode=b=>Buffer.from(b).toString('base64url');
test('image frames stay within secure message limit and round trip',()=>{const bytes=Uint8Array.from([255,216,...Array(1200).fill(42),255,217]),data=encode(bytes),frames=imageFrames('abcdefabcdef',data);assert(frames.length>1);assert(frames.every(x=>Buffer.byteLength(x)<=74));const decoded=frames.map(parseImageFrame);assert(decoded.every((x,i)=>x.index===i&&x.total===frames.length));assert.equal(decoded.map(x=>x.data).join(''),data);assert.deepEqual(parseImageFrame('IA1:abcdefabcdef:1'),{type:'ack',id:'abcdefabcdef',index:1})});
test('malformed and oversized packets are rejected',()=>{for(const v of ['IM1:abcdefabcdef:2:2:abc','IM1:abcdefabcdef:00:1:abc','IM1:abcdefabcdef:0:zz:abc','IA1:abcdefabcdef:zz','IM1:xxx:0:1:abc','IM1:abcdefabcdef:0:2:x'])assert.throws(()=>parseImageFrame(v));assert.throws(()=>imageFrames('abcdefabcdef',encode([1,2,3])));assert.throws(()=>imageFrames('abcdefabcdef',encode([255,216,...Array(1534).fill(2),255,217]))) });

import test from 'node:test';
import assert from 'node:assert/strict';
import {EventEmitter} from 'node:events';
import {channelSender,parseContactCard,importContactCard} from './src/channel-contact.js';

function card() { const b=new Uint8Array(103); b[0]=0x11; b[1]=0; b.fill(7,2,34); return `meshcore://${Buffer.from(b).toString('hex')}`; }
test('channel sender is display text, not a contact identity',()=>{
 assert.equal(channelSender({key:'ch:1',direction:'in',text:'The Toecutter: hello'}),'The Toecutter');
 assert.equal(channelSender({key:'dm:abc',direction:'in',text:'The Toecutter: hello'}),'');
 assert.equal(channelSender({key:'ch:1',direction:'out',text:'The Toecutter: hello'}),'');
 assert.equal(channelSender({key:'ch:1',direction:'in',text:'no sender'}),'');
});
test('only bounded MeshCore advert cards are accepted',()=>{
 const parsed=parseContactCard(card()); assert.equal(parsed.publicKey.length,32); assert.ok(parsed.publicKey.every(x=>x===7));
 for(const bad of ['', 'meshcore://channel/add?name=x&secret=y', 'meshcore://abc', 'meshcore://'+'00'.repeat(103), 'meshcore://'+'11'.repeat(176)]) assert.throws(()=>parseContactCard(bad));
 const malformed=Buffer.from(card().slice(11),'hex'); malformed[1]=0xff;
 assert.throws(()=>parseContactCard('meshcore://'+malformed.toString('hex')));
});
test('import confirms the exact card key on the connected radio',async()=>{
 const parsed=parseContactCard(card()),radio=new EventEmitter(); let frames=[], contacts=[];
 radio.sendToRadioFrame=async frame=>{frames.push(frame);queueMicrotask(()=>radio.emit('rx',new Uint8Array([0])))};
 radio.getContacts=async()=>contacts;
 const expected={publicKey:parsed.publicKey,type:1,advName:'Toecutter Base'};
 const actual=await importContactCard(radio,parsed,{canContinue:()=>true,newAdvertCode:0x8a,wait:async()=>{contacts=[expected]}});
 assert.equal(actual,expected); assert.equal(frames[0][0],18); assert.deepEqual(frames[0].slice(1),parsed.card); assert.equal(radio.listenerCount(0x8a),0);
});
test('manual advert is added only after the matching key is received',async()=>{
 const parsed=parseContactCard(card()),radio=new EventEmitter(); let contacts=[],added=0;
 const advert={publicKey:parsed.publicKey,type:1,flags:0,outPathLen:255,outPath:new Uint8Array(64),advName:'Toecutter Base',lastAdvert:1,advLat:0,advLon:0};
 radio.sendToRadioFrame=async()=>{queueMicrotask(()=>{radio.emit('rx',new Uint8Array([0]));radio.emit(0x8a,advert)})};
 radio.getContacts=async()=>contacts;
 radio.addOrUpdateContact=async(...args)=>{added++;assert.deepEqual(args[0],parsed.publicKey);contacts=[advert]};
 assert.equal(await importContactCard(radio,parsed,{canContinue:()=>true,newAdvertCode:0x8a,wait:async()=>{}}),advert);
 assert.equal(added,1); assert.equal(radio.listenerCount(0x8a),0);
});
test('unconfirmed imports and disconnects never report success',async()=>{
 const parsed=parseContactCard(card()),radio=new EventEmitter();
 radio.sendToRadioFrame=async()=>queueMicrotask(()=>radio.emit('rx',new Uint8Array([0])));
 radio.getContacts=async()=>[{publicKey:new Uint8Array(32),type:1}];
 await assert.rejects(importContactCard(radio,parsed,{canContinue:()=>true,newAdvertCode:0x8a,wait:async()=>{}}),/did not confirm/);
 await assert.rejects(importContactCard(radio,parsed,{canContinue:()=>false,newAdvertCode:0x8a,wait:async()=>{}}),/Connect/);
 assert.equal(radio.listenerCount(0x8a),0);
});

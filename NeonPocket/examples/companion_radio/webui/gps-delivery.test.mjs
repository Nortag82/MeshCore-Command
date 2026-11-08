import test from 'node:test';
import assert from 'node:assert/strict';
import {latestGpsDelivery} from './src/gps-delivery.js';
const owner='01'.repeat(32),alice='02'.repeat(32),bob='03'.repeat(32),hex=bytes=>Array.from(bytes||[],v=>v.toString(16).padStart(2,'0')).join('');
const item=(peer,revision,ackRevision,status='sent')=>({owner,peer,id:'123456789abc',revision,ackRevision,status,name:'My position',note:'GPS'});
test('shows confirmation only for the latest exact revision and peer',()=>{const items=[item('',200,0),item(alice,200,200),item(bob,200,100)];const result=latestGpsDelivery(items,owner,[{publicKey:new Uint8Array(32).fill(2),advName:'Alice'}],hex);assert.equal(result.revision,200);assert.deepEqual(result.recipients.map(x=>[x.name,x.confirmed]),[['Alice',true],['03030303',false]]);});
test('does not mark an older sent position as the current one',()=>{const result=latestGpsDelivery([item('',200,0),item(alice,100,100),item(bob,200,0,'not sent')],owner,[],hex);assert.deepEqual(result.recipients.map(x=>[x.confirmed,x.status]),[[false,'not sent'],[false,'not sent']]);});
test('ignores unrelated, deleted and other-radio markers',()=>{assert.equal(latestGpsDelivery([{...item('',300,0),owner:alice}, {...item('',200,0),deleted:true}, {...item('',100,0),note:'other'}],owner,[],hex),null)});

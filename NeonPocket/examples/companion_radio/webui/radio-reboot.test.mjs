import test from 'node:test';
import assert from 'node:assert/strict';
import {rebootLocalRadio} from './src/radio-reboot.js';
import Connection from '@liamcottle/meshcore.js/src/connection/connection.js';

const fixture=()=>{const state={connected:true,self:{name:'Test board',publicKey:new Uint8Array([1,2,3,4])}},sent=[];let radio=new Connection();radio.sendToRadioFrame=async bytes=>sent.push([...bytes]);return {state,sent,getConnection:()=>radio,run:fn=>fn(),confirmAction:()=>true,setRadio:r=>radio=r}};
test('sends the companion reboot command only after confirmation',async()=>{const f=fixture();assert.equal(await rebootLocalRadio({...f,confirmAction:()=>false}),false);assert.equal(f.sent.length,0);assert.equal(await rebootLocalRadio(f),true);assert.deepEqual(f.sent,[[19,114,101,98,111,111,116]]);});
test('does not send after disconnect or a board change while queued',async()=>{for(const change of ['disconnect','radio','identity']){const f=fixture();f.run=async fn=>{if(change==='disconnect')f.state.connected=false;if(change==='radio')f.setRadio(new Connection());if(change==='identity')f.state.self.publicKey=new Uint8Array([9]);return fn()};await assert.rejects(rebootLocalRadio(f),/changed/);assert.equal(f.sent.length,0)}});
test('reports failed writes and refuses missing radio',async()=>{const f=fixture();f.getConnection().sendToRadioFrame=async()=>{throw Error('write failed')};await assert.rejects(rebootLocalRadio(f),/write failed/);f.state.connected=false;await assert.rejects(rebootLocalRadio(f),/Connect/)});

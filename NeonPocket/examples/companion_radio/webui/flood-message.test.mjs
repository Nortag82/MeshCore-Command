import test from 'node:test';
import assert from 'node:assert/strict';
import {sendFloodMessage} from './src/flood-message.js';
const key=new Uint8Array(32).fill(3);
test('clears the saved path before sending and returns the radio acknowledgement',async()=>{const calls=[],radio={resetPath:async k=>calls.push(['reset',k]),sendTextMessage:async(k,text,type)=>{calls.push(['send',k,text,type]);return {result:1,expectedAckCrc:123}}};const response=await sendFloodMessage(radio,key,'hello');assert.deepEqual(calls.map(c=>c[0]),['reset','send']);assert.equal(response.expectedAckCrc,123)});
test('does not send when route reset fails or radio changes',async()=>{let sent=0,valid=true;const radio={resetPath:async()=>{valid=false},sendTextMessage:async()=>sent++};await assert.rejects(sendFloodMessage(radio,key,'x',{canSend:()=>valid}),/not sent/);radio.resetPath=async()=>{throw Error('rejected')};await assert.rejects(sendFloodMessage(radio,key,'x'),/not sent/);assert.equal(sent,0)});
test('reports an unexpected direct or unreported route without resending',async()=>{for(const result of [0,undefined]){let count=0,warning='';const radio={resetPath:async()=>{},sendTextMessage:async()=>{count++;return {result}}};await sendFloodMessage(radio,key,'x',{onRouteWarning:w=>warning=w});assert.equal(count,1);assert.match(warning,/direct|did not report/)}});

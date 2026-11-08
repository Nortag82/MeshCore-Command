import {EventEmitter} from 'node:events';
import assert from 'node:assert/strict';
import {requestFloodTelemetry} from './src/telemetry-request.js';
const C={ResponseCodes:{Sent:1,Err:2},PushCodes:{TelemetryResponse:3}}, key=Uint8Array.from([1,2,3,4,5,6]);
function radio(route=1){const c=new EventEmitter();c.calls=[];c.resetPath=async()=>c.calls.push('reset');c.login=async()=>c.calls.push('login');c.sendCommandSendTelemetryReq=async()=>{c.calls.push('telemetry');c.emit(1,{result:route,estTimeout:1});c.emit(3,{pubKeyPrefix:key,lppSensorData:[42]})};return c}
let c=radio(),route;await requestFloodTelemetry(c,key,C,{password:'test',onRoute:r=>route=r});assert.deepEqual(c.calls,['reset','login','reset','telemetry']);assert.equal(route,1);assert.equal(c.listenerCount(3),0);
c=radio(0);await requestFloodTelemetry(c,key,C,{onRoute:r=>route=r});assert.deepEqual(c.calls,['reset','telemetry']);assert.equal(route,0);
c=radio();c.resetPath=async()=>{throw Error('failed')};await assert.rejects(requestFloodTelemetry(c,key,C),/Could not clear/);assert.deepEqual(c.calls,[]);
c=radio();c.login=async()=>{c.calls.push('login');throw Error('denied')};await assert.rejects(requestFloodTelemetry(c,key,C,{password:'test'}),/login failed/);assert.deepEqual(c.calls,['reset','login']);
c=radio();let allowed=true;c.resetPath=async()=>{c.calls.push('reset');allowed=false};await assert.rejects(requestFloodTelemetry(c,key,C,{canSend:()=>allowed}),/changed/);assert.deepEqual(c.calls,['reset']);
console.log('Monitoring telemetry clears paths, reports radio routing, and aborts safely.');

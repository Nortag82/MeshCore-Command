import {EventEmitter} from 'node:events';
import assert from 'node:assert/strict';
import {requestTelemetry} from './src/telemetry-request.js';
const C={ResponseCodes:{Sent:1,Err:2},PushCodes:{TelemetryResponse:3}},conn=new EventEmitter(),key=Uint8Array.from([1,2,3,4,5,6]);
conn.sendCommandSendTelemetryReq=async()=>{conn.emit(1,{estTimeout:1});conn.emit(3,{pubKeyPrefix:Uint8Array.from([9,2,3,4,5,6])});conn.emit(3,{pubKeyPrefix:key,lppSensorData:[42]})};
const value=await requestTelemetry(conn,key,C);assert.deepEqual(value.lppSensorData,[42]);assert.equal(conn.listenerCount(3),0);console.log('Matching telemetry survives unrelated responses and listeners are cleaned up.');

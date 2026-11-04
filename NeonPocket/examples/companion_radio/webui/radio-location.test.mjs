import test from 'node:test';import assert from 'node:assert/strict';import {setRadioLocation} from './src/radio-location.js';
const key=new Uint8Array(32).fill(1),hex=a=>Array.from(a).join(',');
function fixture(){const state={connected:true,self:{publicKey:key}},calls=[];let radio={setAdvertLatLong:async(a,b)=>calls.push([a,b]),getSelfInfo:async()=>({publicKey:key,advLat:56026118,advLon:-4750772})};return {state,calls,radio,opts:{getState:()=>state,getConnection:()=>radio,exclusive:f=>f(),hex}}}
test('writes signed microdegrees and verifies the readback',async()=>{const f=fixture();await setRadioLocation(f.opts,56.026118,-4.750772);assert.deepEqual(f.calls,[[56026118,-4750772]]);assert.equal(f.state.self.advLat,56026118)});
test('rejects invalid or unavailable coordinates before transmitting',async()=>{const f=fixture();for(const pos of [[NaN,2],[91,0],[0,0]])await assert.rejects(setRadioLocation(f.opts,...pos));f.state.connected=false;await assert.rejects(setRadioLocation(f.opts,56,-4));assert.equal(f.calls.length,0)});
test('does not claim success when radio reports another position',async()=>{const f=fixture();f.radio.getSelfInfo=async()=>({publicKey:key,advLat:1,advLon:2});await assert.rejects(setRadioLocation(f.opts,56,-4),/different location/)});
test('rejects if radio identity changes',async()=>{const f=fixture();f.radio.getSelfInfo=async()=>({publicKey:new Uint8Array(32),advLat:56000000,advLon:-4000000});await assert.rejects(setRadioLocation(f.opts,56,-4),/connection changed/)});

test('accepts a one-microdegree rounding difference',async()=>{const f=fixture();f.radio.getSelfInfo=async()=>({publicKey:key,advLat:56026117,advLon:-4750771});await setRadioLocation(f.opts,56.026118,-4.750772)});

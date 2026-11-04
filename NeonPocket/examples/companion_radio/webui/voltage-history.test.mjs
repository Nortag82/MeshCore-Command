import test from 'node:test';
import assert from 'node:assert/strict';
import {decodeTelemetry} from './src/telemetry.js';
import {WEEK_MS,deviceVoltage,addVoltageReading,pruneVoltageHistory,voltageGraphGeometry} from './src/voltage-history.js';
const now=1800000000000, reading=decodeTelemetry([1,116,1,151]);
test('only genuine device voltage is stored',()=>{assert.equal(deviceVoltage(reading),4.07);assert.equal(deviceVoltage(decodeTelemetry([2,116,1,151])),null);assert.equal(deviceVoltage({values:{}}),null);assert.deepEqual(addVoltageReading([],reading,now),[[now,4.07]]);assert.deepEqual(addVoltageReading([[now,4.07]],{values:[]},now),[[now,4.07]])});
test('history is bounded and chronological',()=>{const rows=[[now-601000,4],[now-600001,4.1],[now,4.2],[now-WEEK_MS-1,3],[now+1,4]];const history=pruneVoltageHistory(rows,now);assert.equal(history.at(-1)[1],4.2);assert.ok(history.every(([time])=>time>=now-WEEK_MS&&time<=now));assert.ok(history.every((v,i)=>!i||v[0]>history[i-1][0]));const dense=Array.from({length:11000},(_,i)=>[now-i*60000,4]);assert.ok(pruneVoltageHistory(dense,now).length<=1009)});
test('chart leaves gaps and has finite scaling for a single voltage',()=>{const g=voltageGraphGeometry([[now-7200000,4],[now-600000,4],[now,4]],now,3600000);assert.equal(g.points.length,3);assert.equal(g.segments.length,1);assert.ok(g.min<4&&g.max>4);assert.ok(g.points.every(p=>Number.isFinite(p.x)&&Number.isFinite(p.y)))});

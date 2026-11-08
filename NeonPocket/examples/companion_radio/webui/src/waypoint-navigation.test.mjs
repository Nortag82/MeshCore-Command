import test from 'node:test';
import assert from 'node:assert/strict';
import {bearingToWaypoint,formatBearing} from './waypoint-navigation.js';
test('true bearings for cardinal directions and dateline',()=>{for(const [target,want] of [[[1,0],0],[[0,1],90],[[-1,0],180],[[0,-1],270]])assert(Math.abs(bearingToWaypoint([0,0],target)-want)<0.001);assert(Math.abs(bearingToWaypoint([0,179],[0,-179])-90)<0.001)});
test('formats compass direction and wraps rounded north',()=>{assert.equal(formatBearing(90),'090° E true');assert.equal(formatBearing(359.9),'000° N true');assert.equal(formatBearing(bearingToWaypoint([1,2],[1,2])),'At waypoint');assert.throws(()=>bearingToWaypoint([NaN,0],[0,0]))});

import test from 'node:test';
import assert from 'node:assert/strict';
import {currentGpsMarkers,previousOwnTracker} from './src/gps-markers.js';
const gps=(owner,id,revision,peer='')=>({owner,id,revision,peer,name:'My position',note:'GPS',symbol:0});
test('one newest position per station while keeping other markers',()=>{const old=gps('a','01',1),latest=gps('a','02',2),other=gps('b','03',1),ordinary={owner:'a',id:'04',name:'Hospital'};assert.deepEqual(currentGpsMarkers([old,latest,other,ordinary]),[latest,other,ordinary])});
test('reuses the most recent local tracker, not another contact or deleted tracker',()=>{const old=gps('a','01',1),latest=gps('a','02',2),received=gps('b','03',3,'a'),deleted={...gps('a','04',4),deleted:true};assert.equal(previousOwnTracker([old,latest,received,deleted],'a'),latest)});

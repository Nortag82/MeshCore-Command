import assert from 'node:assert/strict';
import {createDeliveryTracker} from './delivery-tracker.js';
let time=100,delivered=0;const radio={},other={},tracker=createDeliveryTracker({now:()=>time,ttl:1000});
let token=tracker.begin(radio);assert.equal(tracker.receive(radio,7),true);assert.equal(tracker.finish(token,7,()=>delivered++),true);assert.equal(delivered,1);
token=tracker.begin(radio);assert.equal(tracker.finish(token,8,()=>delivered++),false);assert.equal(tracker.receive(other,8),false);assert.equal(tracker.receive(radio,9),false);assert.equal(tracker.receive(radio,8),true);assert.equal(delivered,2);assert.equal(tracker.receive(radio,8),false);
token=tracker.begin(radio);tracker.finish(token,10,()=>delivered++);time+=1001;assert.equal(tracker.receive(radio,10),false);assert.equal(delivered,2);
token=tracker.begin(radio);tracker.finish(token,11,()=>delivered++);token=tracker.begin(radio);tracker.finish(token,11,()=>delivered++);assert.equal(tracker.receive(radio,11),false);assert.equal(delivered,2);
token=tracker.begin(radio);tracker.cancel(token);assert.equal(tracker.receive(radio,12),false);
console.log('Radio acknowledgement tracking handles early, late, unrelated, expired and ambiguous acknowledgements.');

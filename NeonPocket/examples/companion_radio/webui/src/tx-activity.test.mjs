import assert from 'node:assert/strict';
import {createTxActivity} from './tx-activity.js';

class Element {
  constructor(){this.children=[];this.hidden=false;this.classList={toggle(){}};this.nodes={};this.textContent='';}
  setAttribute(){} append(...x){this.children.push(...x)} prepend(x){this.children.unshift(x)} replaceChildren(){this.children=[];this.textContent=''}
  set innerHTML(value){for(const key of ['#traffic-tx','#traffic-rx','.traffic-events','.traffic-close'])if(value.includes(key.slice(1)))this.nodes[key]=new Element()}
  querySelector(key){return this.nodes[key]}
}
const body=new Element(),top=new Element(),doc={body,createElement:()=>new Element(),querySelector:()=>top};
const originalSetInterval=globalThis.setInterval,originalSetTimeout=globalThis.setTimeout,originalClearTimeout=globalThis.clearTimeout;
globalThis.setInterval=()=>0;globalThis.setTimeout=()=>0;globalThis.clearTimeout=()=>{};
try{
 let clock=100000;const ui=createTxActivity({document:doc,getView:()=> 'messages',now:()=>clock});
 const widget=top.children[0],panel=body.children[0],tx=widget.querySelector('#traffic-tx'),rx=widget.querySelector('#traffic-rx'),events=panel.querySelector('.traffic-events');
 assert.equal(tx.textContent,'TX —');ui.connected(true);ui.stats({sent:10,recv:20});clock+=1000;ui.stats({sent:11,recv:21});assert.equal(tx.textContent,'TX activity');assert.equal(rx.textContent,'RX activity');
 const radio={},m=ui.begin('Direct message');assert.equal(panel.hidden,false);ui.queued(m,{expectedAckCrc:42},radio,false);assert.match(events.children[0].children[1].textContent,/awaiting radio acknowledgement/);ui.acknowledge(42,radio);assert.match(events.children[0].children[1].textContent,/Radio delivery acknowledged/);
 const early=ui.begin('Direct message');ui.acknowledge(51,radio);ui.queued(early,{expectedAckCrc:51},radio,false);assert.match(events.children[0].children[1].textContent,/acknowledged/);
 const channel=ui.begin('Channel message');ui.queued(channel,undefined,radio,true);assert.match(events.children[0].children[1].textContent,/no delivery confirmation/);
 const failed=ui.begin('Direct message');ui.failed(failed);assert.match(events.children[0].children[1].textContent,/Failed to queue/);ui.connected(false);assert.equal(tx.textContent,'TX —');assert.equal(rx.textContent,'RX —');
 console.log('TX and RX activity, queue results and acknowledgements verified.');
}finally{globalThis.setInterval=originalSetInterval;globalThis.setTimeout=originalSetTimeout;globalThis.clearTimeout=originalClearTimeout}

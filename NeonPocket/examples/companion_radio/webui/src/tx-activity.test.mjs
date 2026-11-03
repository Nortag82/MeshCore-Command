import assert from 'node:assert/strict';
import {createTxActivity} from './tx-activity.js';
class Element {
 constructor(){this.children=[];this.classList={toggle(){}};this.nodes={};this.textContent='';}
 setAttribute(){} prepend(x){this.children.unshift(x)}
 set innerHTML(value){for(const key of ['#traffic-tx','#traffic-rx'])if(value.includes(key.slice(1)))this.nodes[key]=new Element()}
 querySelector(key){return this.nodes[key]}
}
const body=new Element(),top=new Element(),doc={body,createElement:()=>new Element(),querySelector:()=>top};
const original=globalThis.setInterval;globalThis.setInterval=()=>0;
try{
 let clock=100000;const ui=createTxActivity({document:doc,now:()=>clock});
 const widget=top.children[0],tx=widget.querySelector('#traffic-tx'),rx=widget.querySelector('#traffic-rx');
 assert.equal(body.children.length,0,'no transmission popup is added');assert.equal(tx.textContent,'TX —');
 ui.connected(true);ui.stats({sent:10,recv:20});clock+=1000;ui.stats({sent:11,recv:21});assert.equal(tx.textContent,'TX activity');assert.equal(rx.textContent,'RX activity');
 clock+=13000;ui.stats({sent:11,recv:21});assert.equal(tx.textContent,'TX quiet');assert.equal(rx.textContent,'RX quiet');ui.received();assert.equal(rx.textContent,'RX activity');ui.connected(false);assert.equal(tx.textContent,'TX —');assert.equal(rx.textContent,'RX —');
 console.log('TX/RX indicators work without a transmission popup.');
}finally{globalThis.setInterval=original}

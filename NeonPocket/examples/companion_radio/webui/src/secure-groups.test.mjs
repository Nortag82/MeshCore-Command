import assert from 'node:assert/strict';
import {createSecureGroups} from './secure-groups.js';
const elements=new Map();
class FakeElement {constructor(id=''){this.id=id;this.value='';this.children=[];this.hidden=false;this.textContent='';this.disabled=false;this.checked=false} add(o){this.children.push(o)} append(...items){this.children.push(...items)} replaceChildren(...items){this.children=[...items]} querySelectorAll(query){const inputs=this.children.flatMap(x=>x.children||[]).filter(x=>x.type==='checkbox');return query.includes(':checked')?inputs.filter(x=>x.checked):inputs} setAttribute(){} }
function el(id){if(!elements.has(id))elements.set(id,new FakeElement(id));return elements.get(id)}
globalThis.document={getElementById:el,createElement:()=>new FakeElement(),createTextNode:text=>({textContent:text}),hidden:false,hasFocus:()=>true,addEventListener(){}};globalThis.window={addEventListener(){}};globalThis.Option=class{constructor(label,value){this.label=label;this.value=value}};globalThis.confirm=()=>true;
const own='11'.repeat(32),peer='22'.repeat(32),stranger='33'.repeat(32),id='test1234';
const storage=new Map([['meshcore-secure-groups-v1:'+own,JSON.stringify({groups:{[id]:{name:'Test',members:[peer]}},history:{}})]]);
globalThis.localStorage={getItem:k=>storage.get(k)??null,setItem:(k,v)=>storage.set(k,v)};
const state={self:{publicKey:own},contacts:[{publicKey:peer,advName:'Peer'}],view:'home',connected:true};let sent=[];
const secureComs={verifiedContacts:()=>[{publicKey:peer,advName:'Peer'}],sendObject:async(p,text,delivery)=>{sent.push([p,text]);delivery?.onQueued?.({wire:'cipher',response:{expectedAckCrc:77}});return {wire:'cipher'}},openGroupRecord:async()=>`G1:${id}:hello`,selectContact(){}};
const groups=createSecureGroups({state,secureComs,hex:x=>x});groups.refresh();assert.deepEqual(groups.groupMembers(id),[peer]);assert.equal(groups.ingest(stranger,`G1:${id}:hello`,'cipher1'),false);assert.equal(groups.ingest(peer,`G1:${id}:hello`,'cipher1'),true);assert.equal(groups.unread(),1);assert.equal(groups.openUnread(),true);state.view='secure';groups.refresh();assert.equal(groups.unread(),0);
el('secure-group-input').value='hello';await el('secure-group-compose').onsubmit({preventDefault(){}});assert.deepEqual(sent,[[peer,'hello']]);assert.equal(el('secure-group-status').textContent,'queued for 1/1. Radio acknowledgement is not a read receipt.');
console.log('Secure groups reject non-members, record unread messages, and send via individual secure connections.');

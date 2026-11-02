import assert from 'node:assert/strict';
import {createSecureComs} from './secure-coms.js';
const elements=new Map();
function element(id){if(!elements.has(id))elements.set(id,{id,hidden:false,textContent:'',value:'',checked:false,disabled:false,parentElement:{setAttribute(){}},setAttribute(){},replaceChildren(){},add(){},append(){},addEventListener(){}});return elements.get(id)}
globalThis.document={getElementById:element,addEventListener(){},hidden:false,hasFocus:()=>true,createElement:()=>element('generated')};
globalThis.window={addEventListener(){}};
globalThis.Option=class {constructor(text,value){this.text=text;this.value=value}};
const own='11'.repeat(32),peer='22'.repeat(32),db={contacts:{[peer]:{fingerprint:'1234 1234 1234 1234 1234 1234',verified:false}},history:{[peer]:[{out:false,unread:true}]},seen:{}};
const storage=new Map([['meshcore-secure-v1:'+own,JSON.stringify(db)]]);
globalThis.localStorage={getItem:k=>storage.get(k)??null,setItem:(k,v)=>storage.set(k,v)};
const state={self:{publicKey:own},contacts:[{type:1,publicKey:peer,advName:'Test'}],view:'home',connected:true};
const secure=createSecureComs({state,getConnection:()=>null,exclusive:()=>{},hex:x=>x,toast:()=>{}});
secure.refresh();assert.equal(element('secure-unread-indicator').hidden,false);assert.equal(element('secure-unread-indicator').textContent,'Secure messages · 1');
secure.selectFirstUnread();state.view='secure';secure.refresh();assert.equal(element('secure-unread-indicator').hidden,true);assert.equal(JSON.parse(storage.get('meshcore-secure-v1:'+own)).history[peer][0].unread,false);
console.log('Secure unread indicator displays, opens, and clears correctly.');

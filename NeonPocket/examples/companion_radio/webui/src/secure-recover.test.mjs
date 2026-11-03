import assert from 'node:assert/strict';
import {createSecureComs} from './secure-coms.js';
const elements=new Map();
function el(id){if(!elements.has(id))elements.set(id,{id,value:'',checked:false,disabled:false,hidden:false,textContent:'',parentElement:{setAttribute(){}},setAttribute(){},replaceChildren(){},add(){},append(){},addEventListener(){}});return elements.get(id)}
globalThis.document={getElementById:el,addEventListener(){},hidden:false,hasFocus:()=>true,createElement:()=>el('generated')};
globalThis.window={addEventListener(){}};globalThis.Option=class {constructor(text,value){this.text=text;this.value=value}};
const store=new Map();globalThis.localStorage={getItem:k=>store.get(k)??null,setItem:(k,v)=>store.set(k,v)};
const own='11'.repeat(32),peer='22'.repeat(32),state={self:{publicKey:own},contacts:[{type:1,publicKey:peer,advName:'Test'}],view:'home',connected:true};
globalThis.fetch=async()=>({ok:true,json:async()=>({password:'MCC1-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'})});
const secure=createSecureComs({state,getConnection:()=>null,exclusive:()=>{},hex:x=>x,toast:()=>{}});
secure.selectContact(peer);await el('secure-recover').onclick();
const db=JSON.parse(store.get('meshcore-secure-v1:'+own));assert.equal(db.contacts[peer].verified,false);assert.ok(db.contacts[peer].fingerprint);assert.match(el('secure-status').textContent,/replay history/);
const saved=store.get('meshcore-secure-v1:'+own);await el('secure-recover').onclick();assert.equal(store.get('meshcore-secure-v1:'+own),saved);
console.log('Saved credential restores as unverified without replacing existing state.');

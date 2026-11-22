import test from 'node:test';
import assert from 'node:assert/strict';
import {createSecureComs,openEnvelope,seal} from './src/secure-coms.js';
const elements=new Map();function element(id){if(!elements.has(id))elements.set(id,{hidden:false,textContent:'',value:'',checked:false,disabled:false,parentElement:{setAttribute(){}},setAttribute(){},replaceChildren(){},add(){},append(){},addEventListener(){}});return elements.get(id)}
globalThis.document={getElementById:element,addEventListener(){},hidden:false,hasFocus:()=>true,createElement:()=>element('generated')};globalThis.window={addEventListener(){}};globalThis.Option=class{constructor(text,value){this.text=text;this.value=value}};
const own='11'.repeat(32),peer='22'.repeat(32),hex=bytes=>Array.from(bytes,x=>x.toString(16).padStart(2,'0')).join(''),secret=new Uint8Array(32).fill(4),password='MCC1-'+Buffer.from(secret).toString('base64url');const storage=new Map([['meshcore-secure-v1:'+own,JSON.stringify({contacts:{[peer]:{verified:true}},history:{},seen:{}})]]);globalThis.localStorage={getItem:k=>storage.get(k)??null,setItem:(k,v)=>storage.set(k,v)};globalThis.fetch=async()=>({ok:true,json:async()=>({password})});
test('secure map packets remain encrypted and request flooding',async()=>{const calls=[],state={self:{publicKey:new Uint8Array(32).fill(17)},contacts:[{type:1,publicKey:new Uint8Array(32).fill(34),advName:'Peer'}],connected:true},radio={resetPath:async()=>calls.push('reset'),sendTextMessage:async(k,wire)=>{calls.push('send');return {result:1,expectedAckCrc:42}}},secure=createSecureComs({state,getConnection:()=>radio,exclusive:fn=>fn(),hex,toast:()=>{}});const sent=await secure.sendObject(peer,'S2:test');assert.deepEqual(calls,['reset','send']);assert.match(sent.wire,/^MC1\./);assert.equal((await openEnvelope(secret,own,peer,sent.wire)).text,'S2:test')});
test('stopping a secure transfer during path reset prevents transmission',async()=>{let allowed=true,sent=0;const state={self:{publicKey:new Uint8Array(32).fill(17)},contacts:[{type:1,publicKey:new Uint8Array(32).fill(34)}],connected:true},radio={resetPath:async()=>{allowed=false},sendTextMessage:async()=>sent++},secure=createSecureComs({state,getConnection:()=>radio,exclusive:fn=>fn(),hex,toast:()=>{}});await assert.rejects(secure.sendObject(peer,'S2:test',null,()=>allowed),/not sent/);assert.equal(sent,0)});
test('an authenticated incoming secure message requests a generic alert once',async()=>{
 const notifications=[],previousFetch=globalThis.fetch;
 storage.set('meshcore-secure-v1:'+own,JSON.stringify({contacts:{[peer]:{verified:true,fingerprint:'test-fingerprint'}},history:{},seen:{}}));
 globalThis.fetch=async(url,options)=>{if(url==='/api/notify')notifications.push(JSON.parse(options.body));return {ok:true,json:async()=>({password})}};
 try{
  const state={self:{publicKey:new Uint8Array(32).fill(17)},contacts:[{type:1,publicKey:new Uint8Array(32).fill(34),advName:'Peer'}],connected:true,view:'home'};
  const secure=createSecureComs({state,getConnection:()=>({}),exclusive:fn=>fn(),hex,toast:()=>{}});secure.refresh();
  const wire=await seal(secret,peer,own,'private test content');
  assert.equal(secure.ingest({txtType:0,text:wire,pubKeyPrefix:new Uint8Array(6).fill(34)}),true);
  for(let i=0;i<100&&!notifications.length;i++)await new Promise(resolve=>setTimeout(resolve,10));
  assert.equal(notifications.length,1);assert.match(notifications[0].title,/Secure Coms/);assert.doesNotMatch(JSON.stringify(notifications),/private test content/);
  secure.ingest({txtType:0,text:wire,pubKeyPrefix:new Uint8Array(6).fill(34)});await new Promise(resolve=>setTimeout(resolve,100));
  assert.equal(notifications.length,1,'replayed messages do not create duplicate alerts');
 }finally{globalThis.fetch=previousFetch}
});

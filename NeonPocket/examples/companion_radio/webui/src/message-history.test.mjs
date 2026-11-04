import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const source=fs.readFileSync(new URL('./app.js',import.meta.url),'utf8');
const start=source.indexOf('function historyKey()');
const end=source.indexOf('function channelKey(',start);
assert(start>=0&&end>start);
function harness(initial={}, failWrites=false){
 const data=new Map(Object.entries(initial));let failed=failWrites,notice='',toasts=0;
 const localStorage={getItem:k=>data.get(k)??null,setItem:(k,v)=>{if(failed)throw Error('quota');data.set(k,v)},removeItem:k=>data.delete(k)};
 const context={state:{self:{publicKey:'node'},messages:[],historyNode:''},connection:{sessionId:'session'},localStorage,MAX_HISTORY_MESSAGES:5000,MAX_MESSAGE_BYTES:140,hex:v=>v,hashText:v=>v,toast:()=>toasts++,document:{getElementById:()=>({set textContent(v){notice=v},classList:{toggle(){}}})}};
 vm.createContext(context);vm.runInContext(source.slice(start,end)+'\nthis.test={persistHistory,readHistory,loadHistoryForNode};',context);
 return {context,data,get notice(){return notice},get toasts(){return toasts},fail(v){failed=v}};
}
const message={id:'1',key:'ch:0',text:'hello',timestamp:42,direction:'in'};
{
 const h=harness();h.context.state.messages=[message];assert.equal(h.context.test.persistHistory(),true);assert.equal(JSON.parse(h.data.get('rcc6-history-node'))[0].text,'hello');assert.match(h.notice,/saved on this device/);
}
{
 const key='rcc6-history-node',bad='{broken';const h=harness({[key]:bad});h.context.test.loadHistoryForNode();assert.equal(h.data.get(key),bad);assert.match(h.notice,/could not be read/);assert.equal(h.context.test.persistHistory(),false);assert.equal(h.data.get(key),bad);
}
{
 const pending='rcc6-history-pending-session';const h=harness({[pending]:JSON.stringify([message])},true);h.context.test.loadHistoryForNode();assert(h.data.has(pending));assert.match(h.notice,/could not save/i);assert.equal(h.toasts,1);h.context.test.persistHistory();assert.equal(h.toasts,1);
}
{
 const pending='rcc6-history-pending-session';const h=harness({[pending]:'{broken'});h.context.test.loadHistoryForNode();assert.equal(h.data.get(pending),'{broken');assert.match(h.notice,/pending message history/i);h.context.test.persistHistory();assert.match(h.notice,/pending message history/i);
}
{
 const h=harness();h.context.state.messages=Array.from({length:5002},(_,i)=>({...message,id:String(i)}));h.context.test.persistHistory();const saved=JSON.parse(h.data.get('rcc6-history-node'));assert.equal(saved.length,5000);assert.equal(saved[0].id,'2');
}
console.log('Local message history: persistence, 5,000 limit, corrupt data protection and failed-save recovery passed.');

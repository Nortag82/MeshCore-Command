import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

// Run the application's real message ingestion with synthetic radio messages.
const source = readFileSync(new URL('./app.js', import.meta.url), 'utf8');
function section(start, end) { const a=source.indexOf(start), b=source.indexOf(end,a); assert.ok(a>=0 && b>a); return source.slice(a,b); }
test('muted channels and contacts retain messages without alerts, including before identity sync', async () => {
  const saved = new Map([['radio:muted-channels', JSON.stringify(['1:Public'])]]);
  const alerts=[], board=[], keywords=[], radioQueue=[];
  let haveIdentity = false, queueReads = 0;
  const contact={type:1, publicKey:new Uint8Array([10,11]), advName:'Friend'};
  const context={state:{channels:[{channelIdx:1,name:'Public'},{channelIdx:2,name:'Other'}], contacts:[contact],messages:[],view:'home',selected:''},
    localStorage:{getItem:k=>saved.get(k)??null,setItem:(k,v)=>saved.set(k,v)}, historyKey:()=>haveIdentity ? 'radio' : '',
    channelKey:c=>`ch:${c.channelIdx}`,hex:a=>Array.from(a,b=>b.toString(16).padStart(2,'0')).join(''),
    renderMessages(){},renderHome(){},persistHistory(){},toast(){},
    keywordWatch:{check:(m,options)=>keywords.push(options.muted)},
    window:{__MCC_NOTIFY_BOARD_MESSAGE:()=>board.push(1)},document:{hidden:true},
    fetch:(url)=>{alerts.push(url);return Promise.resolve()}, MAX_HISTORY_MESSAGES:100,
    txActivity:{received(){}},secureComs:{ingest:()=>false},repeaterTools:{handleMessage:()=>false},commandDiscovery:{ingest:()=>false},roomTools:{noteMessage(){}},
    messageId:(...parts)=>parts.join(':'),Date,Math,Set,JSON,
    incomingNotificationsReady:false,pendingIncomingMessages:[],unannouncedMessages:0,
    connection:{syncNextMessage:async()=>{queueReads++; return radioQueue.shift();}}};
  vm.createContext(context);
  vm.runInContext(section('function mutedChannelId(', 'function channelScope(')+section('function channelIsMuted(', 'function initials(')+section('function targetForContactPrefix(', 'function normalizeTargets(')+section('function addMessage(', 'function refreshNearbyFromContacts(')+section('async function syncMessages(', 'async function refreshStats('), context);
  const channel=(idx,text)=>({channelMessage:{channelIdx:idx,text,senderTimestamp:1}});
  const direct=text=>({contactMessage:{pubKeyPrefix:new Uint8Array([10,11]),text,senderTimestamp:2}});
  radioQueue.push(channel(1,'muted'));
  await context.syncMessages();
  assert.equal(queueReads,0); assert.equal(alerts.length,0);
  haveIdentity = true;
  context.incomingNotificationsReady = true;
  await context.syncMessages();
  assert.equal(context.state.messages.length,1); assert.equal(alerts.length,0); assert.equal(board.length,0); assert.equal(keywords[0],true);
  context.state.channels=[]; // A failed channel refresh must still honour the saved slot.
  assert.equal(context.ingestWaiting(channel(1,'offline slot')),0); assert.equal(alerts.length,0);
  context.state.channels=[{channelIdx:1,name:'Public'},{channelIdx:2,name:'Other'}];
  context.toggleChannelMute('dm:0a0b');
  assert.equal(context.ingestWaiting(direct('muted direct')),0); assert.equal(alerts.length,0); assert.equal(context.state.messages.length,3);
  context.toggleChannelMute('dm:0a0b');
  assert.equal(context.ingestWaiting(direct('unmuted direct')),1); assert.equal(context.ingestWaiting(channel(2,'unmuted channel')),1);
  assert.equal(alerts.length,2); assert.equal(board.length,2); assert.equal(context.state.messages.length,5);
  context.connection.syncNextMessage = async () => {
    context.incomingNotificationsReady = false;
    return channel(1,'received during reconnect');
  };
  await context.syncMessages();
  assert.equal(context.pendingIncomingMessages.length,1);
  assert.equal(alerts.length,2);
  context.incomingNotificationsReady = true;
  assert.equal(context.ingestWaiting(context.pendingIncomingMessages.shift()),0);
  assert.equal(context.state.messages.length,6);
  assert.equal(alerts.length,2);
});

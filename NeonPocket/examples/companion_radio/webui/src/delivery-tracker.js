// MeshCore radio acknowledgements are delivery hints, not cryptographic read receipts.
export function createDeliveryTracker({now=()=>Date.now(),ttl=300000}={}){
 let active=null,pending=[];
 const codeOf=value=>Number.isInteger(value)?value>>>0:null;
 function prune(){const cutoff=now()-ttl;pending=pending.filter(item=>item.started>=cutoff)}
 function begin(radio){const token={radio,started:now(),early:[]};active=token;return token}
 function cancel(token){if(active===token)active=null}
 function finish(token,value,callback){
  if(active===token)active=null;const code=codeOf(value);if(code===null)return false;prune();
  const early=token.early.findIndex(item=>item.code===code);if(early>=0){try{callback()}catch{}return true}
  pending.push({radio:token.radio,code,started:token.started,callback});if(pending.length>200)pending.shift();return false;
 }
 function receive(radio,value){
  const code=codeOf(value);if(code===null)return false;prune();const matches=pending.filter(item=>item.radio===radio&&item.code===code);
  if(matches.length===1){const item=matches[0];pending=pending.filter(other=>other!==item);try{item.callback()}catch{}return true}
  if(matches.length>1)return false;
  if(active?.radio===radio){active.early.push({code,time:now()});if(active.early.length>40)active.early.shift();return true}
  return false;
 }
 return {begin,cancel,finish,receive};
}

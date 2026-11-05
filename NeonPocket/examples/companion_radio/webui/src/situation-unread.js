// The count belongs to the radio identity, like the received situation picture.
export function createSituationUnread({storage,getOwner,isViewing,onChange}){
 let owner='',count=0;
 function sync(){const current=getOwner()||'';if(current!==owner){owner=current;count=0;if(/^[0-9a-f]{64}$/.test(owner)){try{const n=Number(storage.getItem(`meshcore-situation-unread-v1:${owner}`));if(Number.isSafeInteger(n)&&n>0)count=Math.min(n,999999)}catch{}}onChange(count)}return count}
 function persist(){if(/^[0-9a-f]{64}$/.test(owner))try{if(count)storage.setItem(`meshcore-situation-unread-v1:${owner}`,String(count));else storage.removeItem(`meshcore-situation-unread-v1:${owner}`)}catch{/* Keep the indicator in memory if local storage is unavailable. */}}
 return {sync,received(){sync();if(!/^[0-9a-f]{64}$/.test(owner)||isViewing())return;count=Math.min(count+1,999999);persist();onChange(count)},clear(){sync();if(!count)return;count=0;persist();onChange(count)}};
}

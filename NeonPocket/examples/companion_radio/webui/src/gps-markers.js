export const isGpsMarker=x=>x?.name==='My position'&&x?.note==='GPS'&&x?.symbol===0&&!x.deleted;
export function currentGpsMarkers(items){
 const latest=new Map();for(const x of items)if(isGpsMarker(x)){const key=x.owner;const prev=latest.get(key);if(!prev||x.revision>prev.revision||(x.revision===prev.revision&&x.id>prev.id))latest.set(key,x)}
 return items.filter(x=>!isGpsMarker(x)||latest.get(x.owner)===x);
}
export function previousOwnTracker(items,owner){return items.filter(x=>x.owner===owner&&x.peer===''&&isGpsMarker(x)).sort((a,b)=>b.revision-a.revision)[0]||null}

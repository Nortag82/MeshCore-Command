// Mapzen / Tilezen terrain tiles hosted by the AWS Open Data program.
// Terrarium RGB encodes metres as R*256 + G + B/256 - 32768.
const TILE_SIZE = 256;
const MAX_TILES = 64;
const cache = new Map();
export function tileAddress(lat, lon, zoom) {
  if (!Number.isFinite(lat) || !Number.isFinite(lon) || Math.abs(lat) > 85.05112878 || Math.abs(lon) > 180) throw Error('Terrain tiles are available between 85.05° north and south.');
  const n = 2 ** zoom;
  const longitude = ((lon + 180) % 360 + 360) % 360;
  const worldX = longitude / 360 * n * TILE_SIZE;
  const latRad = lat * Math.PI / 180;
  const worldY = (1 - Math.asinh(Math.tan(latRad)) / Math.PI) / 2 * n * TILE_SIZE;
  const tx = Math.floor(worldX / TILE_SIZE), ty = Math.max(0, Math.min(n - 1, Math.floor(worldY / TILE_SIZE)));
  return { tx, ty, px: Math.floor(worldX) % TILE_SIZE, py: Math.max(0, Math.min(TILE_SIZE - 1, Math.floor(worldY - ty * TILE_SIZE))) };
}
export function chooseTileZoom(points) {
  for (let z = 12; z >= 5; z--) {
    const ids = new Set(points.map(([lat,lon]) => { const t=tileAddress(lat,lon,z); return `${t.tx}/${t.ty}`; }));
    if (ids.size <= MAX_TILES) return z;
  }
  throw Error('Terrain area is too large to load; reduce the radius.');
}
async function tilePixels(zoom, tx, ty) {
  const key = `${zoom}/${tx}/${ty}`;
  if (cache.has(key)) return cache.get(key);
  const promise = (async () => {
    const url = `https://s3.amazonaws.com/elevation-tiles-prod/terrarium/${key}.png`;
    let response;
    for (let attempt=0; attempt<3; attempt++) {
      try { response=await fetch(url,{mode:'cors'}); }
      catch { if (attempt===2) throw Error('Cannot reach the terrain tile service. Check your internet connection.'); }
      if (response?.ok) break;
      if (response && response.status !== 429 && response.status < 500) throw Error(`Terrain tile service returned HTTP ${response.status}.`);
      if (attempt===2) throw Error(`Terrain tile service unavailable${response ? ` (HTTP ${response.status})` : ''}. Please retry.`);
      await new Promise(resolve=>setTimeout(resolve,600 * (attempt+1)));
    }
    const image=await createImageBitmap(await response.blob());
    try {
      if (image.width!==TILE_SIZE || image.height!==TILE_SIZE) throw Error('Terrain tile has an unexpected size.');
      const canvas=document.createElement('canvas');canvas.width=TILE_SIZE;canvas.height=TILE_SIZE;
      const context=canvas.getContext('2d',{willReadFrequently:true});
      if (!context) throw Error('Cannot read terrain tiles.');
      context.drawImage(image,0,0);return context.getImageData(0,0,TILE_SIZE,TILE_SIZE).data;
    } finally { image.close(); }
  })();
  cache.set(key,promise);
  promise.catch(()=>{if(cache.get(key)===promise)cache.delete(key);});
  while(cache.size>96)cache.delete(cache.keys().next().value);
  return promise;
}
export async function sampleTerrain(points, {onProgress=()=>{}, isCurrent=()=>true}={}) {
  if (!Array.isArray(points) || !points.length) throw Error('No terrain points provided.');
  const zoom=chooseTileZoom(points), groups=new Map();
  points.forEach(([lat,lon],index)=>{const t=tileAddress(lat,lon,zoom),key=`${t.tx}/${t.ty}`;if(!groups.has(key))groups.set(key,{...t,points:[]});groups.get(key).points.push({index,px:t.px,py:t.py});});
  const entries=[...groups.values()], results=new Array(points.length);let next=0,done=0;
  async function worker(){while(next<entries.length){if(!isCurrent())throw Error('Inputs changed. Calculate again.');const tile=entries[next++],pixels=await tilePixels(zoom,tile.tx,tile.ty);if(!isCurrent())throw Error('Inputs changed. Calculate again.');for(const point of tile.points){const i=(point.py*TILE_SIZE+point.px)*4;if(pixels[i+3]===0)throw Error('Terrain data is missing at one or more points.');results[point.index]=pixels[i]*256+pixels[i+1]+pixels[i+2]/256-32768;}onProgress(++done,entries.length);}}
  await Promise.all(Array.from({length:Math.min(4,entries.length)},worker));
  return {elevation:results, zoom, tiles:entries.length};
}

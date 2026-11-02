const LIMIT=85.05112878, TILE=256;
function indices(bounds,z){
 const {north,south,east,west}=bounds,n=2**z;
 if(![north,south,east,west].every(Number.isFinite)||north<=south||east<=west||north>LIMIT||south<-LIMIT||west<-180||east>180)throw Error('Zoom in so the visible area stays within the map boundaries.');
 const x=lon=>Math.max(0,Math.min(n-1,Math.floor((lon+180)/360*n)));
 const y=lat=>{const r=lat*Math.PI/180;return Math.max(0,Math.min(n-1,Math.floor((1-Math.asinh(Math.tan(r))/Math.PI)/2*n)))};
 const minX=x(west),maxX=x(east-1e-10),minY=y(north),maxY=y(south+1e-10);
 return {minX,maxX,minY,maxY,count:(maxX-minX+1)*(maxY-minY+1)};
}
export function planElevationTiles(bounds,mapZoom,maxTiles=64){
 if(!Number.isFinite(mapZoom)||!Number.isInteger(maxTiles)||maxTiles<1)throw Error('Invalid map view.');
 for(let z=Math.min(12,Math.max(0,Math.floor(mapZoom)));z>=0;z--){const range=indices(bounds,z);if(range.count<=maxTiles){const tiles=[];for(let y=range.minY;y<=range.maxY;y++)for(let x=range.minX;x<=range.maxX;x++)tiles.push({x,y,z});return {zoom:z,tiles}}}
 throw Error('The visible area is too large; zoom in and try again.');
}
export function elevationMask(pixels,threshold){
 if(!pixels||pixels.length!==TILE*TILE*4||!Number.isFinite(threshold))throw Error('Invalid elevation tile.');
 const data=new Uint8ClampedArray(pixels.length);let high=0,missing=0;
 for(let i=0;i<pixels.length;i+=4){if(pixels[i+3]===0||pixels[i]===0){missing++;continue}const height=pixels[i]*256+pixels[i+1]+pixels[i+2]/256-32768;if(height>threshold){data[i]=201;data[i+1]=72;data[i+2]=246;data[i+3]=150;high++}}
 return {data,high,missing};
}

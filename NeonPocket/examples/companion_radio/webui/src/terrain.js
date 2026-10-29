export const EARTH=6371000, EFFECTIVE_EARTH=EARTH*4/3;
const rad=x=>x*Math.PI/180, deg=x=>x*180/Math.PI;
export function distance(a,b){const p1=rad(a[0]),p2=rad(b[0]),dp=p2-p1,dl=rad(b[1]-a[1]);const h=Math.sin(dp/2)**2+Math.cos(p1)*Math.cos(p2)*Math.sin(dl/2)**2;return EARTH*2*Math.atan2(Math.sqrt(Math.min(1,h)),Math.sqrt(Math.max(0,1-h)));}
export function bearing(a,b){const p1=rad(a[0]),p2=rad(b[0]),dl=rad(b[1]-a[1]);return Math.atan2(Math.sin(dl)*Math.cos(p2),Math.cos(p1)*Math.sin(p2)-Math.sin(p1)*Math.cos(p2)*Math.cos(dl));}
export function destination(a,azimuth,metres){const d=metres/EARTH,p=rad(a[0]),l=rad(a[1]);const p2=Math.asin(Math.sin(p)*Math.cos(d)+Math.cos(p)*Math.sin(d)*Math.cos(azimuth));const l2=l+Math.atan2(Math.sin(azimuth)*Math.sin(d)*Math.cos(p),Math.cos(d)-Math.sin(p)*Math.sin(p2));return [deg(p2),((deg(l2)+540)%360)-180];}
export function samplePath(a,b,n){const d=distance(a,b),az=bearing(a,b);return Array.from({length:n},(_,i)=>i===n-1?b:destination(a,az,d*i/(n-1)));}
export function clearance(elevations,dist,ha,hb,mhz){
 if(elevations.length<2||dist<=0||mhz<=0)throw Error('Invalid path');
 const lambda=299792458/(mhz*1e6), a=elevations[0]+ha, b=elevations.at(-1)+hb;let min=Infinity, minF=Infinity;const profile=[];
 for(let i=0;i<elevations.length;i++){const x=dist*i/(elevations.length-1),bulge=x*(dist-x)/(2*EFFECTIVE_EARTH), terrain=elevations[i]+bulge,line=a+(b-a)*x/dist,fresnel=Math.sqrt(lambda*x*(dist-x)/dist),clear=line-terrain;profile.push({x,terrain,line,fresnel});if(i>0&&i<elevations.length-1){min=Math.min(min,clear);minF=Math.min(minF,clear-.6*fresnel)}}
 return {clear:min>0,fresnelClear:minF>0,min,minF,profile};
}

// One result per target on a ray. Later high ground can regain line of sight;
// keeping each result avoids painting over the shadow behind a ridge.
export function rayVisibility(elevations, spacing, txHeight, rxHeight, mhz) {
 if (!Array.isArray(elevations) || elevations.length < 2 || elevations.some(v => !Number.isFinite(v)) || !Number.isFinite(spacing) || spacing <= 0) throw Error('Incomplete terrain data');
 const results = ['unknown'];
 for (let i=2; i<elevations.length; i++) {
  const result=clearance(elevations.slice(0,i+1),spacing*i,txHeight,rxHeight,mhz);
  results.push(!result.clear?'blocked':result.fresnelClear?'clear':'fresnel');
 }
 return results;
}

// Classify the path from the centre of a regularly sampled terrain grid to
// a target cell. Interior heights are bilinearly sampled at half-cell steps.
export function gridVisibility(heights,size,x,y,dist,txHeight,rxHeight,mhz) {
 if (!Number.isInteger(size) || size<3 || size%2!==1 || heights.length!==size*size || x<0 || y<0 || x>=size || y>=size || !Number.isFinite(dist) || dist<0 || !Number.isFinite(mhz) || mhz<=0) throw Error('Invalid terrain grid');
 const centre=(size-1)/2, dx=x-centre, dy=y-centre;
 if (Math.max(Math.abs(dx),Math.abs(dy))<=1) return 'unknown';
 const start=heights[centre*size+centre]+txHeight, end=heights[y*size+x]+rxHeight;
 if (!Number.isFinite(start) || !Number.isFinite(end)) throw Error('Incomplete terrain data');
 const steps=Math.ceil(Math.max(Math.abs(dx),Math.abs(dy))*2), wavelength=299792458/(mhz*1e6);let fresnelBlocked=false;
 for(let i=1;i<steps;i++){
  const fraction=i/steps, gx=centre+dx*fraction,gy=centre+dy*fraction,ix=Math.floor(gx),iy=Math.floor(gy),fx=gx-ix,fy=gy-iy;
  const h00=heights[iy*size+ix],h10=heights[iy*size+Math.min(ix+1,size-1)],h01=heights[Math.min(iy+1,size-1)*size+ix],h11=heights[Math.min(iy+1,size-1)*size+Math.min(ix+1,size-1)];
  const terrain=(h00*(1-fx)+h10*fx)*(1-fy)+(h01*(1-fx)+h11*fx)*fy;
  if(!Number.isFinite(terrain))throw Error('Incomplete terrain data');
  const d=dist*fraction,bulge=d*(dist-d)/(2*EFFECTIVE_EARTH),clear=start+(end-start)*fraction-terrain-bulge;
  if(clear<=0)return 'blocked';
  if(clear-.6*Math.sqrt(wavelength*d*(dist-d)/dist)<=0)fresnelBlocked=true;
 }
 return fresnelBlocked?'fresnel':'clear';
}

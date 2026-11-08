export function bearingToWaypoint(from, to) {
  if (!Array.isArray(from) || !Array.isArray(to) || from.length < 2 || to.length < 2 || ![...from.slice(0,2), ...to.slice(0,2)].every(Number.isFinite)) throw Error('Invalid coordinates');
  const rad=Math.PI/180, lat1=from[0]*rad, lat2=to[0]*rad, delta=(to[1]-from[1])*rad;
  const y=Math.sin(delta)*Math.cos(lat2), x=Math.cos(lat1)*Math.sin(lat2)-Math.sin(lat1)*Math.cos(lat2)*Math.cos(delta);
  if (Math.abs(x)<1e-15 && Math.abs(y)<1e-15) return null;
  return (Math.atan2(y,x)/rad+360)%360;
}
export function formatBearing(bearing) {
  if (bearing===null) return 'At waypoint';
  const names=['N','NNE','NE','ENE','E','ESE','SE','SSE','S','SSW','SW','WSW','W','WNW','NW','NNW'];
  return `${String(Math.round(bearing)%360).padStart(3,'0')}° ${names[Math.round(bearing/22.5)%16]} true`;
}

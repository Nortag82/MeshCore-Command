import L from 'leaflet';
export function createSettingsLocationMap({onEdit,latId='setting-lat',lonId='setting-lon',panelId='setting-location-map-panel',mapId='setting-location-map',statusId='setting-location-status',selectedMessage='Point selected. Select Save coordinates to write it to the radio.',fallback=()=>null}){
 const $=id=>document.getElementById(id),lat=$(latId),lon=$(lonId),details=$(panelId);let map,marker;
 function coordinates(){if(lat.value.trim()===''||lon.value.trim()==='')return null;const a=Number(lat.value),b=Number(lon.value);return Number.isFinite(a)&&Number.isFinite(b)&&Math.abs(a)<=90&&Math.abs(b)<=180&&(a!==0||b!==0)?[a,b]:null}
 function draw(center=false){if(!map)return;const point=coordinates();if(marker){marker.remove();marker=null}if(point){marker=L.circleMarker(point,{radius:9,color:'#17232e',weight:3,fillColor:'#ffc341',fillOpacity:.95}).addTo(map).bindTooltip('Selected position');if(center)map.setView(point,Math.max(map.getZoom(),12))}}
 function init(){if(map)return;const point=coordinates();map=L.map(mapId).setView(point||fallback()||[56,-4],point||fallback()?12:6);L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:18,attribution:'© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'}).addTo(map);map.on('click',event=>{const {lat:a,lng:b}=event.latlng;if(!Number.isFinite(a)||!Number.isFinite(b)||Math.abs(a)>90||Math.abs(b)>180)return;lat.value=a.toFixed(6);lon.value=b.toFixed(6);onEdit();$(statusId).textContent=selectedMessage;draw()});draw()}
 details.addEventListener('toggle',()=>{if(!details.open)return;init();draw(true);setTimeout(()=>map.invalidateSize(),100)});
 for(const field of [lat,lon])field.addEventListener('input',()=>{onEdit();draw()});
 return {sync(){draw()},center(){draw(true)},reset(){draw();if(map){const p=coordinates()||fallback();if(p)map.setView(p,12)}}};
}

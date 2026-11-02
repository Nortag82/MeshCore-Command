import L from 'leaflet';
import {tilePixels} from './elevation.js';
import {planElevationTiles,elevationMask} from './elevation-finder-core.js';
export function createElevationFinder({getMap,isTerrainBusy=()=>false}){
 const $=id=>document.getElementById(id);let map,layer=null,busy=false,version=0,hasResult=false,cancelCurrent=null;
 const status=text=>{$('terrain-elevation-status').textContent=text};
 function removeLayer(){if(layer&&map)map.removeLayer(layer);layer=null;hasResult=false}
 function invalidate(message){version++;cancelCurrent?.();removeLayer();status(message)}
 function attach(instance){if(map===instance)return;map=instance;if(!map.getPane('elevationHighlight')){const pane=map.createPane('elevationHighlight');pane.style.zIndex='350';pane.style.pointerEvents='none'}map.on('movestart',()=>{if(busy){version++;cancelCurrent?.();status('Map moved. Calculation cancelled; highlight the new view.')}else if(hasResult)status('Map moved. The highlight covers the earlier view; calculate again for this view.')})}
 $('terrain-elevation-clear').onclick=()=>{invalidate(busy?'Cancelling elevation calculation…':'Elevation highlight cleared.')};
 $('terrain-elevation-height').addEventListener('input',()=>{if(busy||hasResult)invalidate('Height changed. Highlight the visible area again.')});
 $('terrain-elevation-show').onclick=async()=>{
  if(busy)return;if(isTerrainBusy()){status('Wait for the current terrain calculation to finish.');return}
  const input=$('terrain-elevation-height'),threshold=Number(input.value);if(input.value.trim()===''||!Number.isFinite(threshold)||threshold< -500||threshold>9000){status('Enter a height between −500 and 9,000 metres.');return}
  map=getMap();if(!map){status('Open the terrain map first.');return}attach(map);const bounds=map.getBounds();let plan;
  try{plan=planElevationTiles({north:bounds.getNorth(),south:bounds.getSouth(),east:bounds.getEast(),west:bounds.getWest()},map.getZoom())}catch(error){status(error.message);return}
  removeLayer();busy=true;const token=++version;$('terrain-elevation-show').disabled=true;status(`Loading elevation tiles: 0 of ${plan.tiles.length}…`);
  const cancelled=new Promise(resolve=>{cancelCurrent=()=>resolve(null)}),results=new Array(plan.tiles.length);let next=0,done=0,missing=0,high=0;
  async function worker(){while(next<plan.tiles.length){if(token!==version)throw Error('cancelled');const index=next++,t=plan.tiles[index],pixels=await Promise.race([tilePixels(t.z,t.x,t.y),cancelled]);if(token!==version||!pixels)throw Error('cancelled');const masked=elevationMask(pixels,threshold);missing+=masked.missing;high+=masked.high;const canvas=document.createElement('canvas');canvas.width=canvas.height=256;const context=canvas.getContext('2d');if(!context)throw Error('Cannot draw elevation data.');const image=context.createImageData(256,256);image.data.set(masked.data);context.putImageData(image,0,0);const nw=map.unproject([t.x*256,t.y*256],t.z),se=map.unproject([(t.x+1)*256,(t.y+1)*256],t.z);results[index]=L.imageOverlay(canvas.toDataURL('image/png'),[[se.lat,nw.lng],[nw.lat,se.lng]],{pane:'elevationHighlight',interactive:false});status(`Loading elevation tiles: ${++done} of ${plan.tiles.length}…`)}}
  try{
   await Promise.all(Array.from({length:Math.min(4,plan.tiles.length)},worker));if(token!==version)return;
   layer=L.layerGroup(results).addTo(map);hasResult=true;const metres=156543.03392*Math.cos(map.getCenter().lat*Math.PI/180)/2**plan.zoom;
   status(`${high?`Purple: estimated ground above ${threshold} m in the selected view.`:`No elevation pixels above ${threshold} m were found in the loaded tiles.`} Tile resolution ≈ ${Math.round(metres)} m/pixel; source data may be coarser.${missing?' Some tile pixels have no data.':''} Move or zoom, then calculate again.`);
  }catch(error){if(token===version){version++;status(`Elevation highlight unavailable: ${error?.message||'terrain data could not be loaded'}. No highlight was displayed.`)}}
  finally{cancelCurrent=null;busy=false;$('terrain-elevation-show').disabled=false;if(token!==version&&$('terrain-elevation-status').textContent==='Cancelling elevation calculation…')status('Elevation calculation cancelled and highlight cleared.')}
 };
 return {attach,busy:()=>busy};
}

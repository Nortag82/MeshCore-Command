import assert from 'node:assert/strict';
import {planElevationTiles,elevationMask} from './elevation-finder-core.js';
const tiles=planElevationTiles({north:56.2,south:56,east:-4.5,west:-4.9},12,64);assert(tiles.tiles.length>0&&tiles.tiles.length<=64);assert(tiles.zoom<=12);assert(tiles.tiles.every(t=>t.z===tiles.zoom));
assert.throws(()=>planElevationTiles({north:56,south:55,east:181,west:170},12),/boundaries/);
const pixels=new Uint8ClampedArray(256*256*4);function set(i,height,alpha=255){const h=height+32768;pixels[i*4]=Math.floor(h/256);pixels[i*4+1]=Math.floor(h%256);pixels[i*4+2]=Math.round((h-Math.floor(h))*256);pixels[i*4+3]=alpha}
set(0,299);set(1,300);set(2,300.5);set(3,301);set(4,500,0);pixels[5*4+3]=255;
const out=elevationMask(pixels,300);assert.equal(out.data[3],0);assert.equal(out.data[7],0);assert.equal(out.data[11],150);assert.equal(out.data[15],150);assert.equal(out.data[19],0);assert.equal(out.data[23],0);assert.equal(out.high,2);assert(out.missing>0);assert.throws(()=>elevationMask(pixels,NaN));
console.log('Elevation masks respect the strict threshold, missing data and bounded terrain tile plans.');

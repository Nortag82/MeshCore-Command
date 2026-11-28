import L from 'leaflet';
import { leafletLayer } from 'protomaps-leaflet';

const status = document.getElementById('status');
const map = L.map('map', { worldCopyJump: true }).setView([20, 0], 2);
const markers = L.featureGroup().addTo(map);

window.chrome.webview.addEventListener('message', ({ data }) => {
  markers.clearLayers();
  for (const station of data) {
    const name = document.createElement('span');
    name.textContent = `${station.callsign} · ${station.grid}`;
    L.circleMarker([station.latitude, station.longitude], {
      radius: 7, color: '#083628', weight: 2, fillColor: '#38e18d', fillOpacity: 1
    }).bindTooltip(name).addTo(markers);
  }
  if (markers.getLayers().length) map.fitBounds(markers.getBounds(), { padding: [36, 36], maxZoom: 8 });
});

try {
  const response = await fetch('offline-map');
  if (!response.ok) throw new Error();
  const info = await response.json();
  if (!info.available) {
    status.textContent = 'Select an offline map in Command’s map settings to show a basemap.';
  } else {
    const maxZoom = info.maxZoom ?? 15;
    const tiles = info.format === 'pmtiles'
      ? leafletLayer({ url: new URL('offline-map.pmtiles', location.href).href, flavor: 'light',
          lang: 'en', maxZoom: 22, maxDataZoom: maxZoom, attribution: '© OpenStreetMap contributors · Protomaps' })
      : L.tileLayer(new URL('offline-tile/', location.href).href + '{z}/{x}/{y}',
          { maxZoom: 22, maxNativeZoom: maxZoom, attribution: 'Offline MBTiles · see map provider attribution' });
    tiles.addTo(map).bringToBack();
    status.textContent = `${info.name} · Contact markers show grid-square centres.`;
  }
} catch {
  status.textContent = 'The offline basemap could not be loaded.';
}

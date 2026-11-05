"""Experimental controller for same-origin multi-board sessions."""
import asyncio,ipaddress,threading,struct,time
from pathlib import Path
from ble_transport import BLETransport,SERVICE_UUID
from ble_pairing import pair_meshcore_ble
from board_registry import BoardRegistry
from bridge import CompanionBridge
from multi_board_hub import BoardHub

def read_identity(bridge,timeout=15):
 # Probe before exposing the board to a view; preserve unrelated incoming frames.
 payload=b'\x01'+b'\x00'*7+b'MeshCore Command'
 with bridge.write_lock:
  if not bridge.alive:raise OSError('Board disconnected during setup.')
  bridge.socket.sendall(b'<'+struct.pack('<H',len(payload))+payload)
 deadline=time.monotonic()+timeout
 while time.monotonic()<deadline:
  if not bridge.alive:raise OSError('Board disconnected during identification.')
  with bridge.lock:
   for entry in tuple(bridge.frames):
    frame=entry[1]
    if len(frame)>=36 and frame[0]==5:
     bridge.frames.remove(entry)
     return bytes(frame[4:36])
  time.sleep(.05)
 raise TimeoutError('The board did not identify itself. Check its companion firmware and connection.')

class MultiBoardController:
 def __init__(self,html,shell,profiles,*,port=0,bridge_class=CompanionBridge,transport_class=BLETransport,identity_reader=read_identity):
  self.html=Path(html);self._bridge_class=bridge_class;self._transport_class=transport_class;self.hub=BoardHub(self.html.read_bytes(),port=port,shell=Path(shell).read_bytes())
  try:self.registry=BoardRegistry(profiles,self._make_bridge)
  except Exception:self.hub.close();raise
  self._scanned={};self._lock=threading.RLock();self._identities={};self._identity_reader=identity_reader
 def _make_bridge(self,profile,device):
  if profile['kind']=='wifi':return self._bridge_class(profile['address'],self.html,port=0)
  # Rescan within the BLE worker loop; Windows scan objects cannot safely
  # outlive the event loop that produced them. Connections are serialized.
  transport=self._transport_class(profile['address'])
  try:return self._bridge_class(None,self.html,port=0,transport=transport)
  except Exception:transport.close();raise
 def list_boards(self):return {'boards':self.registry.list()}
 def _connect(self,kind,address,device=None):
  with self._lock:
   address=str(ipaddress.IPv4Address(address)) if kind=='wifi' else address.upper()
   profile=next((p for p in self.registry.list() if p['kind']==kind and p['address']==address),None)
   if profile is None:profile=self.registry.add(kind,address)
   bridge=self.registry.connect(profile['id'],device)
   try:
    identity=self._identity_reader(bridge)
    if len(identity)!=32 or not any(identity):raise ValueError('Board returned an invalid identity.')
    if identity in self._identities.values():raise ValueError('This radio is already connected through another board tab.')
    self.hub.add(profile['id'],bridge);self._identities[profile['id']]=identity
   except Exception:self.registry.disconnect(profile['id']);raise
   return {'id':profile['id']}
 def connect_wifi(self,address):return self._connect('wifi',address)
 def scan_ble(self):
  async def scan():
   from bleak import BleakScanner
   return await BleakScanner.discover(timeout=10,return_adv=True,service_uuids=[SERVICE_UUID])
  found=asyncio.run(scan());devices=[];cache={}
  for device,advert in found.values():
   if SERVICE_UUID not in [u.lower() for u in advert.service_uuids]:continue
   address=device.address.upper();cache[address]=device;devices.append({'name':device.name or advert.local_name or 'MeshCore companion','address':address})
  self._scanned=cache;return {'devices':devices}
 def connect_ble(self,address,pin):
  address=address.upper() if isinstance(address,str) else ''
  if address not in self._scanned:raise ValueError('Scan Bluetooth and select the board first.')
  asyncio.run(pair_meshcore_ble(address,pin));return self._connect('ble',address,self._scanned[address])
 def rename(self,board_id,name):self.registry.rename(board_id,name);return {'ok':True}
 def disconnect(self,board_id):
  with self._lock:self.hub.remove(board_id);self.registry.disconnect(board_id);self._identities.pop(board_id,None)
  return {'ok':True}
 def close(self):self.registry.close();self.hub.close()

import socket, threading, collections, struct, json, math, urllib.request, urllib.parse, sqlite3, os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import notifier
import credential_store
from offline_maps import OfflineDownloads
import re
from contextlib import closing
import serial

class SerialTransport:
    """Present a companion serial stream through the same framing API as TCP."""
    def __init__(self, port):
        self.serial = serial.Serial(port, 115200, timeout=0.5, write_timeout=5, dsrdtr=False, rtscts=False)
    def settimeout(self, timeout): pass
    def recv(self, count):
        while self.serial.is_open:
            data=self.serial.read(count)
            if data: return data
        return b''
    def sendall(self, data):
        if self.serial.write(data) != len(data): raise OSError('Incomplete serial write')
        self.serial.flush()
    def shutdown(self, how):
        if self.serial.is_open:
            self.serial.cancel_read()
            self.serial.close()
    def close(self): self.serial.close()

class CompanionBridge:
    def __init__(self, host, html_path, port=19483, serial_port=None):
        self.socket = SerialTransport(serial_port) if serial_port else socket.create_connection((host,5000),timeout=5)
        self.socket.settimeout(None)
        self.html = Path(html_path).read_bytes()
        self.offline_map_config = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'MeshCore Command Data' / 'offline-map.json'
        self.offline_map_path = None
        try:
            saved = json.loads(self.offline_map_config.read_text(encoding='utf-8'))
            self.set_offline_map(saved['path'], persist=False)
        except (OSError, ValueError, KeyError, TypeError, sqlite3.Error, json.JSONDecodeError):
            pass
        self.offline_downloads=OfflineDownloads(self)
        self.frames=collections.deque(maxlen=512)
        self.lock=threading.Lock()
        self.write_lock=threading.Lock()
        self.sequence=0
        self.alive=True
        bridge=self
        class Handler(BaseHTTPRequestHandler):
            def log_message(self,*args): pass
            def send(self,status,data=b'',kind='text/plain',headers=None):
                self.send_response(status);self.send_header('Content-Type',kind);self.send_header('Content-Length',str(len(data)));self.send_header('Cache-Control','no-store')
                for k,v in (headers or {}).items(): self.send_header(k,str(v))
                self.end_headers()
                if data:self.wfile.write(data)
            def do_GET(self):
                if self.path in ('/','/index.html'):
                    self.send(200,bridge.html,'text/html; charset=utf-8');return
                if self.path == '/api/offline-download':
                    self.send(200,json.dumps(bridge.offline_downloads.info()).encode(),'application/json');return
                if self.path.split('?')[0] == '/api/offline-map.pmtiles':
                    try:
                        path=bridge.offline_map_path
                        if not path or path.suffix.lower()!='.pmtiles': self.send(404);return
                        size=path.stat().st_size
                        match=re.fullmatch(r'bytes=(\d+)-(\d*)',self.headers.get('Range',''))
                        if not match: self.send(416,headers={'Content-Range':f'bytes */{size}'});return
                        start=int(match.group(1));end=min(int(match.group(2)) if match.group(2) else size-1,size-1)
                        if start>=size or end<start or end-start>8_000_000: self.send(416,headers={'Content-Range':f'bytes */{size}'});return
                        with path.open('rb') as f: f.seek(start);data=f.read(end-start+1)
                        self.send(206,data,'application/octet-stream',{'Content-Range':f'bytes {start}-{end}/{size}','Accept-Ranges':'bytes'});return
                    except OSError: self.send(404);return
                if self.path == '/api/offline-map':
                    info = bridge.offline_map_info()
                    self.send(200, json.dumps(info).encode(), 'application/json'); return
                match = re.fullmatch(r'/api/offline-tile/(\d{1,2})/(\d+)/(\d+)', self.path)
                if match:
                    try:
                        z,x,y=map(int,match.groups())
                        if z > 22 or x >= 2**z or y >= 2**z or not bridge.offline_map_path: self.send(404); return
                        with closing(sqlite3.connect(bridge.offline_map_path.as_uri()+'?mode=ro',uri=True)) as db:
                            row=db.execute('SELECT tile_data FROM tiles WHERE zoom_level=? AND tile_column=? AND tile_row=? LIMIT 1',(z,x,(2**z-1)-y)).fetchone()
                        if not row or not isinstance(row[0],bytes) or len(row[0])>2_000_000: self.send(404); return
                        data=row[0]
                        kind='image/png' if data.startswith(b'\x89PNG\r\n\x1a\n') else 'image/jpeg' if data.startswith(b'\xff\xd8\xff') else 'image/webp' if data.startswith(b'RIFF') and data[8:12]==b'WEBP' else ''
                        if not kind: self.send(415); return
                        self.send(200,data,kind);return
                    except (sqlite3.Error, OSError, ValueError): self.send(404);return
                if self.path=='/api/frame':
                    if not bridge.alive:self.send(503);return
                    with bridge.lock:
                        ack=self.headers.get('X-RCC6-Ack','')
                        if bridge.frames and ack == str(bridge.frames[0][0]): bridge.frames.popleft()
                        if not bridge.frames:self.send(204);return
                        seq,data=bridge.frames[0]
                    self.send(200,data,'application/octet-stream',{'X-RCC6-Seq':seq});return
                self.send(404)
            def do_POST(self):
                if self.path == '/api/offline-download':
                    try:
                        if self.headers.get('Origin','') != f'http://127.0.0.1:{bridge.server.server_address[1]}': self.send(403);return
                        length=int(self.headers.get('Content-Length','0'))
                        if not 1<=length<=1024: raise ValueError('Invalid request.')
                        bridge.offline_downloads.start(json.loads(self.rfile.read(length)))
                        self.send(200,b'{"started":true}','application/json')
                    except (ValueError,TypeError,AttributeError,json.JSONDecodeError) as e: self.send(400,str(e).encode())
                    return
                if self.path in ('/api/repeater-credential', '/api/secure-credential'):
                    try:
                        origin=self.headers.get('Origin', '')
                        if origin != f'http://127.0.0.1:{bridge.server.server_address[1]}': self.send(403); return
                        length=int(self.headers.get('Content-Length','0'))
                        if not 1 <= length <= 4096: self.send(400); return
                        data=json.loads(self.rfile.read(length)); key=data.get('key'); action=data.get('action'); scope='secure' if self.path == '/api/secure-credential' else 'repeater'
                        if not isinstance(key,str) or not re.fullmatch(r'[0-9a-f]{64}',key): raise ValueError('Invalid key')
                        if action == 'get': result={'password':credential_store.get(key,scope)}
                        elif action == 'save' and isinstance(data.get('password'),str): credential_store.put(key,data['password'],scope); result={'ok':True}
                        elif action == 'delete': credential_store.delete(key,scope); result={'ok':True}
                        else: raise ValueError('Invalid operation')
                        self.send(200,json.dumps(result).encode(),'application/json')
                    except (ValueError, TypeError, AttributeError, json.JSONDecodeError): self.send(400,b'Invalid request')
                    except Exception: self.send(503,b'Windows Credential Manager unavailable')
                    return
                if self.path == '/api/notify':
                    try:
                        length=int(self.headers.get('Content-Length','0'))
                        if not 1 <= length <= 4096:self.send(400);return
                        data=json.loads(self.rfile.read(length))
                        title=data.get('title','');body=data.get('body','')
                        if not isinstance(title,str) or not isinstance(body,str) or not title.strip() or not body.strip():self.send(400);return
                        notifier.notify(title[:63].replace('\x00',''),body[:255].replace('\x00',''))
                        self.send(200,b'{"queued":true}','application/json')
                    except (ValueError,TypeError,AttributeError,json.JSONDecodeError):self.send(400)
                    except Exception:self.send(503,b'Windows notification service unavailable')
                    return
                if self.path == '/api/elevation':
                    try:
                        length=int(self.headers.get('Content-Length','0'))
                        if length < 1 or length > 12000: self.send(400);return
                        points=json.loads(self.rfile.read(length))
                        if not isinstance(points,list) or not 1 <= len(points) <= 100: raise ValueError('Expected 1–100 coordinates')
                        lat=[];lon=[]
                        for point in points:
                            a=float(point[0]); b=float(point[1])
                            if not math.isfinite(a) or not math.isfinite(b) or not -90<=a<=90 or not -180<=b<=180: raise ValueError('Invalid coordinate')
                            lat.append(f'{a:.6f}');lon.append(f'{b:.6f}')
                        query=urllib.parse.urlencode({'latitude':','.join(lat),'longitude':','.join(lon)})
                        req=urllib.request.Request('https://api.open-meteo.com/v1/elevation?'+query,headers={'User-Agent':'MeshCoreDesktop/1.0'})
                        with urllib.request.urlopen(req,timeout=25) as response: payload=json.loads(response.read(65536))
                        values=payload.get('elevation')
                        if not isinstance(values,list) or len(values)!=len(points) or not all(isinstance(v,(int,float)) and math.isfinite(v) for v in values): raise ValueError('Elevation data incomplete')
                        self.send(200,json.dumps({'elevation':values}).encode(),'application/json')
                    except (ValueError,TypeError,IndexError,KeyError,json.JSONDecodeError): self.send(400,b'Invalid or unavailable elevation data')
                    except Exception: self.send(502,b'Elevation service unavailable')
                    return
                if self.path!='/api/frame':self.send(404);return
                if not bridge.alive:self.send(503);return
                try:
                    length=int(self.headers.get('Content-Length','0'))
                    if not 1<=length<=176:self.send(400);return
                    frame=self.rfile.read(length)
                    if len(frame)!=length:self.send(400);return
                    with bridge.write_lock:bridge.socket.sendall(b'<' + struct.pack('<H',length)+frame)
                    self.send(200)
                except (OSError, ValueError):self.send(503)
        class LocalServer(ThreadingHTTPServer):
            allow_reuse_address = False
        self.server=LocalServer(('127.0.0.1',port),Handler)
        self.server.daemon_threads=True
        threading.Thread(target=self.server.serve_forever,daemon=True).start()
        threading.Thread(target=self.read_frames,daemon=True).start()
    def set_offline_map(self, path, persist=True):
        candidate = Path(path).expanduser().resolve(strict=True)
        if not candidate.is_file() or candidate.suffix.lower() not in ('.mbtiles','.pmtiles'): raise ValueError('Choose an MBTiles or PMTiles file.')
        if candidate.suffix.lower()=='.pmtiles':
            with candidate.open('rb') as f:
                if f.read(8)!=b'PMTiles\x03': raise ValueError('Invalid PMTiles map.')
        else:
          with closing(sqlite3.connect(candidate.as_uri()+'?mode=ro',uri=True)) as db:
              row = db.execute('SELECT tile_data FROM tiles LIMIT 1').fetchone()
              if not row or not isinstance(row[0], bytes): raise ValueError('This MBTiles file contains no raster tiles.')
              data=row[0]
              if not (data.startswith(b'\x89PNG\r\n\x1a\n') or data.startswith(b'\xff\xd8\xff') or (data.startswith(b'RIFF') and data[8:12]==b'WEBP')): raise ValueError('Use raster PNG, JPEG or WebP MBTiles, not vector tiles.')
        self.offline_map_path = candidate
        if persist:
            self.offline_map_config.parent.mkdir(parents=True,exist_ok=True)
            self.offline_map_config.write_text(json.dumps({'path':str(candidate)}),encoding='utf-8')
        return self.offline_map_info()
    def offline_map_info(self):
        available=bool(self.offline_map_path and self.offline_map_path.is_file())
        max_zoom=None
        if available and self.offline_map_path.suffix.lower()=='.pmtiles':
            try:
                with self.offline_map_path.open('rb') as f:
                    f.seek(101); value=f.read(1)
                if value and value[0]<=22: max_zoom=value[0]
            except OSError: pass
        return {'available': available, 'name': self.offline_map_path.name if self.offline_map_path else '', 'format': self.offline_map_path.suffix.lower()[1:] if self.offline_map_path else '', 'maxZoom':max_zoom}
    @property
    def url(self): return 'http://127.0.0.1:%s/' % self.server.server_address[1]
    def receive(self,n):
        chunks=[]
        while n:
            part=self.socket.recv(n)
            if not part:raise EOFError()
            chunks.append(part);n-=len(part)
        return b''.join(chunks)
    def read_frames(self):
        try:
            while self.alive:
                if self.receive(1)!=b'>':continue
                n=struct.unpack('<H',self.receive(2))[0]
                if not 1<=n<=176:raise ValueError('Invalid companion frame')
                frame=self.receive(n)
                with self.lock:
                    if len(self.frames)==self.frames.maxlen:raise RuntimeError('Frame queue overflow')
                    self.sequence=(self.sequence % 0xffffffff)+1
                    self.frames.append((self.sequence,frame))
        except (OSError,EOFError,ValueError,RuntimeError,serial.SerialException): self.alive=False
    def close(self):
        if getattr(self, "closed", False): return
        self.closed=True
        self.alive=False
        try:self.socket.shutdown(socket.SHUT_RDWR)
        except OSError:pass
        self.socket.close();self.server.shutdown();self.server.server_close()

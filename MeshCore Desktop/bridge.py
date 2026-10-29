import socket, threading, collections, struct, json, math, urllib.request, urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import notifier
import credential_store
import re
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

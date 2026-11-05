"""Experimental same-origin router for independent companion bridges."""
import http.client,json,re,secrets,threading
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from urllib.parse import urlsplit
BOARD_ID=re.compile(r'^[0-9a-f]{32}$')
class BoardHub:
 def __init__(self,html,*,port=0,shell=None):
  self.html=bytes(html);self.shell=bytes(shell) if shell is not None else None;self._boards={};self._first=None;self._lock=threading.RLock();self.token=secrets.token_urlsafe(32);hub=self
  class Handler(BaseHTTPRequestHandler):
   def log_message(self,*_args):pass
   def reply(self,status,data=b'',kind='text/plain',extra=None):
    self.send_response(status);self.send_header('Content-Type',kind);self.send_header('Content-Length',str(len(data)));self.send_header('Cache-Control','no-store')
    for k,v in (extra or {}).items():self.send_header(k,v)
    self.end_headers()
    if data:self.wfile.write(data)
   def do_GET(self):self.route('GET')
   def do_POST(self):self.route('POST')
   def route(self,method):
    try:
     parsed=urlsplit(self.path)
     if method=='GET' and parsed.path=='/' and hub.shell is not None:self.reply(200,hub.shell,'text/html; charset=utf-8');return
     match=re.fullmatch(r'/board/([0-9a-f]{32})(/.*)?',parsed.path)
     if not match:self.reply(404);return
     board_id=match.group(1);path=match.group(2) or '/'
     with hub._lock:board=hub._boards.get(board_id)
     if board is None:self.reply(404);return
     if method=='GET' and path=='/':self.reply(200,hub.board_html(board_id),'text/html; charset=utf-8');return
     tile=method=='GET' and re.fullmatch(r'/api/offline-tile/\d{1,2}/\d+/\d+',path)
     if not path.startswith('/api/') or (not tile and self.headers.get('X-MCC-Board-Token')!=hub.token):self.reply(403);return
     if method=='POST' and self.headers.get('Origin')!=hub.origin:self.reply(403);return
     length=int(self.headers.get('Content-Length','0'))
     if length<0 or length>65536:self.reply(413);return
     body=self.rfile.read(length) if length else None;headers={k:v for k,v in self.headers.items() if k.lower() in ('content-type','x-rcc6-ack','x-rcc6-session','range')}
     if method=='POST':headers['Origin']=f'http://127.0.0.1:{board.server.server_address[1]}'
     conn=http.client.HTTPConnection('127.0.0.1',board.server.server_address[1],timeout=35)
     try:
      conn.request(method,path+('?' + parsed.query if parsed.query else ''),body=body,headers=headers);response=conn.getresponse();data=response.read(9*1024*1024+1)
      if len(data)>9*1024*1024:self.reply(502,b'Board response too large.');return
      extra={k:v for k,v in response.getheaders() if k.lower() in ('x-rcc6-seq','content-range','accept-ranges')};self.reply(response.status,data,response.getheader('Content-Type','application/octet-stream'),extra)
     finally:conn.close()
    except (OSError,ValueError,http.client.HTTPException):
     try:self.reply(503,b'Board connection unavailable.')
     except OSError:pass
  class Server(ThreadingHTTPServer):allow_reuse_address=False;daemon_threads=True
  self.server=Server(('127.0.0.1',port),Handler);self._thread=threading.Thread(target=self.server.serve_forever,daemon=True);self._thread.start()
 @property
 def origin(self):return f'http://127.0.0.1:{self.server.server_address[1]}'
 def add(self,board_id,bridge):
  if not BOARD_ID.fullmatch(board_id):raise ValueError('Invalid board ID.')
  with self._lock:
   if board_id in self._boards:raise ValueError('Board already exists.')
   self._boards[board_id]=bridge
   if self._first is None:self._first=board_id
 def remove(self,board_id):
  with self._lock:return self._boards.pop(board_id,None)
 def board_html(self,board_id):
  if not BOARD_ID.fullmatch(board_id):raise ValueError('Invalid board ID.')
  script='''<script>(()=>{const id=__ID__,token=__TOKEN__,prefix='/board/'+id;window.__MCC_BOARD_ID=id;window.__MCC_BOARD_FIRST=__FIRST__;window.__MCC_BOARD_ACTIVE=false;window.addEventListener('message',event=>{if(event.source===parent&&event.origin===location.origin&&event.data?.type==='mcc-board-active'&&event.data.id===id){window.__MCC_BOARD_ACTIVE=event.data.active===true;window.dispatchEvent(new Event('mcc-board-active-change'))}});const original=window.fetch.bind(window);window.fetch=(input,options={})=>{if(typeof input==='string'&&input.startsWith('/api/')){const headers=new Headers(options.headers||{});headers.set('X-MCC-Board-Token',token);return original(prefix+input,{...options,headers})}return original(input,options)};function api(){try{if(parent!==window&&parent.pywebview){window.pywebview=parent.pywebview;window.dispatchEvent(new Event('pywebviewready'))}}catch{}}if(parent!==window){parent.addEventListener('pywebviewready',api);api()}})();</script>'''.replace('__ID__',json.dumps(board_id)).replace('__TOKEN__',json.dumps(self.token)).replace('__FIRST__',json.dumps(board_id==self._first))
  if b'</head>' not in self.html:raise ValueError('App HTML has no head.')
  return self.html.replace(b'</head>',b'<style>#window-controls{display:none!important}</style>'+script.encode()+b'</head>',1)
 def close(self):self.server.shutdown();self.server.server_close();self._thread.join(timeout=5)

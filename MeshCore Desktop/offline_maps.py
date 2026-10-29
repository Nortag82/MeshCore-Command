import hashlib, json, math, os, re, shutil, subprocess, tempfile, threading, urllib.request, zipfile
from pathlib import Path

HELPER_URL = 'https://github.com/protomaps/go-pmtiles/releases/download/v1.31.2/go-pmtiles_1.31.2_Windows_x86_64.zip'
HELPER_SHA256 = 'a658baa4d7e55020aef6ca17bd9ff9faa1582671266b36f58c52db0ac8e785a1'

class OfflineDownloads:
    def __init__(self, bridge):
        self.bridge = bridge
        self.root = bridge.offline_map_config.parent / 'offline-maps'
        self.lock = threading.Lock()
        self.state = {'status':'idle', 'message':'Select an area on the map.'}
    def info(self):
        with self.lock: return dict(self.state)
    def update(self, **values):
        with self.lock: self.state.update(values)
    def start(self, data):
        bounds = data.get('bounds')
        zoom = data.get('zoom')
        if not isinstance(bounds,list) or len(bounds)!=4 or type(zoom) is not int or not 8<=zoom<=15: raise ValueError('Invalid bounds or detail level.')
        if any(isinstance(x,bool) or not isinstance(x,(int,float)) or not math.isfinite(x) for x in bounds): raise ValueError('Invalid coordinates.')
        west,south,east,north=bounds
        if not (-180<=west<east<=180 and -85<=south<north<=85) or east-west>10 or north-south>10: raise ValueError('Choose a smaller area (at most 10 degrees wide and high).')
        with self.lock:
            if self.state['status']=='running': raise ValueError('A map download is already running.')
            self.state={'status':'running','message':'Preparing offline map download…'}
        threading.Thread(target=self._download,args=(bounds,zoom),daemon=True).start()
    def _download(self,bounds,zoom):
        tmp=None
        try:
            self.root.mkdir(parents=True,exist_ok=True)
            helper=self.root/'pmtiles-1.31.2.exe'
            if not helper.exists():
                self.update(message='Downloading the verified map download tool…')
                request=urllib.request.Request(HELPER_URL,headers={'User-Agent':'MeshCore-Command/1.0'})
                with urllib.request.urlopen(request,timeout=60) as response: archive=response.read(100_000_001)
                if len(archive)>100_000_000 or hashlib.sha256(archive).hexdigest()!=HELPER_SHA256: raise ValueError('Map download tool failed its integrity check.')
                import io
                with zipfile.ZipFile(io.BytesIO(archive)) as z:
                    info=z.getinfo('pmtiles.exe')
                    if info.file_size>100_000_000: raise ValueError('Invalid download tool.')
                    data=z.read(info)
                temp_helper=helper.with_suffix('.tmp');temp_helper.write_bytes(data);os.replace(temp_helper,helper)
            self.update(message='Finding a current Protomaps map…')
            req=urllib.request.Request('https://build-metadata.protomaps.dev/builds.json',headers={'User-Agent':'Mozilla/5.0','Origin':'https://maps.protomaps.com'})
            with urllib.request.urlopen(req,timeout=30) as response: builds=json.load(response)
            keys=sorted((x['key'] for x in builds if isinstance(x,dict) and re.fullmatch(r'20\d{6}\.pmtiles',str(x.get('key',''))) and str(x.get('version','')).startswith('4.')),reverse=True)
            if not keys: raise ValueError('No compatible Protomaps map is available.')
            self.update(message='Downloading map area. Larger areas and more detail take longer…')
            fd,tmp=tempfile.mkstemp(prefix='download-',suffix='.pmtiles',dir=self.root);os.close(fd);os.unlink(tmp)
            bbox=','.join(f'{x:.6f}' for x in bounds)
            result=subprocess.run([str(helper),'extract','https://build.protomaps.com/'+keys[0],tmp,'--bbox='+bbox,'--maxzoom='+str(zoom)],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE,timeout=1800,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
            if result.returncode: raise ValueError('Map extraction failed: '+result.stderr.decode('utf-8','replace')[-400:])
            path=self.root/('map-'+__import__('datetime').datetime.now().strftime('%Y%m%d-%H%M%S-%f')+'.pmtiles')
            os.replace(tmp,path);tmp=None
            self.bridge.set_offline_map(path)
            self.update(status='complete',message=f'Download complete: {path.name} ({path.stat().st_size/1048576:.1f} MB).',name=path.name)
        except Exception as exc:
            self.update(status='error',message='Download failed: '+str(exc))
        finally:
            if tmp:
                try: os.unlink(tmp)
                except OSError: pass

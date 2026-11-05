"""Encrypted, portable backups of the desktop's user-owned data."""
import base64, hashlib, json, os, re, tempfile, zipfile, io
from pathlib import Path
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC
from cryptography.hazmat.primitives import hashes
import credential_store

MAGIC=b'MCCFULL1'; LIMIT=256*1024*1024; ITERATIONS=600000

def derive(password,salt):
    if not isinstance(password,str) or len(password)<12: raise ValueError('Use a backup password of at least 12 characters.')
    return PBKDF2HMAC(algorithm=hashes.SHA256(),length=32,salt=salt,iterations=ITERATIONS).derive(password.encode('utf-8'))

def data_dir(): return Path(os.environ.get('LOCALAPPDATA',str(Path.home()))) / 'MeshCore Command Data'

def validate_storage(storage):
    if not isinstance(storage,dict) or len(storage)>10000 or any(not isinstance(k,str) or not isinstance(v,str) or len(k)>512 for k,v in storage.items()): raise ValueError('Invalid app storage.')
    if len(json.dumps(storage,ensure_ascii=False).encode())>32*1024*1024: raise ValueError('App data is too large for a backup.')
    return {k:v for k,v in storage.items() if k not in ('rcc6-session','rcc6-radio-owner') and not k.startswith(('rcc6-session:','rcc6-radio-owner:'))}

def validate_radio(radio):
    if radio is None: return None
    if not isinstance(radio,dict) or radio.get('version')!=1 or len(json.dumps(radio,ensure_ascii=False).encode('utf-8'))>2_000_000: raise ValueError('Invalid radio backup.')
    return radio

def snapshot(storage, radio=None):
    storage=validate_storage(storage); root=data_dir(); credentials=credential_store.list_saved(); files={}
    devices=root/'devices.json'
    if devices.exists(): files['devices.json']=devices.read_bytes()
    maps={p.resolve() for p in (root/'offline-maps').glob('*') if p.is_file() and p.suffix.lower() in ('.mbtiles','.pmtiles')}
    selected=None; config=root/'offline-map.json'
    if config.exists():
        selected_path=Path(json.loads(config.read_text(encoding='utf-8'))['path']).resolve()
        if not selected_path.is_file() or selected_path.suffix.lower() not in ('.mbtiles','.pmtiles'): raise ValueError('The selected offline map is missing. Re-select or remove it before exporting.')
        maps.add(selected_path)
    else: selected_path=None
    total=sum(p.stat().st_size for p in maps)+sum(map(len,files.values()))
    if total>LIMIT: raise ValueError('Offline maps exceed the 256 MB backup limit. No backup was created.')
    for p in sorted(maps):
        data=p.read_bytes(); name='maps/'+hashlib.sha256(data).hexdigest()+p.suffix.lower();files[name]=data
        if p==selected_path: selected=name
    meta={'version':1,'storage':storage,'credentials':credentials,'selected':selected,'radio':validate_radio(radio)}
    output=io.BytesIO()
    with zipfile.ZipFile(output,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=4) as z:
        z.writestr('manifest.json',json.dumps(meta,ensure_ascii=False))
        for name,data in files.items():z.writestr(name,data)
    data=output.getvalue()
    if len(data)>LIMIT: raise ValueError('Backup is too large; no backup was created.')
    return data

def encrypt(data,password):
    salt=os.urandom(16);nonce=os.urandom(12);return MAGIC+salt+nonce+AESGCM(derive(password,salt)).encrypt(nonce,data,MAGIC)

def decode(data,password):
    if len(data)>LIMIT+1024 or len(data)<52 or not data.startswith(MAGIC): raise ValueError('Invalid or unsupported full backup.')
    try: plain=AESGCM(derive(password,data[8:24])).decrypt(data[24:36],data[36:],MAGIC)
    except Exception as e: raise ValueError('Wrong password or damaged backup; nothing was changed.') from e
    with zipfile.ZipFile(io.BytesIO(plain)) as z:
        items=z.infolist();names=[i.filename for i in items]
        if len(items)>10002 or len(names)!=len(set(names)) or sum(i.file_size for i in items)>LIMIT+32*1024*1024 or 'manifest.json' not in names: raise ValueError('Invalid backup contents.')
        if any(n!='manifest.json' and n!='devices.json' and not re.fullmatch(r'maps/[0-9a-f]{64}\.(mbtiles|pmtiles)',n) for n in names):raise ValueError('Invalid backup file name.')
        meta=json.loads(z.read('manifest.json'));files={n:z.read(n) for n in names if n!='manifest.json'}
    if meta.get('version')!=1:raise ValueError('Unsupported backup version.')
    meta['storage']=validate_storage(meta.get('storage'));meta['radio']=validate_radio(meta.get('radio'));creds=meta.get('credentials')
    if not isinstance(creds,dict) or len(creds)>10000 or any(not re.fullmatch(r'(secure|repeater)/[0-9a-f]{64}',k) or not isinstance(v,str) or not 1<=len(v.encode())<=512 for k,v in creds.items()):raise ValueError('Invalid backup credentials.')
    if meta.get('selected') is not None and meta['selected'] not in files:raise ValueError('Selected offline map is missing from backup.')
    for name,data in files.items():
        if name.startswith('maps/') and hashlib.sha256(data).hexdigest()!=name[5:69]: raise ValueError('Offline map failed its integrity check.')
    if 'devices.json' in files:json.loads(files['devices.json'])
    return meta,files

def write_atomic(path,data):
    path.parent.mkdir(parents=True,exist_ok=True)
    fd,tmp=tempfile.mkstemp(prefix='.mcc-',dir=path.parent)
    try:
        with os.fdopen(fd,'wb') as f:f.write(data);f.flush();os.fsync(f.fileno())
        os.replace(tmp,path)
    finally:
        if os.path.exists(tmp):os.unlink(tmp)

def apply(meta,files):
    root=data_dir();oldfiles={};oldcreds={};written=[]
    def write(path,data):
        oldfiles[path]=path.read_bytes() if path.exists() else None
        write_atomic(path,data);written.append(path)
    try:
        if 'devices.json' in files:write(root/'devices.json',files['devices.json'])
        for name,data in files.items():
            if name.startswith('maps/'):
                target=root/'offline-maps'/Path(name).name
                if not target.exists():write(target,data)
                elif hashlib.sha256(target.read_bytes()).hexdigest()!=name[5:69]:raise ValueError('Existing map conflicts with backup.')
        if meta['selected']:
            target=root/'offline-maps'/Path(meta['selected']).name
            write(root/'offline-map.json',json.dumps({'path':str(target)}).encode())
        for name,value in meta['credentials'].items():
            scope,key=name.split('/');oldcreds[name]=credential_store.get(key,scope);credential_store.put(key,value,scope)
    except Exception:
        failed=False
        for name,old in reversed(list(oldcreds.items())):
            try:
                scope,key=name.split('/')
                if old is None:credential_store.delete(key,scope)
                else:credential_store.put(key,old,scope)
            except Exception:failed=True
        for path in reversed(written):
            try:
                if oldfiles[path] is None:path.unlink(missing_ok=True)
                else:write_atomic(path,oldfiles[path])
            except Exception:failed=True
        if failed:raise RuntimeError('Restore failed and rollback was incomplete. Use the recovery backup.')
        raise

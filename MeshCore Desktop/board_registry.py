"""Experimental multi-board connection registry; not connected to the UI yet."""
import json
import ipaddress
import os
import re
import threading
import uuid
from pathlib import Path


class BoardRegistry:
    """Keep stable board profiles and independent active bridge instances.

    Profiles contain only connection addresses and display names. The registry
    does not move or rewrite the app's existing radio data.
    """
    def __init__(self, path, bridge_factory):
        self.path=Path(path);self._factory=bridge_factory;self._lock=threading.RLock();self._profiles=[];self._active={}
        if self.path.exists():
            value=json.loads(self.path.read_text(encoding='utf-8'))
            if not isinstance(value,dict) or value.get('version')!=1 or not isinstance(value.get('boards'),list):raise ValueError('Board profiles are invalid.')
            for entry in value['boards']:
                self._validate(entry)
                if any(p['id']==entry['id'] or (p['kind'],p['address'])==(entry['kind'],entry['address']) for p in self._profiles):raise ValueError('Duplicate board profile.')
                self._profiles.append(dict(entry))

    @staticmethod
    def _validate(entry):
        if not isinstance(entry,dict) or not re.fullmatch(r'[0-9a-f]{32}',str(entry.get('id',''))):raise ValueError('Invalid board ID.')
        if entry.get('kind') not in ('wifi','ble') or not isinstance(entry.get('address'),str) or not entry['address'].strip() or len(entry['address'])>256:raise ValueError('Invalid board address.')
        if entry['kind']=='wifi':
            try:ipaddress.IPv4Address(entry['address'])
            except ipaddress.AddressValueError:raise ValueError('Enter a valid IPv4 address.')
        elif not re.fullmatch(r'(?:[0-9A-F]{2}:){5}[0-9A-F]{2}',entry['address']):raise ValueError('Enter a valid Bluetooth address.')
        if not isinstance(entry.get('name'),str) or not entry['name'].strip() or len(entry['name'])>64 or any(ord(c)<32 for c in entry['name']):raise ValueError('Invalid board name.')

    def _save(self):
        self.path.parent.mkdir(parents=True,exist_ok=True)
        tmp=self.path.with_name(self.path.name+'.'+uuid.uuid4().hex+'.tmp')
        try:
            tmp.write_text(json.dumps({'version':1,'boards':self._profiles},ensure_ascii=False,indent=2),encoding='utf-8')
            os.replace(tmp,self.path)
        finally:
            try:tmp.unlink(missing_ok=True)
            except OSError:pass

    def list(self):
        with self._lock:return [{**p,'open':p['id'] in self._active,'connected':p['id'] in self._active and bool(self._active[p['id']].alive)} for p in self._profiles]

    def add(self,kind,address,name=None):
        with self._lock:
            if not isinstance(address,str):raise ValueError('Invalid board address.')
            address=address.strip()
            if kind=='ble':address=address.upper()
            if any((p['kind'],p['address'])==(kind,address) for p in self._profiles):raise ValueError('This connection is already saved.')
            profile={'id':uuid.uuid4().hex,'kind':kind,'address':address,'name':name or f'Board {len(self._profiles)+1}'};self._validate(profile)
            self._profiles.append(profile)
            try:self._save()
            except Exception:self._profiles.pop();raise
            return dict(profile)

    def rename(self,board_id,name):
        with self._lock:
            profile=self._get(board_id);old=profile['name'];profile['name']=name.strip() if isinstance(name,str) else name
            try:self._validate(profile);self._save()
            except Exception:profile['name']=old;raise
            return dict(profile)

    def _get(self,board_id):
        profile=next((p for p in self._profiles if p['id']==board_id),None)
        if not profile:raise ValueError('Board not found.')
        return profile

    def connect(self,board_id,device=None):
        with self._lock:
            profile=self._get(board_id)
            if board_id in self._active:raise ValueError('This board is already connected.')
            instance=self._factory(dict(profile),device)
            self._active[board_id]=instance
            return instance

    def active(self,board_id):
        with self._lock:
            self._get(board_id)
            instance=self._active.get(board_id)
            if instance is None:raise ValueError('Board is not connected.')
            return instance

    def disconnect(self,board_id):
        with self._lock:
            self._get(board_id);instance=self._active.pop(board_id,None)
            if instance:instance.close()

    def close(self):
        with self._lock:
            instances=list(self._active.values());self._active.clear()
        for instance in instances:instance.close()

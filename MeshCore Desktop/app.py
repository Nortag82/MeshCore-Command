import sys, os, json, time, threading, subprocess, ipaddress, socket, concurrent.futures, re, urllib.request, urllib.error, ctypes
from pathlib import Path
import hashlib

import serial
import tkinter as tk
from serial.tools import list_ports
import webview
from webview.http import _get_random_port
from bridge import CompanionBridge

ROOT = Path(getattr(sys, '_MEIPASS', Path(__file__).resolve().parent))
FIRMWARE = ROOT / 'firmware'
CREATE_NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)

def disable_udp_connection_resets(sock):
    if sys.platform != 'win32': return
    # Python's socket.ioctl accepts only a limited set of commands on Windows.
    wsa_ioctl=ctypes.windll.ws2_32.WSAIoctl
    wsa_ioctl.argtypes=[ctypes.c_size_t,ctypes.c_ulong,ctypes.c_void_p,ctypes.c_ulong,ctypes.c_void_p,ctypes.c_ulong,ctypes.POINTER(ctypes.c_ulong),ctypes.c_void_p,ctypes.c_void_p]
    wsa_ioctl.restype=ctypes.c_int
    enabled=ctypes.c_ulong(0); returned=ctypes.c_ulong()
    wsa_ioctl(sock.fileno(),0x9800000C,ctypes.byref(enabled),ctypes.sizeof(enabled),None,0,ctypes.byref(returned),None,None)

def verify_firmware(folder, required):
    manifest=json.loads((FIRMWARE/'manifest.json').read_text(encoding='utf-8'))
    for name in required:
        path=FIRMWARE/folder/name
        expected=manifest.get(folder,{}).get(name,'')
        if not re.fullmatch(r'[0-9a-f]{64}',expected) or hashlib.sha256(path.read_bytes()).hexdigest()!=expected:
            raise ValueError('Firmware file is missing or failed its integrity check.')

class API:
    def __init__(self):
        self._window = None
        self._bridge = None
        self._radio_open_lock = threading.Lock()
        self._radio_window = None
        self._state = {'busy': False, 'message': 'Ready.', 'log': '', 'ip': '', 'key': '', 'flash_phase': 'idle', 'flash_progress': None, 'scan_phase': 'idle', 'scan_sent': 0, 'scan_total': 0, 'scan_remaining': None, 'scan_failed': 0}
        self._lock = threading.Lock()
        self._startup_ready = threading.Event()
        self._devices_lock = threading.Lock()
        self._devices_file = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'MeshCore Command Data' / 'devices.json'
    def _update(self, **kw):
        with self._lock: self._state.update(kw)
    def status(self):
        with self._lock: return dict(self._state)
    def ports(self):
        found=[]
        for p in list_ports.comports():
            candidate = p.vid == 0x303A and p.pid == 0x1001
            found.append({'port':p.device, 'name':p.description, 'candidate':candidate or p.vid in (0x10C4,0x1A86,0x303A), 'detail':p.hwid})
        return sorted(found,key=lambda p:(not p['candidate'], p['port']))
    def flash(self, target, port, ssid, password):
        targets={
            'v3-headless':('esp','v3-headless','8MB'),
            'v3-oled':('esp','v3-oled','8MB'),
            'v4-auto':('esp','v4-auto','16MB'),
            'v4-tft':('esp','v4-tft','16MB'),
            'tracker-headless':('esp','tracker-headless','8MB'),
            'tracker-screen':('esp','tracker-screen','8MB'),
            'r8-headless':('esp','r8-headless','16MB'),
            'r8-oled':('esp','r8-oled','16MB'),
            'tbeam-oled':('esp','tbeam-oled','8MB'),
        }
        if self._state['busy']: return {'error':'Another operation is running.'}
        if target not in targets: return {'error':'Choose the exact board and screen.'}
        kind,folder,flash_size=targets[target]
        image_dir=FIRMWARE/folder
        required=['bootloader.bin','partitions.bin','boot_app0.bin','firmware.bin']
        if any(not (image_dir/name).is_file() for name in required): return {'error':'Firmware for this board is not available in this installation.'}
        try: verify_firmware(folder,required)
        except (OSError,ValueError,TypeError,json.JSONDecodeError) as e: return {'error':'Firmware validation failed: '+str(e)}
        if port not in [p.device for p in list_ports.comports()]: return {'error':'Select an available COM port.'}
        if self._bridge and self._bridge.alive and getattr(getattr(self._bridge.socket,'serial',None),'port',None)==port: return {'error':'Close the USB radio window before flashing this COM port.'}
        try: sb, pb = ssid.encode('utf-8'), password.encode('utf-8')
        except Exception: return {'error':'Invalid Wi-Fi credentials.'}
        if not 1 <= len(sb) <= 32 or b'\0' in sb: return {'error':'SSID must be 1–32 UTF-8 bytes.'}
        if len(pb) not in [0] and not 8 <= len(pb) <= 63: return {'error':'Wi-Fi password must be empty or 8–63 UTF-8 bytes.'}
        if b'\0' in pb: return {'error':'Password cannot contain a zero byte.'}
        self._update(busy=True,message='Connecting to the board. Keep USB connected.',log='',ip='',key='',flash_phase='flashing',flash_progress=None)
        threading.Thread(target=self._flash_worker,args=(port,sb,pb,image_dir,flash_size),daemon=True).start()
        return {'ok':True}

    def _run_flasher(self, cmd):
        """Stream esptool output without blocking its pipe; bound time and retained log."""
        env=os.environ.copy(); env['PYTHONUNBUFFERED']='1'; env['NO_COLOR']='1'
        proc=subprocess.Popen(cmd,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,creationflags=CREATE_NO_WINDOW,env=env)
        chunks=[]; length=0; pending=''; output_lock=threading.Lock()
        def consume():
            nonlocal length,pending
            try:
                while True:
                    block=proc.stdout.read1(1024)
                    if not block: break
                    piece=block.decode('utf-8','replace').replace('\x00','')
                    clean=re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]','',piece)
                    with output_lock:
                        chunks.append(clean);length+=len(clean)
                        while length>12000 and len(chunks)>1: length-=len(chunks.pop(0))
                        log=''.join(chunks)[-6000:]
                    pending=(pending+clean)[-3000:]
                    lines=re.split(r'[\r\n]',pending);pending=lines.pop()
                    for line in lines:
                        if 'Writing at' in line:
                            match=re.search(r'(?<!\d)(\d{1,3})\s*%',line)
                            percent=min(100,int(match.group(1))) if match else None
                            self._update(message='Writing firmware'+(f' — current image {percent}%.' if percent is not None else '…')+' Keep USB connected.',flash_progress=percent)
                        elif re.search(r'Hash of data verified|verified successfully',line,re.I):
                            self._update(message='Verifying written firmware…',flash_progress=None)
                    self._update(log=log)
            except (OSError,ValueError):
                pass
        reader=threading.Thread(target=consume,daemon=True);reader.start()
        try:
            proc.wait(timeout=180)
        except subprocess.TimeoutExpired:
            proc.kill();proc.wait();reader.join(timeout=5)
            raise RuntimeError('Flashing timed out. Check the USB connection and retry.')
        reader.join(timeout=5)
        with output_lock: output=''.join(chunks)[-12000:]
        if proc.returncode: raise RuntimeError('Flash failed. '+output[-1500:])
        return output

    def _flash_worker(self, port, ssid, password, image_dir, flash_size):
        try:
            prefix = [str(ROOT / 'flash-helper.exe')] if getattr(sys,'frozen',False) else [sys.executable, '-m', 'esptool']
            cmd=prefix+['--chip','esp32s3','--port',port,'--baud','921600','write-flash','--flash-mode','dio','--flash-size',flash_size,'0x0',str(image_dir/'bootloader.bin'),'0x8000',str(image_dir/'partitions.bin'),'0xe000',str(image_dir/'boot_app0.bin'),'0x10000',str(image_dir/'firmware.bin')]
            output=self._run_flasher(cmd)
            self._update(message='Firmware verified. Waiting for USB Wi-Fi setup…',log=output[-6000:],flash_phase='provisioning',flash_progress=None)
            time.sleep(1)
            with serial.Serial(port,115200,timeout=0.3,dsrdtr=False,rtscts=False) as ser:
                ser.dtr=False; ser.rts=True; time.sleep(.15); ser.rts=False
                deadline=time.monotonic()+65; sent=False; provisioned=False; ip=''; key=''; lines=[]
                while time.monotonic()<deadline:
                    line=ser.readline().decode('utf-8','replace').strip()
                    if not line: continue
                    lines.append(line)
                    if 'MC_PROVISION_READY_V1' in line and not sent:
                        self._update(message='Sending Wi-Fi details over USB…',flash_phase='provisioning',flash_progress=None)
                        ser.write(b'MC_WIFI:'+ssid.hex().encode()+b':'+password.hex().encode()+b'\n'); ser.flush(); sent=True
                    if 'MC_PROVISION_OK' in line: provisioned=True; self._update(message='Wi-Fi details saved. Waiting for DHCP…',flash_phase='dhcp',flash_progress=None)
                    if 'MC_PROVISION_ERROR' in line: raise RuntimeError('The device rejected the Wi-Fi settings: '+line)
                    m=re.search(r'PC WiFi: .* at (\d+\.\d+\.\d+\.\d+)',line)
                    if m: ip=m.group(1)
                    m=re.search(r'Web login: meshcore / ([A-Za-z0-9]+)',line)
                    if m: key=m.group(1)
                    if ip: break
                    if 'PC WiFi: no connection' in line: raise RuntimeError('Wi-Fi credentials were saved, but the radio could not join the network. Check 2.4 GHz coverage and password; use USB to configure again.')
                if not sent or not provisioned: raise RuntimeError('Firmware flashed but USB provisioning was not acknowledged. Retry Flash & configure.')
                if not ip: raise RuntimeError('Wi-Fi was saved, but no DHCP address was reported. Check the 2.4 GHz network or use Scan network.')
                self._update(busy=False,message='Ready: '+ip,ip=ip,key=key,log='Firmware verified; Wi-Fi saved; DHCP address received.',flash_phase='complete',flash_progress=100)
        except Exception as e: self._update(busy=False,message='Error: '+str(e),flash_phase='error',flash_progress=None)
    def default_network(self):
        try:
            s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.connect(('1.1.1.1',80));ip=s.getsockname()[0];s.close()
            return str(ipaddress.ip_network(ip+'/24',strict=False))
        except OSError: return ''
    def scan(self, network, key=''):
        if self._state['busy']: return {'error':'Another operation is running.'}
        try:
            net=ipaddress.ip_network(network,strict=False)
            if net.version != 4 or net.num_addresses > 1024: raise ValueError()
        except ValueError: return {'error':'Enter an IPv4 subnet with at most 1024 addresses, for example 192.168.1.0/24.'}
        self._update(busy=True,message='Sending discovery probes to '+str(net)+'…',results=[],scan_phase='sending',scan_sent=0,scan_total=sum(1 for _ in net.hosts()),scan_remaining=None,scan_failed=0)
        threading.Thread(target=self._scan_worker,args=(net,key),daemon=True).start()
        return {'ok':True}
    def _scan_worker(self, network, key):
        try:
            found=[]
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sock:
                sock.settimeout(0.3)
                # Windows otherwise turns ICMP port-unreachable replies from unrelated
                # hosts into WSAECONNRESET on a later recvfrom on this UDP socket.
                disable_udp_connection_resets(sock)
                total=sum(1 for _ in network.hosts()); attempted=failed=0
                for host in network.hosts():
                    try: sock.sendto(b'MESHCORE_DISCOVER_V1',(str(host),5010))
                    except OSError: failed+=1
                    attempted+=1
                    if attempted%16==0 or attempted==total:self._update(scan_sent=attempted,scan_failed=failed)
                if attempted and failed==attempted:raise RuntimeError('Could not send any discovery probes. Check your network connection.')
                self._update(message='Probes sent. Waiting for replies…',scan_phase='waiting',scan_remaining=5)
                deadline=time.monotonic()+5
                while time.monotonic()<deadline:
                    self._update(scan_remaining=max(0,int(deadline-time.monotonic()+0.999)))
                    try:
                        data,addr=sock.recvfrom(128)
                        if data==b'MESHCORE_V4_COMPANION_V1' and not any(d['ip']==addr[0] for d in found):
                            found.append({'ip':addr[0],'kind':'MeshCore Wi-Fi companion'});self._update(results=found.copy())
                    except socket.timeout: pass
                    except OSError as e:
                        if getattr(e, 'winerror', None) == 10054: continue
                        raise
            note=f' {failed} probe(s) could not be sent.' if failed else ''
            self._update(busy=False,results=found,message='Scan complete: '+str(len(found))+' device(s) found.'+note,scan_phase='complete',scan_remaining=0)
        except Exception as e:self._update(busy=False,message='Scan failed: '+str(e),results=found,scan_phase='error',scan_remaining=None)
    def startup_ready(self):
        self._startup_ready.set()
        return {'ok':True}

    def _read_devices(self):
        try:
            data=json.loads(self._devices_file.read_text(encoding='utf-8'))
            if not isinstance(data, list): raise ValueError('Invalid saved device data')
            return data
        except FileNotFoundError: return []

    def saved_devices(self):
        try:
            with self._devices_lock: return {'devices':self._read_devices()}
        except (OSError, ValueError) as e: return {'error':'Unable to read saved devices: '+str(e)}

    def save_device(self, ip, name):
        try:
            ip=str(ipaddress.IPv4Address(ip.strip()))
            name=name.strip()
            if len(name)>80: raise ValueError('Device name must be 80 characters or fewer.')
            with self._devices_lock:
                devices=self._read_devices()
                device={'ip':ip,'name':name or ip}
                devices=[d for d in devices if isinstance(d,dict) and d.get('ip')!=ip]
                devices.append(device)
                self._devices_file.parent.mkdir(parents=True, exist_ok=True)
                tmp=self._devices_file.with_suffix('.tmp')
                tmp.write_text(json.dumps(devices,ensure_ascii=False,indent=2),encoding='utf-8')
                os.replace(tmp,self._devices_file)
            return {'ok':True}
        except (OSError, ValueError, AttributeError, TypeError) as e: return {'error':'Could not save device: '+str(e)}

    def remove_device(self, ip):
        try:
            ip=str(ipaddress.IPv4Address(ip))
            with self._devices_lock:
                devices=[d for d in self._read_devices() if isinstance(d,dict) and d.get('ip')!=ip]
                self._devices_file.parent.mkdir(parents=True, exist_ok=True)
                tmp=self._devices_file.with_suffix('.tmp')
                tmp.write_text(json.dumps(devices,ensure_ascii=False,indent=2),encoding='utf-8')
                os.replace(tmp,self._devices_file)
            return {'ok':True}
        except (OSError, ValueError, AttributeError, TypeError) as e: return {'error':'Could not remove device: '+str(e)}

    def toggle_fullscreen(self):
        self._window.toggle_fullscreen()

    def minimize(self):
        self._window.minimize()

    def exit_app(self):
        # Close secondary windows before the main window so their bridges shut down.
        for window in reversed(list(webview.windows)):
            try: window.destroy()
            except Exception: pass

    def _backup_dialogs(self, radio):
        def save_secure_backup(contents):
            try:
                if not isinstance(contents, str) or len(contents) > 8_000_000:
                    return {'error': 'Invalid backup size.'}
                envelope = json.loads(contents)
                if envelope.get('format') != 'MeshCoreCommandSecureBackup' or not isinstance(envelope.get('ciphertext'), str):
                    return {'error': 'Invalid encrypted backup.'}
                files = radio.create_file_dialog(webview.FileDialog.SAVE, save_filename='MeshCore-Command-Secure-Backup.mccbackup', file_types=('MeshCore backup (*.mccbackup)',))
                if not files: return {'cancelled': True}
                Path(files[0]).write_text(contents, encoding='utf-8')
                return {'ok': True}
            except Exception:
                return {'error': 'Could not save the backup file.'}
        def open_secure_backup():
            try:
                files = radio.create_file_dialog(webview.FileDialog.OPEN, allow_multiple=False, file_types=('MeshCore backup (*.mccbackup)',))
                if not files: return {'cancelled': True}
                path = Path(files[0])
                if path.stat().st_size > 8_000_000: return {'error': 'Backup file is too large.'}
                return {'contents': path.read_text(encoding='utf-8')}
            except Exception:
                return {'error': 'Could not read the backup file.'}
        return save_secure_backup, open_secure_backup

    def _open_radio(self, address, serial_port=False):
        if not self._radio_open_lock.acquire(blocking=False):
            return {'error':'A radio connection is already opening.'}
        started = time.monotonic()
        try:
            if self._radio_window is not None:
                return {'error':'A radio window is already open. Close it before connecting another radio.'}
            if serial_port:
                if address not in [p.device for p in list_ports.comports()]: return {'error':'Select an available USB COM port.'}
                self._bridge = CompanionBridge(None, ROOT/'radio.html', serial_port=address)
            else:
                address=str(ipaddress.IPv4Address(address))
                self._bridge = CompanionBridge(address, ROOT/'radio.html')
            active_bridge=self._bridge
            radio = webview.create_window('MeshCore Command — '+address, active_bridge.url, width=1250,height=850,min_size=(800,600),fullscreen=True,hidden=True)
            self._radio_window=radio
            def choose_offline_map():
                try:
                    files=radio.create_file_dialog(webview.FileDialog.OPEN, allow_multiple=False, file_types=('Offline maps (*.mbtiles;*.pmtiles)',))
                    if not files: return {'cancelled': True}
                    return active_bridge.set_offline_map(files[0])
                except Exception as error: return {'error': str(error)}
            radio.expose(radio.toggle_fullscreen, radio.minimize, self.exit_app, choose_offline_map, *self._backup_dialogs(radio))
            def radio_closed():
                active_bridge.close()
                if self._radio_window is radio:
                    self._radio_window=None
                    self._bridge=None
                    self._window.show()
            radio.events.closed += radio_closed
            if not radio.events.loaded.wait(30):
                radio.destroy()
                raise RuntimeError('The radio window did not finish loading. Please try again.')
            remaining = 6 - (time.monotonic() - started)
            if remaining > 0: time.sleep(remaining)
            radio.show()
            self._window.hide()
            return {'ok':True}
        except Exception as e:
            if self._bridge:
                self._bridge.close()
                self._bridge=None
            self._radio_window=None
            return {'error':str(e)}
        finally:
            self._radio_open_lock.release()

    def open_serial(self, port):
        return self._open_radio(port, serial_port=True)

    def open_device(self, ip):
        return self._open_radio(ip)

def show_startup_splash(ready):
    """Keep a small native loading window responsive while WebView starts."""
    root=tk.Tk()
    root.title('MeshCore Command')
    root.resizable(False,False)
    root.configure(bg='#172533')
    root.geometry('370x155')
    root.update_idletasks()
    x=(root.winfo_screenwidth()-370)//2; y=(root.winfo_screenheight()-155)//2
    root.geometry(f'370x155+{x}+{y}')
    tk.Label(root,text='MeshCore Command',font=('Segoe UI',15,'bold'),fg='#e6edf3',bg='#172533').pack(pady=(22,8))
    label=tk.Label(root,text='Starting…',font=('Segoe UI',10),fg='#b8c6d0',bg='#172533',wraplength=320)
    label.pack()
    started=time.monotonic()
    def check():
        if ready.is_set(): root.destroy(); return
        if time.monotonic()-started>25:
            label.configure(text='Startup is taking longer than expected. You can close this window and try again.')
        root.after(200,check)
    # Closing the loading box cancels startup, even if WebView has stalled.
    root.protocol('WM_DELETE_WINDOW',lambda:os._exit(1))
    root.after(200,check)
    root.mainloop()

if __name__ == '__main__':
    api=API()
    threading.Thread(target=show_startup_splash,args=(api._startup_ready,),daemon=True).start()
    api._window=webview.create_window('MeshCore Command', str(ROOT/'index.html'), js_api=api,width=1060,height=760,min_size=(780,600),hidden=True,fullscreen=True)
    def reveal():
        api._startup_ready.wait()
        api._window.show()
    threading.Thread(target=reveal,daemon=True).start()
    profile = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'MeshCore WiFi Data' / 'WebView'
    webview.start(gui='edgechromium', private_mode=False, http_port=_get_random_port(), storage_path=str(profile), icon=str(ROOT/'meshcore-command.ico'))

import sys, os, json, time, threading, subprocess, ipaddress, socket, concurrent.futures, re, urllib.request, urllib.error, ctypes
from pathlib import Path
import hashlib

import serial
import tkinter as tk
from serial.tools import list_ports
import webview
from webview.http import _get_random_port
from bridge import CompanionBridge
from multi_board_controller import MultiBoardController
import updater
import full_backup
from version import VERSION

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
        self._board_controller = None
        self._radio_open_lock = threading.Lock()
        self._radio_window = None
        self._state = {'busy': False, 'message': 'Ready.', 'log': '', 'ip': '', 'key': '', 'flash_phase': 'idle', 'flash_progress': None, 'scan_phase': 'idle', 'scan_sent': 0, 'scan_total': 0, 'scan_remaining': None, 'scan_failed': 0}
        self._lock = threading.Lock()
        self._startup_ready = threading.Event()
        self._devices_lock = threading.Lock()
        self._update_lock = threading.Lock()
        self._update_started = False
        self._devices_file = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'MeshCore Command Data' / 'devices.json'
    def _update(self, **kw):
        with self._lock: self._state.update(kw)
    def status(self):
        with self._lock: return dict(self._state)
    def app_version(self):
        return VERSION
    def check_app_update(self):
        try: return updater.check()
        except Exception as error: return {'error': 'Could not check for updates: ' + str(error)}

    def can_prompt_update(self):
        with self._lock: busy = bool(self._state['busy'])
        return {'allowed': not busy and self._radio_window is None and not self._radio_open_lock.locked() and not self._update_started}

    def install_app_update(self, version):
        if not self._update_lock.acquire(blocking=False): return {'error': 'An update is already in progress.'}
        try:
            if self._update_started: return {'error': 'The update is already starting.'}
            if not getattr(sys, 'frozen', False): return {'error': 'Automatic installation is only available in an installed Windows build.'}
            if not isinstance(version, str) or updater.parse_version(version) is None: return {'error': 'Invalid update version.'}
            with self._lock:
                if self._state['busy']: return {'error': 'Wait for the current scan or flash operation to finish.'}
            installer = updater.download(version)
            with self._lock:
                if self._state['busy']: return {'error': 'Wait for the current scan or flash operation to finish.'}
                self._state['busy'] = True
            try:
                subprocess.Popen([installer, '--update', str(os.getpid())], close_fds=True, creationflags=CREATE_NO_WINDOW | getattr(subprocess, 'DETACHED_PROCESS', 0))
            except Exception:
                with self._lock: self._state['busy'] = False
                raise
            self._update_started = True
            threading.Timer(1.5, self.exit_app).start()
            return {'ok': True}
        except Exception as error:
            return {'error': 'Update was not started: ' + str(error)}
        finally:
            self._update_lock.release()
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

    def _full_backup_dialogs(self, radio):
        pending = {}
        def ensure_single_board():
            controller=self._board_controller
            if controller and sum(1 for board in controller.registry.list() if board['open'])>1:
                raise ValueError('Disconnect the other boards before restoring a backup, then reconnect them afterward.')
        def export_full_backup(storage, password, radio_config=None):
            try:
                files=radio.create_file_dialog(webview.FileDialog.SAVE, save_filename='MeshCore-Command-Full-Backup.mccfull', file_types=('MeshCore full backup (*.mccfull)',))
                if not files: return {'cancelled':True}
                encrypted=full_backup.encrypt(full_backup.snapshot(storage,radio_config),password)
                full_backup.write_atomic(Path(files[0]),encrypted)
                return {'ok':True}
            except Exception as error: return {'error':str(error) or 'Could not export backup.'}
        def prepare_full_restore(storage, password):
            pending.clear()
            try:
                ensure_single_board()
                files=radio.create_file_dialog(webview.FileDialog.OPEN, allow_multiple=False, file_types=('MeshCore backups (*.mccfull;*.mccbackup)',))
                if not files: return {'cancelled':True}
                source=Path(files[0]);size=source.stat().st_size
                if size>full_backup.LIMIT+1024: raise ValueError('Backup exceeds the 256 MB limit.')
                contents=source.read_bytes()
                if not contents.startswith(full_backup.MAGIC):
                    if size>8_000_000: raise ValueError('Invalid or unsupported backup.')
                    legacy=json.loads(contents.decode('utf-8'))
                    if not isinstance(legacy,dict) or legacy.get('format')!='MeshCoreCommandSecureBackup' or not isinstance(legacy.get('ciphertext'),str): raise ValueError('Invalid or unsupported backup.')
                    return {'legacy_secure':contents.decode('utf-8')}
                meta,contents=full_backup.decode(contents,password)
                recovery=full_backup.data_dir() / 'recovery-backups' / ('before-restore-'+time.strftime('%Y%m%d-%H%M%S')+'-'+os.urandom(4).hex()+'.mccfull')
                full_backup.write_atomic(recovery,full_backup.encrypt(full_backup.snapshot(storage),password))
                pending.update(meta=meta,contents=contents)
                return {'storage':meta['storage'],'recovery':str(recovery)}
            except Exception as error: return {'error':str(error) or 'Could not open backup.'}
        def commit_full_restore():
            if not pending: return {'error':'No restore is ready.'}
            try:
                full_backup.apply(pending['meta'],pending['contents'])
                pending.clear()
                return {'ok':True}
            except Exception as error:
                pending.clear()
                return {'error':str(error) or 'Could not restore desktop data.'}
        def cancel_full_restore():
            pending.clear()
            return {'ok':True}
        def save_pc_recovery(storage,password):
            try:
                recovery=full_backup.data_dir() / 'recovery-backups' / ('before-legacy-restore-'+time.strftime('%Y%m%d-%H%M%S')+'-'+os.urandom(4).hex()+'.mccfull')
                full_backup.write_atomic(recovery,full_backup.encrypt(full_backup.snapshot(storage),password))
                return {'recovery':str(recovery)}
            except Exception as error: return {'error':str(error) or 'Could not create the recovery backup.'}
        def open_radio_backup(password):
            try:
                ensure_single_board()
                files=radio.create_file_dialog(webview.FileDialog.OPEN, allow_multiple=False, file_types=('MeshCore full backup (*.mccfull)',))
                if not files: return {'cancelled':True}
                source=Path(files[0])
                if source.stat().st_size>full_backup.LIMIT+1024: raise ValueError('Backup exceeds the 256 MB limit.')
                meta,_=full_backup.decode(source.read_bytes(),password)
                if not meta.get('radio'): raise ValueError('This backup has no radio configuration or identity.')
                return {'radio':meta['radio']}
            except Exception as error: return {'error':str(error) or 'Could not read radio backup.'}
        def save_radio_recovery(storage,password,radio_config):
            try:
                if not radio_config: raise ValueError('Current radio backup is missing.')
                recovery=full_backup.data_dir() / 'recovery-backups' / ('before-radio-restore-'+time.strftime('%Y%m%d-%H%M%S')+'-'+os.urandom(4).hex()+'.mccfull')
                full_backup.write_atomic(recovery,full_backup.encrypt(full_backup.snapshot(storage,radio_config),password))
                return {'recovery':str(recovery)}
            except Exception as error: return {'error':str(error) or 'Could not create the radio recovery backup.'}
        return export_full_backup,prepare_full_restore,commit_full_restore,cancel_full_restore,open_radio_backup,save_radio_recovery,save_pc_recovery

    def _open_radio(self, address, serial_port=False):
        if not self._radio_open_lock.acquire(blocking=False):
            return {'error':'A radio connection is already opening.'}
        started = time.monotonic()
        try:
            if self._radio_window is not None:
                return {'error':'A radio window is already open. Close it before connecting another radio.'}
            controller=None
            if serial_port:
                if address not in [p.device for p in list_ports.comports()]: return {'error':'Select an available USB COM port.'}
                self._bridge = CompanionBridge(None, ROOT/'radio.html', serial_port=address)
                window_url=self._bridge.url
            else:
                address=str(ipaddress.IPv4Address(address))
                profiles=Path(os.environ.get('LOCALAPPDATA',str(Path.home()))) / 'MeshCore Command Data' / 'boards.json'
                controller=MultiBoardController(ROOT/'radio.html',ROOT/'multi_board_shell.html',profiles,port=19483)
                self._board_controller=controller
                initial=controller.connect_wifi(address)['id']
                self._bridge=controller.registry.active(initial)
                window_url=controller.hub.origin+'/'
            active_bridge=self._bridge
            radio = webview.create_window('MeshCore Command — '+address, window_url, width=1250,height=850,min_size=(800,600),fullscreen=True,hidden=True)
            self._radio_window=radio
            def choose_offline_map(board_id=None):
                try:
                    files=radio.create_file_dialog(webview.FileDialog.OPEN, allow_multiple=False, file_types=('Offline maps (*.mbtiles;*.pmtiles)',))
                    if not files: return {'cancelled': True}
                    target=controller.registry.active(board_id) if controller and board_id else active_bridge
                    result=target.set_offline_map(files[0])
                    if controller:
                        for board in controller.registry.list():
                            if board['open'] and board['id']!=board_id:
                                controller.registry.active(board['id']).set_offline_map(files[0],persist=False)
                    return result
                except Exception as error: return {'error': str(error)}
            def save_terrain_plan(contents):
                try:
                    if not isinstance(contents,str) or len(contents.encode('utf-8'))>1_000_000: raise ValueError('Plan exceeds the 1 MB limit.')
                    files=radio.create_file_dialog(webview.FileDialog.SAVE, save_filename='MeshCore-Terrain-Plan.mccplan', file_types=('MeshCore terrain plan (*.mccplan)',))
                    if not files: return {'cancelled':True}
                    full_backup.write_atomic(Path(files[0]),contents.encode('utf-8'))
                    return {'ok':True}
                except Exception as error: return {'error':str(error) or 'Could not save plan.'}
            def open_terrain_plan():
                try:
                    files=radio.create_file_dialog(webview.FileDialog.OPEN, allow_multiple=False, file_types=('MeshCore terrain plan (*.mccplan)',))
                    if not files: return {'cancelled':True}
                    source=Path(files[0])
                    if source.stat().st_size>1_000_000: raise ValueError('Plan exceeds the 1 MB limit.')
                    return {'contents':source.read_text(encoding='utf-8')}
                except Exception as error: return {'error':str(error) or 'Could not open plan.'}
            if controller:
                def call_board(action,*args):
                    try:return action(*args)
                    except Exception as error:return {'error':str(error) or 'Board operation failed.'}
                def board_list():return call_board(controller.list_boards)
                def board_connect_wifi(address):return call_board(controller.connect_wifi,address)
                def board_scan_ble():return call_board(controller.scan_ble)
                def board_connect_ble(address,pin):return call_board(controller.connect_ble,address,pin)
                def board_rename(board_id,name):return call_board(controller.rename,board_id,name)
                def board_disconnect(board_id):return call_board(controller.disconnect,board_id)
                radio.expose(board_list,board_connect_wifi,board_scan_ble,board_connect_ble,board_rename,board_disconnect)
            radio.expose(radio.toggle_fullscreen, radio.minimize, self.exit_app, choose_offline_map, save_terrain_plan, open_terrain_plan, self.check_app_update, self.install_app_update, *self._full_backup_dialogs(radio))
            def radio_closed():
                if controller:controller.close()
                else:active_bridge.close()
                if self._radio_window is radio:
                    self._radio_window=None
                    self._bridge=None
                    self._board_controller=None
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
            if self._board_controller:
                self._board_controller.close()
                self._board_controller=None
                self._bridge=None
            elif self._bridge:
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

def show_startup_splash():
    """Run Tk on the main thread of a disposable splash-only process."""
    ready=threading.Event()
    def wait_for_parent():
        try: sys.stdin.buffer.read(1)
        except (OSError, AttributeError): pass
        ready.set()
    threading.Thread(target=wait_for_parent,daemon=True).start()
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
    # Closing the loading box only closes this helper, not the main app.
    root.protocol('WM_DELETE_WINDOW',root.destroy)
    root.after(200,check)
    root.mainloop()

def start_splash_process():
    command=[sys.executable]
    if not getattr(sys, 'frozen', False): command.append(str(Path(__file__).resolve()))
    command.append('--startup-splash')
    try:
        return subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                                stderr=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW)
    except OSError:
        return None

def stop_splash_process(process):
    if process and process.stdin:
        try: process.stdin.close()
        except OSError: pass

if __name__ == '__main__':
    if '--startup-splash' in sys.argv:
        show_startup_splash()
        sys.exit(0)
    api=API()
    splash=start_splash_process()
    api._window=webview.create_window('MeshCore Command', str(ROOT/'index.html'), js_api=api,width=1060,height=760,min_size=(780,600),hidden=True,fullscreen=True)
    def reveal():
        api._startup_ready.wait()
        stop_splash_process(splash)
        api._window.show()
    threading.Thread(target=reveal,daemon=True).start()
    profile = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'MeshCore WiFi Data' / 'WebView'
    webview.start(gui='edgechromium', private_mode=False, http_port=_get_random_port(), storage_path=str(profile), icon=str(ROOT/'meshcore-command.ico'))

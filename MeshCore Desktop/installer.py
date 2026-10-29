import base64
import os
import queue
import subprocess
import sys
import threading
import zipfile
from pathlib import Path
import tkinter as tk
from tkinter import messagebox, ttk

ROOT = Path(getattr(sys, '_MEIPASS', Path(__file__).parent))
TARGET = Path(os.environ['LOCALAPPDATA']) / 'MeshCore Command'


def install_payload(root, target, report):
    report('Preparing installation…', 0)
    target.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(root / 'payload.zip') as archive:
        entries = archive.infolist()
        for entry in entries:
            if not (target / entry.filename).resolve().is_relative_to(target.resolve()):
                raise ValueError('Invalid installer path')
        total = sum(entry.file_size for entry in entries if not entry.is_dir()) or 1
        done = 0
        for entry in entries:
            destination = target / entry.filename
            if entry.is_dir():
                destination.mkdir(parents=True, exist_ok=True)
                continue
            destination.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, destination.open('wb') as output:
                while True:
                    block = source.read(1024 * 1024)
                    if not block:
                        break
                    output.write(block)
                    done += len(block)
                    report('Installing files…', min(95, 95 * done / total))
    report('Creating shortcuts…', 96)
    env = os.environ.copy()
    env['MESHCORE_INSTALL_TARGET'] = str(target / 'MeshCore Command.exe')
    script = r'''
$w = New-Object -ComObject WScript.Shell
$exe = $env:MESHCORE_INSTALL_TARGET
$locations = @($w.SpecialFolders.Item('Programs'), $w.SpecialFolders.Item('Desktop'))
foreach ($folder in $locations) {
  $link = $w.CreateShortcut((Join-Path $folder 'MeshCore Command.lnk'))
  $link.TargetPath = $exe
  $link.WorkingDirectory = Split-Path -Parent $exe
  $link.IconLocation = "$exe,0"
  $link.Description = 'MeshCore Command desktop radio controller'
  $link.Save()
}
$old = Join-Path $w.SpecialFolders.Item('Programs') 'MeshCore WiFi.lnk'
if (Test-Path -LiteralPath $old) {
  $prior = $w.CreateShortcut($old).TargetPath
  $known = @('MeshCore WiFi Alerts','MeshCore WiFi Repeaters','MeshCore WiFi PC','MeshCore WiFi')
  foreach ($name in $known) {
    $expected = Join-Path (Join-Path $env:LOCALAPPDATA $name) 'MeshCore WiFi.exe'
    if ($prior -ieq $expected) { Remove-Item -LiteralPath $old; break }
  }
}
'''
    encoded = base64.b64encode(script.encode('utf-16le')).decode()
    subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
                   check=True, env=env, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    report('Installation complete.', 100)


def main():
    if '--silent' in sys.argv:
        try:
            install_payload(ROOT, TARGET, lambda *_: None)
            return 0
        except Exception:
            return 1

    window = tk.Tk()
    window.title('Install MeshCore Command')
    window.resizable(False, False)
    window.configure(bg='#172533')
    window.geometry('440x190')
    window.update_idletasks()
    x = (window.winfo_screenwidth() - 440) // 2
    y = (window.winfo_screenheight() - 190) // 2
    window.geometry(f'440x190+{x}+{y}')
    tk.Label(window, text='Installing MeshCore Command', font=('Segoe UI', 15, 'bold'),
             bg='#172533', fg='#e6edf3').pack(pady=(23, 12))
    status = tk.StringVar(value='Preparing installation…')
    tk.Label(window, textvariable=status, font=('Segoe UI', 10), bg='#172533',
             fg='#b8c6d0').pack()
    progress = ttk.Progressbar(window, orient='horizontal', length=360, mode='determinate', maximum=100)
    progress.pack(pady=14)
    percent = tk.StringVar(value='0%')
    tk.Label(window, textvariable=percent, bg='#172533', fg='#b8c6d0').pack()
    window.protocol('WM_DELETE_WINDOW', lambda: None)
    events = queue.SimpleQueue()
    result = {"exit_code": 0}

    def worker():
        try:
            install_payload(ROOT, TARGET, lambda text, value: events.put(('progress', text, value)))
            events.put(('done',))
        except Exception as error:
            events.put(('error', str(error)))

    def update():
        try:
            while True:
                event = events.get_nowait()
                if event[0] == 'progress':
                    status.set(event[1]); progress['value'] = event[2]; percent.set(f'{int(event[2])}%')
                elif event[0] == 'error':
                    result['exit_code'] = 1
                    status.set('Installation failed.')
                    messagebox.showerror('Installation failed', event[1], parent=window)
                    window.destroy(); return
                else:
                    status.set('Installation complete.'); progress['value'] = 100; percent.set('100%')
                    window.update_idletasks()
                    launch = messagebox.askyesno('MeshCore Command', 'Installation complete. Start MeshCore Command now?', parent=window)
                    if launch:
                        try: os.startfile(str(TARGET / 'MeshCore Command.exe'))
                        except OSError as error:
                            messagebox.showerror('Could not start MeshCore Command', str(error), parent=window)
                    window.destroy()
                    return
        except queue.Empty:
            pass
        window.after(100, update)

    threading.Thread(target=worker, daemon=True).start()
    window.after(100, update)
    window.mainloop()
    return result['exit_code']


if __name__ == '__main__':
    sys.exit(main())

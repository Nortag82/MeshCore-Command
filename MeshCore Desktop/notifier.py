"""Windows notification-area balloon notifications for the desktop app."""
import atexit
import sys
from pathlib import Path
import ctypes
from ctypes import wintypes
import queue
import threading
import winsound

WM_APP_NOTIFY = 0x8001
WM_CLOSE = 0x0010
WM_DESTROY = 0x0002
NIM_ADD, NIM_MODIFY, NIM_DELETE = 0, 1, 2
NIF_MESSAGE, NIF_ICON, NIF_TIP, NIF_INFO = 1, 2, 4, 16
NIIF_INFO = 1

class GUID(ctypes.Structure):
    _fields_ = [('Data1',wintypes.DWORD),('Data2',wintypes.WORD),('Data3',wintypes.WORD),('Data4',ctypes.c_ubyte*8)]

class NOTIFYICONDATA(ctypes.Structure):
    _fields_ = [('cbSize',wintypes.DWORD),('hWnd',wintypes.HWND),('uID',wintypes.UINT),('uFlags',wintypes.UINT),('uCallbackMessage',wintypes.UINT),('hIcon',wintypes.HICON),('szTip',wintypes.WCHAR*128),('dwState',wintypes.DWORD),('dwStateMask',wintypes.DWORD),('szInfo',wintypes.WCHAR*256),('uTimeoutOrVersion',wintypes.UINT),('szInfoTitle',wintypes.WCHAR*64),('dwInfoFlags',wintypes.DWORD),('guidItem',GUID),('hBalloonIcon',wintypes.HICON)]

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT,wintypes.HWND,wintypes.UINT,wintypes.WPARAM,wintypes.LPARAM)
class WNDCLASS(ctypes.Structure):
    _fields_=[('style',wintypes.UINT),('lpfnWndProc',WNDPROC),('cbClsExtra',ctypes.c_int),('cbWndExtra',ctypes.c_int),('hInstance',wintypes.HINSTANCE),('hIcon',wintypes.HICON),('hCursor',wintypes.HANDLE),('hbrBackground',wintypes.HBRUSH),('lpszMenuName',wintypes.LPCWSTR),('lpszClassName',wintypes.LPCWSTR)]

class DesktopNotifier:
    def __init__(self):
        self.ready=threading.Event();self.pending=queue.Queue();self.hwnd=None;self.error=None
        threading.Thread(target=self._run,daemon=True,name='notification-area').start()
        if not self.ready.wait(5) or self.error: raise RuntimeError(self.error or 'Notification area did not start')
        atexit.register(self.close)
    def _run(self):
        try:
            user=ctypes.WinDLL('user32',use_last_error=True);shell=ctypes.WinDLL('shell32',use_last_error=True);kernel=ctypes.WinDLL('kernel32',use_last_error=True)
            kernel.GetModuleHandleW.argtypes=[wintypes.LPCWSTR];kernel.GetModuleHandleW.restype=wintypes.HMODULE
            user.LoadIconW.argtypes=[wintypes.HINSTANCE,wintypes.LPCWSTR];user.LoadIconW.restype=wintypes.HICON
            user.RegisterClassW.argtypes=[ctypes.POINTER(WNDCLASS)];user.RegisterClassW.restype=wintypes.ATOM
            user.CreateWindowExW.argtypes=[wintypes.DWORD,wintypes.LPCWSTR,wintypes.LPCWSTR,wintypes.DWORD,ctypes.c_int,ctypes.c_int,ctypes.c_int,ctypes.c_int,wintypes.HWND,wintypes.HMENU,wintypes.HINSTANCE,wintypes.LPVOID];user.CreateWindowExW.restype=wintypes.HWND
            user.DefWindowProcW.argtypes=[wintypes.HWND,wintypes.UINT,wintypes.WPARAM,wintypes.LPARAM];user.DefWindowProcW.restype=LRESULT
            user.PostMessageW.argtypes=[wintypes.HWND,wintypes.UINT,wintypes.WPARAM,wintypes.LPARAM];user.PostMessageW.restype=wintypes.BOOL
            user.DestroyWindow.argtypes=[wintypes.HWND];user.DestroyWindow.restype=wintypes.BOOL
            user.GetMessageW.argtypes=[ctypes.POINTER(wintypes.MSG),wintypes.HWND,wintypes.UINT,wintypes.UINT];user.GetMessageW.restype=ctypes.c_int
            user.TranslateMessage.argtypes=[ctypes.POINTER(wintypes.MSG)];user.DispatchMessageW.argtypes=[ctypes.POINTER(wintypes.MSG)]
            shell.Shell_NotifyIconW.argtypes=[wintypes.DWORD,ctypes.POINTER(NOTIFYICONDATA)];shell.Shell_NotifyIconW.restype=wintypes.BOOL
            inst=kernel.GetModuleHandleW(None); name='MeshCoreWifiWatchWindow'
            def window_proc(hwnd,msg,wparam,lparam):
                if msg==WM_APP_NOTIFY:
                    try:
                        title,body,result,done=self.pending.get_nowait()
                        n=NOTIFYICONDATA();n.cbSize=ctypes.sizeof(n);n.hWnd=hwnd;n.uID=1;n.uFlags=NIF_INFO;n.dwInfoFlags=NIIF_INFO;n.szInfoTitle=title[:63];n.szInfo=body[:255]
                        try:
                            if not shell.Shell_NotifyIconW(NIM_MODIFY,ctypes.byref(n)): raise RuntimeError('Windows rejected the notification')
                        except Exception as exc: result.append(str(exc))
                        finally: done.set()
                    except queue.Empty: pass
                    return 0
                if msg==WM_CLOSE: user.DestroyWindow(hwnd);return 0
                if msg==WM_DESTROY:
                    n=NOTIFYICONDATA();n.cbSize=ctypes.sizeof(n);n.hWnd=hwnd;n.uID=1;shell.Shell_NotifyIconW(NIM_DELETE,ctypes.byref(n));user.PostQuitMessage(0);return 0
                return user.DefWindowProcW(hwnd,msg,wparam,lparam)
            self.proc=WNDPROC(window_proc); wc=WNDCLASS();wc.lpfnWndProc=self.proc;wc.hInstance=inst;wc.lpszClassName=name
            if not user.RegisterClassW(ctypes.byref(wc)) and ctypes.get_last_error()!=1410: raise ctypes.WinError(ctypes.get_last_error())
            hwnd=user.CreateWindowExW(0,name,'MeshCore Command alerts',0,0,0,0,0,None,None,inst,None)
            if not hwnd: raise ctypes.WinError(ctypes.get_last_error())
            n=NOTIFYICONDATA();n.cbSize=ctypes.sizeof(n);n.hWnd=hwnd;n.uID=1;n.uFlags=NIF_MESSAGE|NIF_ICON|NIF_TIP;n.uCallbackMessage=0x8002;user.LoadImageW.argtypes=[wintypes.HINSTANCE,wintypes.LPCWSTR,wintypes.UINT,ctypes.c_int,ctypes.c_int,wintypes.UINT];user.LoadImageW.restype=wintypes.HANDLE
            icon_path=Path(getattr(sys, '_MEIPASS', Path(__file__).resolve().parent)) / 'meshcore-command.ico'
            n.hIcon=user.LoadImageW(None,str(icon_path),1,32,32,0x10) or user.LoadIconW(None,ctypes.cast(ctypes.c_void_p(32516),wintypes.LPCWSTR));n.szTip='MeshCore Command alerts'
            if not shell.Shell_NotifyIconW(NIM_ADD,ctypes.byref(n)): raise RuntimeError('Windows could not create the notification icon')
            self.hwnd=hwnd;self.user=user;self.ready.set();message=wintypes.MSG()
            while user.GetMessageW(ctypes.byref(message),None,0,0)>0:user.TranslateMessage(ctypes.byref(message));user.DispatchMessageW(ctypes.byref(message))
        except Exception as exc: self.error=str(exc);self.ready.set()
    def show(self,title,body):
        try: winsound.MessageBeep(winsound.MB_ICONEXCLAMATION)
        except RuntimeError: pass
        result=[]; done=threading.Event()
        self.pending.put((title,body,result,done))
        if not self.user.PostMessageW(self.hwnd,WM_APP_NOTIFY,0,0): raise RuntimeError('Could not queue notification')
        if not done.wait(5): raise RuntimeError('Windows notification timed out')
        if result: raise RuntimeError(result[0])
    def close(self):
        if self.hwnd:
            self.user.PostMessageW(self.hwnd,WM_CLOSE,0,0);self.hwnd=None

_instance=None
_lock=threading.Lock()
def notify(title,body):
    global _instance
    with _lock:
        if _instance is None:_instance=DesktopNotifier()
        _instance.show(title,body)

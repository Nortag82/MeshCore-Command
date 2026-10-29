"""Windows Credential Manager storage for repeater passwords."""
import ctypes
from ctypes import wintypes

class CREDENTIAL(ctypes.Structure):
    _fields_ = [('Flags', wintypes.DWORD), ('Type', wintypes.DWORD), ('TargetName', wintypes.LPWSTR),
                ('Comment', wintypes.LPWSTR), ('LastWritten', wintypes.FILETIME), ('CredentialBlobSize', wintypes.DWORD),
                ('CredentialBlob', ctypes.POINTER(ctypes.c_ubyte)), ('Persist', wintypes.DWORD),
                ('AttributeCount', wintypes.DWORD), ('Attributes', ctypes.c_void_p),
                ('TargetAlias', wintypes.LPWSTR), ('UserName', wintypes.LPWSTR)]

advapi = ctypes.WinDLL('advapi32', use_last_error=True)
advapi.CredReadW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(ctypes.POINTER(CREDENTIAL))]
advapi.CredReadW.restype = wintypes.BOOL
advapi.CredWriteW.argtypes = [ctypes.POINTER(CREDENTIAL), wintypes.DWORD]
advapi.CredWriteW.restype = wintypes.BOOL
advapi.CredDeleteW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD]
advapi.CredDeleteW.restype = wintypes.BOOL
advapi.CredFree.argtypes = [ctypes.c_void_p]

def target(key, scope='repeater'):
    if scope not in ('repeater', 'secure'): raise ValueError('Invalid credential scope')
    return 'MeshCore Command/' + scope + '/' + key

def get(key, scope='repeater'):
    cred = ctypes.POINTER(CREDENTIAL)()
    if not advapi.CredReadW(target(key, scope), 1, 0, ctypes.byref(cred)):
        if ctypes.get_last_error() == 1168: return None
        raise ctypes.WinError(ctypes.get_last_error())
    try: return ctypes.string_at(cred.contents.CredentialBlob, cred.contents.CredentialBlobSize).decode('utf-8')
    finally: advapi.CredFree(cred)

def put(key, password, scope='repeater'):
    data = password.encode('utf-8')
    if not 1 <= len(data) <= 512: raise ValueError('Password must be 1–512 UTF-8 bytes')
    blob = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    cred = CREDENTIAL(Type=1, TargetName=target(key, scope), CredentialBlobSize=len(data), CredentialBlob=blob, Persist=2, UserName='MeshCore repeater')
    if not advapi.CredWriteW(ctypes.byref(cred), 0): raise ctypes.WinError(ctypes.get_last_error())

def delete(key, scope='repeater'):
    if not advapi.CredDeleteW(target(key, scope), 1, 0) and ctypes.get_last_error() != 1168: raise ctypes.WinError(ctypes.get_last_error())

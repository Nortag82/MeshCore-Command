# -*- mode: python ; coding: utf-8 -*-
from PyInstaller.utils.hooks import collect_all

datas = [('index.html', '.'), ('radio.html', '.'), ('meshcore-command.ico', '.'), ('firmware/manifest.json', 'firmware'), *[(f'firmware/{name}', f'firmware/{name}') for name in ('v3-headless', 'v3-oled', 'v4-auto', 'v4-tft', 'tracker-headless', 'tracker-screen', 'r8-headless', 'r8-oled', 'tbeam-oled')], ('flash-helper.exe', '.')]
binaries = []
hiddenimports = ['serial.tools.list_ports_windows']
tmp_ret = collect_all('webview')
datas += tmp_ret[0]; binaries += tmp_ret[1]; hiddenimports += tmp_ret[2]


a = Analysis(
    ['app.py'],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='MeshCore Command',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    icon="meshcore-command.ico",
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='MeshCore Command',
)

"""Stage the exact locally built companion images and record their SHA-256 hashes."""
import hashlib, json, shutil
from pathlib import Path

root=Path(__file__).resolve().parent
out=root/'firmware'
build=root.parent/'NeonPocket'/'.pio'/'build'
boot=out/'boot_app0.bin'
esp={
    'v3-headless':out/'v3-headless',
    'v3-oled':out/'v3-oled',
    'v4-auto':out,
    'v4-tft':out/'v4-tft',
    'tracker-headless':out/'tracker-headless',
    'tracker-screen':out/'tracker-screen',
    'r8-headless':out/'r8-headless',
    'r8-oled':out/'r8-oled',
    'tbeam-oled':build.parent/'build-tbeam'/'meshcommand_tbeam_oled',
}
manifest={}
for name,src in esp.items():
    files={part:src/part for part in ('bootloader.bin','partitions.bin','firmware.bin')}
    files['boot_app0.bin']=boot
    if files['bootloader.bin'].read_bytes()[:1]!=b'\xe9' or files['firmware.bin'].read_bytes()[:1]!=b'\xe9' or files['partitions.bin'].read_bytes()[:2]!=b'\xaa\x50':
        raise ValueError(f'{name}: invalid ESP32 image')
    data=files['firmware.bin'].read_bytes()
    if b'MC_PROVISION_READY_V1' not in data or b'MESHCORE_V4_COMPANION_V1' not in data:
        raise ValueError(f'{name}: missing Wi-Fi provisioning or discovery')
    target=out/name;target.mkdir(exist_ok=True)
    manifest[name]={}
    for part,path in files.items():
        data=path.read_bytes();(target/part).write_bytes(data);manifest[name][part]=hashlib.sha256(data).hexdigest()
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
print('Staged and validated',', '.join(manifest))

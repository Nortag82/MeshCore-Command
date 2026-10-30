# MeshCore Command branding build
from pathlib import Path
import base64
from datetime import datetime, timezone
from html import escape
root=Path(__file__).resolve().parent
version='1.0.1-dev'
build=datetime.now(timezone.utc).strftime('%Y%m%d%H%M%S')
source=root.parent / 'NeonPocket/examples/companion_radio/webui/.build/index.html'
icon=base64.b64encode((root/'meshcore-command-small.png').read_bytes()).decode('ascii')
s=source.read_text(encoding='utf-8')
placeholder='<span class="app-build" id="desktop-build" hidden></span>'
if placeholder not in s:
    raise RuntimeError('Desktop version placeholder is missing')
s=s.replace(placeholder, f'<span class="app-build" id="desktop-build">Version {escape(version)} · Build {build}</span>')
s=s.replace('</head>','<style>.ultimate-hero,.ultimate-metrics,.ultimate-grid,.ultimate-settings-grid,.device-details .device-card,.device-details .settings-stack > :not(:first-child),#network-setup-button{display:none !important}</style></head>')
s=s.replace('NeonPocketMC','MeshCore Command').replace('MeshCore Desktop','MeshCore Command').replace('MeshCore Command · Ultimate','MeshCore Command').replace('MeshCore word alert','MeshCore Command alert').replace('MeshCore WiFi.', 'MeshCore Command.')
s=s.replace('<svg class="brand-mark" aria-hidden="true"><use href="#brand-pocket"/></svg>',f'<img class="brand-mark" alt="" src="data:image/png;base64,{icon}">')
(root/'radio.html').write_text(s,encoding='utf-8')

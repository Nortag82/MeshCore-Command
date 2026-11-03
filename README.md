# MeshCore Command

A Windows desktop companion for controlling a Wi-Fi connected MeshCore radio, with messaging, contacts, repeater management, monitoring, terrain planning and a shared situation map with offline basemaps.

This repository contains the desktop source in `MeshCore Desktop/` and the modified companion firmware and interface sources in `NeonPocket/`. The firmware tree is based on [NeonPocketMC](https://github.com/n30nex/NeonPocketMC-Heltec-V4) and retains its license files and upstream notices. See [desktop documentation](MeshCore%20Desktop/README.md) for features and device support.


The current installer build is **2.0.1**. Secure groups are local recipient lists that send individual encrypted messages; no group key is needed. The separate [secure pairing research prototype](experimental/secure-pairing/) is not integrated. Command discovery still uses Public.

## Download for Windows

Download the [version 2.0.1 Windows installer](https://github.com/Nortag82/MeshCore-Command/releases/tag/v2.0.1), then follow the [user manual](docs/USER-MANUAL.md). The release page lists the installer checksum. Windows Edge WebView2 is required. The installer and bundled firmware are unsigned and experimental; use the exact board option and back up important radio data before flashing.

## A look inside

[See screenshots of every menu](docs/SCREENSHOTS.md), including connection and firmware setup.

> Screenshots use simulated contacts, messages, telemetry and map markers. Map data © OpenStreetMap contributors.

### Secure Situation Map

Build a shared picture using markers, civilian and emergency icons, and drawn areas. The floating toolbar provides marker, area, sharing and map controls; right-click to create or edit a marker. To move it, select it, choose **Move selected marker**, click the new position and save. Markers cannot be dragged. Markers can be saved locally, even before you add a secure contact.

![Secure Situation Map showing the floating toolbar, selected recipients and example objects](docs/images/situation-map.png)

Share marker and area updates with selected verified Secure Coms contacts or local groups. Full-picture sharing targets selected recipients; forwarding received markers is separately opt-in. The app shows which marker updates the receiving app has confirmed saving. Both users need compatible MeshCore Command installations and a connected radio. Sharing uses the experimental Secure Coms protocol; it has not been independently audited and does not provide forward secrecy. It is not ATAK compatible.

Version 2.0 adds a persistent sharing queue with automatic retries, pause and resume, and per-recipient incremental picture sharing. You can retry one unconfirmed object, force a full picture, or remove a pending deletion from your local lists. TX/RX indicators and a transmission activity view help you follow radio activity.

### Secure images

Convert an image on your PC into a small JPEG thumbnail, preview its size and packet count, and send it to a verified Secure Coms contact. Both installations need the image-enabled update. Each encrypted packet is confirmed separately, so transfers can take several minutes and use substantial mesh airtime. Received thumbnails are stored locally on the PC. See the [manual](docs/USER-MANUAL.md#secure-images) for details and limitations.

### Find other Command users

In **Manage Secure Coms**, use **Discover Command installs** to send a discovery request on the Public channel using the radio's default flood scope. Other compatible, running Command installations can respond. Responses announce their public key and app presence, and automatic responses can be turned off. Discovery replies are unverified and can be spoofed: exchange a secret privately and compare fingerprints before trusting a contact. Other MeshCore apps may display discovery traffic as ordinary channel messages.

### Offline maps

Open **Settings** on the Situation map, select two corners of an area, choose a detail level and download it for later use without internet. You can also import a Protomaps PMTiles map or a raster MBTiles map. Map downloads require internet and use local disk space; terrain coverage tools need separate online elevation data.

![Secure Situation Map settings and offline map options](docs/images/situation-map-settings.png)

### Overview
See radio status, recent activity, monitored nodes and quick actions in one place.

![Overview showing an example radio, message activity and monitored node](docs/images/overview.png)

### Messages
Switch between direct messages, channels and rooms; unread counts show which tabs and conversations have new messages. Choose a flood scope for a channel.

![Example channel conversation and flood scope selector](docs/images/messages.png)

### Elevation finder

In **Terrain tools**, enter a height above sea level to highlight terrain above that threshold across the visible map. The result uses online Mapzen terrain tiles and is an estimate; its displayed resolution and the underlying source data limit the detail. Move or zoom the map and recalculate for the new view.

![Terrain tools showing the elevation finder](docs/images/terrain-tools.png)

### Monitoring
Schedule checks and review the latest available measurements for contacts and repeaters.

![Monitoring page with an example repeater and battery estimate](docs/images/monitoring.png)

## User manual

Read the [MeshCore Command user manual](docs/USER-MANUAL.md) for setup, messaging, repeater management, mapping and troubleshooting.

## Building

The desktop app uses Python, pywebview, pyserial, esptool and PyInstaller. The radio interface is built with Node.js from `NeonPocket/examples/companion_radio/webui`, then `MeshCore Desktop/build_radio.py` creates `radio.html`. The flasher and packaged installer additionally require board-specific compiled firmware, a firmware manifest and a separately built flash-helper executable. Those generated binaries and local device data are not included. Firmware targets and packaging must be verified before distributing builds.

Do not commit Wi-Fi credentials, radio identity backups, message histories or private keys.

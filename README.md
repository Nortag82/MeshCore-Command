# MeshCore Command

A Windows desktop companion for controlling a Wi-Fi connected MeshCore radio, with messaging, contacts, repeater management, monitoring, terrain planning and a shared situation map with offline basemaps.

This repository contains the desktop source in `MeshCore Desktop/` and the modified companion firmware and interface sources in `NeonPocket/`. The firmware tree is based on [NeonPocketMC](https://github.com/n30nex/NeonPocketMC-Heltec-V4) and retains its license files and upstream notices. See [desktop documentation](MeshCore%20Desktop/README.md) for features and device support.


## Download for Windows

Download the installer and checksum from the [Windows preview release](https://github.com/Nortag82/MeshCore-Command/releases/tag/v1.0.0-preview.2), then follow the [user manual](docs/USER-MANUAL.md). Windows Edge WebView2 is required. The installer and bundled firmware are unsigned and experimental; use the exact board option and back up important radio data before flashing.

## A look inside

> Screenshots use simulated contacts, messages and telemetry.

### Overview
See radio status, recent activity, monitored nodes and quick actions in one place.

![Overview showing an example radio, message activity and monitored node](docs/images/overview.png)

### Messages
Switch between direct messages, channels and rooms; choose a flood scope for a channel.

![Example channel conversation and flood scope selector](docs/images/messages.png)

### Monitoring
Schedule checks and review the latest available measurements for contacts and repeaters.

![Monitoring page with an example repeater and battery estimate](docs/images/monitoring.png)

## User manual

Read the [MeshCore Command user manual](docs/USER-MANUAL.md) for setup, messaging, repeater management, mapping and troubleshooting.

## Building

The desktop app uses Python, pywebview, pyserial, esptool and PyInstaller. The radio interface is built with Node.js from `NeonPocket/examples/companion_radio/webui`, then `MeshCore Desktop/build_radio.py` creates `radio.html`. The flasher and packaged installer additionally require board-specific compiled firmware, a firmware manifest and a separately built flash-helper executable. Those generated binaries and local device data are not included. Firmware targets and packaging must be verified before distributing builds.

Do not commit Wi-Fi credentials, radio identity backups, message histories or private keys.

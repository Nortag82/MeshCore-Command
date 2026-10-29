# MeshCore Command

A Windows desktop companion for controlling a Wi-Fi connected MeshCore radio, with messaging, contacts, repeater management, monitoring and terrain planning.

This repository contains the desktop source in `MeshCore Desktop/` and the modified companion firmware and interface sources in `NeonPocket/`. The firmware tree is based on [NeonPocketMC](https://github.com/n30nex/NeonPocketMC-Heltec-V4) and retains its license files and upstream notices. See [desktop documentation](MeshCore%20Desktop/README.md) for features and device support.

## Building

The desktop app uses Python, pywebview, pyserial, esptool and PyInstaller. The radio interface is built with Node.js from `NeonPocket/examples/companion_radio/webui`, then `MeshCore Desktop/build_radio.py` creates `radio.html`. The flasher and packaged installer additionally require board-specific compiled firmware, a firmware manifest and a separately built flash-helper executable. Those generated binaries and local device data are not included. Firmware targets and packaging must be verified before distributing builds.

Do not commit Wi-Fi credentials, radio identity backups, message histories or private keys.

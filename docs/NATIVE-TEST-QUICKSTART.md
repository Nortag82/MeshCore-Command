# Native Windows quick start

The [2.0.57 Windows release](https://github.com/Nortag82/MeshCore-Command/releases/tag/v2.0.57) adds [Mercury broadcast chat](HFLINK.md#send-and-receive-mercury-broadcasts) to HFLink. It supports new installations and updates from previous native builds. The legacy Python updater can offer the [2.0.43 migration release](https://github.com/Nortag82/MeshCore-Command/releases/tag/v2.0.43). The previous legacy release and its [user manual](USER-MANUAL.md) remain available.

## Update or install

On a new PC, open **MeshCore Command Setup**, choose **Install**, then **Open MeshCore Command**. Setup creates a Start menu shortcut and the app prepares a new profile on first launch. If Setup shows **Install WebView2**, select it first. Setup includes Microsoft’s bootstrapper, which downloads the runtime over the internet. For offline installation, use Microsoft’s standalone installer on the link in Setup, then choose **Check again**. [Microsoft WebView2 downloads](https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download)

Back up important settings first. On the old app, choose **Check for updates** and accept the update; on the native app, use the update control in **Settings**. Finish sending messages and close other native Command windows first. A downloaded installer can also be run separately. Windows WebView2 and internet access are required.

The legacy upgrade starts the native app and checks its copied profile before switching recognised shortcuts. It moves the old program directory to a uniquely named `MeshCore Command previous …` folder under `%LOCALAPPDATA%` as a recovery copy. The old application data is retained separately. If an independent native test profile is already present, the installer asks whether to use it. **Changes in the old and native profiles are not merged.** Cancelling leaves the old shortcuts and data in place. See [upgrade details](../MeshCore%20Windows/LEGACY-UPGRADE.md).

## Connect and communicate

Use **+ Board** to add a compatible Wi-Fi, Bluetooth or USB companion. For a saved Wi-Fi board, choose it and press **Connect** beneath the list; manual IP entry remains available. Removing a saved Wi-Fi board asks for confirmation and does not reset the radio. Pick the correct radio and firmware; flashing and some hardware combinations are still experimental. Secure Coms and shared Situation Map data add AES-256-GCM encryption inside MeshCore direct messages. Both people need compatible Command installations, a shared secret exchanged privately, and matching fingerprints verified through a trusted channel. Secure messages are limited to 74 UTF-8 bytes; the composer shows how many bytes to remove if a draft is too long. See [security and tradeoffs](SECURE-COMS-SECURITY.md).

To find a missing chat contact, ask the person to send a flood advert from the radio you want to reach. You can also right-click an incoming channel message and choose **Find or add sender**. Channel sender names are unverified: to import a missing sender, obtain their `meshcore://` contact card separately and compare its public key with them before trusting it.

## HFLink

Open **HFLink → Settings** to configure your station, audio and PTT. HFLink checks Windows Firewall on the first visit with bundled Mercury selected and offers to fix it if needed. After you select **Open receive window**, **Back to radio** keeps the session open so incoming HFLink messages can alert you while you use MeshCore. [HFLink setup](HFLINK.md) · [HFLink screenshots](SCREENSHOTS.md#hflink)

## Full app backup

Choose **Settings** beside **Disconnect** in the main toolbar and select **Export full backup**. The password-encrypted file contains saved board profiles, local MeshCore messages and settings, Secure Coms secrets, maps, HFLink callsign and history, and the Mercury configuration saved in HFLink. You can export with no board connected.

On the destination PC, open the same Settings dialog, disconnect any boards and choose **Restore backup**. MeshCore Command saves an encrypted recovery copy before replacing matching data. Verify the audio devices and PTT port on the destination PC and complete its firewall setup if prompted. To include or restore the identity and supported settings stored on the physical MeshCore radio, use **Encrypted backup** in the connected radio’s Settings page.

## Important limitations

The app, installer and bundled firmware are unsigned. Further testing is needed for hardware combinations, flashing, full feature parity and migration. Secure Coms has not been independently audited and does not provide forward secrecy. A radio acknowledgement does not prove an app decrypted or read a message. A shared waypoint's line is straight-line guidance, not a route that accounts for access or terrain.

In **Messages**, select a channel or direct conversation and use **Mute channel** or **Mute contact** to stop its message popups. Muted messages remain in your history. Muting a contact also suppresses Secure Coms message popups from that contact; use the same button to unmute.

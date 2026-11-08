# MeshCore Command for Android — early test build

Version 0.2.6 adds opt-in Secure Situation Map GPS sharing. Version 0.2.3 rebuilds the Android interface from the current shared desktop code, including repeater management and monitoring changes. These changes have not yet been verified with a physical Android phone and radio. Version 0.2.2 adds contact and group recipient checkboxes to the Secure Situation Map marker editor. Version 0.2.1 adds a dedicated conversation list and compact full-height chat. Version 0.2.0 adds a mobile More menu with overview, Secure Coms management, contact map, monitoring, repeater management, radio and adverts, word alerts and radio settings, along with a compact phone layout. Room and repeater passwords use a separate Android Keystore-encrypted store. This experimental Android companion uses Bluetooth LE with a **MeshCore BLE companion radio**. It includes normal messages, contacts and contact tools, Secure Coms (including locally defined recipient groups), the Secure Situation Map and the available radio and monitoring tools. It shares the desktop app's message and map protocols. Android 12 or newer is the intended test target.

## Before testing

Version 0.2.5 makes Messages and Contacts more compact and adds a **Contacts on/off** layer to the Secure Situation Map. This displays the last advertised locations supplied by the connected radio; a contact without a valid location is omitted, and the displayed location can be old. The layer is local and is not sent with secure map objects. Version 0.2.4 shows the map full screen in landscape, with floating tools and a Back button. Use **Secure Chat** in the navigation bar, or **More → Secure Chat**; configure shared secrets under **More → Manage Secure Coms**.

- Install the APK on an Android phone and allow the Bluetooth and notification permissions when requested. Android may require you to allow installation from the chosen file app.
- The Heltec V3 needs MeshCore **BLE companion** firmware, not the Wi-Fi companion or repeater firmware. Installing firmware can erase settings: back up the board's identity and configuration first. This project does not flash or change the board.
- Power the board, start the app, scan, choose the radio and complete the Android pairing prompt. Use the board's displayed pairing PIN if requested.
- Contacts come from the connected radio. To use Secure Coms with a desktop installation, exchange the shared secret privately and verify its fingerprint on both devices. Groups are local recipient lists, not additional shared keys.
- Map tiles need internet access in this build; offline maps have not been implemented. Keep the app open and connected for receiving messages and notifications; background operation is not yet supported.
- Android keeps secure keys encrypted with Android Keystore and disables system backup. Uninstalling or clearing app data removes the phone's local keys, messages and map data. Retain your shared secrets separately in a secure place.

This build has not yet been tested with a physical phone and board. It is not independently security audited. Check delivery acknowledgements rather than assuming a message or map object arrived.

## Build

Install JDK 17, Android SDK platform 34 and build tools 34.0.0, Gradle 8.2 and Node.js. Run `npm ci` in `NeonPocket/examples/companion_radio/webui`. From `Android/`, run `node scripts/prepare-web.cjs`, then `gradle assembleDebug`. The result is `app/build/outputs/apk/debug/app-debug.apk`. For a shared dependency directory, set `MESHCORE_NODE_MODULES` to its absolute path before generating the interface. Run `node test-bridge.cjs` for the native bridge adapter checks.

The phone-sized visual test uses Playwright and a simulated radio bridge; it does not test actual Bluetooth or radio delivery.

## Features still missing on Android

Terrain planning and elevation tools require the desktop terrain backend. Offline map import/download, encrypted Secure Coms backup file import/export, firmware flashing, full background reception, and some features of the official MeshCore app are not implemented in this test build. Feature parity and real radio compatibility require further development and physical-device testing.

Secure GPS sharing: on **Secure Situation Map → Share**, select verified contacts or a group, open **Share my GPS position**, choose an interval from 1–360 minutes and start sharing. Grant precise location permission when Android asks. The app must remain open and Bluetooth connected; Android can delay updates in the background. Each recipient gets an encrypted map update individually. Stop sharing using the status button; recipients retain positions already delivered. Normal radio adverts remain separate. This feature has only been tested with simulated GPS and radio connections.

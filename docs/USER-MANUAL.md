# MeshCore Command user manual

MeshCore Command is a Windows desktop companion for a compatible Wi-Fi MeshCore radio. The PC hosts the interface and connects to the radio on your local network. Screenshots in the [project README](../README.md) use simulated data. This manual describes installer build 2.0.33. Secure groups are local recipient lists; the separate secure group pairing prototype is not integrated.

An experimental Android app is described separately in the [Android instructions](../Android/README.md). Windows 2.0.5 includes Wi-Fi reconnection and startup-crash mitigations.

## Before you start

- You need Windows, the Edge WebView2 runtime, a compatible Wi-Fi radio and an appropriate LoRa antenna. Connect the antenna before powering or transmitting with the radio.
- The PC and radio need network access to each other. The companion connection uses TCP port 5000; discovery uses UDP port 5010.
- Use a trusted local network: the companion protocol itself is unauthenticated and unencrypted. The PC bridge listens on loopback.
- Download the installer from the [version 2.0.33 release](https://github.com/Nortag82/MeshCore-Command/releases/tag/v2.0.33), run it and follow the prompts. The installer and bundled firmware are unsigned and experimental. Source-build information is in the [build notes](../README.md#building).

### Updating the Windows app

On startup, Command checks for a newer release in the background and asks before installing. If the radio is opening, a radio window is active, or a scan or flash is running, the prompt waits until the connection window is available. You can also choose **Check for updates** on the connection screen or under **Settings**. The app checks published GitHub releases, including test releases, and identifies the Windows installer for a newer numbered version. It asks before downloading and restarting. Finish any flash operation or message first. The download is checked against GitHub's SHA-256 asset digest; this detects mismatched downloads but is not an independent code signature. If verification fails, no installer runs. Updating does not flash the radio. Keep a Secure Coms backup and protect its password. Internet access is required.

## 1. Set up or connect a radio

1. Start MeshCore Command. If your radio is already configured, select or enter its IP address. You can save several radios with names for future use.
2. To find a radio, enter the appropriate local network subnet and choose **Scan network**. Progress shows probes attempted followed by a wait for replies; a probe is not proof that an address is reachable. If you use a VPN or multiple adapters, check that the selected subnet belongs to the Wi-Fi network. You can also enter the IP manually.
3. Select **Connect**. An animated loading screen appears for at least six seconds while the radio view opens. Only one companion controller should use the radio connection at a time.

The setup window hides while the radio window is open. The app protects an active radio session from another window on supported Windows webviews. If **Another tab active** persists, close any other Command windows and reconnect. The app must remain connected to receive messages and show alerts; the minimise-and-restore fix has not yet been verified with a live radio.

For a new board, open **Set up a new board** or **Flash firmware**, choose the exact board and screen variant, select its COM port, and enter the 2.4 GHz Wi-Fi name and password. The app uses DHCP and shows the address once provisioning succeeds. The progress panel shows the current flashing stage, any per-image percentage reported by the tool, Wi-Fi setup and DHCP; expand the log for diagnostics. A suggested serial port is not proof of the board model. Flashing replaces firmware; preserve important data first. Existing settings or data from another firmware layout may not be accessible afterwards.

The supported choices include Heltec V3, V4 and V4 R8 variants, Heltec Wireless Tracker V2 and LilyGO T-Beam S3 Supreme SX1262. Choose the exact model and display option; other T-Beam models are not interchangeable. After you choose a board, the flasher shows board-specific USB and boot instructions. The app attempts an automatic reset; manually entering boot mode does not disable it. Refresh and reselect the correct COM port if it changes. Keep USB connected during flashing and Wi-Fi setup. The firmware's brief USB provisioning window occurs at boot.

### Multiple boards (2.0.22 and later)

Connect your saved Wi-Fi radio first. At the top of its window select **+ Board**, choose **Bluetooth**, and scan for a standard MeshCore BLE companion. Select the board and connect; if Windows has not already paired it securely, enter the six-digit PIN shown on the board. Select a board tab to use that radio, and use **Rename** to give the tab a memorable name. Other open boards continue running in the background. The same radio cannot be connected twice. If a Bluetooth connection drops, disconnect its tab and add it again. Board names are saved locally.

This is an early implementation: a Wi-Fi radio and one Bluetooth radio have been tested together, but extended reliability and all operations have not been verified. Some boards negotiate a Bluetooth packet size too small for the largest commands; the app reports an error rather than truncating those commands.

## 2. Check your mesh settings

Open **Settings** to change the radio name and other settings. **ScotMesh setup check** reads the radio's default scope and checks its settings against EU/UK Narrow. The **Set radio default to sco** button writes the scope and reads it back. For other networks, confirm the appropriate radio preset and scopes with the local community.

A **channel** is a group chat; a **flood scope** is a routing choice. MeshCore Command remembers channel scope selections locally for each radio and channel. A channel choice does not change the radio's default scope, which also affects default-scoped flood adverts. Direct routes do not use a flood scope. Incoming messages may say the region was not reported when the radio does not provide it.

### Off-grid companion repeat

In **Settings → Off-grid companion repeat**, choose **Check current frequency** to ask the connected radio which frequencies it permits for companion repeat. On an allowed frequency, **Enable off-grid repeat** requests forwarding while the board continues to operate as a companion. **Disable repeat** requests that forwarding stop. The control preserves the radio's current frequency, bandwidth, spreading factor and coding rate; it does not change the network for you. The radio may acknowledge the request without reporting its actual repeat on/off state, so an accepted request is not independent verification that packets are being forwarded.

This is MeshCore's standard off-grid companion mode, intended for small temporary networks without established repeater infrastructure; it is not a remotely managed repeater. The firmware supplied with this app permits 869.495 MHz, not UK Narrow at 869.618 MHz. To use an off-grid network, all participants must configure matching radio settings; changing frequency disconnects you from your previous mesh until you restore those settings. Saving **Radio** settings in this app turns companion repeat off, so re-enable it after changing the radio settings if needed. Use the radio's reported frequency list rather than assuming every firmware permits the same frequencies.

## 3. Messages and channels

1. Open **Messages**, then select **Direct messages**, **Channels** or **Rooms**. Unread badges identify the tabs and conversations with new messages; muted channels do not contribute to the badges.
2. Search for a conversation, or start a direct conversation from **Contacts**.
3. Type a message and press **Enter** to send; use **Shift+Enter** for a new line. Right-click a message to copy text, reply with a quote or send a thumbs-up as an ordinary message.

Ordinary direct, channel and room conversation history is stored locally in the PC's MeshCore Command WebView profile under `%LOCALAPPDATA%\MeshCore WiFi Data\WebView`, separately for each radio identity. Command keeps the latest 5,000 messages per radio; older entries are discarded from this local history. The Messages page shows the local storage status and warns if history cannot be read or saved. Do not delete the WebView profile if you want to retain this history. Ordinary messages in this profile are not separately encrypted by Command.

The companion radio temporarily queues messages in memory until the app retrieves them, and room servers maintain their own retained posts. Older PC Wi-Fi companion firmware may also keep a persistent on-radio message journal. Updating the PC app alone does not change the radio firmware or erase that journal. The 2.0.8 flasher includes an updated **Heltec V4 automatic display/headless** PC Wi-Fi build that disables new journal records while preserving existing journal files. It must be flashed to the exact matching board to take effect; back up the radio first. Other bundled board variants are unchanged. The new firmware has compiled successfully but has not yet been tested on physical hardware. Secure Coms uses separately protected storage and is included in the unified encrypted backup.
4. Use **Mute channel** to suppress notifications and unread badges for that channel. Messages remain available; word matches are still recorded silently.

New direct, room and unmuted channel messages request a Windows tray alert and an app pop-up. Secure messages use the separate **Alert me to secure messages** setting in **Manage Secure Coms**. Message text is not included in these general alerts. The app and radio must be connected, and Windows can suppress tray alerts or sound. In Secure Coms, your messages are blue and incoming messages are red.

To join a channel, select **Channels → Add channel**. A hashtag channel derives its key from its exact name and is readable by anyone who knows that name. For a private channel, enter or generate the shared key and share it securely with the other members. You can paste a `meshcore://channel/add` invite into the invite field to fill the name and key; verify them before adding. Keep the invite private because it contains the shared key. Choose the flood region separately. For example, ScotMesh recommends `sco` as the default Scotland scope and uses `ioi` for the Island of Ireland path. Check current local guidance before configuring repeaters.

Select a conversation and use **Edit** or **Delete** in its header. Editing a channel changes its name or key on the connected radio; deleting it clears the radio slot and its history on this PC. Changing a key affects which messages the radio can receive. Hashtag keys are derived from the exact channel name, so use the correct key when renaming one. Room and direct conversation names are local display names only. Deleting a room removes its local registration, saved password and history; deleting a direct conversation removes its local history, not the contact. Other devices and room servers retain their own copies.

Right-click a message and choose **Delete from this PC** to remove its local copy. For a sent message, **Edit and send a correction** prepares a new message; review it and send it yourself. Previously delivered messages cannot be edited or deleted on other devices.

A delivery tick or acknowledgement applies only when the protocol reports one; a queued message is not proof that it was received or read. Channel broadcasts do not offer individual delivery confirmation for every recipient.

The **TX** and **RX** indicators at the top summarize activity observed by the app. Packet counters update periodically; the indicators are not continuous measurements of radio airtime. A radio acknowledgement does not establish that a person read a message.

### Room servers

Under **Rooms**, add a known room server and enter its password. You may save the password in Windows Credential Manager. While connected, the app checks added rooms periodically; **Check room now** requests a check. **Load retained history** asks a compatible room server to resend posts it still holds using a known direct route. It cannot recover discarded posts or guarantee every old message is available, and replies can arrive gradually.

## 4. Contacts and contact map

When a compatible radio reports that its contact table is full, Command can replace the oldest inactive eligible contact with the newly received advert while the app is connected. It compares the last advert and any newer received direct message. Favourites (on the PC or radio), Secure Coms contacts, monitored nodes, saved rooms, and the selected conversation are protected. Contacts without a valid advert time are skipped. You can turn this off in **Settings → MeshCore settings**. The radio may also have an independent overwrite-oldest setting; its own rules apply if enabled. If a replacement fails, Command attempts to restore the removed contact and reports the outcome. This feature is tested with simulated radio responses.

Open **Contacts** to search nodes by name, role or key, and star favourites. Choose a contact for messaging, status, telemetry, path reset, ping or route tracing. **Discover contacts** searches for nearby compatible nodes; it does not guarantee that all network users will respond.

A chat ping sends the word `ping` as a message. A repeater ping uses a direct trace. Traces in this version support known single-byte paths only; short hashes can collide, and a trace is not proof of final message delivery. Availability of status and telemetry depends on node firmware, permissions and reachability.

**Contact map** shows nodes with advertised positions. To set your radio location, choose **Set my location**, click the map, and select **Save location**. Command reads the radio back to confirm. A local or flood advert can announce the new position only if the radio’s location-sharing policy allows it. Under **Settings → MeshCore settings → Advertised location**, expand **Choose location on map** and click a point to fill the coordinates; press **Save coordinates** to write and verify the position. You can also type coordinates. Its received-packet list can display reported flood-path segments when key prefixes can be matched unambiguously to located contacts. Gaps and hash collisions are possible; lines are not proof of delivery. Internet access is needed for map tiles.

## 5. Repeaters

Select a repeater in the searchable list on the left. Use Overview to log in and request status or telemetry; the Settings, Regions, Neighbours and Console tabs group the remaining tools. The Console includes read-only shortcuts documented by MeshCore; availability depends on repeater firmware. Log in before changing settings, inspect replies and read changes back. The official [MeshCore CLI reference](https://github.com/meshcore-dev/MeshCore/blob/main/docs/cli_commands.md) documents supported remote commands.

Search for a repeater on **Repeaters**, select it and log in with its admin password. You can optionally save the password in Windows Credential Manager and star the repeater for easier access.

- **Status** and **Telemetry** show supported readings when returned by the node.
- **Refresh neighbours** requests the current list. **Discover neighbours** starts discovery, counts down 60 seconds and then requests the list again.
- The neighbour map needs a unique contact match and valid advertised locations at both ends. A listed SNR is what the selected repeater reported, not a guaranteed bidirectional measurement.
- **Read settings** and the remote command controls depend on the repeater firmware. Read and verify values before changing them. To set its location, log in, open **Choose repeater location on map**, click a point, and choose **Set location**. Confirm the coordinates, then use **Read settings** to check both values; queued commands are not confirmation of a successful save. If the link fails, latitude may have changed while longitude did not. Map tiles require internet access.
- The region controls can read the tree and send individual region changes. Review the result and explicitly save the region configuration when appropriate. The app does not automatically install a ScotMesh region tree.
- The repeater advert path-size setting affects that repeater's own adverts. The radio's message path setting is separate; compatibility varies with firmware.

## 6. Monitoring and word alerts

On **Monitoring**, add a known chat contact or repeater, then expand **Schedule checks** to choose an interval and checks. Compact cards show the latest results. Expand **Global telemetry schedule** to enable periodic telemetry for all monitored nodes, including nodes added later; it is independent of per-node ping and trace schedules. Requests are processed one at a time while Command is open and connected. Expand **Repeater time** for **Read time**, **Sync to PC** and any diagnostic details; these require an active admin login or saved password. A repeater may reject a backwards time correction; the displayed time is the last reading, not a live clock. Checks run only while the app is open and connected. A voltage-based battery percentage is an estimate and may not be appropriate for every power source.

When adding a repeater, Command offers an optional admin password. A supplied password is saved in Windows Credential Manager and shared with the Repeaters page; existing monitored repeaters have a Repeater password section. For monitored repeater telemetry, Command attempts login when a password is saved, then requests telemetry. You can add a repeater without a password, but telemetry still depends on its permissions. A failed login or telemetry request may time out; the message identifies which step failed.

Monitored repeater cards show up to seven days of device-voltage telemetry stored on this PC. Run Telemetry or enable it under Schedule checks; readings collect only while Command is open and connected and the repeater responds. Gaps indicate missing readings, and existing history cannot be reconstructed. Device voltage is not necessarily battery voltage. Removing a monitored node also removes its local graph history.

On **Word alerts**, enter one word or phrase per line and save. The app checks newly received direct messages and joined channel messages; it cannot monitor traffic it cannot receive or decrypt. Use **Send test notification** to check Windows notifications and sound. Windows notification or focus settings can suppress alerts. Matches are stored locally, and enabling message previews can put message text in desktop notifications.

## 7. Secure Coms

Both participants need a compatible MeshCore Command installation and a MeshCore chat contact. In **Manage Secure Coms**, one person generates a secret, shares it with the other person through a trusted separate channel, and both save it. Compare the displayed fingerprints using a trusted channel before marking the contact verified.

If a contact entry is missing but its secret remains in Windows Credential Manager, connect the same radio, select the contact in **Manage Secure Coms**, and choose **Recover saved secret**. The contact is restored as unverified. This does not recover missing messages or replay-protection history; exchange a new secret privately to restore replay protection, then compare fingerprints before verifying.

Secure Coms encrypts on the PC using AES-256-GCM and sends the encrypted content over MeshCore direct messages. It is experimental, has not had an independent security audit and does not provide forward secrecy or protect a compromised PC. Messages are short because of packet limits. New outgoing messages can show a radio delivery acknowledgement; this does not prove that the receiving app decrypted or a person read the message. Earlier messages cannot be retrospectively confirmed.

Version 2.0.3 removes Secure Images and image transfers. Previously received image data remains in the app's local storage, but the app no longer displays it. This does not affect Secure Situation Map picture sharing.

### Discover Command installations

In **Manage Secure Coms**, select **Discover Command installs** to send a request over the Public channel using the radio's default flood scope. Compatible installations that are running and can hear it can reply. Discovery requests and responses are visible to Public-channel users and disclose the sender's public key and app presence. Other MeshCore apps may show them as ordinary channel messages. Automatic responses are enabled by default; disable **Respond to discovery requests** if you do not want to reply. Requests and replies are rate limited.

Replies can be spoofed and are not verified identities. Select **Add & set up** to add an unknown radio contact, or **Set up** for a known one. Exchange the shared secret privately and compare fingerprints before marking the contact verified. Discovery requires compatible versions, the Public channel and a reachable flood scope; it cannot guarantee a response.

New or updated received map markers show a message and a blinking **New map marker** box at the top of Command, plus a count beside Secure Situation Map. The count is saved on the PC per radio and clears when you open the map. Duplicates, acknowledgements and deletions do not create marker alerts.

### Local groups and backup

Create a group in **Manage Secure Coms** by choosing a name and verified contacts. The group is a local recipient list; members do not need the group or another key. Sending to the group sends an individually encrypted direct message to each member using the existing shared secret. Replies appear in individual conversations. The group view shows per-recipient radio acknowledgements, which are not read receipts. The same groups can select recipients on the Secure Situation Map.

Under **Settings → Encrypted backup**, enter a password of at least 12 characters. With **Include the connected radio** checked, **Export full backup** reads the radio identity and supported settings before creating an encrypted file. It includes PC settings, messages, Secure Coms secrets, saved repeater and room passwords, saved radio addresses, situation map data, offline map files, the radio private identity, contacts, channel slots and keys, radio parameters, location, auto-add, default scope, path mode, tuning and BLE PIN where supported. Private-key export and import must be enabled on the radio firmware. If identity export is disabled or any required radio data cannot be read, export fails rather than silently creating an incomplete radio backup; uncheck the radio option for a PC-only backup. Offline maps are limited to 256 MB total. Wi-Fi credentials, radio message queues, companion repeat mode and some firmware-specific or sensor settings are not included; configure those separately. Do not run the same radio identity on two radios at once.

To recover, first choose **Import backup** and select the backup. Then connect the target radio, enter the password again, choose **Restore connected radio**, select the same file and review the confirmation. This overwrites its identity and matching radio settings and channel slots, and merges contacts. Command checks that the target has enough capacity and creates an encrypted recovery backup of the target first. Radio restore can be partial if a command or connection fails; use the recovery backup and check the radio. It verifies the identity, core radio settings, channel slots and presence of contacts, but compatibility and physical-device behaviour vary. Importing PC data also creates an encrypted recovery backup. An old backup can roll back replay protection; verify fingerprints and exchange a new shared secret where replay protection matters.

The unified encrypted backup includes Secure Coms contacts, secrets, groups and message state. Use **Import backup** with the original password to import an older `.mccbackup` Secure Coms file. Older imports merge replay history and do not overwrite conflicting secrets; imported contacts need fingerprint verification. The app makes an encrypted PC recovery backup before applying an older file.

## 8. Terrain tools and coverage

Open **Terrain tools**, choose **Place repeaters on map**, then click once for each planned site. Choose **Finish placing repeaters** when done. New repeater antenna heights default to 1 m above ground; edit each height if needed. You can also add located known repeaters, drag planned sites, and choose which are included. Set the coverage radius and receiver antenna height, then choose **Calculate coverage & links**. Use the **Terrain visibility opacity** slider to adjust the coloured coverage overlay without recalculating; the setting is saved on this PC. Each selected site gets a terrain-visibility layer; for two to 20 selected sites, every pair gets a coloured line and an expandable terrain profile. Open a link in the list or click it on the map to inspect it. Selecting a link opens its elevation graph at the top of the map. Hover over its elevation graph to highlight the sampled position on the map, with distance and ground elevation. Drag a repeater to recalculate coverage and links automatically; calculations can take time and require online terrain data. Use **Calculate links** when coverage is not needed. Existing saved plans retain their heights. Use the search radius to choose an area and display coverage for known nodes. Map tiles and elevation data require internet access and the calculation sends coordinates to those services.

Open **Elevation finder**, enter a height in metres above sea level and select **Highlight visible area**. Purple marks terrain pixels above that height. The tool downloads Mapzen terrain tiles for the current view; the displayed tile resolution and underlying data limit the detail, and missing pixels are not assessed. After moving or zooming, calculate again. **Clear / cancel** removes the elevation highlight without clearing coverage or planned repeaters.

Coverage is a terrain visibility estimate, **not** measured signal strength or a guarantee of reception. Resolution, buildings, trees, antenna patterns, interference and weather affect real links. Verify important links in the field.

Under **Repeater plan**, choose **Save / share plan** to save a `.mccplan` file containing the sites, their positions, antenna heights and selection, plus the terrain calculation inputs. Share the file with another Command user, who can choose **Open plan**. Opening replaces the current plan after confirmation; save it first if needed. The file is not encrypted and contains locations. Coverage and link profiles are recalculated after opening and need online terrain data. The current repeater list also saves automatically on the PC for the connected radio.

To send a plan over LoRa, both people need this update and a verified Secure Coms connection. Under **Repeater plan**, choose a verified contact and select **Send plan over mesh**. Review the packet count before confirming. The app sends encrypted parts with app-level acknowledgements and retries. Keep both apps connected; large plans use considerable airtime. The receiving app stores completed plans separately, displays a Terrain tools unread badge and in-app alert, and requests a Windows notification when Secure Coms alerts are enabled. Select a plan by sender and time under **Received plans**, then choose **Open selected plan**. Opening asks before replacing the current plan. You can delete received plans individually; up to 100 transfers are retained per connected radio. Both apps must be running and connected to exchange plans. The feature stores incomplete transfers locally and does not automatically resume an interrupted sending job. Known repeaters in a plan show a small telemetry card beside their map marker. Use its refresh button to request a current reading. The seven-day graph shows reported power in watts when available, otherwise device voltage in volts. It uses readings collected on this PC, including Monitoring voltage history; a single reading appears as a dot. The app does not infer watts from voltage.

Received terrain plans appear in the right-hand **Terrain sharing** panel. Tick a plan to show its repeater sites, untick it to hide them, or delete it locally. **Open** replaces the current editable plan after confirmation; recalculate coverage and links locally, as coverage images are not included in a mesh transfer. Delivery status confirms that all packets were acknowledged and the receiving app saved the plan; an unconfirmed transfer is not proof of failure or delivery.

## 9. Secure Situation Map and offline maps

The floating toolbar provides **Marker**, **Area**, **Out of bounds**, **Share**, **Objects** and **Settings**. Choose Marker and click the map, or right-click it, then enter details and save. Select a marker and choose **Move selected marker** to change its position; markers are not draggable. Civilian and emergency icons are practical symbols, not certified emergency symbology. The newer icons require a compatible updated app at the receiving end.

To draw an area, choose **Area** or **Out of bounds**, enter a short name, click three to eight corners and finish. Large shapes are split into acknowledged encrypted packets, with retries; the receiving app needs the multi-packet update and both sides must remain connected during transfer. The app shows complete areas only after assembly. Out-of-bounds areas are visual planning aids and do not enforce boundaries.

Use **Share with** to select verified contacts or apply a local group. With no recipients selected, new objects stay on this PC. Changes to previously shared markers also go to their previous recipients after confirmation. **Send picture to selected** queues new and changed markers and areas separately for each selected recipient; previously confirmed revisions are skipped. In **More sharing options**, select **Force full picture** to resend confirmed objects, or opt in to forwarding received markers.

The sharing queue runs while you use the app, survives restart, and resumes after your radio reconnects. Unconfirmed updates are retried with increasing delays. **Pause queue** stops further attempts; use **Resume queue** to continue. Changes to an object waiting in the queue replace its older queued revision. The object confirmations panel shows each recipient's result and provides an individual retry. A pending deletion can be removed from your local lists without claiming the other person deleted it. Select **Show locally removed** in the confirmations panel to restore it to the list; a later picture send can still retry a pending deletion. App acknowledgements indicate a saved update, not that someone saw it. The experimental protocol is not ATAK compatible.

For an offline background, open **Settings**, select **Select download area**, click two opposite corners on the map, choose a detail level and select **Download area**. The initial download needs internet and obtains a verified Protomaps helper. Larger areas or higher detail take more space and time. You can also import a Protomaps vector PMTiles file or raster MBTiles file. Online maps include light and dark street styles. The downloaded background works offline within its saved area and zoom levels. Terrain coverage calculations require separate online elevation data.

When creating or editing a marker, use the contact and group checkboxes in its editor to choose recipients. Group members receive individual encrypted updates. A marker with no recipients is saved locally. Previously shared markers continue to include their previous recipients when updated, after confirmation.

## 10. Troubleshooting

- **Cannot connect:** check radio power, Wi-Fi and DHCP address, local subnet, firewall and whether another controller is connected. Try the address manually.
- **Cannot hear the local mesh:** check the radio preset, default and channel flood scope, antenna and nearby repeaters. On ScotMesh, `#test` with scope `sco` can be used for a manual test; a reply from another user confirms two-way communication.
- **No location on a map:** the contact must advertise a valid location. Ambiguous key prefixes cannot be mapped reliably.
- **No telemetry or status:** check reachability, compatible firmware and permissions. A timeout does not establish why a node did not respond.
- **No desktop alert:** use the test button and check Windows notification and focus settings; keep the app connected.
- **Missing room history:** check login and the saved direct route. Only retained posts the server can provide can be requested.
- **Unsupported request or old firmware:** a control may be unavailable even if it appears in the interface. Read the response before retrying or changing settings.

For local Scotland guidance, see the [ScotMesh MeshCore FAQ](https://wiki.scotmesh.uk/en/MeshCore/faq). For implementation details and limitations, see the [desktop notes](../MeshCore%20Desktop/README.md).

## Firmware checks in version 2.0.27

Before flashing, Command checks the connected ESP32-S3 flash capacity and refuses an image whose partition layout exceeds it. Select the exact board and display variant; the capacity check cannot identify every hardware difference. The V3 OLED firmware includes a MeshCore Command startup screen and disables Wi-Fi modem sleep. This change was tested on one Heltec V3; other bundled board images retain their previous firmware and have not been physically verified with this change. Save radio settings before flashing.

## Secure GPS position sharing

On the Secure Situation Map, open **Share**, choose verified contacts or apply a local group, and expand **Share my GPS position**. On Windows connect an NMEA USB GPS receiver, refresh the ports, choose its COM port and baud rate. On Android grant precise location permission and enable phone location. Choose an interval from 1 to 360 whole minutes and select **Start GPS sharing**. Confirm the recipients and interval. The first update uses a fresh fix; each selected contact receives a separate encrypted Secure Coms map object. GPS position markers show the MeshCore Command logo and the station name from your radio or known contacts; if a contact is unknown, its key prefix is shown. A GPS indicator stays visible with a Stop button. GPS sharing stops when requested, the radio disconnects or changes, or the app closes; it never starts automatically after reopening. A message already handed to the radio may still transmit. Old positions already received remain on recipients' maps. If a fix is unavailable, the app reports the failure and retries at the next interval. Keep the app open and connected: operating-system background restrictions can delay updates. Open Object confirmations on the map to see the latest GPS update and a separate status for each recipient. A green tick means that recipient’s app confirmed that exact position update; awaiting confirmation does not prove failure or receipt. The confirmation is not a read receipt. A group with a short interval generates separate packets and acknowledgements for every member, so choose a conservative interval for network capacity. Position sharing does not change the radio's advertised position, but normal radio adverts are controlled separately. Secure Coms is experimental and has not received an independent security audit.

To restart the selected local companion, open **Settings → MeshCore settings → Reboot connected radio** and confirm the radio name and key. The command can temporarily disconnect the board and interrupt queued radio activity. A sent command does not confirm that the board restarted; check the connection afterwards. On Android, use **More → Radio settings**. Rebooting a remote repeater remains a separate operation on the Repeaters page.


## Message routing

Command requests flood routing for outgoing direct messages, room messages, Secure Coms messages, secure map objects and chat pings by clearing each recipient’s saved radio path immediately before sending. This changes the contact’s saved route on the radio; the radio may learn another route after receiving packets. Command warns if the radio reports direct routing or does not report the route; it does not automatically resend a message that was already queued. Channel messages use flood routing. Flooding increases airtime and network congestion, and does not guarantee delivery. A flood-routed direct message remains a direct message; routing does not post it on a public channel. Room history polling and repeater administration have separate routing requirements and are not forced to flood. Resetting a room’s path for a message may temporarily affect history retrieval until a usable path is learned again.


On the Secure Situation Map, **My position** displays a local blue GPS dot and centres the map. On Windows select the USB GPS port under **Share → Share my GPS position** first; on Android grant location permission. The dot moves with new fixes and does not itself transmit. Use **Stop local GPS display** in that panel to stop it. The map shows only the latest GPS tracker per station, while keeping older tracker records for acknowledgements. GPS sharing reuses the previous local tracker ID when restarted. GPS timestamps depend on each sending device’s clock.

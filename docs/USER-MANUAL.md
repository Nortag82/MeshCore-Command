# MeshCore Command user manual

MeshCore Command is a Windows desktop companion for a compatible Wi-Fi MeshCore radio. The PC hosts the interface and connects to the radio on your local network. Screenshots in the [project README](../README.md) use simulated data. This manual describes installer build 1.0.1-preview.5. Secure groups are local recipient lists; the separate secure group pairing prototype is not integrated.

## Before you start

- You need Windows, the Edge WebView2 runtime, a compatible Wi-Fi radio and an appropriate LoRa antenna. Connect the antenna before powering or transmitting with the radio.
- The PC and radio need network access to each other. The companion connection uses TCP port 5000; discovery uses UDP port 5010.
- Use a trusted local network: the companion protocol itself is unauthenticated and unencrypted. The PC bridge listens on loopback.
- Download the installer from the [Preview 5 release](https://github.com/Nortag82/MeshCore-Command/releases/tag/v1.0.1-preview.5), run it and follow the prompts. The installer and bundled firmware are unsigned and experimental. Source-build information is in the [build notes](../README.md#building).

## 1. Set up or connect a radio

1. Start MeshCore Command. If your radio is already configured, select or enter its IP address. You can save several radios with names for future use.
2. To find a radio, enter the appropriate local network subnet and choose **Scan network**. Progress shows probes attempted followed by a wait for replies; a probe is not proof that an address is reachable. If you use a VPN or multiple adapters, check that the selected subnet belongs to the Wi-Fi network. You can also enter the IP manually.
3. Select **Connect**. Only one companion controller should use the radio connection at a time.

The setup window hides while the radio window is open. The app protects an active radio session from another window on supported Windows webviews. If **Another tab active** persists, close any other Command windows and reconnect. The app must remain connected to receive messages and show alerts; the minimise-and-restore fix has not yet been verified with a live radio.

For a new board, open **Set up a new board** or **Flash firmware**, choose the exact board and screen variant, select its COM port, and enter the 2.4 GHz Wi-Fi name and password. The app uses DHCP and shows the address once provisioning succeeds. The progress panel shows the current flashing stage, any per-image percentage reported by the tool, Wi-Fi setup and DHCP; expand the log for diagnostics. A suggested serial port is not proof of the board model. Flashing replaces firmware; preserve important data first. Existing settings or data from another firmware layout may not be accessible afterwards.

The supported choices include Heltec V3, V4 and V4 R8 variants, Heltec Wireless Tracker V2 and LilyGO T-Beam S3 Supreme SX1262. Choose the exact model and display option; other T-Beam models are not interchangeable. After you choose a board, the flasher shows board-specific USB and boot instructions. The app attempts an automatic reset; manually entering boot mode does not disable it. Refresh and reselect the correct COM port if it changes. Keep USB connected during flashing and Wi-Fi setup. The firmware's brief USB provisioning window occurs at boot.

## 2. Check your mesh settings

Open **Settings** to change the radio name and other settings. **ScotMesh setup check** reads the radio's default scope and checks its settings against EU/UK Narrow. The **Set radio default to sco** button writes the scope and reads it back. For other networks, confirm the appropriate radio preset and scopes with the local community.

A **channel** is a group chat; a **flood scope** is a routing choice. MeshCore Command remembers channel scope selections locally for each radio and channel. A channel choice does not change the radio's default scope, which also affects default-scoped flood adverts. Direct routes do not use a flood scope. Incoming messages may say the region was not reported when the radio does not provide it.

## 3. Messages and channels

1. Open **Messages**, then select **Direct messages**, **Channels** or **Rooms**. Unread badges identify the tabs and conversations with new messages; muted channels do not contribute to the badges.
2. Search for a conversation, or start a direct conversation from **Contacts**.
3. Type a message and press **Enter** to send; use **Shift+Enter** for a new line. Right-click a message to copy text, reply with a quote or send a thumbs-up as an ordinary message.
4. Use **Mute channel** to suppress notifications and unread badges for that channel. Messages remain available; word matches are still recorded silently.

New direct, room and unmuted channel messages request a Windows tray alert and an app pop-up. Secure messages use the separate **Alert me to secure messages** setting in **Manage Secure Coms**. Message text is not included in these general alerts. The app and radio must be connected, and Windows can suppress tray alerts or sound. In Secure Coms, your messages are blue and incoming messages are red.

To join a channel, select **Channels → Add channel**. A hashtag channel derives its key from its exact name and is readable by anyone who knows that name. For a private channel, enter or generate the shared key and share it securely with the other members. You can paste a `meshcore://channel/add` invite into the invite field to fill the name and key; verify them before adding. Keep the invite private because it contains the shared key. Choose the flood region separately. For example, ScotMesh recommends `sco` as the default Scotland scope and uses `ioi` for the Island of Ireland path. Check current local guidance before configuring repeaters.

Select a conversation and use **Edit** or **Delete** in its header. Editing a channel changes its name or key on the connected radio; deleting it clears the radio slot and its history on this PC. Changing a key affects which messages the radio can receive. Hashtag keys are derived from the exact channel name, so use the correct key when renaming one. Room and direct conversation names are local display names only. Deleting a room removes its local registration, saved password and history; deleting a direct conversation removes its local history, not the contact. Other devices and room servers retain their own copies.

Right-click a message and choose **Delete from this PC** to remove its local copy. For a sent message, **Edit and send a correction** prepares a new message; review it and send it yourself. Previously delivered messages cannot be edited or deleted on other devices.

A delivery tick or acknowledgement applies only when the protocol reports one; a queued message is not proof that it was received or read. Channel broadcasts do not offer individual delivery confirmation for every recipient.

### Room servers

Under **Rooms**, add a known room server and enter its password. You may save the password in Windows Credential Manager. While connected, the app checks added rooms periodically; **Check room now** requests a check. **Load retained history** asks a compatible room server to resend posts it still holds using a known direct route. It cannot recover discarded posts or guarantee every old message is available, and replies can arrive gradually.

## 4. Contacts and contact map

Open **Contacts** to search nodes by name, role or key, and star favourites. Choose a contact for messaging, status, telemetry, path reset, ping or route tracing. **Discover contacts** searches for nearby compatible nodes; it does not guarantee that all network users will respond.

A chat ping sends the word `ping` as a message. A repeater ping uses a direct trace. Traces in this version support known single-byte paths only; short hashes can collide, and a trace is not proof of final message delivery. Availability of status and telemetry depends on node firmware, permissions and reachability.

**Contact map** shows nodes with advertised positions. Its received-packet list can display reported flood-path segments when key prefixes can be matched unambiguously to located contacts. Gaps and hash collisions are possible; lines are not proof of delivery. Internet access is needed for map tiles.

## 5. Repeaters

Search for a repeater on **Repeaters**, select it and log in with its admin password. You can optionally save the password in Windows Credential Manager and star the repeater for easier access.

- **Status** and **Telemetry** show supported readings when returned by the node.
- **Refresh neighbours** requests the current list. **Discover neighbours** starts discovery, counts down 60 seconds and then requests the list again.
- The neighbour map needs a unique contact match and valid advertised locations at both ends. A listed SNR is what the selected repeater reported, not a guaranteed bidirectional measurement.
- **Read settings** and the remote command controls depend on the repeater firmware. Read and verify values before changing them.
- The region controls can read the tree and send individual region changes. Review the result and explicitly save the region configuration when appropriate. The app does not automatically install a ScotMesh region tree.
- The repeater advert path-size setting affects that repeater's own adverts. The radio's message path setting is separate; compatibility varies with firmware.

## 6. Monitoring and word alerts

On **Monitoring**, add a known chat contact or repeater, then expand **Schedule checks** to choose an interval and checks. Compact cards show the latest results. Expand **Repeater time** for **Read time**, **Sync to PC** and any diagnostic details; these require an active admin login or saved password. A repeater may reject a backwards time correction; the displayed time is the last reading, not a live clock. Checks run only while the app is open and connected. A voltage-based battery percentage is an estimate and may not be appropriate for every power source.

On **Word alerts**, enter one word or phrase per line and save. The app checks newly received direct messages and joined channel messages; it cannot monitor traffic it cannot receive or decrypt. Use **Send test notification** to check Windows notifications and sound. Windows notification or focus settings can suppress alerts. Matches are stored locally, and enabling message previews can put message text in desktop notifications.

## 7. Secure Coms

Both participants need a compatible MeshCore Command installation and a MeshCore chat contact. In **Manage Secure Coms**, one person generates a secret, shares it with the other person through a trusted separate channel, and both save it. Compare the displayed fingerprints using a trusted channel before marking the contact verified.

Secure Coms encrypts on the PC using AES-256-GCM and sends the encrypted content over MeshCore direct messages. It is experimental, has not had an independent security audit and does not provide forward secrecy or protect a compromised PC. Messages are short because of packet limits. New outgoing messages can show a radio delivery acknowledgement; this does not prove that the receiving app decrypted or a person read the message. Earlier messages cannot be retrospectively confirmed.

### Discover Command installations

In **Manage Secure Coms**, select **Discover Command installs** to send a request over the Public channel using the radio's default flood scope. Compatible installations that are running and can hear it can reply. Discovery requests and responses are visible to Public-channel users and disclose the sender's public key and app presence. Other MeshCore apps may show them as ordinary channel messages. Automatic responses are enabled by default; disable **Respond to discovery requests** if you do not want to reply. Requests and replies are rate limited.

Replies can be spoofed and are not verified identities. Select **Add & set up** to add an unknown radio contact, or **Set up** for a known one. Exchange the shared secret privately and compare fingerprints before marking the contact verified. Discovery requires compatible versions, the Public channel and a reachable flood scope; it cannot guarantee a response.

### Local groups and backup

Create a group in **Manage Secure Coms** by choosing a name and verified contacts. The group is a local recipient list; members do not need the group or another key. Sending to the group sends an individually encrypted direct message to each member using the existing shared secret. Replies appear in individual conversations. The group view shows per-recipient radio acknowledgements, which are not read receipts. The same groups can select recipients on the Secure Situation Map.

Under **Settings**, export an encrypted Secure Coms backup with a password, and import it with that same password. It contains contacts, their shared secrets, group definitions and secure message state; it is not a complete backup of radio settings or offline maps. Keep both file and password safe. After importing, verify contact fingerprints again. An old backup cannot contain replay records created after it was made.

## 8. Terrain tools and coverage

Open **Terrain tools** and use the map or coordinate fields to set points. You can add located known repeaters and planned repeaters, adjust positions and antenna heights, and calculate estimated line of sight and terrain visibility. Use the search radius to choose an area and display coverage for known nodes. Map tiles and elevation data require internet access and the calculation sends coordinates to those services.

Open **Elevation finder**, enter a height in metres above sea level and select **Highlight visible area**. Purple marks terrain pixels above that height. The tool downloads Mapzen terrain tiles for the current view; the displayed tile resolution and underlying data limit the detail, and missing pixels are not assessed. After moving or zooming, calculate again. **Clear / cancel** removes the elevation highlight without clearing coverage or planned repeaters.

Coverage is a terrain visibility estimate, **not** measured signal strength or a guarantee of reception. Resolution, buildings, trees, antenna patterns, interference and weather affect real links. Verify important links in the field.

## 9. Secure Situation Map and offline maps

The floating toolbar provides **Marker**, **Area**, **Out of bounds**, **Share**, **Objects** and **Settings**. Choose Marker and click the map, or right-click it, then enter details and save. Select a marker and choose **Move selected marker** to change its position; markers are not draggable. Civilian and emergency icons are practical symbols, not certified emergency symbology. The newer icons require a compatible updated app at the receiving end.

To draw an area, choose **Area** or **Out of bounds**, enter a short name, click three to eight corners and finish. Large shapes are split into acknowledged encrypted packets, with retries; the receiving app needs the multi-packet update and both sides must remain connected during transfer. The app shows complete areas only after assembly. Out-of-bounds areas are visual planning aids and do not enforce boundaries.

Use **Share with** to select verified contacts or apply a local group. With no recipients selected, new objects stay on this PC. Changes to previously shared markers also go to their previous recipients after confirmation. **Send picture to selected** sends your markers and areas to selected recipients; forwarding markers received from others is an explicit option. App acknowledgements indicate a saved update, not that someone saw it. The experimental protocol is not ATAK compatible.

For an offline background, open **Settings**, select **Select download area**, click two opposite corners on the map, choose a detail level and select **Download area**. The initial download needs internet and obtains a verified Protomaps helper. Larger areas or higher detail take more space and time. You can also import a Protomaps vector PMTiles file or raster MBTiles file. Online maps include light and dark street styles. The downloaded background works offline within its saved area and zoom levels. Terrain coverage calculations require separate online elevation data.

## 10. Troubleshooting

- **Cannot connect:** check radio power, Wi-Fi and DHCP address, local subnet, firewall and whether another controller is connected. Try the address manually.
- **Cannot hear the local mesh:** check the radio preset, default and channel flood scope, antenna and nearby repeaters. On ScotMesh, `#test` with scope `sco` can be used for a manual test; a reply from another user confirms two-way communication.
- **No location on a map:** the contact must advertise a valid location. Ambiguous key prefixes cannot be mapped reliably.
- **No telemetry or status:** check reachability, compatible firmware and permissions. A timeout does not establish why a node did not respond.
- **No desktop alert:** use the test button and check Windows notification and focus settings; keep the app connected.
- **Missing room history:** check login and the saved direct route. Only retained posts the server can provide can be requested.
- **Unsupported request or old firmware:** a control may be unavailable even if it appears in the interface. Read the response before retrying or changing settings.

For local Scotland guidance, see the [ScotMesh MeshCore FAQ](https://wiki.scotmesh.uk/en/MeshCore/faq). For implementation details and limitations, see the [desktop notes](../MeshCore%20Desktop/README.md).

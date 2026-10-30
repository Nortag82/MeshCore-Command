# MeshCore Command user manual

MeshCore Command is a Windows desktop companion for a compatible Wi-Fi MeshCore radio. The PC hosts the interface and connects to the radio on your local network. Screenshots in the [project README](../README.md) use simulated data. This manual describes the current development source (1.0.1-dev); the published preview installer may not include every feature below.

## Before you start

- You need Windows, the Edge WebView2 runtime, a compatible Wi-Fi radio and an appropriate LoRa antenna. Connect the antenna before powering or transmitting with the radio.
- The PC and radio need network access to each other. The companion connection uses TCP port 5000; discovery uses UDP port 5010.
- Use a trusted local network: the companion protocol itself is unauthenticated and unencrypted. The PC bridge listens on loopback.
- Download [Install MeshCore Command.exe](https://github.com/Nortag82/MeshCore-Command/releases/tag/v1.0.0-preview.5), run it and follow the prompts. The installer and bundled firmware are unsigned and experimental. Source-build information is in the [build notes](../README.md#building).

## 1. Set up or connect a radio

1. Start MeshCore Command. If your radio is already configured, select or enter its IP address. You can save several radios with names for future use.
2. To find a radio, enter the appropriate local network subnet and choose **Scan network**. If you use a VPN or multiple adapters, check that the selected subnet belongs to the Wi-Fi network. You can also enter the IP manually.
3. Select **Connect**. Only one companion controller should use the radio connection at a time.

For a new board, open **Set up a new board** or **Flash firmware**, choose the exact board and screen variant, select its COM port, and enter the 2.4 GHz Wi-Fi name and password. The app uses DHCP and shows the address once provisioning succeeds. A suggested serial port is not proof of the board model. Flashing replaces firmware; preserve important data first. Existing settings or data from another firmware layout may not be accessible afterwards.

The supported choices include Heltec V3, V4 and V4 R8 variants, Heltec Wireless Tracker V2 and LilyGO T-Beam S3 Supreme SX1262. Choose the exact model and display option; other T-Beam models are not interchangeable. After you choose a board, the flasher shows board-specific USB and boot instructions. The app attempts an automatic reset; manually entering boot mode does not disable it. Refresh and reselect the correct COM port if it changes. Keep USB connected during flashing and Wi-Fi setup. The firmware's brief USB provisioning window occurs at boot.

## 2. Check your mesh settings

Open **Settings** to change the radio name and other settings. **ScotMesh setup check** reads the radio's default scope and checks its settings against EU/UK Narrow. The **Set radio default to sco** button writes the scope and reads it back. For other networks, confirm the appropriate radio preset and scopes with the local community.

A **channel** is a group chat; a **flood scope** is a routing choice. MeshCore Command remembers channel scope selections locally for each radio and channel. A channel choice does not change the radio's default scope, which also affects default-scoped flood adverts. Direct routes do not use a flood scope. Incoming messages may say the region was not reported when the radio does not provide it.

## 3. Messages and channels

1. Open **Messages**, then select **Direct messages**, **Channels** or **Rooms**. Unread badges identify the tabs and conversations with new messages; muted channels do not contribute to the badges.
2. Search for a conversation, or start a direct conversation from **Contacts**.
3. Type a message and press **Enter** to send; use **Shift+Enter** for a new line. Right-click a message to copy text, reply with a quote or send a thumbs-up as an ordinary message.
4. Use **Mute channel** to suppress notifications and unread badges for that channel. Messages remain available; word matches are still recorded silently.

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

On **Monitoring**, add a known chat contact or repeater, choose an interval, and enable the desired ping, trace and telemetry checks. Checks run only while the app is open and connected. A voltage-based battery percentage is an estimate and may not be appropriate for every power source.

On **Word alerts**, enter one word or phrase per line and save. The app checks newly received direct messages and joined channel messages; it cannot monitor traffic it cannot receive or decrypt. Use **Send test notification** to check Windows notifications and sound. Windows notification or focus settings can suppress alerts. Matches are stored locally, and enabling message previews can put message text in desktop notifications.

## 7. Secure Coms

Both participants need a compatible MeshCore Command installation and a MeshCore chat contact. One person generates a secret, shares it with the other person through a trusted separate channel, and both save it. Compare the displayed fingerprints using a trusted channel before marking the contact verified.

Secure Coms encrypts on the PC using AES-256-GCM and sends the encrypted content over MeshCore direct messages. It is experimental, has not had an independent security audit and does not provide forward secrecy or protect a compromised PC. Messages are short because of packet limits. Delivery acknowledgement is not a cryptographic read receipt.

### Discover Command installations

In **Secure Coms**, select **Discover Command installs** to send a request over the Public channel using the radio's default flood scope. Compatible installations that are running and can hear it can reply. Discovery requests and responses are visible to Public-channel users and disclose the sender's public key and app presence. Other MeshCore apps may show them as ordinary channel messages. Automatic responses are enabled by default; disable **Respond to discovery requests** if you do not want to reply. Requests and replies are rate limited.

Replies can be spoofed and are not verified identities. Select **Add & set up** to add an unknown radio contact, or **Set up** for a known one. Exchange the shared secret privately and compare fingerprints before marking the contact verified. Discovery requires compatible versions, the Public channel and a reachable flood scope; it cannot guarantee a response.

## 8. Terrain tools and coverage

Open **Terrain tools** and use the map or coordinate fields to set points. You can add located known repeaters and planned repeaters, adjust positions and antenna heights, and calculate estimated line of sight and terrain visibility. Use the search radius to choose an area and display coverage for known nodes. Map tiles and elevation data require internet access and the calculation sends coordinates to those services.

Coverage is a terrain visibility estimate, **not** measured signal strength or a guarantee of reception. Resolution, buildings, trees, antenna patterns, interference and weather affect real links. Verify important links in the field.

## 9. Situation map and offline maps

Open **Situation map** to create and share map markers. Right-click the map to create a marker, or right-click an existing marker to edit it. To move one of your markers, select it, choose **Move selected marker**, click the new position, then select **Save marker**. Press Escape while choosing a position to cancel. Markers cannot be dragged. The marker editor opens when needed. Markers can be saved locally without a Secure Coms contact. To share updates, set up and verify a Secure Coms contact on both PCs, then choose recipients under **Settings**. Select a marker to see per-contact confirmations; these indicate that the receiving app saved that update, not that a person saw it.

To send the picture, select **Send my picture**. The app sends markers separately to every verified Secure Coms contact currently known to the radio, and reports confirmations and failures per recipient. The View contact selector filters the display; it does not limit sending. Both apps must remain connected; retransmit unconfirmed markers as needed. Markers are identified by creator and random marker ID. Received markers are forwarded as copies; copies relayed by different contacts can appear separately. The protocol is experimental and does not support ATAK interoperability.

For an offline background, open **Settings**, select **Select download area**, click two opposite corners on the map, choose a detail level and select **Download area**. The initial download needs internet and obtains a verified Protomaps helper. Larger areas or higher detail take more space and time. You can also import a Protomaps vector PMTiles file or raster MBTiles file. The downloaded background works offline within its saved area and zoom levels. Terrain coverage calculations require separate online elevation data.

## 10. Troubleshooting

- **Cannot connect:** check radio power, Wi-Fi and DHCP address, local subnet, firewall and whether another controller is connected. Try the address manually.
- **Cannot hear the local mesh:** check the radio preset, default and channel flood scope, antenna and nearby repeaters. On ScotMesh, `#test` with scope `sco` can be used for a manual test; a reply from another user confirms two-way communication.
- **No location on a map:** the contact must advertise a valid location. Ambiguous key prefixes cannot be mapped reliably.
- **No telemetry or status:** check reachability, compatible firmware and permissions. A timeout does not establish why a node did not respond.
- **No desktop alert:** use the test button and check Windows notification and focus settings; keep the app connected.
- **Missing room history:** check login and the saved direct route. Only retained posts the server can provide can be requested.
- **Unsupported request or old firmware:** a control may be unavailable even if it appears in the interface. Read the response before retrying or changing settings.

For local Scotland guidance, see the [ScotMesh MeshCore FAQ](https://wiki.scotmesh.uk/en/MeshCore/faq). For implementation details and limitations, see the [desktop notes](../MeshCore%20Desktop/README.md).

# MeshCore Command — PC-controlled companion

The Windows app contains the control interface. It serves that interface locally on the PC (127.0.0.1) and connects to the V4 using the MeshCore companion protocol over TCP port 5000. The V4 does not run a web server or present a web login. Its UDP port 5010 answers device-discovery requests.

The desktop application provides messaging, contacts, channel slots, radio and node settings, and sco/ioi scope selection. This is not feature parity with every official MeshCore client: not all repeater and room administration tools are included. Browser history from earlier versions is not automatically imported into the PC interface.

## Connect
Open MeshCore Command from the Windows Start menu. Scan a local IPv4 subnet or enter the radio IP and choose Open radio. Enter an optional name and select Save radio to keep multiple addresses on this PC; use Connect or Remove beside a saved radio. If your computer has several network adapters or a VPN, the proposed subnet may be wrong; enter the subnet of your radio, for example `192.168.1.0/24`. The radio and PC must be able to reach each other. Only one companion controller should connect at a time.

## Flash and configure
Use the Flash firmware menu. Attach the intended radio by USB and attach a suitable LoRa antenna. Select the exact board and display variant: Heltec V3, Heltec V4, Heltec V4 R8, Heltec Wireless Tracker V2, or LilyGO T-Beam S3 Supreme SX1262. The T-Beam option is for the SX1262 model; its SH1106 OLED is detected automatically when fitted; other T-Beam models are not interchangeable. Select its COM port, enter the 2.4 GHz SSID and password, then press Flash & configure. ESP32-S3 USB detection is a candidate, not proof of the exact board model. Firmware, bootloader and partition metadata are written and verified; the app then provisions Wi-Fi over USB and reports the DHCP address. The app does not save your Wi-Fi password; the radio stores it in NVS.

The firmware's 12-second USB provisioning window runs on each boot. If the network is unreachable, USB provisioning can be repeated. The flash process does not intentionally erase the data partition, but data from a different firmware or partition layout may be inaccessible. The firmware defaults to UK Narrow and the sco region on fresh storage; existing saved settings are preserved where supported.

## Security and installation
The MeshCore TCP companion protocol is unauthenticated and unencrypted, and the discovery service is unauthenticated. Use a trusted local network. The PC's local bridge listens only on loopback, but other local programs may be able to connect. The installer installs per-user under `%LOCALAPPDATA%\MeshCore Command` and creates a Start menu shortcut. Edge WebView2 Runtime is required. The installer and firmware are unsigned and experimental. The previous full flash backup may contain private device keys; keep it private.


## Repeaters and terrain
In the radio window, select **Repeaters**. Choose a discovered repeater from the contact list, log in with its admin password, and use status, neighbours, path reset or the remote CLI. Quick commands provide common read operations and adverts. You may choose to save a repeater password in Windows Credential Manager, or forget it later. Favourited repeaters are kept in the PC list; if a saved repeater is no longer in the radio contacts, refresh or rediscover it before connecting. Repeater results depend on reachability and permissions. The initial implementation has been exercised using simulated repeater responses; actual repeater behaviour and supported CLI commands vary by firmware.

The map uses OpenStreetMap tiles and the Open-Meteo elevation API (Copernicus DEM GLO-90, nominal 90 m). Map use downloads tiles; calculating terrain sends the entered coordinates to Open-Meteo. Line of sight samples 80 points, uses a 4/3 effective Earth radius and reports 60% Fresnel clearance. Coverage is a coarse polygon based on 16 radials and 12 steps per radial, and uses the entered receiver height. It is a terrain visibility estimate, not RF link-budget modelling, reception testing or actual coverage. Elevation resolution, buildings, trees, antenna patterns, radio settings, atmospheric effects and interference can change the result.

## Word alerts
Open the PC radio window and select **Word alerts**. Enter up to 50 case-insensitive words or phrases, one per line, and save. Incoming direct messages and received messages on configured channels are checked as they arrive. Messages already in the local history are not rescanned, and the device cannot read channels it has not joined or messages it cannot decrypt. Keep the app and its radio window connected. A Windows notification-area alert is queued on a match; Windows notification settings, focus modes and other system conditions can suppress it. Use **Send test notification** to check. Match records and watched terms are kept in the app's persistent local WebView profile; message text is included in pop-ups only if enabled. Matches may contain private data.

## Contacts
Use the Contacts page to search saved nodes by name, role or public key. Select a node to message a chat contact, ping, trace a known route, query compatible nodes, copy a public key, reset a path or remove a contact. Ping to a chat contact sends the visible message `ping` and reports the delivery acknowledgement when received. Ping to a repeater performs a direct trace; it is not a general multi-hop ping. Route tracing follows known one-byte repeater hashes; hashes can collide, and tracing does not prove the final chat contact received a message. Multi-byte paths and unknown routes cannot be traced here. The message screen also searches contact and channel names or keys; it does not search message text.

Star a contact on the Contacts page to add it to favourites. Repeaters can also be starred on the Repeaters page, where the search field matches names and full public keys. Favourites appear first in both lists and are saved in the PC app's local profile. A repeater selected before a search stays selected even when it does not match the search.

## Repeater neighbours and settings
After repeater login, the settings panel can read and change supported name, forwarding and location settings. Check the repeater responses, as command support and permissions vary. Discover neighbours sends the repeater discovery command; refresh neighbours retrieves its report. The map draws a link only when its key prefix uniquely matches a contact and both nodes advertise valid locations. SNR is the value reported by the selected repeater, not an independently measured bidirectional link. Online OpenStreetMap tiles require internet access.

Messages are separated into channel and direct tabs. Direct conversations appear after a message is sent or received, or when started from Contacts.

## Channels and regions
On Messages, choose Channels and expand Add channel. Hashtag channels derive their key from the exact name; anyone who knows it can read the channel. Private channels accept a 16-byte shared key in hexadecimal or base64, or can generate one to share securely. The app checks for an empty radio slot and verifies the saved value. Flood region is a separate routing choice; Scotland, Ireland, the radio default and custom region names are available. The Terrain tools page contains the line-of-sight and coverage tools; repeater neighbour links stay on the Repeaters page.

Message bubbles show the received flood hop count when the radio reports it, and the selected flood region for successfully queued outgoing flood messages. Direct routes do not use a flood region. Incoming regions and outgoing hop counts are not reported by this companion protocol and are shown as unavailable; old messages may also lack these details.

Repeater settings are requested one at a time; the app fills values as replies arrive. Repeater CLI replies do not identify the request, so check them before making changes. The neighbour map shows known locations and links; the list explains missing or ambiguous contacts and locations. In Messages, Read radio default queries the connected radio. A fresh compatible firmware configuration defaults to sco, but previously saved settings may differ. Channel keys and flood regions are separate; the region is chosen when sending.

## Contact and packet map
The Contact map shows known contacts that advertise a location and, when available, the local radio. It lists the last 100 raw packets heard while the app is open. Select a packet or follow the latest to see matching flood-path segments. A path hash is only a short public-key prefix; ambiguous, missing and unlocated matches remain gaps, and links are reported paths rather than proof of delivery. Direct packets carry a remaining route and are not drawn as a travelled path. Map tiles require internet access.

## Channel mute and overview
In Messages, select a channel and use Mute channel or Unmute channel. The setting is stored on this PC for the selected radio and channel. Messages remain available; muted channels do not contribute to notification pop-ups or unread badges. Word matches are still recorded silently in Word alerts. The Overview provides radio sync, local and flood advert buttons, shortcuts, unread messages and a 24-hour chart of locally stored message activity.

## Message actions
Right-click a message to reply with a short quote or immediately send a thumbs-up referencing it. Both are ordinary MeshCore text messages, so other MeshCore clients can read them. Quotes count towards the 140-byte message limit.

## Terrain visibility
Terrain Tools opens as a full-window planning map. Add up to eight located repeaters from radio contacts or place planned repeaters by choosing the map-click option. Drag markers, name each repeater and set its antenna height; plans are saved locally for the radio. Calculate repeater links to estimate every pair and click a link for its profile. Coverage can be calculated for point A or for selected repeaters, with independent switches for overlapping layers.

Terrain tools estimate line of sight and 60% Fresnel clearance across a 241 × 241 terrain grid. The map colours show each calculated cell as clear, Fresnel-obstructed, blocked or unknown. Terrain height comes from Mapzen terrain tiles hosted by AWS; underlying data resolution varies. The app displays the grid cell size, which increases with the radius. Trees, buildings and propagation effects are not fully modelled, and this is not a signal-strength prediction.



## ScotMesh setup and scopes
In Settings, use **ScotMesh setup check** to inspect the connected radio's EU/UK Narrow settings and read its actual default flood scope. **Set radio default to sco** writes the default and reads it back; it affects default-scoped floods including adverts. Channel scope choices in Messages are remembered locally per radio and channel, and do not change the radio default. The scope controls flood routing separately from the channel name. The test guidance describes how to use #test; discovery sends a nearby discovery request when clicked.

In Repeaters, log in before reading the region tree. Region commands are sent only when clicked and confirmed. Read the tree before making changes, then explicitly save the region configuration after review; support depends on the repeater firmware and tagged CLI responses. No specific region tree is automatically installed. Longer path tracing remains unsupported by the bundled tracing library; incoming message scope remains unavailable when the radio does not report it.


## Situation map and offline maps
Create markers on the Situation map without a secure contact. To share, add the other radio as a chat contact in Secure Coms on both PCs, exchange the secret privately and verify the fingerprint. Marker updates use the experimental encrypted direct-message envelope. The app has no automatic reconciliation or ATAK compatibility. Marker data is stored locally on the PC; do not rely on it for time-critical decisions.

On the Situation map, open **Settings**, select **Select download area**, click two opposite corners, choose a detail level, and select **Download area**. The first download also obtains the verified Protomaps command-line helper. Internet is needed for the download, but the resulting Protomaps map is stored on the PC and can be displayed offline. Larger areas and higher detail require more storage and time. Downloads are limited to areas no more than 10 degrees wide or high. The chosen map and background are remembered; choose **Online street map** to return to the online layer. Only the selected map area and downloaded zoom levels are available offline. Protomaps data © OpenStreetMap contributors.

Use **Choose offline map** to select an existing Protomaps vector `.pmtiles` or raster `.mbtiles` (PNG, JPEG or WebP). Vector MBTiles are not supported. The file must remain at its original location. The background map does not include terrain elevation data; coverage and line-of-sight calculations still need their elevation service.


### Situation map symbols and confirmations
Right-click the map, choose **Create marker here**, select one of the supported NATO APP-06 symbols, enter a short name and optional note, choose verified Secure Coms recipients in **Settings**, and select **Save marker**. Right-click a marker you created and select **Edit marker** to change it. Select the marker to see the confirmation state for each recipient. The selected view contact is always included; updates to an existing marker are sent to all its previous recipients too. The creator may edit or delete a marker; recipients can view it.

A confirmation is a separate encrypted response from a recipient's updated MeshCore Command app after it saves the update. It is not a read receipt. If no confirmation arrives, use **Resend**; it is possible for an update or its confirmation to be lost. Older versions cannot handle the new symbol and confirmation messages, so update every participant. Labels and notes must fit a single small encrypted packet; shorten them if prompted. The app currently supports a limited selection of APP-06 symbols, not the entire standard or ATAK interoperability. Symbol rendering uses Milsymbol (MIT licence).


### Local markers and sharing a complete picture
Markers can be created and edited locally even when there are no verified Secure Coms contacts. To send the picture, select a verified contact and choose **Send my picture**. The app sends one encrypted marker update at a time, waits up to 30 seconds for the recipient app to confirm that it saved it, then retries once if no confirmation arrives. It reports confirmations per marker and keeps missing responses as unconfirmed. Use **Stop sending** to stop after the current operation.

The picture includes local markers and received markers from other contacts, which are sent as forwarded copies and labelled accordingly. An acknowledged marker confirms an app saved that update; it does not confirm a person saw it or that all other markers arrived. Both installations need a compatible up-to-date version, and the radio must remain connected. Offline basemaps must be downloaded or imported before going offline.


### Moving a situation marker
Select one of your markers, choose **Move selected marker**, then click the new position on the map. Markers cannot be dragged. The new coordinates appear in the marker form. Choose **Save marker** to commit the move and send it to the existing verified recipients. Press Escape while choosing a location to cancel. Received markers are editable only by their creator.

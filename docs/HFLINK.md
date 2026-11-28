# HFLink for JS8Call

HFLink in MeshCore Command 2.0.61 sets up and starts the JS8Call modem on the same PC. Other stations can exchange plain-text messages with you using standard JS8Call.

## Connect to JS8Call

1. Open **HFLink**. If **Install JS8Call…** appears, select it to download the official Windows installer and open setup.
2. Open **HFLink → Settings** and select your station callsign, grid, radio receive and transmit audio, and PTT method. HFLink supports VOX or RTS/DTR on a COM port. Set the dial frequency and USB mode on the radio.
3. Choose **Save and start modem**. Command starts JS8Call in the background with its own profile and local connection. Close any standalone JS8Call session before starting the HFLink modem so it can open the radio devices.
4. The **waterfall** reads the same Windows receive device you chose in HFLink. **Back to radio** keeps the modem listening and can show a Windows notification when a message arrives. Closing Command stops the modem it started.

The official engine can also be installed from the [JS8Call downloads page](https://js8call.com/downloads.html). The earlier [2.0.60 release](https://github.com/Nortag82/MeshCore-Command/blob/v2.0.60/docs/HFLINK.md) uses a separately started JS8Call session.

## Inbox and heard stations

- **Inbox** shows directed messages and imports incoming messages from the local JS8Call inbox. To send, enter a callsign and message, press **Enter** or select **Send…**, and confirm.
- **Contacts** lists stations JS8Call hears and shows their last-heard times and available signal reports. Select a callsign and choose **Message station** to address it.
- **Submitted to JS8Call** means HFLink handed the message to the modem. Use JS8Call to see transmit progress.
- **Delete message…** removes the selected entry from local HFLink history after confirmation. Manage the standalone JS8Call inbox in JS8Call.
- **Earlier HFLink history** shows messages saved by previous Mercury-based builds.

## Public

Choose **Public** to send to the group shown by the editor. It defaults to `@ALLCALL`. Set a shared group in **HFLink → Settings** and add the group in JS8Call if your group uses one.

## Relay inbox

While your station is on air, JS8Call can save messages addressed to you while you are away. If your station is off air, agree on a reachable relay that supports JS8Call message storage and inbox requests.

1. Enter the relay's callsign in **HFLink → Settings**.
2. To leave a message with the relay, address it to the recipient in **Inbox**, choose **Leave at relay…**, and confirm.
3. To look for your own messages, choose **Check relay** and confirm. If the relay replies with a message ID, choose **Retrieve message** and confirm.

Relay requests are on-air transmissions. HFLink checks your local JS8Call inbox automatically while connected.

[JS8Call user guide](https://js8call.com/JS8Call-improved/d6/d14/md_docs_2user__guide_2JS8Call__User__Guide.html) · [JS8Call TCP API](https://js8call.com/JS8Call-improved/d7/d15/md_docs_2API.html)

See the [Mercury-based HFLink guide](https://github.com/Nortag82/MeshCore-Command/blob/v2.0.59/docs/HFLINK.md) for Windows 2.0.59.

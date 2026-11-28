# HFLink for JS8Call

HFLink in MeshCore Command 2.0.60 and newer connects to JS8Call on the same PC. Other stations can exchange plain-text messages with you using standard JS8Call.

## Connect to JS8Call

1. Start JS8Call with your usual callsign, audio and PTT settings.
2. In **JS8Call → Settings → Reporting → Network Message API**, enable the TCP server on `127.0.0.1`, check **Accept TCP Requests**, and note the TCP port. The JS8Call 3.0.3 default is `2442`.
3. Open **HFLink** in MeshCore Command. If JS8Call uses a different port, enter it in **HFLink → Settings**. Wait for HFLink to show the connected station callsign.
4. Keep JS8Call running. **Back to radio** returns to MeshCore while HFLink stays connected and can show a Windows notification when a message arrives. Use JS8Call for tuning, audio, PTT and the waterfall.

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

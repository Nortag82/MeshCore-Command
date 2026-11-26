# HFLink on native Windows

Experimental HFLink sends openly readable direct messages between two attended stations. Operators are responsible for station identification, frequency, content and their applicable licence conditions. HFLink uses the current Windows app profile. The local operator approves each live send.

## Set up simulation

1. Open **HFLink** in the main toolbar, then choose **Settings → Station**. Enter a test callsign such as `M0ABC`, leave **Simulation (no RF)** checked and select **Save station**.
2. Enter a destination such as `G1XYZ` and up to 512 UTF-8 bytes of text. Select **Queue for approval**, then select the row.
3. Choose a simulation outcome and select **Approve and send**. **Simulate receipt** shows **Delivered to destination station** in a row marked **Simulation**. A missing receipt leaves the message unconfirmed. A failed or unconfirmed message can be queued again with **Retry**.
4. Select **Open receive window**, then **Back to radio** to see a desktop notification from the simulated sender. Click it to return to HFLink.

## Set up bundled Mercury

1. On your first HFLink visit with bundled Mercury selected, HFLink checks Windows Firewall and offers to fix it if needed. Accept the offer and approve the Windows administrator prompt, or do it later from **Settings → Firewall → Fix firewall**. This enables the Domain, Private and Public firewall profiles and blocks remote TCP access to bundled Mercury. Managed policy may prevent the rule from applying. HFLink checks the effective rule before Mercury starts and while it runs.
2. Go to **Settings → Station**, enter your own transmitting callsign, keep **Use bundled Mercury** selected, clear **Simulation (no RF)** and select **Save station**. Bundled Mercury is the default for new profiles and uses `127.0.0.1` on ports `8300` and `8301`. Close any standalone Mercury instance using those ports.
3. Go to **Settings → Audio and PTT**, choose the radio’s receive and transmit audio devices and set your PTT method. For serial RTS, choose **serial**, your COM port and **rts**. Select **Save settings**. Set frequency and sideband on the transceiver. [Mercury INI reference](https://github.com/Rhizomatica/mercury/blob/v1.9.25/mercury.ini.example)
4. Close settings and select **Check modem** to start bundled Mercury and check its TCP ports.
5. To exchange a message, use two attended stations with compatible Mercury versions. Agree on a clear frequency and the same sideband. The destination operator selects **Open receive window**. The source operator queues the message, selects it and chooses **Approve and send**. Mercury can transmit its own ARQ control frames during an open receive window.

### Set ARQ bandwidth

In **HFLink → Settings → Station**, set **ARQ bandwidth** to **500 Hz (narrow)**, **2300 Hz** or **2750 Hz**, then choose **Save station**. **Use modem setting** leaves the modem’s current bandwidth in effect. HFLink applies an explicit selection before calling or opening a receive window, with bundled or external Mercury and either message format.

The setting is a maximum. Mercury negotiates the lower bandwidth offered by the stations, so one station selecting 500 Hz is enough. The **Session status** panel shows the negotiated width once connected. [Mercury bandwidth controls](https://github.com/Rhizomatica/mercury/blob/v1.9.25/docs/TNC.md#bw-bandwidth)

## Chat with standalone Mercury

1. Use compatible Mercury modem versions. MeshCore Command bundles Mercury 1.9.25. Agree on a clear frequency and the same sideband. Your friend opens **Mercury Client**, enters their station callsign, selects **Connect modem**, and leaves the window open to accept your call. [Mercury Client implementation](https://github.com/Rhizomatica/mercury/blob/v1.9.25/gui_interface/mercury-client/client/client.go)
2. In HFLink, enter your friend’s callsign and set **Format** to **Mercury ARQ Chat**. Type one line of up to 1,024 UTF-8 bytes, choose **Queue for approval**, select the outgoing row, then choose **Approve and send**. HFLink remembers the format for that callsign as soon as you queue a message.
3. After your outgoing session ends, choose **Open receive window**. Your friend enters your callsign and chooses **Connect ARQ** in Mercury Client, then uses **Send message** for the reply and **Disconnect ARQ** to finish. Incoming chat lines are saved as **Mercury ARQ Chat** under their callsign. Select an incoming row and choose **Reply** to fill in the callsign and format.

**Sent to local modem** is the final status for a completed Mercury Chat transfer. A reply from your friend confirms they saw your message. Select **HFLink** as the format when chatting with an HFLink station and you want HFLink delivery receipts.

## Receive while using MeshCore

The **Session status** panel on the right shows the current step and the other station’s callsign. During live use, it shows modem-reported PTT, channel activity, SNR, estimated bitrate, connection bandwidth and queued transmit bytes when Mercury supplies them. **Sent to Mercury** means the local modem accepted the data. **Delivered to destination station** means HFLink received a matching receipt from the other station.

Select **Back to radio** after opening the receive window. The window stays open for the configured timeout or until the receive session ends. MeshCore Command shows a desktop notification for a new incoming message while you are on the radio page or the app is minimized. Click the notification to return to HFLink. If the session is still open, you can select **Send receipt** or **Decline receipt** there. The message is saved as soon as it arrives.

Use **Stop operation** in HFLink to end the receive window early. Closing MeshCore Command also ends it. **Stop Mercury** is available when the bundled modem is running and idle.

[HFLink screenshots](SCREENSHOTS.md#hflink)

## Receive waterfall

The waterfall opens by default in HFLink. Frequency labels are audio offsets from 0 to 4 kHz. The display shows signals within your radio’s tuned passband. In **Settings → Audio and PTT**, select the Windows receive input wired to the radio and save it. For the FT-450, use the same USB audio input that receives in JS8Call.

The waterfall uses shared-mode Windows audio alongside Mercury. You can view it before opening a receive window. **Hide waterfall** and **Back to radio** release the audio capture. In simulation mode the display uses generated signals.

## Mercury version and updates

HFLink displays the installed Mercury version beside the station mode. When you open HFLink with bundled Mercury selected, it checks Rhizomatica’s latest stable Windows x64 release and asks before installing a newer version. You can also select **HFLink → Settings → Mercury updates → Check for Mercury updates** to check and install on demand.

HFLink verifies the archive’s SHA-256 checksum and runs a version check before selecting the new modem. The update keeps your audio, PTT and station settings. If Mercury is running idle, HFLink stops it once the download and checks pass. The next modem operation uses the new version. External Mercury installations have their own update process. [Rhizomatica Mercury releases](https://github.com/Rhizomatica/mercury/releases)

### Existing external Mercury setups

Existing external configurations are preserved. Clear **Use bundled Mercury** in **Settings → Station** and save the external modem’s host and ports. If Mercury runs on another PC, configure its modem and firewall there. The integrated firewall rule applies only to bundled Mercury. [Mercury TCP interface](https://github.com/Rhizomatica/mercury/blob/mercuryv2/docs/TNC.md)

## Delivery and storage

To remove a received or outgoing message, select its row, choose **Delete message**, and confirm. This removes it from HFLink on this PC. Deletion is available when the modem operation has ended.

**Sent to local modem** records that the data socket accepted a write. **Delivered to destination station** requires an HFLink receipt for the current message and attempt, from the destination callsign on the corresponding ARQ session. Callsign and receipt checks correlate the exchange. They provide no cryptographic authentication. A session interrupted after the local write remains unconfirmed.

The HFLink format uses bounded, newline-delimited JSON over Mercury’s raw TCP data socket. Mercury ARQ Chat sends plain UTF-8 lines on the same socket. It accepts LF, CR and CRLF line endings, and saves a final unterminated chat line when the ARQ session closes. Message text is stored in `hflink-native.sqlite3` in the active Windows profile’s data directory. The store also holds station settings, contact formats and audit events. History from earlier releases stays available. Legacy account records are unused. The audit contains metadata and excludes message bodies. The encrypted full app backup includes HFLink history, station settings, contact formats and saved Mercury configuration. Restore on the destination PC, then check that the audio input, output and PTT port match its hardware.

The native Windows app runs on .NET 10 with a WinForms host, WebView2 and a loopback bridge for the shared MeshCore interface. MeshCore boards use TCP, USB serial or BLE, with UDP discovery for Wi-Fi. Existing board settings use local profiles, Windows credential storage and WebView2 data. SQLite already supports offline maps. HFLink uses an independent SQLite file and asynchronous modem operations. The native app also integrates GPS, firmware flashing, notifications, offline maps, elevation and release updates.

The modem interface supports replacing Mercury. Routing, relays, unattended operation, automatic retries, Modem73 and a MeshCore-to-HF bridge are future work. Opening the app never starts a modem session. HFLink has no path to forward encrypted MeshCore traffic.

## Verification

Run the no-RF SQLite, simulation and Mercury TCP loopback checks from the repository root:

```powershell
dotnet run --project "MeshCore Windows/MeshCore.Windows.Core.Tests"
```

The loopback checks exercise Mercury’s TCP command and data protocol against a fake TNC. A real Mercury and two-radio HF test is still needed.

## Bundled component

The Windows build stages the standalone Rhizomatica Mercury 1.9.25 runtime, pins and verifies the upstream Windows archive and runtime binaries, and includes the upstream Mercury and FreeDV licence files. The [Windows release](https://github.com/Nortag82/MeshCore-Command/releases/tag/v2.0.56) includes a source and notices archive for the bundled modem and its dependencies. [Mercury release and source](https://github.com/Rhizomatica/mercury/releases/tag/v1.9.25) · [Mercury source at the pinned commit](https://github.com/Rhizomatica/mercury/tree/33efac4e3089f64f493fde1232b8e96305d15185)

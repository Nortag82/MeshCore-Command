# HFLink on native Windows

Experimental HFLink sends openly readable direct messages between two attended stations. Operators are responsible for station identification, frequency, content and their applicable licence conditions. HFLink uses the current Windows app profile. The local operator approves each live send.

## Set up simulation

1. Open **HFLink** in the main toolbar. Open **Station settings**, enter a test callsign such as `M0ABC` and leave **Simulation (no RF)** checked.
2. Enter a destination such as `G1XYZ` and up to 512 UTF-8 bytes of text. Select **Queue for approval**, then select the row.
3. Choose a simulation outcome and select **Approve and send**. **Simulate receipt** shows **Delivered to destination station** in a row marked **Simulation**. A missing receipt leaves the message unconfirmed. A failed or unconfirmed message can be queued again with **Retry**.
4. Use **Open receive window** for a simulated inbound message. **Back to radio** returns to the radio page and stops any active HFLink operation.

## Set up bundled Mercury

1. In **Station settings**, enter your own transmitting callsign, select **Use bundled Mercury**, and clear **Simulation (no RF)**. Bundled Mercury uses `127.0.0.1` with ports `8300` and `8301`. Close any standalone Mercury instance using those ports.
2. In **Mercury settings**, choose the receive and transmit audio devices for your radio and set your PTT method. For a serial RTS interface, choose **serial**, your COM port and **rts**. Set frequency and sideband on the transceiver. [Mercury INI reference](https://github.com/Rhizomatica/mercury/blob/v1.9.25/mercury.ini.example)
3. Select **Configure firewall** and approve the Windows administrator prompt. This turns on Windows Firewall on all three Windows network profiles and creates an inbound block rule for non-loopback TCP connections to the bundled modem. It creates no inbound allow rule. Managed policy may prevent the rule from applying. HFLink checks effective protection before starting Mercury, and checks it again while Mercury runs.
4. Select **Check modem**. HFLink starts Mercury and checks the two local TCP ports. **Stop Mercury** closes it. Returning to the radio page or closing MeshCore Command also stops it.
5. To exchange a message, use two attended stations with compatible Mercury versions. Agree on a clear frequency and the same sideband. The destination operator selects **Open receive window**. The source operator queues the message, selects it, and chooses **Approve and send**. Mercury can transmit its own ARQ control frames during an open receive window. The destination operator can approve the delivery receipt during the session.

### Existing external Mercury setups

Existing external configurations are preserved. Clear **Use bundled Mercury** in **Station settings** to enter the host and ports of an external modem. If Mercury runs on another PC, configure that modem and its firewall on that PC. The integrated firewall rule applies only to the bundled local Mercury executable. [Mercury TCP interface](https://github.com/Rhizomatica/mercury/blob/mercuryv2/docs/TNC.md)

## Delivery and storage

**Sent to local modem** records that the data socket accepted a write. **Delivered to destination station** requires an HFLink receipt for the current message and attempt, from the destination callsign on the corresponding ARQ session. Callsign and receipt checks correlate the exchange. They provide no cryptographic authentication. A session interrupted after the local write remains unconfirmed.

HFLink uses bounded, newline-delimited JSON over Mercury’s raw TCP data socket. Message text is stored in `hflink-native.sqlite3` in the active Windows profile’s data directory. The store also holds station settings and audit events. History from earlier releases stays available. Legacy account records are unused. The audit contains metadata and excludes message bodies. HFLink history and account data are separate from the MeshCore full-backup workflow and from the earlier Python prototype.

The native Windows app runs on .NET 10 with a WinForms host, WebView2 and a loopback bridge for the shared MeshCore interface. MeshCore boards use TCP, USB serial or BLE, with UDP discovery for Wi-Fi. Existing board settings use local profiles, Windows credential storage and WebView2 data. SQLite already supports offline maps. HFLink uses an independent SQLite file and asynchronous modem operations. The native app also integrates GPS, firmware flashing, notifications, offline maps, elevation and release updates.

The modem interface supports replacing Mercury. Routing, relays, unattended operation, automatic retries, Modem73 and a MeshCore-to-HF bridge are future work. Opening the app never starts a modem session. HFLink has no path to forward encrypted MeshCore traffic.

## Verification

Run the no-RF SQLite, simulation and Mercury TCP loopback checks from the repository root:

```powershell
dotnet run --project "MeshCore Windows/MeshCore.Windows.Core.Tests"
```

The loopback checks exercise Mercury’s TCP command and data protocol against a fake TNC. A real Mercury and two-radio HF test is still needed.

## Bundled component

The Windows build stages the standalone Rhizomatica Mercury 1.9.25 runtime, pins and verifies the upstream Windows archive and runtime binaries, and includes the upstream Mercury and FreeDV licence files. The [Windows release](https://github.com/Nortag82/MeshCore-Command/releases/tag/v2.0.47) includes a source and notices archive for the bundled modem and its dependencies. [Mercury release and source](https://github.com/Rhizomatica/mercury/releases/tag/v1.9.25) · [Mercury source at the pinned commit](https://github.com/Rhizomatica/mercury/tree/33efac4e3089f64f493fde1232b8e96305d15185)

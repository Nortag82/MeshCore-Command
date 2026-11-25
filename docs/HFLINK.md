# HFLink on native Windows

Experimental HFLink sends openly readable direct messages between two attended stations. Operators are responsible for station identification, frequency, content and their applicable licence conditions. A callsign identifies a station or requester in the app. The licence-check field records the administrator’s assessment. The app provides no licence verification.

## Set up simulation

1. Start a native MeshCore Command build that includes HFLink. Select **HFLink** in the Windows shell toolbar. A MeshCore board connection is optional.
2. Select **Sign in** and create a password for the local `admin` account. Use at least 12 characters.
3. Open **Station settings**, enter an example station callsign such as `M0ABC`, keep **Simulation (no RF)** checked and save. **Check modem** confirms that the simulator is ready.
4. In **Users and permissions**, create a requester with a callsign, a password, HFLink access and transmit-request permission. Create a second account with the **Designated operator** role and HFLink access. The operator can also request messages if granted that permission. To revoke access, select the account and clear **Approve HFLink access**.
5. Sign in as the requester. Enter a destination such as `G1XYZ` and up to 512 UTF-8 bytes of text. Choose **Queue for approval**. The request survives a restart. Select it and choose **Cancel request** to withdraw it.
6. Sign in as the operator. Select the message, select a simulation outcome, and choose **Approve and send**. Confirm the destination and text. **Simulate no receipt** ends as **Unconfirmed**. **Simulate receipt** shows **Delivered to destination station** in a row labelled **Simulation**. The timeout and disconnect outcomes exercise failure handling. Select **Retry** on a failed or unconfirmed message to request fresh approval.
7. Select **Open receive window** to simulate an inbound message and a duplicate. The first copy appears in history, with an option to approve a receipt. The duplicate is suppressed. Filter history by a callsign or select **Audit log** to see approval and transmission events.

## Connect Mercury

1. Use two stations with native builds that include HFLink and separate Mercury v2 instances. Configure the audio devices, RF frequency and PTT in Mercury and the radio. Choose a Mercury-managed PTT backend. HFLink leaves frequency, audio levels, bandwidth selection and PTT configuration to the operator.
2. Reserve Mercury’s ARQ TCP ports for HFLink during the session. The defaults are control `8300` and data `8301`. Set a private host reachable from Windows, preferably `127.0.0.1` when Mercury runs locally. Protect the modem ports from untrusted clients. [Mercury TNC command reference](https://github.com/Rhizomatica/mercury/blob/mercuryv2/docs/TNC.md)
3. On both stations, sign in as the HFLink administrator. In **Station settings**, enter the transmitting station’s callsign, the host and both ports. Choose a session timeout, clear **Simulation (no RF)** and save. The isolated Windows development preview requires `--enable-experimental-radio` to expose live mode.
4. Select **Check modem**. HFLink opens both TCP sockets, configures the station callsign, checks a control response and releases the sockets. It attempts one reconnect if TCP setup fails. Session failures after a call starts require a fresh operator approval to try again.
5. Keep operators present on both ends. On the destination station, sign in as the operator and select **Open receive window**. Mercury can transmit its ARQ responses during this explicit, time-limited window. The operator can stop it early.
6. Queue the message on the originating station. Its operator selects it and chooses **Approve and send**. The destination operator sees the saved message and can authorize the HFLink receipt during the same session. A declined or missing receipt leaves delivery unconfirmed. If the window expires, establish a new attended session for a retry.

## Delivery and storage

**Sent to local modem** records that the data socket accepted a write. **Delivered to destination station** requires an HFLink receipt for the current message and attempt, from the destination callsign on the corresponding ARQ session. Callsign and receipt checks correlate the exchange. They provide no cryptographic authentication. A session interrupted after the local write remains unconfirmed.

HFLink uses bounded, newline-delimited JSON over Mercury’s raw TCP data socket. Message text is stored in `hflink-native.sqlite3` in the active Windows profile’s data directory. The store also holds local password hashes, permissions, station settings and audit events. The audit contains metadata and excludes message bodies. HFLink history and account data are separate from the MeshCore full-backup workflow and from the earlier Python prototype.

The native Windows app runs on .NET 10 with a WinForms host, WebView2 and a loopback bridge for the shared MeshCore interface. MeshCore boards use TCP, USB serial or BLE, with UDP discovery for Wi-Fi. Existing board settings use local profiles, Windows credential storage and WebView2 data. SQLite already supports offline maps. HFLink uses an independent SQLite file and asynchronous modem operations. The native app also integrates GPS, firmware flashing, notifications, offline maps, elevation and release updates.

The modem interface supports replacing Mercury. Routing, relays, unattended operation, automatic retries, Modem73 and a MeshCore-to-HF bridge are future work. Opening the app never starts a modem session. HFLink has no path to forward encrypted MeshCore traffic.

## Verification

Run the no-RF SQLite, permissions, simulation and Mercury TCP loopback checks from the repository root:

```powershell
dotnet run --project "MeshCore Windows/MeshCore.Windows.Core.Tests"
```

The loopback checks exercise Mercury’s TCP command and data protocol against a fake TNC. A real Mercury and two-radio HF test is still needed.

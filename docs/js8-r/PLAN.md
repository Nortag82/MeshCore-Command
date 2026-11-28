# JS8-R implementation plan

| Phase | Work | Exit evidence |
| --- | --- | --- |
| 1. Headless PHY | Build a Qt Core console worker from pinned source. Encode and decode WAV files. Add a bounded local API and a Command client. Follow with receive audio, spectrum events and supervised lifecycle. | Upstream sample decoding, generated-WAV loopback, version and control checks, and clean exit. Before leaving the phase, verify live receive and process recovery with transmit disabled. |
| 2. Legacy communications | Reuse Varicode for directed text, CQ and heartbeats. Connect the native event stream to the existing HFLink views and storage. Add exclusive radio ownership, the FT-450 CAT adapter and an explicit TX-enable control. | Interoperability against stock JS8Call in offline fixtures, followed by controlled radio tests. |
| 3. Reliability | Define wire framing from measured usable payload. Implement durable fragments, negotiated versions, selective repeat and end-to-end CRC32C. | Missing, duplicate, corrupt and out-of-order fragments, lost acknowledgements, and restart recovery at both ends. |
| 4. Adaptive speed | Exchange link observations and agree on a speed change before use. Back off to the rendezvous speed after timeout. | Compare delivery probability and airtime under repeatable fading and asymmetric channels. |
| 5. Frequencies | Add validated channel plans, receive scanning, LQA and agreed rendezvous windows. Add opt-in sounding after policy review. | Repeatable failover with a defined rendezvous after a lost link. |
| 6. HF gateways | Add a shared transport contract, approved plaintext payloads, durable queues, priorities, routing and hop limits. | Replay, loops and interrupted routes covered in offline gateway tests. |
| 7. HF Lab | Add seeded channel impairments, controlled noise bandwidth, repeat runs, metrics and CSV export. | Reproduction instructions, comparable runs and separate measured and estimated results. |

Keep later phases behind their exit criteria. Validate the licensed operating policy, watchdog and radio shutdown before any automatic transmissions. Stock JS8Call and the Mercury source remain reference points while the native path is validated.

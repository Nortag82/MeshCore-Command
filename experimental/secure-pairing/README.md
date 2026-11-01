# Secure pairing research prototype — not in the app

This directory contains isolated experimental primitives only. They are not imported by MeshCore Command and must not be used to protect real messages or situation data. They do not implement the requested feature.

The prototype explores signed public invitations, pairing key agreement, signed encrypted group envelopes, bounded fragmentation and a serialized replay window. The code has not received an independent security review.

Missing: invitation export/import UI, on-mesh pairing exchange, mutual approval and persistent protected identities; private channel provisioning, group creation and key distribution/rotation; durable sender counter and replay storage integration; group chat and map integration, acknowledgements, rate limiting, migration, interoperability and hardware tests. The existing application still uses Public-channel discovery and its existing pairwise Secure Coms.

Run the isolated tests using Node.js with WebCrypto: `node test-crypto.mjs`, `node test-framing.mjs`, `node test-receiver.mjs`.

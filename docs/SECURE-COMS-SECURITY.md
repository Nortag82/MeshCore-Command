# Secure Coms and Secure Situation Map security

Secure Coms is an experimental Command-to-Command layer for short messages and shared Situation Map data. It adds application-level encryption **inside** MeshCore direct messages; it does not replace MeshCore's existing encryption. It is not the encryption used by ordinary Command channel or direct conversations.

## What happens when you send

1. Each pair of people sets up a randomly generated shared secret in compatible Command apps, exchanges the secret privately outside the mesh, and compares the displayed fingerprint through a trusted channel.
2. The sending app derives an AES-256 key for each message using HKDF-SHA-256 and a random 12-byte nonce. AES-256-GCM encrypts the content and adds a 128-bit authentication tag. The sender and recipient radio identities are bound into the encryption context.
3. Command encodes the encrypted payload as an `MC1.` message and sends it as a MeshCore direct message. The companion radio applies MeshCore's own direct-message transport protection. At the other end, the receiving radio passes the inner payload to Command, which authenticates and decrypts it.

Secure Situation Map markers, areas, waypoints and opt-in GPS updates sent to contacts use this same Secure Coms mechanism. Local recipient groups are lists on the sender's device: Command sends a separately encrypted direct message to each member using that member's existing verified secret. There is no additional group key. [MeshCore's packet and payload documentation](https://github.com/meshcore-dev/MeshCore/blob/main/docs/payloads.md) describes the underlying mesh transport.

## Benefits

- **An extra confidentiality boundary:** a party that can inspect or decrypt the MeshCore transport still needs the Command shared secret to read the inner secure content. The secret is used by the Command apps, rather than being sent as part of the radio message.
- **Integrity checking:** GCM authentication rejects modified content or content that cannot be authenticated with the shared secret and expected identities. Both participants hold the same secret, so this is not a digital signature or proof to a third party of which person created a message.
- **Per-contact sharing:** a map update or group message is sent separately to each selected verified contact. An app-level map confirmation indicates that the receiving Command app reported saving the update; a radio acknowledgement alone does not prove decryption or reading.
- **Replay checks:** the app records received nonces and rejects repeats it recognises. This protection depends on retaining the current local replay history.

## Tradeoffs and limits

- **No forward secrecy.** A fresh per-message key does not provide forward secrecy when it is derived from a long-term shared secret. If that secret is later compromised, recorded secure traffic using it may be decryptable. Exchange a new secret if compromise is suspected.
- **Metadata remains.** Encryption does not hide all mesh headers, routing, timing, traffic volume or relationships that can be inferred from traffic. Ordinary radio adverts may reveal a station name or configured location. Command discovery uses the Public channel and is not authenticated; verify fingerprints independently.
- **Endpoint and local storage matter.** A compromised PC or phone can expose secrets or plaintext. On Windows the installed candidate uses Windows Credential Manager for saved secrets; the temporary preview uses session-only credentials. Android uses encrypted app storage backed by Android Keystore. Local map objects and other settings may be stored on the device, so do not assume that every local file is encrypted. Protect devices, backups and any exported secrets.
- **Short messages and extra airtime.** Encryption and encoding add overhead, leaving up to 74 UTF-8 bytes per secure text message. Some characters use more than one byte. Group sends and multi-part map updates create multiple transmissions, consume airtime and may take longer or fail on congested links.
- **Recovery has limits.** Restoring older secure data may restore an older replay history and cannot recover messages for which a suitable secret was never saved. After a restore, compare fingerprints and exchange a new secret where replay protection matters. Old and native Windows profiles are separate and are not automatically merged.
- **Compatibility and delivery.** Both ends need compatible Command versions and a correctly paired secret. Other MeshCore apps may show the `MC1.` ciphertext as an ordinary message instead of decrypting it. Confirmations can be lost, and a map update marked unconfirmed may still have arrived.
- **Not independently audited.** AES-GCM is a standard authenticated-encryption algorithm, but this application protocol and implementation have not had an independent security audit. It is not a substitute for a threat assessment or a guarantee of safety.

The implementation is in [`secure-coms.js`](../NeonPocket/examples/companion_radio/webui/src/secure-coms.js); map sharing uses [`situation-map.js`](../NeonPocket/examples/companion_radio/webui/src/situation-map.js). The [native quick start](NATIVE-TEST-QUICKSTART.md) covers setup and profile recovery.

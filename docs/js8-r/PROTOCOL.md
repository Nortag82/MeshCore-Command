# JS8-R protocol draft

## Physical layer budget

The pinned encoder accepts 12 symbols from a 64-symbol alphabet. That is 72 data bits, or 9 octets when used as raw binary. The PHY adds three frame-type bits and a 12-bit PHY CRC to make 87 input bits for the LDPC encoder. The waveform uses 79 eight-tone symbols including Costas sync. In the legacy compressed-data format with its two-bit prefix, at least one further bit terminates the compressed content, leaving at most 69 compressed data bits. In an unprefixed compressed format the equivalent ceiling is 71 bits. Text capacity depends on the dictionary and directed headers. Measure representative directed messages before finalising an enhanced carrier. [Varicode frame packing](https://github.com/JS8Call-improved/JS8Call-improved/blob/v3.0.3/JS8_Main/Varicode.cpp)

| Mode | Slot seconds | Useful symbol duration, seconds | Nominal eight-tone bandwidth, Hz |
| --- | ---: | ---: | ---: |
| Slow | 30 | 25.28 | 25 |
| Normal | 15 | 12.64 | 50 |
| Fast | 10 | 7.90 | 80 |
| Turbo | 6 | 3.95 | 160 |
| Ultra | 4 | 2.528 | 250 |

These are calculations from the pinned [encoder](https://github.com/JS8Call-improved/JS8Call-improved/blob/v3.0.3/JS8_Mode/JS8.cpp) and [timing constants](https://github.com/JS8Call-improved/JS8Call-improved/blob/v3.0.3/JS8_Include/commons.h). Measure occupied bandwidth and sensitivity before making performance claims.

## Enhanced transfer semantics

Establish capability and a mutually supported version over a legacy-compatible rendezvous before sending enhanced frames. Bind each transfer to source, destination, session and message ID. Start with a manifest containing fragment count, exact length and CRC32C. Send indexed fragments with integrity checks. Return cumulative receipt bitmaps by window. The sender retries missing fragments only. Persist partial state and the receiver's delivery receipt before acknowledging a complete transfer. Return that receipt again on retransmission, with no duplicate application delivery. A sender enters Delivered only after matching the final receipt to the transfer and its checksum. CRC32C detects accidental corruption and provides no authentication.

The wire encoding, frame assignments, bitmap width, timer defaults and namespace remain open until test vectors and the legacy carrier budget are available. Use publicly documented encoding and plaintext. JSON is for local IPC only.

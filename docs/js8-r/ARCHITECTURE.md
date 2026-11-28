# JS8-R architecture

## Repository assessment

MeshCore Command for Windows is a C#/.NET application. WinForms hosts the native HFLink controls and WebView2 hosts the shared JavaScript interface. `MeshCore.Windows.Core/HfLink.cs` stores messages, drafts, band activity and station history in SQLite. `HfLinkBackup.cs` includes that data in the full backup. The native app already has a NAudio receive waterfall, audio endpoint selection, station maps, and a modem lifecycle in `Js8ManagedModem.cs`. That lifecycle drives a separate JS8Call desktop installation through its local JSON API. The Mercury adapters and process management remain in the source tree and are candidates for a later transport adapter. MeshCore board access uses the native session and board endpoints. The legacy Python desktop, Android application and firmware are outside the first phase.

## Upstream

The original JS8Call repository directs future development to JS8Call-improved. Use JS8Call-improved v3.0.3, commit `4c592bd9a034f18178a3e7179db92acb14939668`, as the initial compatibility baseline. Its C++20 implementation puts frame packing and text dictionaries in `JS8_Main/Varicode.cpp` and `JS8_JSC`. `JS8_Mode/JS8.cpp` implements the PHY encoder, Costas acquisition, CRC-12, LDPC and decoder. `FrequencyTracker.cpp` handles drift tracking. `commons.h` and `JS8Submode.cpp` define timing. `Modulator.cpp`, `Detector.cpp` and `JS8_Audio` connect the waveform to audio. The existing decoder uses Qt Core, FFTW, Boost and bundled Eigen. The full application also uses Qt Widgets, multimedia and Hamlib. Links [original](https://github.com/js8call/js8call), [pinned source](https://github.com/JS8Call-improved/JS8Call-improved/tree/v3.0.3).

Subspace Edition is an active GPL-3.0 derivative. Its build uses CMake, Boost, FFTW, Hamlib and Qt. At the reviewed commit, `ChunkedArq` describes stop-and-wait chunks with CRC-16 and ACK/NACK sequence numbers carried in legacy directed frames. It includes version-gated extensions and native ARQ tests. Use those tests for comparison when designing a receive-window bitmap and end-to-end CRC32C. [Subspace Edition source reviewed](https://github.com/avantol/Subspace-Edition/tree/82d1ac9e1173c66835809d0810e129fc5ff379db)

## Process boundary

Build a headless C++ process around the pinned PHY. Command keeps message storage, UI, configuration and operator policy. A local versioned control protocol connects them. The worker accepts file audio and emits decoded frames. Command can capture Windows audio in shared mode, resample it to mono 12 kHz and submit complete UTC-aligned slots to that worker. The parent owns the process, enforces request timeouts, reports failures and retries the current slot once with a new worker. Live transmit operation will require a single radio owner, transmit disabled at startup, bounded transmit leases and an emergency stop. Keep legacy JS8 and enhanced JS8-R capabilities explicit in the transport contract.

## Distribution and operating review

The two upstream projects and Subspace Edition identify GPL-3.0. The new C++ worker linked with the upstream PHY is distributed only after corresponding-source, attribution and runtime dependency obligations have been reviewed. Review Qt, FFTW, Boost and Eigen redistribution terms and the combined product separately. A process boundary alone does not settle the licence analysis. The initial source build uses an external pinned checkout and the local worker stays out of the Windows installer.

Before enabling automatic transmission, validate occupied bandwidth against the operator's approved channels and review the current licence and band plan. Ofcom's guidance lists 25 W PEP for Foundation on applicable bands and refers to Schedule 1 for exceptions. Review unattended operation separately. The offline build has no default transmit channels. [Ofcom guidance](https://www.ofcom.org.uk/spectrum/radio-equipment/amateur-radio-info) · [RSGB band plans](https://rsgb.org/main/operating/band-plans/)

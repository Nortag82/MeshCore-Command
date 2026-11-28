# Headless JS8 PHY development build

The worker runs the pinned JS8Call PHY as a console process. It reads and generates 12 kHz mono PCM16 WAV files. It has no audio-device, CAT or PTT control. See [the architecture](../../docs/js8-r/ARCHITECTURE.md) for the planned live modem.

The C++ wrapper is licensed under GPL-3.0-or-later. [GPL licence text](COPYING)

## Build on Windows

Use CMake, Ninja, a C++20 compiler, Qt 6 Core, Boost headers and the single-precision FFTW library. The commands below use an MSYS2 UCRT64 toolchain with those dependencies installed. Put matching Qt and FFTW runtime DLLs on the development process's PATH.

```powershell
git clone --depth 1 --branch v3.0.3 https://github.com/JS8Call-improved/JS8Call-improved.git "$env:TEMP/js8call-improved-3.0.3"
$env:PATH = "C:\msys64\ucrt64\bin;$env:PATH"
cmake -S './MeshCore Windows/Js8Native' -B "$env:TEMP/mcc-js8-phy" -G Ninja -DCMAKE_BUILD_TYPE=Release '-DCMAKE_CXX_COMPILER=C:/msys64/ucrt64/bin/g++.exe' '-DCMAKE_PREFIX_PATH=C:/msys64/ucrt64' "-DJS8_SOURCE=$env:TEMP/js8call-improved-3.0.3"
cmake --build "$env:TEMP/mcc-js8-phy"
$env:MCC_JS8_PHY = "$env:TEMP/mcc-js8-phy/meshcore-js8-phy.exe"
$env:MCC_JS8_UPSTREAM_FIXTURE = "$env:TEMP/js8call-improved-3.0.3/media/tests/A_1_4.wav"
dotnet run --project './MeshCore Windows/MeshCore.Windows.Core.Tests' -c Release
```

In the same shell, run `& './MeshCore Windows/Build-Preview.ps1'` with the usual [Windows build prerequisites](../README.md). Start `MeshCore Windows/artifacts/preview-publish/MeshCore.Windows.Preview.exe` from that shell. **HFLink → HF Lab** opens the receiver. Choose a mode and decode an aligned WAV containing one slot or less, or choose **Start receive** to use the radio input selected in HFLink Settings. The preferred offset helps the decoder prioritise candidates. HF Lab is receive-only.

Live capture uses Windows shared audio, converts it to mono at 12 kHz, and waits for a complete slot aligned to the PC's UTC clock. It then passes a temporary WAV to the worker. The input meter shows digital audio level in dBFS. The receiver retries the current slot once if the worker exits or stops responding. It keeps up to two pending slots and drops the oldest pending slot if decoding falls behind. **Stop receive** closes the capture and worker.

## Local IPC

Send one UTF-8 JSON object per line on stdin. Read one reply per line on stdout. The client sends one request at a time and closes stdin to shut down. A response echoes `protocol` and `id` and includes either `result` or `error`.

| Operation | Fields | Result |
| --- | --- | --- |
| `status` | `protocol` (1), `id`, `operation` | Pinned upstream commit, sample rate and file-only capability |
| `encodeWav` | Common fields plus `path`, `mode`, `offsetHz`, `frame`, `type` | Writes a new WAV with one slot and returns an empty result |
| `decodeWav` | Common fields plus `path`, `mode`, `offsetHz` | Decoded raw frames with frame type, offset, SNR and timing offset |

Modes are Normal (0), Fast (1), Turbo (2), Slow (4) and Ultra (8). The raw frame is exactly 12 characters from `0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-+`. The frame type is 0–7. This API operates below legacy Varicode and the future JS8-R application protocol.

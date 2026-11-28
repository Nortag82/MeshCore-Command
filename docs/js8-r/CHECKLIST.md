# JS8-R development checklist

- [x] Assess existing HFLink, SQLite, Mercury, radio and audio code.
- [x] Review the upstream PHY and compare candidate derivative components.
- [x] Record the architecture, phased plan and draft protocol.
- [x] Build and exercise the native offline PHY worker.
- [x] Validate the Command client against the worker.
- [x] Wire Windows receive audio to UTC-aligned slots and add worker recovery.
- [ ] Validate receive audio on a physical radio and add waterfall events from the native receive stream.
- [ ] Finish legacy Varicode interoperability and verify transmit safeguards.
- [ ] Implement and test persistent selective-repeat delivery.
- [ ] Validate speed negotiation, channel plans, gateways and HF Lab.
- [x] Stage the matching runtime DLLs, notices and corresponding sources for the Windows receive preview.
- [ ] Review distribution for the eventual general release.
- [ ] Validate the FT-450, RF timing, de-keying and controlled over-the-air interoperability.

## Offline results

- The client encoded one raw frame to PCM WAV and recovered it with matching type and audio offset in Slow, Normal, Fast, Turbo and Ultra.
- The pinned upstream Normal-mode recording `media/tests/A_1_4.wav` yielded four frames through the headless worker.
- The same recording displayed decoded legacy heartbeat text through upstream Varicode.
- The Windows development build passed its integration checks with HF Lab enabled.
- The slot assembler discarded an interrupted slot and completed a UTC-aligned slot across a jittered callback.
- The receive decoder recovered a test frame from an assembled slot, then recovered the same frame after a forced worker exit with a new process.
- These tests used file audio and establish basic compatibility. They provide no sensitivity, interference or delivery-rate measurements.

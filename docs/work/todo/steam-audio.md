# Steam Audio Integration — Remaining Validation

The design and architecture notes for this work now live in [docs/features/steam-audio.md](../../features/steam-audio.md).

## Runtime Blocker Status

The playback-path blocker from the original migration is closed.

- The V2 source path now routes uploaded PCM through `EffectsProcessor.ProcessBuffer(...)` across the entire buffer instead of only the first internal Steam Audio frame.

The remaining work here is validation, not missing runtime plumbing.

## Remaining Validation Tests

- [ ] Run the editor and verify the V2 OpenAL path preserves existing audio playback behavior.
- [ ] Verify OpenAL transport + Steam Audio effects in the `AudioTesting` world by playing a mono source and confirming HRTF spatialization.
- [ ] Verify Steam Audio occlusion by placing geometry between listener and source and confirming audible attenuation/occlusion.

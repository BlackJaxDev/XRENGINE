# Native Audio Project Split

The NAudio output, capture, and codec implementations now live in
`XREngine.Audio.NAudio`. Steam Audio bindings and acoustic scene components live
in `XREngine.Audio.SteamAudio`. Both preserve existing namespaces and public
type names. Desktop bootstrap registers these backends explicitly. `AudioData`
retains its serialized identity in Data and delegates WAV/MP3 import to the
registered desktop decoder. NVorbis remains available for managed Ogg decode.

The OpenAL transport, capture wrappers, EFX processor, and EFX effect pools
live in `XREngine.Audio.OpenAL`. The portable listener, source, and buffer
facades remain in `XREngine.Audio`; `IAudioListenerBackend` preserves legacy
source state, distance and cone controls, offsets, listener diagnostics, and
EFX routing without a native dependency. The default legacy behavior remains
selected, while both listener modes obtain their device from the explicit
backend registry. The OpenAL native gate covers context selection, operations,
and error checks across listeners. The OpenAL Soft package and native license
copy items now belong to the leaf with their original versions and output
paths.

## Validation

- `XREngine.Audio`, all three audio leaves, AudioIntegration, Editor, Server,
  and VRClient build with zero warnings.
- An isolated legacy OpenAL/EFX Audio Testing World reached Playing. The
  looping test source had one active listener, `State=Playing`, `Type=Static`,
  and `AnySourcePlaying=True` through MCP. Its audio log had no OpenAL or EFX
  errors. Session logs: `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260929-133540-native-split-audio-openal/logs/`.
- A separate V2 Steam Audio session reached Playing with the same source
  state. Logs confirmed `V2=True`, `Effects=SteamAudio`, processor
  initialization, committed acoustic geometry, and 36 probes registered.
  Its audio and general logs had no errors. Session logs:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260929-133943-native-split-audio-steam/logs/`.
- The ignored Audio Testing World settings were restored byte-for-byte after
  the Steam session. Neither world contained an active microphone component,
  so real microphone capture and voice networking remain unverified. The
  live Steam run confirms scene setup and playback state; it does not measure
  processed spatial output samples.
- Existing audio test fixtures now register backend leaves explicitly and
  reject silent Steam fallback. The targeted `XREngine.UnitTests.Audio` run
  passed all 191 tests, including legacy OpenAL source controls, EFX presence,
  NAudio transport, Steam processing, and Steam scene occlusion. After adding
  cross-listener buffer ownership coverage, the OpenAL regression fixture
  passed all 38 tests with no skips.

## Follow-up validation

A hardware microphone session is still needed to validate NAudio capture and
voice streaming. The isolated live scenes had no microphone component.

# Browser audio transport

`WebAudioTransport` implements the shared `IAudioTransport` contract. Sources,
PCM buffers, the listener, gain, spatialization, and static looping remain owned
by one browser audio context. Playback stays suspended until the existing
gesture-driven unlock succeeds.

Queued PCM uses the audio clock rather than frame timing or `ended` callbacks.
Each source retains at most 32 queued mono/stereo buffers with one sample rate
and channel count. The scheduler submits adjacent one-shot source nodes at
contiguous times, exposes processed buffers in order, and retains buffer
ownership until they are unqueued. Attached or queued buffers cannot be
overwritten or destroyed. Pause preserves the current buffer offset; rewind
resets processed state; stop marks queued buffers processed. Source/context
destruction disconnects scheduled nodes and releases the queue.

Polling and managed queue/unqueue marshalling use bounded reusable storage.
Buffer submission creates the one-shot nodes required by Web Audio. A changed
pitch or Doppler rate also reschedules the remaining queue, since source-node
start times are immutable; the cost of frequently changing streaming rates
still needs device profiling. Static-source looping is supported. Looping an
entire live stream is rejected by name; its producer must repeat the content
explicitly. Capture, output-device selection, and Steam Audio are not supplied
by this leaf.

The scheduling model follows the
[Web Audio source-node contract](https://www.w3.org/TR/webaudio/#AudioBufferSourceNode).
The browser smoke harness renders known queued samples through a real
`OfflineAudioContext` at rates 1 and 2. That check does not establish audible
device output, user-gesture activation, spatial quality, or the browser codec
matrix. Local deterministic scheduling/teardown probes and an actual .NET WASM
PCM/Int32-memory-view probe pass; the browser sample check awaits CI acceptance.

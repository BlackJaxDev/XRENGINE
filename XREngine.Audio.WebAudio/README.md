# Browser audio transport

`WebAudioTransport` implements `IAudioTransport` and the opt-in shared
`IAudioSpatialTransport` contract. The existing source/listener facades forward
authored relative positioning, direction/cones, distance models and rolloff,
gain bounds, Doppler settings and seek controls. Each listener owns one output
context; globally unique opaque handles reject cross-owner and retired handles.
Mono audio uses browser HRTF direction with explicitly calculated distance/cone
attenuation and velocity-dependent pitch. Stereo bypasses the panner to preserve
its authored channel image. Gain bounds apply before listener gain. Singular
nonpositive/infinite Doppler rates fail by name rather than being silently clamped.

Initial playback is deferred until a trusted page gesture authorizes output.
Page visibility/freeze/cache and drawable-surface blockers compose independently.
Suspension mutes output immediately and freezes the audio clock without retiring
source buffers or offsets. Resume reuses granted activation when the browser
permits it; the unlock button remains available otherwise. Open/close and blocker
generations reject stale resume completions. State changes update the player UI
without per-frame string polling.

`XRWorld.RequiresAudio` defaults to false. A required world retains a dedicated
activation context throughout the session, including intervals without an
authored listener. It gates fixed and variable simulation while allowing jobs,
input, visibility collection and presentation to continue. Optional suspended
audio never gates the game. Gate transitions discard simulation debt and reset
the frame clock without changing application pause state.

Queued PCM uses the audio clock rather than frame timing or `ended` callbacks.
Each source retains at most 32 queued mono/stereo buffers with one sample rate
and channel count and PCM sample width. The scheduler submits adjacent one-shot source nodes at
contiguous times, exposes processed buffers in order, and retains buffer
ownership until they are unqueued. Attached or queued buffers cannot be
overwritten or destroyed. Pause preserves the current buffer offset; rewind
resets processed state; stop marks queued buffers processed. Source/context
destruction disconnects scheduled nodes and releases the queue. Seek setters and
byte/seconds property queries are relative to the retained queue's beginning;
traversed entries become processed and paused seeks remain paused. The transport's
`GetSampleOffset` supplies current-buffer sample-frame precision separately.
Managed batch ownership and retirement complete before user notifications;
callback failures, source reuse and reentrant refill cannot lose admitted buffers.
Buffers still held by another queue or static source are retained.

Readiness polling, managed memory-view marshalling and queue bookkeeping use
reusable/bounded storage. The browser session coalesces ordinary pose updates to
one final evaluation per changed source per frame. Explicit Play or queue
admission flushes the affected source earlier, and later pose changes can require
another flush. Standalone transport setters retain immediate behavior outside
that synchronous frame scope.

Nonlooped buffer submission creates the one-shot nodes required by Web Audio. A
changed pitch or Doppler rate destroys and reschedules every remaining queued source
node because scheduled start times are immutable. Moving or accelerating sources
can change Doppler every frame, so this is a recurring bounded browser API
allocation, even after redundant pose rescheduling is coalesced. It is not a
zero-allocation audio or performance qualification; device profiling remains open.
Static and queued-source looping are supported. A queued loop copies the entire
retained queue into one native looping Web Audio buffer at loop enablement,
queue mutation or replay/seek after stop or rewind. Wrapping is independent of
frame callbacks, timers and page throttling. Playing, paused and initial loops
expose zero processed buffers; stop still marks the queue processed for unqueue,
and rewind restores its initial state.
Disabling looping continues the current traversal and then stops. Enqueued PCM
extends the next traversal while preserving the current queue-relative position.
Queue mutation and lifecycle restart may allocate and copy float PCM. Copies use
`copyFromChannel` rather than reading back acquired source buffers. Active,
replacement and not-yet-retired aggregates share an 8 MiB per-output budget;
admission rejects an oversized replacement before taking buffer ownership. A
playing-source change uses one native audio-clock handoff approximately two
render quanta ahead. Initial queued-loop Play uses the same two-quantum lead,
with its offset stationary until that native start time. Until a replacement
handoff, offset queries report the old playback
segment; the new segment takes over at the shared native start/stop timestamp.
A second incompatible enqueue, seek or loop toggle during that short handoff is
rejected by name. Pause cancels the handoff and freezes the current position
while retaining admitted PCM. Changed rates on a pending loop node are applied
at its start; a pending nonlooped queue is rebuilt at the new effective float32
rate. When disabling a loop too near its end for that handoff, the native node
instead stops at the current traversal end, without an extra traversal. Ended
callbacks only retire stopped nodes; they never schedule playback. Retired nodes
are bounded, and an aggregate stays in the output budget until every native
node holding it retires. Stop, rewind, detach and context close synchronously
release aggregate reservations and native nodes. The handoff lead does not
guarantee sample-contiguous switching if JavaScript itself stalls beyond that
lead. Enabling a loop within one render quantum of nonlooped queue exhaustion
can leave up to one quantum of silence while using a future switch timestamp;
a late native control message takes effect as soon as the browser applies it.
Capture, output-device selection, and Steam Audio are not supplied by this leaf.

The scheduling model follows the
[Web Audio source-node contract](https://www.w3.org/TR/webaudio/#AudioBufferSourceNode),
with authored spatial behavior following the
[OpenAL source model](https://www.openal.org/documentation/openal-1.1-specification.pdf).
The browser smoke harness renders known queued samples through a real
`OfflineAudioContext` at rates 1 and 2. That check does not establish audible
device output, user-gesture activation, spatial quality, or the browser codec
matrix. Local deterministic scheduling/teardown probes and an actual .NET WASM
PCM/Int32-memory-view probe pass. The real Chromium sample check passed at both
rates with maximum sample error zero in
[run 36933343571](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36933343571).

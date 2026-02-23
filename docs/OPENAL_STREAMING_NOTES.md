# OpenAL Streaming Audio — Lessons Learned

Hard-won knowledge from debugging HLS video/audio streaming in XREngine's
`UIVideoComponent` pipeline. These notes document OpenAL behaviour that is
**not obvious** from the specification and has caused real playback bugs.

---

## 1. STOPPED sources mark all queued buffers as "processed"

**Discovery**: Fix 4–6 (format-change stall → choppy cycling → natural underrun stall)

When an OpenAL source transitions to the **STOPPED** state — whether from an
explicit `alSourceStop()` call or from naturally running out of queued
buffers — *every* buffer currently on that source is immediately reported as
`AL_BUFFERS_PROCESSED`.

This means that any subsequent `alSourceQueueBuffers()` preceded by an
`alSourceUnqueueBuffers(processed)` pattern (which is the normal streaming
idiom) will **unqueue the buffer it just queued on the previous cycle**,
because `BuffersProcessed` always equals `BuffersQueued` on a stopped source.
The net effect is the source can never accumulate more than one buffer at a
time.

### Consequence for pre-buffer gates

If your playback restart logic waits for N pre-buffered buffers (e.g. 8)
before calling `Play()`, that gate will **never** be satisfied on a stopped
source because the queue depth can never exceed 1.

### Fix — `alSourceRewind()`

Calling `alSourceRewind()` (Silk.NET: `source.Rewind()`) transitions the
source from **STOPPED → INITIAL**. In the INITIAL state,
`AL_BUFFERS_PROCESSED` returns **0**, so newly queued buffers accumulate
normally and the pre-buffer gate can be satisfied.

`Rewind()` is a no-op on a source that is already in the INITIAL state, so
it is safe to call unconditionally on any stopped-and-empty source.

**Applied in**:
- `FlushAudioQueue()` — after `Stop()` + `UnqueueConsumedBuffers()`, call
  `Rewind()` to prepare for re-filling.
- Top of `DrainStreamingAudio()` — before Phase 1 filling, rewind any
  stopped source with `BuffersQueued == 0` to handle natural underruns.

---

## 2. `AutoPlayOnQueue` must be disabled for manual streaming

**Discovery**: Early pipeline setup

`AudioSource.QueueBuffers()` checks `AutoPlayOnQueue` and calls
`alSourcePlay()` automatically after every enqueue if the source is not
already playing. For streaming with a pre-buffer strategy this must be
disabled (`source.AutoPlayOnQueue = false`) so the caller controls exactly
when playback begins.

**Applied in**: `SuppressAutoPlayOnAudioSources()` sets
`AutoPlayOnQueue = false` on every active listener each drain cycle.

---

## 3. Format changes require a full flush

**Discovery**: Fix 4 (Twitch ad-to-stream transition, 44.1 kHz → 48 kHz)

OpenAL requires **all buffers queued on a single source to share the same
format** (sample rate, channel count, bit depth). HLS segment boundaries —
especially Twitch ad slates transitioning to the live stream — can change
the audio format mid-stream.

When a format change is detected:
1. `Stop()` — halt playback.
2. `UnqueueConsumedBuffers()` — drain all buffers from the source.
3. `Rewind()` — transition to INITIAL (see Note 1 above).
4. Reset all clock accounting (`_firstAudioPts`, `_processedSampleCount`,
   `_submittedSampleCounts`, etc.) so the hardware clock re-seeds from the
   next submitted frame.

**Applied in**: `FlushAudioQueue()` in `UIVideoComponent.Audio.cs`.

---

## 4. Hardware clock returns 0 when source is stopped

**Discovery**: Fix 4 (post-format-change permanent video stall)

The A/V sync master clock is derived from:

```
pts = _firstAudioPts
    + (_processedSampleCount + source.SampleOffset)
      * TicksPerSecond / sampleRate
    - HardwareAudioLatencyTicks
```

`source.SampleOffset` (`AL_SAMPLE_OFFSET`) returns **0** when the source is
in the STOPPED or INITIAL state. Combined with the `IsPlaying` guard in
`GetAudioClock()`, the clock reports 0 whenever audio is not actively
playing.

If the video gate requires a positive audio clock to release frames, a
permanently stopped source will **freeze video indefinitely**. This is why
the `allowUnsyncedFallback` path exists: after `RebufferThresholdTicks`
(750 ms) of stall with no queued audio, video falls back to wall-clock
pacing so frames keep flowing while audio recovers.

---

## 5. `BuffersProcessed` events fire during `UnqueueConsumedBuffers`

**Discovery**: Clock accounting investigation

The `BufferProcessed` event (used to advance `_processedSampleCount`) fires
synchronously inside `UnqueueConsumedBuffers()`, which is called:
- Explicitly at the top of each drain cycle (`audioSource.DequeueConsumedBuffers()`).
- Implicitly inside every `QueueBuffers()` call (it auto-unqueues processed
  buffers before queuing new ones).

This means `_processedSampleCount` can advance **during** the fill loop,
not just between drain cycles. The clock formula accounts for this because
`SampleOffset` resets when a buffer boundary is crossed, and the
`_processedSampleCount` increment compensates.

---

## 6. Pool exhaustion vs. queue capacity

**Discovery**: Backpressure tuning

`QueueBuffers()` returns `false` when `BuffersQueued >= maxBuffers`
(configured via `MaxStreamingBuffers`, currently 200). When this happens the
buffer passed in is returned to the listener's pool to avoid leaks.

This is the normal backpressure signal — the drain loop should stop
submitting for this cycle and retry next frame. It does **not** indicate an
error.

---

## 7. Clock must be reset on every underrun, not just format changes

**Discovery**: Fix 7 (audio/video desync after natural underruns)

When audio underruns naturally (source plays all buffers → STOPPED), the
Rewind() from Note 1 allows buffers to accumulate again. But the **clock
accounting** (`_processedSampleCount`, `_firstAudioPts`) still reflects the
position the source was at before the underrun.

During the underrun gap, video continues via wall-clock fallback (Note 4)
and advances past the audio timeline position. When audio restarts, the
clock resumes from its pre-underrun position (e.g. 4400 ms) while video is
at 9000+ ms. The sweep-present loop sees drift = +4600 ms > hold threshold
(40 ms) and **freezes video** until the audio clock catches up. This
manifests as choppy, desynced playback with periodic multi-second freezes.

### Fix — Reset clock on underrun recovery

Whenever the Rewind block fires on a source that had a previously seeded
clock (`_firstAudioPts != long.MinValue`), reset all clock accounting:

- `_firstAudioPts = long.MinValue`
- `_processedSampleCount = 0`
- `_submittedSampleCounts.Clear()`
- `_totalAudioDurationSubmittedTicks = 0`
- `_totalAudioBuffersSubmitted = 0`

The next call to `SubmitDecodedAudioFrame` re-seeds `_firstAudioPts` using
the existing fast-forward logic that aligns to `_lastPresentedVideoPts`.
This eliminates the accumulated drift from the wall-clock fallback gap.

**Applied in**: The Rewind block at the top of `DrainStreamingAudio()`,
guarded by `anyRewound && _firstAudioPts != long.MinValue`.

---

## 8. Pre-buffer depth must account for decoder deficit on live streams

**Discovery**: Fix 7 tuning (frequent underruns on Twitch live streams)

On live HLS streams (especially Twitch), the audio decoder can produce
data at a slight deficit vs. real-time consumption — for example,
~39.2 buffers/s submitted vs. ~41.6 buffers/s consumed (2.4 buf/s
shortfall). This is caused by network latency and segment-boundary delays.

With a pre-buffer of 500 ms (~23 AAC buffers at 48 kHz), the cushion
depletes in ~10 seconds at that deficit rate, causing repeated underruns.

### Tuning

`MinAudioQueuedDurationBeforePlayTicks` was increased from **500 ms** to
**1000 ms** to provide a deeper cushion before restarting playback after
an underrun. This roughly doubles the time before the queue runs dry.

The Phase 2 gate in `DrainStreamingAudio` requires **both**:
- `GetEstimatedQueuedAudioDurationTicks >= MinAudioQueuedDurationBeforePlayTicks`
- `queuedAudioBuffers >= MinAudioBuffersBeforePlay` (8)

---

## 9. EWMA drift tracking & adaptive frame dropping

**Discovery**: Fix 8 (video gradually falling behind audio over time)

The default `VideoDropThresholdTicks` of −150 ms means video can lag audio
by up to 149 ms without any corrective action. On streams where the decoder
produces frames at a slight deficit compared to real-time (e.g. 57 fps
output for a 60 fps stream due to network jitter), a slow cumulative lag
builds that never reaches the fixed threshold — video drifts behind audio
imperceptibly at first, then noticeably.

### Fix — EWMA + adaptive drop threshold

1. **Per-frame drift EWMA**: After each video frame presentation, compute
   `drift = presentedPts − audioClockTicks` and update an exponentially-
   weighted moving average (α = 0.1, ~10-frame window):
   ```
   ewma = α * drift + (1 − α) * ewma
   ```

2. **Adaptive threshold**: When `ewma < VideoCatchupDriftThresholdTicks`
   (−33 ms ≈ 2 frames at 60 fps), the drop threshold tightens from −150 ms
   to `ewma / 2`, floored at −16 ms (~1 frame at 60 fps). This aggressively
   drops frames that are even slightly behind until the EWMA returns to near
   zero.

3. **Brief drift monitor**: A compact `[AV Drift]` log line fires every 2 s
   (not 10 s) when EWMA or video debt exceeds ±5 ms, giving early warning
   of slow-growing drift.

4. **EWMA reset**: The EWMA is reset alongside clock accounting on
   underrun recovery, format-change flush, and extreme-drift reset, so
   stale drift history doesn't contaminate the new timeline.

**Constants**:
- `VideoCatchupDriftThresholdTicks` = −33 ms
- `DriftEwmaAlpha` = 0.1
- `DriftStatusIntervalTicks` = 2 s

**Fields**:
- `_driftEwmaTicks` — current EWMA in .NET ticks
- `_driftEwmaSeeded` — whether first measurement has been taken
- `_telemetryCatchupDrops` — frames dropped by the tighter threshold

**Applied in**: Phase 6a (adaptive threshold) and Phase 7 (EWMA update) in
`DrainStreamingFramesOnMainThread()`, with resets in `FlushAudioQueue()` and
the underrun-recovery Rewind block.

---

## 10. Display pipeline latency compensation

**Discovery**: Fix 9 (video perceptually behind audio despite drift=0ms)

After all drift and underrun fixes, telemetry showed `drift=0ms ewma=0ms
catchupDrops=0` — the audio clock and video PTS were mathematically
aligned. Yet the user perceived video as constantly behind audio.

**Root cause**: The A/V sync pipeline already subtracts
`HardwareAudioLatencyTicks` (50 ms) from the audio clock to account for
the delay between OpenAL accepting a sample and the speaker producing
sound. But there was **no equivalent compensation for video display
latency**: uploading a frame to the GPU texture on one render pass doesn't
put it on screen until the swap chain presents (double/triple buffering at
60 Hz → 33–50 ms). By the time the viewer sees the frame, the audio has
advanced past it.

### Fix — `VideoDisplayLatencyCompensationTicks`

A new constant (50 ms) is added to the raw `audioClockTicks` to produce
`videoClockTicks`. All video pacing decisions (TryDequeueVideoFrame,
hold/drop/reset drift checks, EWMA update) now use `videoClockTicks`
instead of `audioClockTicks`. This shifts the presentable window forward
so frames are uploaded ~50 ms earlier than the raw audio position — by the
time they reach the screen, the audio has caught up.

```
videoClockTicks = audioClockTicks + VideoDisplayLatencyCompensationTicks
```

Telemetry now reports both:
- `drift` — effective drift against the compensated video clock
- `rawDrift` — drift against the uncompensated audio clock

**Applied in**: Phase 4 of `DrainStreamingFramesOnMainThread()`.

---

## 11. Commercial-break resilience: deeper buffering & smooth video fallback

**Discovery**: Fix 10 (post-commercial desync + repeated underruns)

After all prior fixes, playback was perfectly synced during normal viewing.
When a Twitch commercial break triggered a format change (48 kHz → 44.1 kHz),
audio flushed and re-buffered cleanly through the commercial.  However, when
the commercial ended and format changed back (44.1 kHz → 48 kHz), the audio
queue drained faster than the decoder could refill it.  6 underruns occurred
in ~1 minute, each causing a ~1 s audio gap and clock reseed, creating
perceptible desync and audio stutter.

### Root cause — HLS segment-boundary delivery deficit

Twitch streams deliver audio in segment-sized bursts.  After a commercial
break the CDN may deliver segments at a slight deficit vs real-time
consumption (~83 % real-time was observed) while recovering from the segment
boundary.  With only 2 s of OpenAL buffer and 1 s pre-play threshold, the
buffer was too thin to absorb inter-segment download gaps, causing a rapid
underrun → re-buffer → underrun cycle.

Additionally, two secondary issues were identified:

1. **Spurious underrun on format-change flush**: `FlushAudioQueue()` stops
   the source, but `_wasAudioPlaying` remained True, so Phase 3 logged a
   Playing→Stopped transition as an underrun — inflating the count.

2. **Video freeze during re-buffer**: After a format change or underrun, the
   audio clock returned 0 (source not playing), closing the video gate.
   Video froze until the pre-buffer threshold was met and audio restarted.
   During a commercial transition this caused a visible video stutter.

### Fixes

| Change | Before | After |
|---|---|---|
| `TargetAudioQueuedDurationTicks` | 2 s | **5 s** |
| `MinAudioQueuedDurationBeforePlayTicks` | 1 s | **2 s** |
| `TargetOpenAlQueuedBuffers` | 40 | **100** |
| `TargetAudioSourceMaxStreamingBuffers` | 200 | **350** |
| Session `AudioQueueCapacity` | 96 | **250** |

- **`_wasAudioPlaying` cleared in `FlushAudioQueue()`** so a format-change
  stop is not counted as an underrun.
- **`_audioHasEverPlayed` flag** — set True when Phase 2 first starts
  playback, preserved across format changes and underrun recoveries, reset
  only on pipeline stop.
- **Video gate (Phase 5)** — when `_audioHasEverPlayed` is True but the
  audio clock is inactive (re-buffering), the video gate allows wall-clock
  fallback immediately instead of freezing.  On initial startup (audio never
  played yet) the conservative 750 ms stall gate remains.

**Applied in**: `UIVideoComponent.cs` (constants, field),
`UIVideoComponent.FrameDrain.cs` (Phase 2 & 5),
`UIVideoComponent.Audio.cs` (`FlushAudioQueue`),
`UIVideoComponent.Pipeline.cs` (session capacity, stop reset).

---

# A/V Synchronisation Architecture

This section documents how XREngine synchronises video presentation to the
audio hardware clock.

## Master clock: OpenAL sample position

Audio is the **master clock**. The formula is:

```
audioPts = _firstAudioPts
         + (_processedSampleCount + source.SampleOffset)
           * TimeSpan.TicksPerSecond / sampleRate
         - HardwareAudioLatencyTicks
```

- `_firstAudioPts` — PTS of the first submitted frame (with optional
  fast-forward to match video position; see below).
- `_processedSampleCount` — total samples from fully consumed OpenAL
  buffers (advanced via `BufferProcessed` event).
- `source.SampleOffset` — partial progress within the currently playing
  buffer (from `AL_SAMPLE_OFFSET`).
- `HardwareAudioLatencyTicks` — fixed 50 ms compensation for the OpenAL
  output pipeline.

Returns **0** whenever the source is not playing (see Note 4).

## Clock seeding & fast-forward

The clock is seeded the first time `_firstAudioPts == long.MinValue` during
`SubmitDecodedAudioFrame`. If video is already ahead of the audio PTS
(because it was playing via wall-clock fallback), `_firstAudioPts` is
fast-forwarded to `_lastPresentedVideoPts` so the audio clock starts at the
current video position. This avoids a long hold/freeze while the clock
catches up.

Seeding occurs:
1. **Initial startup** — first audio frame ever submitted.
2. **After format-change flush** — `FlushAudioQueue` resets to `long.MinValue`.
3. **After natural underrun** — the Rewind block resets to `long.MinValue`.

## Video pacing — sweep-present

Each render pass, `DrainStreamingFramesOnMainThread` computes the audio
clock and derives a **video clock** by adding
`VideoDisplayLatencyCompensationTicks` (50 ms). All pacing decisions use
the video clock so frames are presented ahead of the raw audio position:

```
videoClock = audioClock + VideoDisplayLatencyCompensationTicks
drift      = videoPts − videoClock
```

| Condition | Action |
|---|---|
| `drift > +40 ms` (VideoHoldThresholdTicks) | **Hold** — video ahead of audio |
| `drift < −150 ms` (VideoDropThresholdTicks) | **Drop** — video behind audio (default) |
| `drift < adaptive` (when EWMA < −33 ms) | **Catch-up drop** — tighter threshold (see Note 9) |
| `drift < −500 ms` (VideoResetThresholdTicks) | **Reset** — flush & reseed |
| within presentable window | **Present** — show this frame |

When multiple frames are in the presentable window (render rate < video
frame rate), only the **latest** one is presented; earlier ones are
soft-dropped. This prevents gradual drift accumulation.

## Wall-clock fallback

When `GetAudioClock()` returns 0 (audio not playing):
- `audioSyncActive = false`
- If audio has previously played (`_audioHasEverPlayed`),
  `allowUnsyncedFallback = true` **immediately** — video continues via
  wall-clock during format-change or underrun recovery re-buffering.
- On initial startup (audio never played): after
  `RebufferThresholdTicks` (750 ms) with no audio queued,
  `allowUnsyncedFallback = true`
- Video frames are presented at render-loop rate without drift correction,
  paced by `HlsMediaStreamSession`'s internal wall-clock timer

The fallback ensures video keeps flowing during ad breaks, format changes,
or transient decoder stalls. When the audio clock comes back online, the
fallback flag clears and audio-synced pacing resumes with the re-seeded
clock.

---

*Last updated: 2026-02-23 — Fixes 1–10 (wall-clock catch-up, sweep-present,
clock fast-forward, format-change flush, Rewind on flush, Rewind on natural
underrun, clock reset on underrun + pre-buffer tuning, EWMA drift tracking +
adaptive frame dropping, display pipeline latency compensation,
commercial-break resilience: deeper buffering + smooth video fallback).
Added A/V sync architecture notes.*

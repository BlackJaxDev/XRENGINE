# OpenAL Streaming Audio

XREngine uses OpenAL as the streaming audio backend for media playback paths such as `UIVideoComponent`. The implemented model is a manually paced streaming queue: decoded PCM frames are converted or submitted to OpenAL buffers, queued to a source, and synchronized against the OpenAL sample clock.

This feature doc replaces the active-design role of `docs/work/design/audio/OPENAL_STREAMING_NOTES.md`. That older document remains useful as debugging history.

## Runtime Model

- Audio playback uses OpenAL as the master clock for A/V sync.
- `UIVideoComponent` owns the media drain loop, prebuffer gates, underrun recovery, format-change flushing, drift correction, and video pacing decisions.
- `OpenALTransport` owns OpenAL-specific transport behavior and sample conversion helpers.
- Streaming code disables automatic source playback while the prebuffer gate is filling, then starts playback explicitly once enough audio is queued.

## Queue Lifecycle

OpenAL reports every queued buffer as processed when a source is stopped. To avoid repeatedly unqueuing freshly queued buffers after underruns or format changes, the streaming path rewinds stopped and empty sources before refilling them.

The normal refill flow is:

1. Unqueue consumed buffers.
2. Rewind stopped empty sources before attempting to accumulate new buffers.
3. Submit decoded PCM frames until the queue reaches the configured target depth or backpressure limit.
4. Start playback only after the minimum queued duration and buffer count are satisfied.

Format changes force a full flush because OpenAL requires queued buffers on a source to share sample rate, channel count, and sample format.

## A/V Sync

Audio is the master timeline. The audio clock is derived from:

- the presentation timestamp of the first submitted audio frame,
- the number of fully processed samples,
- the source sample offset inside the current OpenAL buffer,
- and the configured hardware audio latency compensation.

Video pacing uses a display-latency-compensated clock so frames are uploaded early enough to appear on screen in sync with audio. When audio is temporarily inactive after initial playback, video can fall back to wall-clock pacing during rebuffering so ad breaks, format changes, and underruns do not freeze the picture.

## Drift And Underrun Recovery

The playback loop tracks drift with an exponentially weighted moving average. If video starts falling behind audio, the drop threshold tightens temporarily so the renderer can catch up without waiting for large visible desync.

On underrun recovery the audio clock is reseeded from the latest video position. This prevents the audio clock from resuming from a stale pre-underrun timestamp after video has continued via wall-clock fallback.

## Diagnostics

The streaming path reports:

- raw and display-compensated A/V drift,
- EWMA drift,
- catch-up frame drops,
- underrun recovery,
- format-change flushes,
- and queue depth/backpressure behavior.

These diagnostics are intentionally compact enough to be useful during live-stream debugging without flooding steady-state playback logs.

## Implementation References

- `XRENGINE/Scene/Components/UI/Core/UIVideoComponent.Audio.cs`
- `XRENGINE/Scene/Components/UI/Core/UIVideoComponent.FrameDrain.cs`
- `XRENGINE/Scene/Components/UI/Core/UIVideoComponent.Pipeline.cs`
- `XREngine.Audio/OpenAL/OpenALTransport.cs`
- `XREngine.UnitTests/Audio/OpenALTransportTests.cs`
- `XREngine.UnitTests/Audio/OpenALRegressionTests.cs`

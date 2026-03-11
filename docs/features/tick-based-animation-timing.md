# Tick-Based Animation Timing

Last Updated: 2026-03-10

This document describes the shipped animation timing model in XRENGINE after the stopwatch-tick migration work was completed.

## Overview

XRENGINE now uses `long` stopwatch ticks as the authoritative runtime time source for animation playback.

The animation system no longer depends on float accumulation for runtime playback progress, loop wrapping, or absolute seek. Instead:

- runtime playback is driven by clip-relative stopwatch ticks
- authored frame identity is preserved separately from float-second views
- authored clip cadence survives import and baking
- float-facing APIs remain available as compatibility and sampling surfaces, not as the source of truth

This fixes the three practical timing problems that existed before the migration:

1. imported keyframes lost their original frame identity immediately
2. looped playback accumulated drift through float modulo/wrap behavior
3. imported clips lost their authored frame cadence after import

## Goals

The migration was scoped to solve the real timing bugs without introducing a second global engine timeline domain.

The goals were:

- preserve exact authored frame identity for integer-FPS clips
- keep `Stopwatch` ticks as the only authoritative runtime playback clock
- eliminate float drift in loop wrapping and long-running playback
- preserve authored cadence metadata on imported and baked animations
- keep existing float-based inspectors and sampling APIs working

## Non-Goals

The migration explicitly did not try to:

- replace every gameplay use of `Engine.Delta`
- move shader-facing or interpolation outputs away from float
- convert continuous systems like Rive into timeline ticks
- add rational-time support for NTSC cadences in this wave

## Runtime Model

### Authoritative clock

Runtime animation playback now uses stopwatch ticks end to end:

- `BaseAnimation` stores playback position in `long _currentTicks`
- `AnimationClipComponent` stores playback position in `long _playbackTimeTicks`
- state-machine evaluation feeds track ticking with stopwatch-tick deltas
- loop wrapping uses integer modulo in tick space rather than float remap logic

Float properties such as `CurrentTime` and `PlaybackTime` remain available, but they are derived views of the tick-backed state.

### Authored cadence

Imported and baked animation content can preserve explicit authored cadence through `AuthoredCadence`:

- `FrameCount`
- `FramesPerSecond`
- helper methods for length, frame floor, frame fraction, and frame-to-second conversion

This gives the runtime two pieces of information that used to be collapsed into one float timeline:

- exact runtime playback position in stopwatch ticks
- exact authored clip cadence and frame addressing

### Keyframe identity

`Keyframe` now carries `AuthoredFrameIndex` with `-1` meaning “seconds-authored / no exact frame identity available”.

For imported integer-FPS content:

- `Second` is still populated for compatibility
- `AuthoredFrameIndex` preserves the original authored frame

That means frame-accurate logic can prefer exact frame identity when cadence metadata exists, while older float-based code paths still function.

## Implemented Changes

### Core animation types

The timing migration changed the core animation stack as follows:

- `AuthoredCadence` added as the shared cadence metadata type
- `BaseAnimation` now uses tick-backed playback accumulation and seek
- `BaseAnimation.Tick(long deltaTicks)` and `Seek(long ticks, bool wrapLooped)` are now first-class APIs
- float `Tick(float)` and `Seek(float, ...)` remain as compatibility wrappers

### Property animation evaluation

The property animation layer now supports tick-native broadcast and exact cadence-aware frame windows:

- `MotionBase.TickPropertyAnimations(long deltaTicks)` keeps broadcast ticking in tick space
- baked/keyframed helpers no longer depend on `int frame = (int)(second * _bakedFPS)`-style addressing in the migrated paths
- `BasePropAnimBakeable._bakedFPS` is now `int`
- baked frame count / FPS are aligned with authored cadence metadata

### Clip playback and state machines

Runtime clip playback and state-machine playback were updated as well:

- `AnimationClipComponent` accumulates clip time in `_playbackTimeTicks`
- absolute clip seek now pushes `long` tick positions to child animations
- `AnimStateMachineComponent` now evaluates using update delta ticks rather than rebuilding ticks from float seconds
- `Spline3DTransform` now uses tick-backed accumulation instead of `AnimationSecond += Engine.Delta`

### Engine convenience accessors

The engine timing surface now exposes tick-based per-frame convenience accessors:

- `Engine.UndilatedDeltaTicks`
- `Engine.DeltaTicks`
- `Engine.FixedDeltaTicks`

Equivalent accessors are also available through `Engine.Time`.

## Compatibility Policy

The migration kept float-facing APIs intentionally, but changed what they mean.

Still float-facing by design:

- `BaseAnimation.CurrentTime`
- `AnimationClipComponent.PlaybackTime`
- sampling helpers such as `GetValue(float second)`
- keyframe interpolation entry points such as `Interpolate(float desiredSecond)`

These remain because they are useful editor/runtime convenience boundaries and because interpolation outputs are still float-native. They are no longer authoritative storage.

## Validation

Focused validation now covers:

- frame-index preservation
- authored cadence preservation
- long-duration looping (`24 fps` clip over `10 minutes` at `90 Hz` tick cadence)
- reverse playback clamping
- exact seek boundary behavior
- mixed-rate playback mapping (`24 -> 90`, `30 -> 120`, `60 -> 144`)
- importer cadence and frame-identity preservation
- engine timer tick accessors

At the time of this document update, the focused animation timing/importer/engine-timer suite passes.

## Scope Limits and Future Work

Two deliberate limits remain:

1. NTSC / rational authored time (`24000/1001`, `30000/1001`, `60000/1001`) is still out of scope.
2. Float-facing sampling APIs remain in place because they are compatibility and final-boundary APIs rather than authoritative clocks.

If future sequencing, timeline editing, or import pipelines need NTSC or mixed-timescale authored media, the next expansion path should be rational-time support rather than another float-based extension.

## Related Documentation

- [Animation System](../api/animation.md)
- [Documentation Index](../README.md)
- [MCP Server](mcp-server.md)
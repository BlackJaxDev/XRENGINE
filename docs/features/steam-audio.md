# Steam Audio Integration

Last Updated: 2026-03-10

This document captures the Steam Audio design that drove the audio transport and effects refactor, along with the current shipped integration state.

For the broader audio subsystem overview, see [docs/architecture/audio-architecture.md](../architecture/audio-architecture.md).

## Overview

Steam Audio is a DSP and simulation library, not an output device. It spatializes and transforms PCM, but final playback still belongs to a transport such as OpenAL or NAudio.

That constraint is the reason XRENGINE moved away from the old OpenAL-only listener model and adopted a transport/effects split:

| Axis | Responsibility | Current Implementations |
|---|---|---|
| Transport | Device I/O, source and buffer lifecycle, playback, capture | OpenAL, NAudio |
| Effects | DSP, spatialization, simulation, environmental acoustics | OpenAL EFX, Steam Audio, Passthrough |

The practical outcome is that Steam Audio is modeled as an `IAudioEffectsProcessor`, not as a separate backend.

## Design Goals

- Keep component-facing engine APIs stable enough that `AudioListenerComponent` and `AudioSourceComponent` continue to work through `ListenerContext`.
- Allow transport and effects selection independently, while enforcing capability constraints such as OpenAL EFX requiring an OpenAL context.
- Keep OpenAL + OpenAL EFX as the default path.
- Make Steam Audio opt-in and compatible with both OpenAL and NAudio transports.
- Expose scene geometry, materials, probes, and baking through engine scene components rather than through an isolated side system.

## Core Architecture

The current integration centers on `ListenerContext`, which composes a transport and an effects processor.

```text
AudioManager
  -> ListenerContext
       -> IAudioTransport
       -> IAudioEffectsProcessor
```

### Transport Layer

`IAudioTransport` owns:

- device lifetime
- listener state pushed to the output backend
- source and buffer handle creation/destruction
- playback controls
- queue and unqueue behavior for streaming
- optional capture support

Current transport implementations:

- `OpenALTransport`
- `NAudioTransport`

### Effects Layer

`IAudioEffectsProcessor` owns:

- listener pose for processors that need simulation state
- per-source registration and pose updates
- per-frame simulation work
- optional scene geometry integration
- buffer processing when the processor is not in-driver

Current effects implementations:

- `OpenALEfxProcessor`
- `SteamAudioProcessor`
- `PassthroughProcessor`

## Supported Pairings

| Transport | Effects | Status | Notes |
|---|---|---|---|
| OpenAL | OpenAL EFX | Supported | Default path. Requires EFX extension support. |
| OpenAL | Steam Audio | Implemented | Steam Audio processes uploaded PCM in full frame-sized blocks, and OpenAL outputs the processed stereo result. Final end-to-end validation is still pending. |
| OpenAL | Passthrough | Supported | Minimal path with no extra DSP. |
| NAudio | Steam Audio | Implemented | Managed output path for Steam Audio processed PCM. Final end-to-end validation is still pending. |
| NAudio | Passthrough | Supported | Simple software-mixed playback. |
| NAudio | OpenAL EFX | Invalid | Rejected because EFX requires an OpenAL context. |

## Steam Audio Processing Model

`SteamAudioProcessor` is responsible for the Steam Audio-specific runtime state:

- `IPLContext`
- `IPLAudioSettings`
- `IPLHRTF`
- `IPLSimulator`
- per-source effect chains and simulation inputs

The intended per-source processing path is:

```text
Mono input
  -> DirectEffect
  -> BinauralEffect
  -> optional ReflectionEffect
  -> optional PathEffect
  -> stereo output
  -> transport playback
```

For OpenAL EFX, the processor is effectively a no-op because the DSP is applied in-driver. For Steam Audio, the processor must receive PCM explicitly and return processed output buffers.

## Listener, Source, and Buffer Responsibilities

The refactor kept `ListenerContext`, `AudioSource`, and `AudioBuffer` as the engine-facing objects, but reduced their direct backend ownership.

- `ListenerContext` now owns one transport and one optional effects processor.
- `AudioSource` wraps a transport source handle and, when applicable, an effects source handle.
- `AudioBuffer` wraps a transport buffer handle.

This kept the scene and world integration stable while moving backend-specific behavior into the transport and effects abstractions.

## Scene Integration

Steam Audio scene data is bridged through engine components rather than being maintained manually.

### Geometry

`SteamAudioGeometryComponent` feeds triangle geometry from scene meshes into the Steam Audio scene representation.

Responsibilities:

- create static or instanced acoustic meshes
- update instanced transforms when scene transforms change
- remove geometry from the Steam Audio scene on deactivation

### Materials

`SteamAudioMaterial` represents acoustic material parameters such as:

- three-band absorption
- scattering
- three-band transmission

These values are mapped to `IPLMaterial` when geometry is built.

### Probes and Baking

The integration also includes probe and bake support through:

- `SteamAudioProbeComponent`
- `SteamAudioProbeBatch`
- `SteamAudioBaker`

That covers offline reflections and pathing workflows for static scenes.

## File Layout

Key areas of the implementation live in:

```text
XREngine.Audio/
  Abstractions/
  OpenAL/
  Steam/
  ListenerContext.cs
  AudioSource.cs
  AudioBuffer.cs
  AudioManager.cs

XRENGINE/Scene/Components/Audio/
  AudioListenerComponent.cs
  AudioSourceComponent.cs
  SteamAudioGeometryComponent.cs
  SteamAudioProbeComponent.cs

XREngine.Editor/ComponentEditors/
  SteamAudioGeometryComponentEditor.cs
  SteamAudioProbeComponentEditor.cs
```

## Migration Outcome

The original Steam Audio work started as a design and migration plan. Most of that plan is now implemented.

Completed areas:

- transport/effects abstraction layer
- OpenAL extraction into `OpenALTransport` and `OpenALEfxProcessor`
- passthrough processor
- Steam Audio processor and scene wrappers
- Steam Audio geometry, material, probe, and baking components
- runtime transport/effects selection and validation
- editor tooling for Steam Audio materials and probes
- supporting audio architecture documentation

## Runtime Status

The V2 playback path now routes uploaded PCM through `EffectsProcessor.ProcessBuffer(...)` across the full source buffer, not just the first internal Steam Audio frame.

That closes the remaining playback-path implementation gap from the original design. Static and streaming uploads that use `AudioSource.SetBufferData(...)` now hand complete mono input buffers to Steam Audio, receive chunked stereo output, and pass that processed PCM to the selected transport.

The remaining work is validation of that shipped path in editor/runtime scenarios.

## Remaining Validation

The remaining Steam Audio work is now operational validation rather than architecture or playback-path implementation.

- Run the editor and verify the V2 OpenAL transport path preserves existing baseline playback behavior.
- Verify OpenAL transport plus Steam Audio effects in the `AudioTesting` world by playing a mono source and confirming HRTF spatialization.
- Verify Steam Audio occlusion by placing geometry between listener and source and confirming audible attenuation and occlusion.

## Current Mitigations

The runtime currently mitigates the main implementation risks in these ways:

- chunked full-buffer processing avoids dropping every sample after the first Steam Audio frame and keeps transport uploads contiguous
- processor state changes and scene/probe/source mutation are serialized to reduce thread-safety hazards between simulation, geometry updates, and buffer processing
- simulator work is skipped when no Steam Audio sources are registered, reducing avoidable per-listener CPU cost

## Risks

The main operational risks for this feature remain:

- additional latency when Steam Audio output is streamed through another transport
- simulation and geometry update thread-safety
- higher cost for multi-listener scenarios
- CPU cost of per-source processing compared with in-driver OpenAL EFX

## References

- Steam Audio SDK Documentation: <https://valvesoftware.github.io/steam-audio/>
- Steam Audio GitHub: <https://github.com/ValveSoftware/steam-audio>
- Existing bindings: `XREngine.Audio/Steam/Phonon.cs`
# Audio Architecture

> Last updated: 2026-09-29.

## Overview

The XREngine audio subsystem uses a **transport / effects split** architecture
that cleanly separates I/O (transport) from spatial-audio processing (effects).
Both layers compose inside a `ListenerContext`, which is the top-level owner of
all audio state for one logical listener.

The portable `XREngine.Audio` project owns contracts, listener/source/buffer
facades, and backend selection. `XREngine.Runtime.AudioIntegration` owns the
ordinary scene audio, microphone, speech, and managed Audio2Face components.
Native implementations live in separate projects: `XREngine.Audio.OpenAL`,
`XREngine.Audio.NAudio`, `XREngine.Audio.SteamAudio`,
`XREngine.Audio.OVRLipSync`, and `XREngine.Audio.Audio2Face`. These projects
reference the lower contracts; the audio leaves do not reference each other.
Several public types retain their original namespaces for serialized identity,
so a namespace alone does not identify the project that compiles a type.
See [Runtime Project Organization](../runtime/project-organization.md) for the
full project dependency map.

```
┌────────────────────────────────┐
│        AudioManager            │  Creates & owns ListenerContexts
│  DefaultTransport / Effects    │
└──────────┬─────────────────────┘
           │ NewListener()
           ▼
┌────────────────────────────────┐
│       ListenerContext          │
│  ┌────────────┐ ┌───────────┐ │
│  │ IAudioTrans│ │IAudioEffec│ │
│  │   port     │ │tsProcessor│ │
│  └────────────┘ └───────────┘ │
│  Sources, Buffers, Gain, Fade │
└────────────────────────────────┘
```

## Transport Backends

| Backend | Key | Notes |
|---------|-----|-------|
| **OpenAL** | `EAudioTransport.OpenAL` | OpenAL Soft playback and spatial listener controls. Default path. |
| **NAudio** | `EAudioTransport.NAudio` | Managed software mixer (`NAudioMixer`). Supports streaming queues and pitch-adjusted playback. |

## Effects Processors

| Processor | Key | Requires |
|-----------|-----|----------|
| **OpenAL EFX** | `EAudioEffects.OpenAL_EFX` | OpenAL transport (EFX needs an active AL context). |
| **Steam Audio** | `EAudioEffects.SteamAudio` | `phonon.dll` (fetched via `Tools/Dependencies/Get-Phonon.ps1`). Works with any transport. |
| **Passthrough** | `EAudioEffects.Passthrough` | None. No spatial processing. |

Invalid combos (e.g. EFX + NAudio) are auto-corrected by `AudioManager.ValidateCombo()`.

Desktop startup installs the OpenAL, NAudio, Steam Audio, OVR Lip Sync, and
Audio2Face native backends explicitly
through `OpenALBackend.Register()`, `NAudioBackend.Register()`, and
`SteamAudioBackend.Register()`, `OVRLipSyncBackend.Register()`, and
`Audio2Face3DNativeBackend.Register()`. NAudio
registration supplies transport, microphone capture, WAV/MP3 import, and voice
conversion codecs. Steam registration supplies the effects processor and its
native scene components. If a requested backend is not registered, creation
reports the missing backend by name. `AudioData` keeps its serialized type in
`XREngine.Data`; its WAV/MP3 decoder is supplied by the desktop NAudio leaf.

The default listener keeps its legacy behavior (`AudioArchitectureV2=false`),
but owns an `IAudioListenerBackend` supplied by the OpenAL leaf. Portable
`ListenerContext`, `AudioSource`, and `AudioBuffer` call that backend for native
state, playback, and buffer uploads. The OpenAL leaf owns the device/context,
capture wrappers, EFX pools, native packages, and native license output. Native
operations select the owning context and run under one shared OpenAL gate.
`ListenerContext` disposes sources, buffers, and effects before the transport.

The optional V2 path composes an `IAudioTransport` and an
`IAudioEffectsProcessor`. OpenAL EFX requires the OpenAL transport; requesting
an unregistered or unknown backend fails with a named error. The documented
EFX + NAudio combination correction to Passthrough remains in
`AudioManager.ValidateCombo()`.

## Steam Audio Integration

Steam Audio is integrated as an `IAudioEffectsProcessor` implementation
(`SteamAudioProcessor`). The full stack:

1. **`SteamAudioProcessor`** — Lifecycle, per-source DSP chain (direct → binaural
   HRTF, reflections → ambisonics decode, pathing).
2. **`SteamAudioScene`** — Wraps `IPLScene`. Geometry is fed by
   `SteamAudioGeometryComponent` instances in the scene graph.
3. **`SteamAudioProbeBatch`** — Wraps `IPLProbeBatch`. Managed by
   `SteamAudioProbeComponent` which auto-generates probes and drives baking.
4. **`SteamAudioBaker`** — Wraps `IPLBaker`. Bakes reflections and pathing
   offline or on-demand from the editor.
5. **`SteamAudioMaterial`** — Per-surface acoustic properties (3-band absorption,
   scattering, 3-band transmission) with named presets.

### Scene Components

| Component | Purpose | Editor |
|-----------|---------|--------|
| `AudioListenerComponent` | Creates a `ListenerContext` on the world. | Default inspector. |
| `AudioSourceComponent` | Binds an `AudioSource` to the scene graph (position, gain, pitch, streaming). | Default inspector. |
| `SteamAudioGeometryComponent` | Feeds sibling mesh geometry into the acoustic scene. | `SteamAudioGeometryComponentEditor` (ImGui). |
| `SteamAudioProbeComponent` | Places and manages a probe batch, drives baking. | `SteamAudioProbeComponentEditor` (ImGui). |

> **Note:** Audio transport/effects/V2 settings are configured through the
> cascading settings system (User Settings → Game Settings → Editor Preferences),
> not as a scene component. See [Configuration Flow](#configuration-flow) below.

### Per-Source Effect Chain

```
Mono input ──► DirectEffect (attenuation, air absorption, occlusion, transmission)
            ├──► BinauralEffect (HRTF, mono → stereo)
            │
            ├──► ReflectionEffect (mono → ambisonics)
            │    └──► AmbisonicsDecodeEffect (ambisonics → stereo)
            │
            └──► PathEffect (mono → stereo spatialized)
                       │
                       ▼
              Mix (direct + reflections + pathing) → interleaved stereo output
```

All per-source IPL buffers are **pre-allocated** in `SourceChain` at source
creation time. Zero heap allocations occur in `Tick()`, `ProcessBuffer()`, or
`SetSourceInputs()`.

## Configuration Flow

Audio settings use the engine's **cascading settings system**. The effective
value for each audio setting is resolved in priority order:

```
Editor Prefs Override  (highest — dev/testing)
        ▼
  Game Settings Override  (game requirements)
        ▼
    User Settings  (user preference — base/default)
        ▼
  Engine.EffectiveSettings  ──resolves──►  ApplyAudioPreferences()
                                                │
                                   AudioSettings (static globals)
                                                │
                                        AudioManager.ApplyTo()
                                                │
                                          ListenerContext
```

### Settings Locations

| Level | Class | Properties |
|-------|-------|------------|
| **User** | `UserSettings` | `AudioTransport`, `AudioEffects`, `AudioArchitectureV2`, `AudioSampleRate` |
| **Game** | `GameStartupSettings` | `AudioTransportOverride`, `AudioEffectsOverride`, `AudioArchitectureV2Override`, `AudioSampleRateOverride` |
| **Editor** | `EditorPreferencesOverrides` | `AudioTransportOverride`, `AudioEffectsOverride`, `AudioArchitectureV2Override`, `AudioSampleRateOverride` |

### Data-Layer Enums

| Data Enum (`XREngine.Data`) | Audio Enum (`XREngine.Audio`) |
|-----------------------------|-------------------------------|
| `EAudioTransport.OpenAL` | `AudioTransportType.OpenAL` |
| `EAudioTransport.NAudio` | `AudioTransportType.NAudio` |
| `EAudioEffects.OpenAL_EFX` | `AudioEffectsType.OpenAL_EFX` |
| `EAudioEffects.SteamAudio` | `AudioEffectsType.SteamAudio` |
| `EAudioEffects.Passthrough` | `AudioEffectsType.Passthrough` |

The `Engine.Settings.ApplyAudioPreferences()` method maps data-layer enums to
audio-layer enums and pushes the resolved values to `AudioSettings` statics,
then calls `AudioSettings.ApplyTo(Engine.Audio)`. Any property change at any
cascade level triggers this flow automatically.

## Hot-Path Allocation Discipline

The audio tick and mix paths are designed for **zero per-frame heap allocations**:

- All IPL interop types (`IPLSimulationInputs`, `IPLBinauralEffectParams`, etc.)
  are C# value types / structs.
- `SourceChain` pre-allocates all native audio buffers.
- `ListenerContext.GetOrientation()` uses `stackalloc` instead of `new float[]`.
- `NAudioMixer.Read()` uses pre-existing dictionary struct enumerators.
- `Dictionary<K,V>.TryGetValue` / `foreach` over `.Values` are allocation-free.

## File Map

```
XREngine.Audio/
  AudioManager.cs          – Factory, transport/effects combo validation
  AudioSettings.cs         – Global static configuration
  ListenerContext.cs       – Per-listener owner (transport + effects + sources)
  AudioBackendRegistry.cs  – Explicit backend registration
  Abstractions/            – Transport, listener, and effects contracts

XREngine.Audio.OpenAL/OpenAL/
  OpenALTransport.cs       – OpenAL transport and legacy listener backend
  OpenALEfxProcessor.cs    – OpenAL EFX processing
  Effects/                 – Native EFX effect and context implementations

XREngine.Audio.NAudio/
  NAudioMixer.cs           – Software mixer (ISampleProvider)
  NAudioTransport.cs       – NAudio transport and output
  NAudioCaptureStream.cs   – Microphone capture adapter

XREngine.Audio.SteamAudio/Steam/
  SteamAudioProcessor.cs  – IAudioEffectsProcessor (full DSP chain)
  SteamAudioScene.cs      – IPLScene wrapper
  SteamAudioProbeBatch.cs – IPLProbeBatch wrapper
  SteamAudioBaker.cs      – IPLBaker wrapper
  SteamAudioMaterial.cs   – Acoustic material presets
  Phonon.cs               – P/Invoke declarations for phonon.dll
  OpaqueHandles.cs        – Typed wrappers for IPL opaque handles

XREngine.Audio.SteamAudio/Scene/Components/Audio/
  SteamAudioGeometryComponent.cs
  SteamAudioProbeComponent.cs

XREngine.Audio.OVRLipSync/
  OVRLipSync.cs            – Native lip-sync integration
  OVRLipSyncBackend.cs     – Session registration

XREngine.Audio.Audio2Face/
  Audio2Face3DNativeBackend.cs – Native bridge registration
  Scene/Components/Audio/Audio2Face3D/
    Audio2Face3DNativeBridgeComponent.cs

XREngine.Data/Core/Enums/
  EAudioTransport.cs        – Data-layer transport enum (OpenAL, NAudio)
  EAudioEffects.cs          – Data-layer effects enum (OpenAL_EFX, SteamAudio, Passthrough)

XREngine.Data/Core/
  UserSettings.cs           – User audio preferences

XREngine.Runtime.Bootstrap/Engine/
  Engine.Settings.cs         – ApplyAudioPreferences(), enum mapping
  Subclasses/
    Engine.EffectiveSettings.cs – AudioTransport/AudioEffects/AudioArchitectureV2/AudioSampleRate cascade

XREngine.Runtime.Bootstrap/Settings/
  GameStartupSettings.cs     – AudioTransportOverride, AudioEffectsOverride, etc.
  EditorPreferencesOverrides.cs – Audio override properties for editor

XREngine.Runtime.AudioIntegration/Scene/Components/Audio/
  AudioListenerComponent.cs
  AudioSourceComponent.cs
  MicrophoneComponent.cs
  Audio2Face3D/Audio2Face3DComponent.cs

XREngine.Editor/ComponentEditors/
  SteamAudioGeometryComponentEditor.cs
  SteamAudioProbeComponentEditor.cs

Tools/Dependencies/
  Get-Phonon.ps1           – Downloads phonon.dll from Steam Audio GitHub releases
```

## License

Steam Audio (`phonon.dll`) is licensed under **Apache-2.0** by Valve Corporation.
See `docs/DEPENDENCIES.md` for the full dependency/license audit.

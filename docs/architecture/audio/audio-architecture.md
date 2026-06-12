# Audio Architecture

> Last updated: Phase 8 (Audio settings migrated to cascading settings system).

## Overview

The XREngine audio subsystem uses a **transport / effects split** architecture
that cleanly separates I/O (transport) from spatial-audio processing (effects).
Both layers compose inside a `ListenerContext`, which is the top-level owner of
all audio state for one logical listener.

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
| **OpenAL** | `AudioTransportType.OpenAL` | Hardware-accelerated via OpenAL Soft. Default path. |
| **NAudio** | `AudioTransportType.NAudio` | Managed software mixer (`NAudioMixer`). No native OpenAL dependency. Supports streaming queues and pitch-adjusted playback. |

## Effects Processors

| Processor | Key | Requires |
|-----------|-----|----------|
| **OpenAL EFX** | `AudioEffectsType.OpenAL_EFX` | OpenAL transport (EFX needs an active AL context). |
| **Steam Audio** | `AudioEffectsType.SteamAudio` | `phonon.dll` (fetched via `Tools/Dependencies/Get-Phonon.ps1`). Works with any transport. |
| **Passthrough** | `AudioEffectsType.Passthrough` | None. No spatial processing. |

Invalid combos (e.g. EFX + NAudio) are auto-corrected by `AudioManager.ValidateCombo()`.

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
  NAudioMixer.cs           – Managed software mixer (ISampleProvider)
  NAudioTransport.cs       – NAudio IAudioTransport implementation
  OpenALTransport.cs       – OpenAL IAudioTransport implementation
  Steam/
    SteamAudioProcessor.cs – IAudioEffectsProcessor (full DSP chain)
    SteamAudioScene.cs     – IPLScene wrapper
    SteamAudioProbeBatch.cs – IPLProbeBatch wrapper
    SteamAudioBaker.cs     – IPLBaker wrapper
    SteamAudioMaterial.cs  – Acoustic material presets
    Phonon.cs              – P/Invoke declarations for phonon.dll
    OpaqueHandles.cs       – Typed wrappers for IPL opaque handles

XREngine.Data/Core/Enums/
  EAudioTransport.cs        – Data-layer transport enum (OpenAL, NAudio)
  EAudioEffects.cs          – Data-layer effects enum (OpenAL_EFX, SteamAudio, Passthrough)

XRENGINE/Engine/
  Engine.Settings.cs         – ApplyAudioPreferences(), enum mapping
  Subclasses/
    Engine.EffectiveSettings.cs – AudioTransport/AudioEffects/AudioArchitectureV2/AudioSampleRate cascade

XRENGINE/Settings/
  GameStartupSettings.cs     – AudioTransportOverride, AudioEffectsOverride, etc.
  EditorPreferencesOverrides.cs – Audio override properties for editor

XRENGINE/Scene/Components/Audio/
  AudioListenerComponent.cs
  AudioSourceComponent.cs
  SteamAudioGeometryComponent.cs
  SteamAudioProbeComponent.cs

XREngine.Editor/ComponentEditors/
  SteamAudioGeometryComponentEditor.cs
  SteamAudioProbeComponentEditor.cs

Tools/Dependencies/
  Get-Phonon.ps1           – Downloads phonon.dll from Steam Audio GitHub releases
```

## License

Steam Audio (`phonon.dll`) is licensed under **Apache-2.0** by Valve Corporation.
See `docs/DEPENDENCIES.md` for the full dependency/license audit.

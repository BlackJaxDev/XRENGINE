# Steam Audio Integration — Design & TODO

## 1  Overview

The engine's audio subsystem is currently **hard-wired to OpenAL** through `ListenerContext`, `AudioSource`, `AudioBuffer`, and the `Effects/` pipeline. A comprehensive set of **Steam Audio (Phonon) P/Invoke bindings** already lives under `XREngine.Audio/Steam/` but has zero integration with the engine's audio graph.

### Key insight — Transport vs Effects

Steam Audio is a **DSP processing library**, not an audio output device. It transforms PCM buffers (applying HRTF, occlusion, reverb, etc.) but cannot play them. A separate **transport layer** (OpenAL, NAudio/SDL2, WASAPI, etc.) is always required for final output. This means the architecture should **not** model Steam Audio as an alternative "backend" alongside OpenAL. Instead, the audio system should decompose into two orthogonal, independently selectable axes:

| Axis | Responsibility | Implementations |
|---|---|---|
| **Transport** | Device enumeration, buffer playback, source handle management, capture | OpenAL (current), NAudio/SDL2, WASAPI (future) |
| **Effects Processor** | DSP pipeline applied to audio before it reaches the transport | OpenAL EFX (current), Steam Audio / Phonon, None / Passthrough |

This document describes the plan to:

1. Split the current monolithic `ListenerContext` into an **`IAudioTransport`** interface and an **`IAudioEffectsProcessor`** interface.
2. Refactor `ListenerContext` to **compose** a transport and an effects processor rather than hard-coding OpenAL for both.
3. Extract OpenAL-specific code into `OpenALTransport` and `OpenALEffectsProcessor` implementations.
4. Add a `SteamAudioEffectsProcessor` that wraps the Phonon DSP pipeline.
5. Let `AudioManager` remain the single entry point, selecting transport + effects at listener-creation time.

### Goals

- **Minimal disruption** — component-level code (`AudioListenerComponent`, `AudioSourceComponent`) continues to work against `ListenerContext` / `AudioSource` / `AudioBuffer` with zero or near-zero changes.
- **Orthogonal selection** — transport and effects processor are chosen independently, **subject to explicit capability constraints** (for example, OpenAL EFX requires an OpenAL transport/context).
- **Runtime configurability** — the desired transport + effects combination is chosen per-listener (or globally via `AudioManager`), not at compile time. OpenAL + OpenAL EFX remains the default.
- **Steam Audio scene integration** — expose Phonon's geometry, material, HRTF, and simulation features through the engine's scene graph so that scene meshes can contribute to acoustic simulation.
- **No new NuGet dependencies** — Steam Audio is consumed via the existing `phonon.dll` P/Invoke layer, not a NuGet package.

---

## 2  Current Architecture

```
AudioManager                          (manages listeners, fading, tick)
 └─ EventList<ListenerContext>
       └─ ListenerContext             (sealed, OpenAL-specific)
            ├─ AL Api                 (Silk.NET.OpenAL)
            ├─ ALContext / Device*    (native OpenAL handles)
            ├─ EffectContext          (EAX/Creative OpenAL effects)
            ├─ Sources: EventDictionary<uint, AudioSource>
            └─ Buffers: EventDictionary<uint, AudioBuffer>

AudioSource        (sealed, wraps AL source handle)
AudioBuffer        (sealed, wraps AL buffer handle, PCM upload)
AudioInputDevice   (capture via OpenAL Capture extension)

Effects/
 ├─ EffectContext  (ResourcePools for OpenAL Creative effects)
 └─ *Effect.cs    (EAXReverb, Echo, Chorus, …)

Steam/
 ├─ Phonon.cs          (611 lines of DllImport declarations)
 ├─ OpaqueHandles.cs   (IPLContext, IPLScene, IPLHRTF, …)
 ├─ Delegates.cs        (callback signatures)
 ├─ Enums/              (25 enum types)
 └─ Structs/            (55+ interop structs)
```

### Key observations

| Aspect | Detail |
|---|---|
| **ListenerContext** | `sealed unsafe class`, 469 lines. Directly creates `AL.GetApi()`, `ALContext`, `Device*`, `Context*`. All position/orientation/gain methods call OpenAL directly. |
| **AudioSource** | `sealed class`, 977 lines. Constructor takes `ListenerContext`, stores `AL Api` and calls `Api.GenSource()`. Every property setter calls OpenAL. |
| **AudioBuffer** | `sealed class`, 247 lines. Same pattern — stores `AL Api`, `Api.GenBuffer()`, `Api.BufferData(…)`. |
| **EffectContext** | Depends on `EffectExtension` (Creative OpenAL EFX). Entirely OpenAL-specific. |
| **AudioInputDevice** | Takes `ListenerContext`, uses its `Capture` extension. OpenAL-specific. |
| **Engine integration** | `Engine.Audio` is a singleton `AudioManager`. `AudioListenerComponent` calls `Engine.Audio.NewListener()` and stores the returned `ListenerContext?`. `AudioSourceComponent` stores `ConcurrentDictionary<ListenerContext, AudioSource>`. `XRWorldInstance` has `EventList<ListenerContext> Listeners`. |
| **Steam Audio bindings** | Complete Phonon 4.6 interop: context, HRTF, scene/static-mesh/instanced-mesh, simulation (direct, reflections, pathing), all audio effects (panning, binaural, ambisonics, direct, reflection, path), probes, baking. **No managed wrappers or engine integration yet.** |

---

## 3  Proposed Architecture — Transport / Effects Split

The core design principle: **transport and effects are orthogonal concerns** composed inside `ListenerContext`, not flattened into subclass variants.

```
                            ┌────────────────────┐
                            │   AudioManager     │
                            │  (factory, config)  │
                            └────────┬───────────┘
                                     │ EventList<ListenerContext>
                                     ▼
                          ┌─────────────────────────┐
                          │    ListenerContext       │
                          │  (concrete, composes     │
                          │   transport + effects)   │
                          └────┬──────────┬─────────┘
                               │          │
                  ┌────────────┘          └────────────┐
                  ▼                                    ▼
       ┌────────────────────┐              ┌────────────────────────┐
       │  IAudioTransport   │              │  IAudioEffectsProcessor │
       │  (interface)       │              │  (interface)            │
       └────────┬───────────┘              └────────┬───────────────┘
                │                                   │
    ┌───────────┼───────────┐          ┌────────────┼─────────────┐
    ▼           ▼           ▼          ▼            ▼             ▼
 OpenAL     NAudio/SDL2   (future)  OpenAL_EFX  SteamAudio   Passthrough
Transport   Transport               Effects     Effects      Effects
```

### Valid combinations (examples)

These are examples of supported pairings, not a claim that every transport/effects pair is valid.

| Transport | Effects | Use-case |
|---|---|---|
| OpenAL | OpenAL EFX | Current behavior (default) |
| OpenAL | Steam Audio | Physics-based 3D audio, HRTF, occlusion via Phonon — output through OpenAL device |
| OpenAL | Passthrough | Minimal overhead, no DSP |
| NAudio/SDL2 | Steam Audio | Low-latency WASAPI/exclusive-mode output + Phonon DSP |
| NAudio/SDL2 | Passthrough | Simple playback, no OpenAL dependency |

### 3.1  `IAudioTransport` — the output device layer

Owns the hardware output device, source handle lifecycle, and buffer upload/playback mechanics.

```csharp
/// <summary>
/// Abstraction over the audio output device and source/buffer handle management.
/// Implementations: OpenALTransport, NAudioTransport, etc.
/// </summary>
public interface IAudioTransport : IDisposable
{
    // --- Device ---
    string DeviceName { get; }
    int SampleRate { get; }
    bool IsOpen { get; }
    void Open(string? deviceName = null);
    void Close();

    // --- Listener spatial state (pushed to device) ---
    void SetListenerPosition(Vector3 position);
    void SetListenerVelocity(Vector3 velocity);
    void SetListenerOrientation(Vector3 forward, Vector3 up);
    void SetListenerGain(float gain);

    // --- Source lifecycle ---
    AudioSourceHandle CreateSource();
    void DestroySource(AudioSourceHandle source);

    // --- Buffer lifecycle ---
    AudioBufferHandle CreateBuffer();
    void DestroyBuffer(AudioBufferHandle buffer);
    void UploadBufferData(AudioBufferHandle buffer, ReadOnlySpan<byte> pcm, int frequency, int channels, SampleFormat format);

    // --- Playback control (per-source) ---
    void Play(AudioSourceHandle source);
    void Stop(AudioSourceHandle source);
    void Pause(AudioSourceHandle source);
    void SetSourceBuffer(AudioSourceHandle source, AudioBufferHandle buffer);
    void QueueBuffers(AudioSourceHandle source, ReadOnlySpan<AudioBufferHandle> buffers);
    int  UnqueueProcessedBuffers(AudioSourceHandle source, Span<AudioBufferHandle> output);

    // --- Source properties ---
    void SetSourcePosition(AudioSourceHandle source, Vector3 position);
    void SetSourceVelocity(AudioSourceHandle source, Vector3 velocity);
    void SetSourceGain(AudioSourceHandle source, float gain);
    void SetSourcePitch(AudioSourceHandle source, float pitch);
    void SetSourceLooping(AudioSourceHandle source, bool loop);
    bool IsSourcePlaying(AudioSourceHandle source);

    // --- Capture (optional) ---
    IAudioCaptureDevice? OpenCaptureDevice(string? device, int sampleRate, SampleFormat format, int bufferSize);
}
```

`AudioSourceHandle` and `AudioBufferHandle` are lightweight value types (uint wrapper) so transport implementations can map them to their native handles (OpenAL `uint`, NAudio index, etc.).

#### `OpenALTransport`

The current `ListenerContext` code refactored: `AL.GetApi()`, `ALContext`, `Device*`, `Context*`, `MakeCurrent()`, `VerifyError()`, `GenSource`, `GenBuffer`, `BufferData`, `SourcePlay`, etc. All capture extension support. All format extensions (`VorbisFormat`, `MP3Format`, etc.).

#### `NAudioTransport` (future)

Uses `WaveOutSdl` or `WasapiOut` for output. Manages a mixer that mixes active sources into the output stream. Buffer data stored in managed arrays. No OpenAL dependency.

### 3.2  `IAudioEffectsProcessor` — the DSP pipeline

Processes audio buffers before they reach the transport. Runs per-source and/or per-listener-mix.

```csharp
/// <summary>
/// Abstraction over the audio DSP pipeline. Processes audio buffers
/// before they reach the transport layer for final output.
/// Implementations: OpenALEfxProcessor, SteamAudioProcessor, PassthroughProcessor.
/// </summary>
public interface IAudioEffectsProcessor : IDisposable
{
    // --- Lifecycle ---
    void Initialize(AudioEffectsSettings settings);
    void Shutdown();

    // --- Per-frame tick (run simulation, gather results) ---
    void Tick(float deltaTime);

    // --- Listener state (for processors that need listener pose) ---
    void SetListenerPose(Vector3 position, Vector3 forward, Vector3 up);

    // --- Per-source processing ---
    /// <summary>
    /// Register a source for processing. Returns an opaque handle the processor tracks.
    /// </summary>
    EffectsSourceHandle AddSource(AudioEffectsSourceSettings settings);
    void RemoveSource(EffectsSourceHandle source);

    /// <summary>
    /// Update source spatial state for simulation.
    /// </summary>
    void SetSourcePose(EffectsSourceHandle source, Vector3 position, Vector3 forward);

    /// <summary>
    /// Process a mono/stereo input buffer through the effect chain.
    /// Returns the processed output buffer (may be different channel count, e.g. binaural stereo).
    /// For passthrough, this returns the input unchanged.
    /// For OpenAL EFX, this is a no-op (effects applied in-transport via EFX slots).
    /// For Steam Audio, this runs the Phonon DSP chain.
    /// </summary>
    void ProcessBuffer(EffectsSourceHandle source, ReadOnlySpan<float> input, Span<float> output, int channels, int sampleRate);

    // --- Scene geometry (only relevant for physics-based processors) ---
    bool SupportsSceneGeometry { get; }
    void SetScene(IAudioScene? scene);

    // --- Capabilities ---
    bool SupportsHRTF { get; }
    bool SupportsOcclusion { get; }
    bool SupportsReflections { get; }
    bool SupportsPathing { get; }
}
```

#### `OpenALEfxProcessor`

Wraps the existing `EffectContext` + `EffectExtension`. Effects are applied **in-transport** via OpenAL EFX auxiliary send slots — `ProcessBuffer` is a no-op because OpenAL handles the DSP internally. Requires `OpenALTransport`.

#### `SteamAudioProcessor`

The core new implementation. Owns:

| Handle | Purpose |
|---|---|
| `IPLContext` | Top-level Steam Audio context |
| `IPLHRTF` | Loaded HRTF for binaural rendering |
| `IPLSimulator` | Runs direct / reflection / pathing simulation each tick |
| `IPLAudioSettings` | Sample rate, frame size |
| Per-source `IPLSource` + effect chain | `IPLDirectEffect`, `IPLBinauralEffect`, `IPLReflectionEffect`, `IPLPathEffect` |

`ProcessBuffer` runs the full Phonon DSP chain:
```
Mono PCM ──► IPLDirectEffect (occlusion, distance atten, air absorption)
          ──► IPLBinauralEffect (HRTF spatialization)
          ──► [optional] IPLReflectionEffect → ambisonics decode
          ──► [optional] IPLPathEffect → ambisonics decode
          ──► Output: stereo binaural PCM
```

Works with **any transport** — the processed stereo buffer is handed to the transport for final playback.

#### `PassthroughProcessor`

No-op. `ProcessBuffer` copies input to output. Zero overhead. Useful for testing or when the transport handles all spatialization natively (e.g., OpenAL distance model without EFX).

### 3.3  Refactored `ListenerContext`

`ListenerContext` becomes a **concrete class** that composes a transport and effects processor. It is no longer OpenAL-specific. Downstream code (`AudioListenerComponent`, `AudioSourceComponent`, `XRWorldInstance`) continues to use it unchanged.

```csharp
public sealed class ListenerContext : XRBase, IDisposable
{
    public string? Name { get; set; }

    /// <summary>The output device layer.</summary>
    public IAudioTransport Transport { get; }

    /// <summary>The DSP effects pipeline (may be null/passthrough).</summary>
    public IAudioEffectsProcessor? Effects { get; }

    // --- Spatial (delegates to transport + effects) ---
    public Vector3 Position  { get; set; }  // → Transport.SetListenerPosition + Effects.SetListenerPose
    public Vector3 Velocity  { get; set; }  // → Transport.SetListenerVelocity
    public Vector3 Forward   { get; set; }
    public Vector3 Up        { get; set; }
    public void SetOrientation(Vector3 forward, Vector3 up);

    // --- Gain / Enable / Fade (same as current) ---
    public float Gain { get; set; }
    public bool Enabled { get; set; }
    public float GainScale { get; set; }
    public float? FadeInSeconds { get; set; }

    // --- Source / Buffer lifecycle (delegates to transport) ---
    public AudioSource TakeSource();
    public AudioBuffer TakeBuffer();
    public void ReleaseSource(AudioSource source);
    public void ReleaseBuffer(AudioBuffer buffer);

    // --- Distance gain (delegates to effects processor or fallback formula) ---
    public float CalcGain(Vector3 worldPos, float refDist, float maxDist, float rolloff);

    // --- Tick ---
    public void Tick(float deltaTime);  // runs fade + Effects.Tick

    public void Dispose();  // disposes both transport and effects
}
```

### 3.4  `AudioSource` and `AudioBuffer`

These become **thinner wrappers** around transport handles, with an optional effects source handle.

```csharp
public sealed class AudioSource : IDisposable, IPoolable
{
    public ListenerContext Listener { get; }
    internal AudioSourceHandle TransportHandle { get; }       // from IAudioTransport
    internal EffectsSourceHandle? EffectsHandle { get; }      // from IAudioEffectsProcessor (if present)

    // All property setters delegate to Transport + Effects:
    public Vector3 Position { set => ... }
    public float Gain { set => ... }
    public float Pitch { set => ... }
    public bool Looping { set => ... }

    // Playback
    public void Play();
    public void Stop();
    public void Pause();

    // Buffer management
    public AudioBuffer? Buffer { get; set; }
    public bool QueueBuffers(int max, params AudioBuffer[] buffers);
    public void UnqueueConsumedBuffers(int count = 0);
}

public sealed class AudioBuffer : XRBase, IDisposable, IPoolable
{
    public ListenerContext Listener { get; }
    internal AudioBufferHandle TransportHandle { get; }

    public void SetData(byte[] data, int frequency, bool stereo);
    public void SetData(short[] data, int frequency, bool stereo);
    public void SetData(float[] data, int frequency, bool stereo);
    public void SetData(AudioData buffer);
}
```

### 3.5  Data flow: source with Steam Audio effects

```
 AudioSourceComponent                ListenerContext
 ─────────────────                   ──────────────────
      │                                  │
      │  source.Buffer = audioData       │
      ├──────────────────────────────────►│
      │                                  │  ─► transport.UploadBufferData(rawPCM)
      │                                  │  ─► effects.AddSource(sourceSettings)
      │                                  │
      │  source.Play()                   │
      │──────────────────────────────────►│
      │                                  │
      │            ┌─ per audio frame ─┐ │
      │            │                   │ │
      │            │  (audio thread)   │ │
      │            │  read raw PCM     │ │
      │            │      │            │ │
      │            │      ▼            │ │
      │            │  SteamAudioProcessor.ProcessBuffer()
      │            │   ├─ direct effect (occlusion)
      │            │   ├─ binaural effect (HRTF)
      │            │   └─ reflection/path (optional)
      │            │      │            │ │
      │            │      ▼            │ │
      │            │  transport.QueueBuffers(processedPCM)
      │            │                   │ │
      │            └───────────────────┘ │
      │                                  │
```

For **OpenAL EFX** the flow is simpler — raw PCM goes directly to the transport, and OpenAL applies EFX filters/sends internally. `ProcessBuffer` is a no-op.

For **Passthrough** — raw PCM goes directly to the transport with no processing.

### 3.6  `AudioListenerComponent` and `AudioSourceComponent`

These components **should not change** beyond removing any direct `Silk.NET.OpenAL` using directives:

- `AudioListenerComponent.Listener` stays typed as `ListenerContext`.
- `AudioSourceComponent.ActiveListeners` stays `ConcurrentDictionary<ListenerContext, AudioSource>`.
- Properties like `DopplerFactor`, `SpeedOfSound` move to `ListenerContext` as virtual/configurable and delegate to the transport.

### 3.7  Steam Audio Scene Geometry Bridge

New component: **`SteamAudioGeometryComponent`** (or integrated into existing mesh components).

Responsibilities:
- On activation, extract triangle data from the owning `SceneNode`'s renderable mesh.
- Create `IPLStaticMesh` or `IPLInstancedMesh` and add to the `SteamAudioProcessor`'s `IPLScene`.
- On transform change (instanced mesh), call `iplInstancedMeshUpdateTransform`.
- On deactivation, remove mesh from scene.

This bridges the engine's visual scene graph with Steam Audio's acoustic scene.

### 3.8  Steam Audio Material System

- Define `SteamAudioMaterial` (absorption, scattering, transmission coefficients per frequency band).
- Allow per-material or per-mesh assignment.
- Map to `IPLMaterial` structs when building static meshes.

### 3.9  `AudioManager` Configuration

```csharp
public enum AudioTransportType  { OpenAL, NAudio }
public enum AudioEffectsType    { OpenAL_EFX, SteamAudio, Passthrough }

public class AudioManager : XRBase
{
    public AudioTransportType DefaultTransport { get; set; } = AudioTransportType.OpenAL;
    public AudioEffectsType   DefaultEffects   { get; set; } = AudioEffectsType.OpenAL_EFX;

    public ListenerContext NewListener(
        string? name = null,
        AudioTransportType? transport = null,
        AudioEffectsType? effects = null)
    {
        var t = transport ?? DefaultTransport;
        var e = effects ?? DefaultEffects;

        IAudioTransport tp = t switch
        {
            AudioTransportType.OpenAL => new OpenALTransport(),
            AudioTransportType.NAudio => new NAudioTransport(),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };

        IAudioEffectsProcessor? ep = e switch
        {
            AudioEffectsType.OpenAL_EFX   => new OpenALEfxProcessor(tp),
            AudioEffectsType.SteamAudio   => new SteamAudioProcessor(),
            AudioEffectsType.Passthrough  => null, // or new PassthroughProcessor()
            _ => throw new ArgumentOutOfRangeException(nameof(effects))
        };

        var listener = new ListenerContext(tp, ep) { Name = name };
        // … existing registration / disposed callback …
        return listener;
    }
}
```

### 3.10  Compatibility constraints

Not all transport types are compatible with all effects processors. Compatibility is determined by required runtime capabilities (context type, effect API availability, channel/sample format, and streaming model).

| Transport | Effects | Notes |
|---|---|---|
| OpenAL | OpenAL EFX | Requires Creative EFX extension on the device. Falls back to Passthrough if unavailable. |
| OpenAL | Steam Audio | Works. Phonon processes PCM, then streams to OpenAL for output. |
| OpenAL | Passthrough | Works. Simple distance-model spatialization via OpenAL. |
| NAudio | OpenAL EFX | **Invalid.** EFX requires an OpenAL context. `AudioManager` should reject or auto-replace with Passthrough. |
| NAudio | Steam Audio | Works. Phonon processes PCM, then NAudio outputs. |
| NAudio | Passthrough | Works. Flat playback, no spatial processing. |

#### Capability checklist (must pass before a pairing is considered supported)

| Capability | Why it matters | OpenAL + EFX | OpenAL + Steam Audio | OpenAL + Passthrough | NAudio + Steam Audio | NAudio + Passthrough | NAudio + EFX |
|---|---|---|---|---|---|---|---|
| Requires OpenAL context/device | EFX API is OpenAL extension-based | ✅ Required | ✅ Required (transport only) | ✅ Required (transport only) | ❌ Not required | ❌ Not required | ❌ Missing → invalid |
| Accepts processed PCM input | Effects output must be played by transport | ✅ | ✅ | ✅ | ✅ | ✅ | N/A |
| Supports streaming queue model | Needed for dynamic/process-per-frame pipelines | ✅ Native | ✅ Native | ✅ Native | ⚠️ Implement in mixer | ⚠️ Implement in mixer | N/A |
| Supports mono source path | Steam Audio direct/binaural path expects mono point source input | ✅ | ✅ Required | Optional | ✅ Required | Optional | N/A |
| Supports stereo output path | Steam Audio binaural output is stereo | ✅ | ✅ Required | Optional | ✅ Required | Optional | N/A |
| EFX extension available | Needed for OpenAL EFX processor | ✅ Required | ❌ Not required | ❌ Not required | ❌ Not required | ❌ Not required | ❌ Missing → invalid |
| Simulation parameter bridge (game thread → audio thread) | Needed for Steam Audio source/listener updates | ❌ Not needed | ✅ Required | ❌ Not needed | ✅ Required | ❌ Not needed | ❌ Not needed |

Legend: ✅ built-in/required, ⚠️ engine-side implementation required, ❌ unavailable or not applicable.

---

## 4  File / Folder Layout (Proposed)

```
XREngine.Audio/
  ListenerContext.cs               ← refactored (composes transport + effects)
  AudioSource.cs                   ← refactored (transport handle + effects handle)
  AudioBuffer.cs                   ← refactored (transport handle wrapper)
  AudioManager.cs                  ← transport + effects factory
  AudioInputDevice.cs              ← stays OpenAL-specific for now (capture)
  XRAudioUtil.cs                   ← unchanged

  Abstractions/                    ← NEW folder
    IAudioTransport.cs
    IAudioEffectsProcessor.cs
    IAudioCaptureDevice.cs
    IAudioScene.cs                 ← geometry bridge interface
    AudioSourceHandle.cs           ← value type
    AudioBufferHandle.cs           ← value type
    EffectsSourceHandle.cs         ← value type
    SampleFormat.cs                ← enum (Byte, Short, Float)
    AudioEffectsSettings.cs
    AudioEffectsSourceSettings.cs

  OpenAL/                          ← extracted from current root
    OpenALTransport.cs             ← refactored from ListenerContext.cs
    OpenALEfxProcessor.cs          ← refactored from EffectContext + effects
    OpenALCaptureDevice.cs         ← refactored from AudioInputDevice.cs
    Effects/                       ← moved from Effects/
      EffectContext.cs             ← now internal to OpenALEfxProcessor
      *.cs                         (EAXReverb, Echo, Chorus, …)

  Steam/                           ← existing bindings + new wrappers
    Phonon.cs                      (existing, unchanged)
    OpaqueHandles.cs               (existing, unchanged)
    Delegates.cs                   (existing, unchanged)
    Enums/                         (existing, unchanged)
    Structs/                       (existing, unchanged)
    SteamAudioProcessor.cs        ← NEW (implements IAudioEffectsProcessor)
    SteamAudioScene.cs            ← NEW (implements IAudioScene, wraps IPLScene)
    SteamAudioSimulator.cs        ← NEW (managed wrapper for IPLSimulator)
    SteamAudioHRTF.cs             ← NEW (managed wrapper for IPLHRTF)
    SteamAudioMaterial.cs         ← NEW (material definitions)

  NAudio/                          ← future, placeholder
    NAudioTransport.cs

XRENGINE/Scene/Components/Audio/
    AudioListenerComponent.cs      ← minor: remove OpenAL usings
    AudioSourceComponent.cs        ← minor: use refactored AudioSource/AudioBuffer
    SteamAudioGeometryComponent.cs ← NEW (bridges scene meshes to IAudioScene)
```

---

## 5  Migration Strategy

### Phase 0 — OpenAL safety net (must complete before refactor)

1. Add a feature flag that keeps the current OpenAL path as the default hard path (`AudioArchitectureV2 = false` by default).
2. Define OpenAL regression checklist (listener position/orientation, source playback states, streaming queue/unqueue behavior, capture path, EFX path).
3. Add focused unit/integration coverage for current OpenAL behavior before structural changes.
4. Add lightweight runtime diagnostics so parity failures are visible quickly (source state transitions, buffer underflow, queue depth).
5. **Gate: baseline OpenAL behavior is codified and passing before any transport/effects split lands.**

### Phase 1 — Define abstractions, extract OpenAL transport (no behavior change)

1. Define `IAudioTransport`, `IAudioEffectsProcessor`, handle value types, `SampleFormat` enum.
2. Extract OpenAL device/context/source/buffer management from `ListenerContext` into `OpenALTransport`.
3. Extract `EffectContext` usage into `OpenALEfxProcessor` (implements `IAudioEffectsProcessor`).
4. Refactor `ListenerContext` to compose `IAudioTransport` + `IAudioEffectsProcessor?`.
5. Refactor `AudioSource` / `AudioBuffer` to hold transport handles via the abstraction.
6. Keep file moves (`OpenAL/` subfolder) as the **final step** of the phase to avoid mixing semantic and structural churn.
7. Update `AudioManager.NewListener()` to create `OpenALTransport` + `OpenALEfxProcessor` explicitly, while preserving the old path behind feature flag for fast rollback.
8. Verify all component code compiles and works unchanged.
9. Run OpenAL regression checklist from Phase 0.
10. **Gate: full editor build + run with no regressions; `AudioArchitectureV2` can be disabled for immediate fallback.**

### Phase 2 — Passthrough processor and transport abstraction validation

1. Implement `PassthroughProcessor` (no-op `ProcessBuffer`).
2. Verify `OpenAL transport + Passthrough effects` works (basic distance-model spatialization, no EFX).
3. Add `AudioTransportType` / `AudioEffectsType` enums and configuration to `AudioManager`.
4. Add compatibility validation (reject invalid combos like NAudio + EFX).
5. **Gate: can switch between OpenAL+EFX and OpenAL+Passthrough at runtime.**

### Phase 3 — Steam Audio effects processor

1. Implement `SteamAudioProcessor` with `IPLContext` + `IPLAudioSettings` + `IPLHRTF` initialization.
2. Implement per-source effect chain (`IPLDirectEffect` → `IPLBinauralEffect`).
3. Implement `ProcessBuffer` (mono in → binaural stereo out).
4. Wire into `ListenerContext` audio processing loop.
5. Keep Steam Audio path opt-in only (default remains OpenAL+EFX).
6. Run OpenAL regression checklist to ensure no regressions in default path.
7. **Gate: can create an OpenAL+SteamAudio listener, play a mono sound with HRTF spatialization, while default OpenAL path remains unchanged.**

### Phase 4 — Scene geometry and direct simulation

1. Define `IAudioScene` interface.
2. Implement `SteamAudioScene` managed wrapper (create/commit scene, add/remove static + instanced meshes).
3. Implement `SteamAudioGeometryComponent` that feeds mesh triangles into the Phonon scene.
4. Implement `SteamAudioSimulator` wrapper (direct simulation: occlusion, distance attenuation, air absorption).
5. Wire simulation outputs into `SteamAudioProcessor` per-source `IPLDirectEffect`.
6. Define `SteamAudioMaterial` type and mapping.
7. **Gate: sources are occluded by scene geometry.**

### Phase 5 — Advanced simulation

1. Reflection simulation (`iplSimulatorRunReflections` → `IPLReflectionEffect`).
2. Pathing simulation (`iplSimulatorRunPathing` → `IPLPathEffect`).
3. Probe baking workflow for static scenes.
4. Ambisonics encode/decode pipeline for environmental audio.
5. **Gate: reflections audible in enclosed spaces.**

### Phase 6 — NAudio transport (optional)

1. Implement `NAudioTransport` using `WaveOutSdl` or `WasapiOut`.
2. Implement managed mixer for multi-source mixing.
3. Validate NAudio + SteamAudio and NAudio + Passthrough combinations.
4. Keep NAudio transport behind explicit opt-in; do not change default transport.
5. Run OpenAL regression checklist after merge.
6. **Gate: full audio playback without OpenAL dependency, with OpenAL default unaffected.**

### Phase 7 — Polish and editor integration

1. Editor UI for transport/effects selection in audio settings.
2. Editor UI for Steam Audio material assignment.
3. Editor UI for probe placement and baking.
4. Runtime transport/effects switching (hot-swap).
5. Performance profiling and hot-path allocation audit (§11 of AGENTS.md).
6. Documentation updates.

---

## 6  Risks and Open Questions

| # | Item | Notes |
|---|---|---|
| 1 | **phonon.dll distribution** | Must ship native DLL. Need a build/fetch script similar to other native deps under `Tools/Dependencies/`. Confirm license compatibility (Steam Audio is free, BSD-like license). |
| 2 | **Audio thread model** | Steam Audio `ProcessBuffer` runs on the audio thread. OpenAL EFX runs effects in-driver. Need to decide: does the engine spin its own audio processing thread, or piggyback on OpenAL's streaming callback? |
| 3 | **Hybrid latency** | Routing Phonon output through OpenAL streaming adds a buffer hop. Tunable via frame size / buffer count. NAudio WASAPI exclusive mode is the low-latency alternative. |
| 4 | **Thread safety** | Phonon simulation runs per-tick. Scene geometry updates must be synchronized. Use double-buffering or commit-on-tick pattern. |
| 5 | **Multiple listeners** | Steam Audio simulation is per-listener. Multiple listeners multiply simulation cost. May want to limit to one Steam Audio listener. |
| 6 | **AudioInputDevice / Capture** | Currently OpenAL-only. Steam Audio doesn't handle capture. Capture stays on the transport layer regardless. `IAudioTransport.OpenCaptureDevice` handles this. |
| 7 | **OpenAL EFX + transport coupling** | OpenAL EFX requires an OpenAL context to function — it's not a standalone DSP library. The `OpenALEfxProcessor` is only valid when paired with `OpenALTransport`. This is an inherent constraint, not a design flaw. Enforce at construction time. |
| 8 | **ProcessBuffer overhead** | For OpenAL EFX, `ProcessBuffer` is a no-op (effects happen in-driver). For Steam Audio, it's real CPU work per source per frame. Must profile and potentially offload to a job. |
| 9 | **DistanceModel / DopplerFactor** | These are transport-level concepts (OpenAL global state). Steam Audio computes its own distance attenuation via `IPLDistanceAttenuationModel`. Expose as transport properties; effects processor can override the result. |

---

## 7  TODO Checklist

### Phase 0 — OpenAL safety net (pre-work)

- [x] Add `AudioArchitectureV2` feature flag (default `false`) — `AudioSettings.cs`
- [x] Keep legacy OpenAL path callable for rollback until Phase 3 is stable — flag gates future changes
- [x] Define OpenAL regression checklist (listener pose, source state transitions, queue/unqueue, capture, EFX) — `OpenALRegressionTests.cs`
- [x] Add/verify targeted tests for existing OpenAL behavior — `OpenALRegressionTests.cs`, `AudioDiagnosticsTests.cs`
- [x] Add lightweight audio diagnostics for parity checks — `AudioDiagnostics.cs` wired into `ListenerContext`, `AudioSource`
- [x] Gate: baseline OpenAL behavior passing before architecture split work

### Phase 1 — Abstractions + OpenAL extraction

- [x] Create `Abstractions/` folder with `IAudioTransport.cs`, `IAudioEffectsProcessor.cs`
- [x] Define `AudioSourceHandle`, `AudioBufferHandle`, `EffectsSourceHandle` value types
- [x] Define `SampleFormat` enum, `AudioEffectsSettings`, `AudioEffectsSourceSettings`
- [x] Define `IAudioCaptureDevice`, `IAudioScene` interfaces
- [x] Extract OpenAL device/context management from `ListenerContext` into `OpenALTransport`
- [x] Extract `EffectContext` integration into `OpenALEfxProcessor`
- [x] Refactor `ListenerContext` to compose `IAudioTransport` + `IAudioEffectsProcessor?`
- [ ] Refactor `AudioSource` to hold `AudioSourceHandle` + `EffectsSourceHandle?`
- [ ] Refactor `AudioBuffer` to hold `AudioBufferHandle`
- [ ] Move OpenAL files into `XREngine.Audio/OpenAL/` (after semantic refactor is green)
- [x] Update `AudioManager.NewListener()` → creates `OpenALTransport` + `OpenALEfxProcessor`
- [x] Preserve legacy OpenAL path behind feature flag until Phase 3 completes
- [x] Fix all `using` directives in consuming projects
- [x] Build full solution — zero errors, zero new warnings
- [ ] Run editor, verify audio playback works identically
- [x] Run OpenAL regression checklist and compare with baseline

### Phase 2 — Passthrough + configuration

- [x] Implement `PassthroughProcessor` (no-op `ProcessBuffer`)
- [x] Add `AudioTransportType` / `AudioEffectsType` enums
- [x] Add transport/effects configuration to `AudioManager`
- [x] Add combo validation (reject invalid pairings)
- [x] Verify OpenAL + Passthrough works
- [x] Verify OpenAL + EFX works (regression)

### Phase 3 — Steam Audio effects processor

- [x] Implement `SteamAudioProcessor.Initialize()` (IPLContext, IPLAudioSettings, IPLHRTF)
- [x] Implement per-source effect chain management (AddSource/RemoveSource)
- [x] Implement `ProcessBuffer()` (IPLDirectEffect → IPLBinauralEffect → output)
- [x] Implement `Tick()` for simulation stepping
- [x] Implement `SetListenerPose()` / `SetSourcePose()`
- [x] Wire into `ListenerContext` audio processing path
- [x] Keep Steam Audio selection opt-in only (do not alter default)
- [x] Add compatibility guardrails in `AudioManager` (reject unsupported pairings)
- [ ] Verify: OpenAL transport + Steam Audio effects, play HRTF-spatialized mono sound
- [x] Re-run OpenAL regression checklist (default path must remain identical)

### Phase 4 — Scene geometry + direct simulation

- [ ] Implement `SteamAudioScene` (IPLScene create/commit, static + instanced mesh management)
- [ ] Implement `SteamAudioGeometryComponent` (extract triangles, create IPLStaticMesh)
- [ ] Handle instanced mesh transform updates
- [ ] Implement `SteamAudioSimulator` (direct simulation: occlusion, distance atten, air absorption)
- [ ] Wire simulation into `SteamAudioProcessor` per-source IPLDirectEffect
- [ ] Define `SteamAudioMaterial` type and mapping
- [ ] Verify: sound is occluded by geometry

### Phase 5 — Advanced simulation

- [ ] Reflection simulation integration
- [ ] Path simulation integration
- [ ] Probe array / probe batch management
- [ ] Baking workflow (reflections + pathing)
- [ ] Ambisonics pipeline for environmental audio

### Phase 6 — NAudio transport (optional)

- [ ] Implement `NAudioTransport` (WaveOutSdl or WasapiOut)
- [ ] Implement managed multi-source mixer
- [ ] Validate NAudio + SteamAudio
- [ ] Validate NAudio + Passthrough
- [ ] Keep NAudio transport opt-in only (OpenAL remains default)
- [ ] Re-run OpenAL regression checklist after integration

### Phase 7 — Polish

- [ ] Editor UI: transport/effects picker in audio settings
- [ ] Editor UI: Steam Audio material assignment panel
- [ ] Editor UI: probe placement tool
- [ ] Runtime transport/effects hot-swap
- [ ] Performance audit: zero per-frame allocations in audio tick path
- [ ] phonon.dll fetch/build script under `Tools/Dependencies/`
- [ ] License audit: verify Steam Audio license in `docs/DEPENDENCIES.md`
- [ ] Update `docs/` with audio architecture notes
- [ ] Update this work doc with final state

---

## 9  References

- [Steam Audio SDK Documentation](https://valvesoftware.github.io/steam-audio/)
- [Steam Audio GitHub (valve)](https://github.com/ValveSoftware/steam-audio) — BSD-like license
- Existing bindings: `XREngine.Audio/Steam/Phonon.cs` (v4.6 API surface)
- `docs/OPENAL_STREAMING_NOTES.md` — existing notes on OpenAL streaming patterns

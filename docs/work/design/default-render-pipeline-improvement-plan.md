# DefaultRenderPipeline Improvement Plan

**Goal:** Maximize readability, minimize out-of-render-pipeline processing, and fully leverage the new VPRC command inventory (including `VPRC_CacheOrCreateBuffer`, `VPRC_CacheOrCreateRenderBuffer`, `VPRC_BindBuffer`, `VPRC_PushShaderGlobals`, and `VPRC_SetVariable`).

---

## 1. Executive Summary

The `DefaultRenderPipeline` has grown organically into ~2500+ lines across five partial files, with a ~500-line monolithic `CreateViewportTargetCommands()` method, ~15 `SettingUniforms` event handlers that perform GPU work outside the command chain, ~300 lines of light probe SSBO management as raw field manipulation, and ~3 `VPRC_Manual` escape hatches. The pipeline now has **43 implemented VPRC command types** — including scoped buffer binding, shader globals push, and data buffer lifecycle commands — but the default pipeline only uses a fraction of them.

This plan proposes a phased migration that:
1. Moves all GPU state mutation into the declarative command chain
2. Decomposes monolithic methods into named, composable sub-chains
3. Brings SSBO/buffer lifecycle under `VPRC_CacheOrCreateBuffer` management
4. Replaces `VPRC_Manual` with first-class commands
5. Converts `SettingUniforms` callbacks to `VPRC_PushShaderGlobals` + `VPRC_BindTexture` + `VPRC_BindBuffer` scopes

---

## 2. Current Architecture Analysis

### 2.1 File Layout

| File | Lines | Purpose |
|------|-------|---------|
| `DefaultRenderPipeline.cs` | ~2500 | Constants, fields, command chain generation, AO configuration, probe SSBO management, SettingUniforms for LightCombine |
| `DefaultRenderPipeline.Textures.cs` | ~500 | ~40+ texture factory methods |
| `DefaultRenderPipeline.FBOs.cs` | ~810 | ~20+ FBO factory methods with SettingUniforms subscriptions |
| `DefaultRenderPipeline.PostProcessing.cs` | ~1400 | Post-process schema, uniform application, bloom/DoF/motion blur handlers |
| `DefaultRenderPipeline.ExactTransparency.cs` | ~430 | PPLL/depth-peeling buffer management, FBO factories, VPRC_Manual usage |

### 2.2 Command Chain Structure (CreateViewportTargetCommands)

The current method is a single ~500-line block that sequentially builds:

```
TemporalAccumulation.Begin
├── CacheTextures (40+ textures)
├── VoxelConeTracing (conditional)
├── PushViewportRenderArea (internal resolution)
│   ├── AO Switch (8 modes)
│   ├── MSAA GBuffer FBO caching
│   ├── Deferred GBuffer geometry render
│   ├── MSAA GBuffer resolve (conditional)
│   ├── Forward pre-pass (conditional, 2 sub-strategies)
│   ├── AO resolve switch
│   ├── LightCombine FBO cache + render
│   ├── MSAA mark + light volumes (conditional)
│   ├── Forward pass w/ MSAA branching
│   ├── MSAA resolve blit (conditional)
│   ├── WB-OIT transparent accumulation + resolve
│   ├── Exact transparency (PPLL + depth peeling)
│   ├── Velocity pass
│   ├── Bloom
│   ├── Motion blur / DoF (conditional)
│   ├── Temporal accumulation
│   ├── Post-temporal transparent + on-top forward
│   ├── Post-process FBO cache
│   ├── Debug viz FBO caching (5 conditional blocks)
│   ├── FXAA / TSR resource caching
│   └── FXAA / TSR upscale chain
├── Exposure update
├── Temporal commit
├── PushViewportRenderArea (full resolution)
│   ├── Final output selection (debug viz / vendor upscale / FXAA / TSR / direct)
│   └── PostRender + UI
└── return
```

### 2.3 Out-of-Pipeline Processing Inventory

#### 2.3.1 SettingUniforms Event Handlers (15 total)

These run as FBO callbacks during quad renders, completely invisible to the command chain:

| Handler | Subscribes In | What It Does |
|---------|---------------|--------------|
| `PostProcessFBO_SettingUniforms` | FBOs.cs:66 | Sets ~50+ uniforms: vignette, color grading, chromatic aberration, fog, lens distortion, bloom, tonemapping |
| `LightCombineFBO_SettingUniforms` | FBOs.cs:787 | Sets AO uniforms, binds 5+ SSBOs (probe positions, params, tetra, grid cells, grid indices), binds 2 texture arrays |
| `FxaaFBO_SettingUniforms` | FBOs.cs:144 | Sets FXAA texel step |
| `TsrUpscaleFBO_SettingUniforms` | FBOs.cs:182 | Sets ~15 TSR uniforms (jitter, feedback, variance, catmull, depth reject) |
| `TransparentResolveFBO_SettingUniforms` | FBOs.cs:258 | Binds OIT resolve textures |
| `TransparentAccumulationDebugFBO_SettingUniforms` | FBOs.cs:269 | Binds debug texture |
| `TransparentRevealageDebugFBO_SettingUniforms` | FBOs.cs:276 | Binds debug texture |
| `TransparentOverdrawDebugFBO_SettingUniforms` | FBOs.cs:283 | Binds debug textures |
| `BrightPassFBO_SettingUniforms` | PostProcessing.cs | Bloom threshold uniforms |
| `DepthOfFieldFBO_SettingUniforms` | PostProcessing.cs | DoF mode, focus, aperture, CoC |
| `MotionBlurFBO_SettingUniforms` | PostProcessing.cs | Motion blur velocity scale, samples, texel size |
| `TemporalAccumulationFBO_SettingUniforms` | PostProcessing.cs | TAA jitter, feedback, history readiness |
| `PpllResolveFBO_SettingUniforms` | ExactTransparency.cs | PPLL head pointer + screen size uniforms |
| `PpllResolveMaterial_SettingUniforms` | ExactTransparency.cs | Binds PPLL node + counter SSBOs |
| `PpllFragmentCountDebugFBO_SettingUniforms` | ExactTransparency.cs | Binds debug texture |

#### 2.3.2 Direct SSBO/Buffer Management (not in command chain)

**Light Probe System** (~300 lines in main file):
- `_probePositionBuffer` — SSBO, binding 0, rebuilt on probe config change
- `_probeParamBuffer` — SSBO, binding 2, stores influence/proxy data
- `_probeTetraBuffer` — SSBO, binding 1, Delaunay tetrahedralization indices
- `_probeGridCellBuffer` — SSBO, binding 3, uniform grid acceleration cells
- `_probeGridIndexBuffer` — SSBO, binding 4, grid cell probe index lists
- `_probeIrradianceArray` — Texture2DArray, sampler unit 7
- `_probePrefilterArray` — Texture2DArray, sampler unit 8

All created/destroyed manually in `BuildProbeResources()` / `ClearProbeResources()`, bound in `LightCombineFBO_SettingUniforms()`.

**PPLL System** (~100 lines in ExactTransparency.cs):
- `_ppllNodeBuffer` — SSBO, binding 24, per-pixel linked list nodes
- `_ppllCounterBuffer` — SSBO, binding 25, atomic counter

Created/resized in `EnsureExactTransparencyBuffers()`, bound in `PpllResolveMaterial_SettingUniforms()`, reset via `VPRC_Manual`.

#### 2.3.3 VPRC_Manual Escape Hatches (3 total)

| Location | Lambda | Purpose |
|----------|--------|---------|
| ExactTransparency.cs:146 | `ResetPpllResources` | Clears PPLL head pointers + resets counter via compute dispatch |
| ExactTransparency.cs:161 | `_activeDepthPeelLayerIndex = capture` | Sets depth peel layer for shader queries |
| ExactTransparency.cs:170 | `_activeDepthPeelLayerIndex = -1` | Resets depth peel layer index |

---

## 3. Proposed Changes

### Phase 1: Structural Decomposition (Readability)

**Goal:** Break `CreateViewportTargetCommands()` into named, self-contained builder methods.

#### 3.1.1 Extract Named Sub-Chain Builders

Replace the monolithic method with composable sections:

```csharp
private ViewportRenderCommandContainer CreateViewportTargetCommands()
{
    ViewportRenderCommandContainer c = new(this);
    bool enableCompute = EnableComputeDependentPasses;
    bool bypassVendorUpscale = /* ... */;

    c.Add<VPRC_TemporalAccumulationPass>().Phase = EPhase.Begin;
    CacheTextures(c);

    if (enableCompute)
        AppendVoxelConeTracingPass(c);

    c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
    c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

    using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
    {
        AppendAmbientOcclusionSwitch(c, enableCompute);
        AppendDeferredGBufferPass(c);
        AppendForwardDepthPrePass(c);
        AppendAmbientOcclusionResolve(c);
        AppendLightingPass(c, enableCompute);
        AppendForwardPass(c, enableCompute);
        AppendTransparencyPasses(c);
        AppendVelocityPass(c);
        AppendBloomPass(c);
        AppendMotionBlurAndDoF(c);
        AppendTemporalAccumulation(c);
        AppendPostTemporalForwardPasses(c);
        AppendPostProcessResourceCaching(c);
        AppendDebugVisualizationCaching(c);
        AppendAntiAliasingResourceCaching(c);
    }

    AppendFxaaTsrUpscaleChain(c);
    AppendExposureUpdate(c);
    AppendTemporalCommit(c);
    AppendFinalOutput(c, bypassVendorUpscale);

    c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
    c.Add<VPRC_RenderScreenSpaceUI>();
    return c;
}
```

Each `Append*` method is 20–60 lines max, self-documenting, and independently testable.

#### 3.1.2 Group CacheTextures by Subsystem

Split the ~200-line `CacheTextures()` into logical groups:

```csharp
private void CacheTextures(ViewportRenderCommandContainer c)
{
    CacheGBufferTextures(c);          // depth/stencil, albedo, normal, RMSE, transformId
    CacheHistoryTextures(c);          // history depth, views
    CacheMsaaDeferredTextures(c);     // MSAA variants of GBuffer
    CacheLightingTextures(c);         // diffuse, HDR scene, BRDF
    CacheVelocityTextures(c);         // velocity
    CacheTemporalTextures(c);         // temporal input, accumulation, history
    CachePostProcessTextures(c);      // bloom, auto-exposure, post-process output
    CacheTransparencyTextures(c);     // accum, revealage, scene copy
    CacheExactTransparencyTextures(c);// PPLL, depth peel
    CacheGITextures(c);              // ReSTIR, light volumes, radiance cascades, surfel
    CacheAOTextures(c);              // AO intensity, noise, blur intermediate
}
```

#### 3.1.3 Move to a Dedicated Partial File

Create `DefaultRenderPipeline.CommandChain.cs` containing:
- `CreateViewportTargetCommands()` (now slim)
- All `Append*` sub-chain builders
- `CreateFBOTargetCommands()` (already small)
- `CreateFinalBlitCommands()`

This isolates the command chain from resource creation (Textures.cs, FBOs.cs) and from post-processing (PostProcessing.cs).

---

### Phase 2: Move SSBOs Into the Command Chain

**Goal:** Use `VPRC_CacheOrCreateBuffer` and `VPRC_BindBuffer` so all buffer lifecycle and binding is visible in the command chain.

#### 3.2.1 Light Probe Buffers

**Current:** 5 SSBOs + 2 texture arrays created in `BuildProbeResources()` (called from `LightCombineFBO_SettingUniforms`), bound inline via `BindTo()`.

**Proposed:** Register probe buffers as pipeline resources and bind them in the command chain before the LightCombine pass:

```csharp
private void AppendLightingPass(ViewportRenderCommandContainer c, bool enableCompute)
{
    // Cache probe buffers as pipeline resources when they exist
    c.Add<VPRC_CacheOrCreateBuffer>().SetOptions(
        LightProbePositionBufferName,
        CreateOrUpdateProbePositionBuffer,
        NeedsRecreateProbeBuffer);

    c.Add<VPRC_CacheOrCreateBuffer>().SetOptions(
        LightProbeParamBufferName,
        CreateOrUpdateProbeParamBuffer,
        NeedsRecreateProbeBuffer);

    // ... similar for tetra, grid cell, grid index buffers

    c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
        LightCombineFBOName,
        CreateLightCombineFBO,
        GetDesiredFBOSizeInternal)
        .UseLifetime(RenderResourceLifetime.Transient);

    // Bind probe SSBOs into scope so the light combine shader can access them
    using (c.AddUsing<VPRC_BindBuffer>(x => { x.BufferName = LightProbePositionBufferName; x.BindingLocation = 0; }))
    using (c.AddUsing<VPRC_BindBuffer>(x => { x.BufferName = LightProbeParamBufferName; x.BindingLocation = 2; }))
    using (c.AddUsing<VPRC_BindBuffer>(x => { x.BufferName = LightProbeTetraBufferName; x.BindingLocation = 1; }))
    using (c.AddUsing<VPRC_BindBuffer>(x => { x.BufferName = LightProbeGridCellBufferName; x.BindingLocation = 3; }))
    using (c.AddUsing<VPRC_BindBuffer>(x => { x.BufferName = LightProbeGridIndexBufferName; x.BindingLocation = 4; }))
    using (c.AddUsing<VPRC_BindTexture>(x => { x.TextureName = LightProbeIrradianceArrayName; x.TextureUnit = 7; }))
    using (c.AddUsing<VPRC_BindTexture>(x => { x.TextureName = LightProbePrefilterArrayName; x.TextureUnit = 8; }))
    using (c.AddUsing<VPRC_PushShaderGlobals>(x =>
    {
        x.BoolUniforms["UseAmbientOcclusion"] = ShouldUseAmbientOcclusion();
        x.IntUniforms["ProbeCount"] = GetActiveProbeCount();
        x.BoolUniforms["UseProbeGrid"] = UseProbeGridAcceleration;
        // ... remaining uniforms
    }))
    {
        // Light combine pass
        RenderLightCombinePass(c);
    }
}
```

**Benefits:**
- Probe buffer lifecycle visible in GPU profilers/debuggers via command names
- Buffer binding scope is explicit — no mystery about what's bound during LightCombine
- `VPRC_CacheOrCreateBuffer` handles recreation policy automatically
- Resource cleanup handled by pipeline instance lifecycle, not manual `Dispose()` calls

#### 3.2.2 PPLL Buffers

**Current:** 2 SSBOs created in `EnsureExactTransparencyBuffers()`, reset via `VPRC_Manual → ResetPpllResources()`, bound in `PpllResolveMaterial_SettingUniforms()`.

**Proposed:**

```csharp
private void AppendExactTransparencyCommands(ViewportRenderCommandContainer c)
{
    if (!ExactTransparencyEnabled)
        return;

    // Register PPLL buffers as pipeline resources
    c.Add<VPRC_CacheOrCreateBuffer>().SetOptions(
        PpllNodeBufferName,
        CreatePpllNodeBuffer,
        NeedsPpllNodeBufferResize)
        .UseAccessPattern(EBufferAccessPattern.ReadWrite);

    c.Add<VPRC_CacheOrCreateBuffer>().SetOptions(
        PpllCounterBufferName,
        CreatePpllCounterBuffer,
        null);

    // Clear PPLL head pointers via compute dispatch (replaces VPRC_Manual)
    c.Add<VPRC_DispatchCompute>().SetOptions(
        "ClearPpllHeadPointers",
        clearPpllComputeProgram,
        (InternalWidth + 15) / 16,
        (InternalHeight + 15) / 16, 1);

    // Bind PPLL SSBOs for the PPLL forward pass
    using (c.AddUsing<VPRC_BindBuffer>(x => { x.BufferName = PpllNodeBufferName; x.BindingLocation = 24; }))
    using (c.AddUsing<VPRC_BindBuffer>(x => { x.BufferName = PpllCounterBufferName; x.BindingLocation = 25; }))
    {
        // Color write disabled, depth-only PPLL collection pass
        // ... existing PPLL forward render commands
    }

    // PPLL resolve with SSBOs still in scope
    using (c.AddUsing<VPRC_BindBuffer>(x => { x.BufferName = PpllNodeBufferName; x.BindingLocation = 24; }))
    using (c.AddUsing<VPRC_BindBuffer>(x => { x.BufferName = PpllCounterBufferName; x.BindingLocation = 25; }))
    {
        c.Add<VPRC_RenderQuadFBO>().FrameBufferName = PpllResolveFBOName;
    }

    // Depth peeling: replace VPRC_Manual with VPRC_SetVariable
    for (int layerIndex = 0; layerIndex < ActiveDepthPeelLayerCount; layerIndex++)
    {
        int capture = layerIndex;
        c.Add<VPRC_SetVariable>().Set("ActiveDepthPeelLayer", capture);
        // ... depth peel layer rendering
    }
    c.Add<VPRC_SetVariable>().Set("ActiveDepthPeelLayer", -1);
}
```

**Benefits:**
- Eliminates all 3 `VPRC_Manual` usages
- PPLL buffer lifecycle and binding explicit in the chain
- Depth peel layer index trackable via `VPRC_SetVariable` instead of mutable field
- Compute dispatch for PPLL clear is a proper command, visible in GPU profiler

---

### Phase 3: Replace SettingUniforms with Command-Chain Equivalents

**Goal:** Move uniform-setting callbacks into `VPRC_PushShaderGlobals` scopes so all GPU state is visible in the command chain.

#### 3.3.1 Classification of Handlers

| Handler | Complexity | Migration Path |
|---------|-----------|----------------|
| `FxaaFBO_SettingUniforms` | 2 uniforms | `VPRC_PushShaderGlobals` — trivial |
| `TransformIdDebugQuadFBO_SettingUniforms` | 1 sampler + 2 uniforms | `VPRC_BindTexture` + `VPRC_PushShaderGlobals` |
| `TransparentResolveFBO_SettingUniforms` | 3 samplers | `VPRC_BindTexture` ×3 |
| `BrightPassFBO_SettingUniforms` | ~5 uniforms | `VPRC_PushShaderGlobals` |
| `MotionBlurFBO_SettingUniforms` | ~8 uniforms | `VPRC_PushShaderGlobals` |
| `DepthOfFieldFBO_SettingUniforms` | ~12 uniforms | `VPRC_PushShaderGlobals` |
| `TemporalAccumulationFBO_SettingUniforms` | ~10 uniforms | `VPRC_PushShaderGlobals` |
| `TsrUpscaleFBO_SettingUniforms` | ~15 uniforms | `VPRC_PushShaderGlobals` |
| `PostProcessFBO_SettingUniforms` | ~50+ uniforms | `VPRC_PushShaderGlobals` — large but mechanical |
| `LightCombineFBO_SettingUniforms` | Uniforms + 5 SSBOs + 2 textures | Phase 2 migration (3.2.1) |
| `PpllResolveFBO_SettingUniforms` | 2 samplers + 3 uniforms | `VPRC_BindTexture` + `VPRC_PushShaderGlobals` |
| `PpllResolveMaterial_SettingUniforms` | 2 SSBOs | Phase 2 migration (3.2.2) |
| Debug handlers (×3) | 1 sampler each | `VPRC_BindTexture` |

#### 3.3.2 Migration Pattern

Before (current):
```csharp
// In CreateFxaaFBO():
fxaaFbo.SettingUniforms += FxaaFBO_SettingUniforms;

// Callback hidden from command chain:
private void FxaaFBO_SettingUniforms(XRRenderProgram program)
{
    float width = Math.Max(1u, FullWidth);
    float height = Math.Max(1u, FullHeight);
    program.Uniform("FxaaTexelStep", new Vector2(1f / width, 1f / height));
}
```

After (proposed):
```csharp
// In AppendFxaaTsrUpscaleChain():
using (c.AddUsing<VPRC_PushShaderGlobals>(x =>
{
    float w = Math.Max(1u, FullWidth);
    float h = Math.Max(1u, FullHeight);
    x.Vector2Uniforms["FxaaTexelStep"] = new Vector2(1f / w, 1f / h);
}))
{
    fxaaUpscale.Add<VPRC_RenderQuadToFBO>().SetTargets(FxaaFBOName, FxaaFBOName);
}
```

The FBO factory becomes a pure resource factory (no event subscriptions), and the uniform setup is visible in the command chain.

#### 3.3.3 PostProcessFBO_SettingUniforms — Special Case

This is the most complex handler (~50+ uniforms from multiple post-process stages). Rather than one giant `VPRC_PushShaderGlobals`, decompose it:

```csharp
private void AppendPostProcessPass(ViewportRenderCommandContainer c)
{
    // Each post-process stage pushes its own uniform scope
    using (c.AddUsing<VPRC_PushShaderGlobals>(x => ApplyVignetteUniforms(x)))
    using (c.AddUsing<VPRC_PushShaderGlobals>(x => ApplyColorGradingUniforms(x)))
    using (c.AddUsing<VPRC_PushShaderGlobals>(x => ApplyBloomUniforms(x)))
    using (c.AddUsing<VPRC_PushShaderGlobals>(x => ApplyTonemappingUniforms(x)))
    using (c.AddUsing<VPRC_PushShaderGlobals>(x => ApplyFogUniforms(x)))
    using (c.AddUsing<VPRC_PushShaderGlobals>(x => ApplyChromaticAberrationUniforms(x)))
    using (c.AddUsing<VPRC_PushShaderGlobals>(x => ApplyLensDistortionUniforms(x)))
    {
        c.Add<VPRC_RenderQuadToFBO>().SetTargets(PostProcessFBOName, PostProcessOutputFBOName);
    }
}
```

Each `Apply*Uniforms` helper is a small pure method that reads post-process settings and writes uniforms to the globals scope. No event subscription plumbing required.

---

### Phase 4: Debug Visualization as Runtime Conditionals

**Goal:** Replace build-time `if (EnableTransformIdVisualization)` FBO caching with runtime `VPRC_ConditionalRender` commands.

#### Current Problem

```csharp
// Build-time decisions baked into the command chain at construction:
if (EnableTransformIdVisualization)
{
    c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
        TransformIdDebugQuadFBOName, ...);
}
```

If settings change at runtime, the entire command chain must be regenerated via `HandleRenderingSettingsChanged()`. This is especially painful for debug overlays that should toggle instantly.

#### Proposed

```csharp
// Runtime conditional — no chain regeneration needed:
var debugViz = c.Add<VPRC_ConditionalRender>();
debugViz.VariableName = "DebugViz_TransformId";
debugViz.Body = new ViewportRenderCommandContainer(this);
debugViz.Body.Add<VPRC_CacheOrCreateFBO>().SetOptions(
    TransformIdDebugQuadFBOName, ...);

// Variable set from settings change handler without chain rebuild:
// pipeline.SetVariable("DebugViz_TransformId", enabled);
```

Apply the same pattern to all 5 debug visualization FBO caching blocks:
- TransformId
- TransparentAccumulation
- TransparentRevealage
- TransparentOverdraw
- DepthPeelingLayer

---

### Phase 5: GPU Profiling Annotations

**Goal:** Add `VPRC_Annotation` and `VPRC_GPUTimerBegin`/`VPRC_GPUTimerEnd` to major pipeline stages.

The command chain executes many logical stages but has zero GPU debug markers. Adding annotations makes the pipeline inspectable in RenderDoc/Nsight without reading C# code:

```csharp
c.Add<VPRC_Annotation>().Label = "Deferred GBuffer";
c.Add<VPRC_GPUTimerBegin>().Name = "GBuffer";
// ... deferred geometry pass
c.Add<VPRC_GPUTimerEnd>();

c.Add<VPRC_Annotation>().Label = "Lighting";
c.Add<VPRC_GPUTimerBegin>().Name = "Lighting";
// ... light combine pass
c.Add<VPRC_GPUTimerEnd>();
```

Suggested annotation points (one per logical stage):
1. `"Texture Caching"` — CacheTextures
2. `"AO Compute"` — AO switch
3. `"Deferred GBuffer"` — geometry render
4. `"MSAA GBuffer Resolve"` — conditional resolve
5. `"Forward Pre-Pass"` — depth+normal pre-pass
6. `"AO Resolve"` — AO blur/resolve
7. `"Lighting"` — LightCombine + light volumes
8. `"Forward Render"` — opaque + masked forward
9. `"GI Composite"` — ReSTIR / light volumes / radiance cascades / surfel
10. `"Transparency"` — WB-OIT + PPLL + depth peel
11. `"Velocity"` — motion vector pass
12. `"Bloom"` — bloom downsample/upsample
13. `"Motion Blur / DoF"` — conditional passes
14. `"Temporal Accumulation"` — TAA/TSR resolve
15. `"Post-Temporal Forward"` — transparent + on-top
16. `"Post-Processing"` — tonemapping, color grading, etc.
17. `"AA Upscale"` — FXAA / TSR
18. `"Final Output"` — swapchain present

---

## 4. Underutilized Commands

The following implemented VPRC commands could improve the pipeline but are not currently used in `DefaultRenderPipeline`:

| Command | Potential Use |
|---------|--------------|
| `VPRC_BindBuffer` | Replace all `BindTo(program, N)` calls in SettingUniforms |
| `VPRC_BindTexture` | Replace sampler binds in SettingUniforms callbacks |
| `VPRC_PushShaderGlobals` | Replace all uniform-setting callbacks |
| `VPRC_SetVariable` | Replace `VPRC_Manual` for depth peel layer index; pass inter-stage data |
| `VPRC_CacheOrCreateBuffer` | Manage probe SSBOs, PPLL buffers in-chain |
| `VPRC_CacheOrCreateRenderBuffer` | Manage MSAA renderbuffers if migrated from textures |
| `VPRC_ConditionalRender` | Runtime debug viz toggles without chain rebuild |
| `VPRC_Annotation` | GPU debug markers in RenderDoc/Nsight |
| `VPRC_GPUTimerBegin/End` | Per-stage GPU timing |
| `VPRC_Fence` | Explicit compute→graphics barriers for GI passes |
| `VPRC_MemoryBarrier` | PPLL clear → draw barrier (currently implicit) |
| `VPRC_DispatchCompute` | PPLL head pointer clear (replaces VPRC_Manual) |
| `VPRC_ForEach` | Depth peel layer iteration (replaces manual for loop + VPRC_Manual) |

---

## 5. Migration Risk Assessment

| Phase | Risk | Mitigation |
|-------|------|-----------|
| Phase 1 | Very Low | Pure refactoring — identical behavior, just better organization |
| Phase 2 | Medium | SSBOs change binding model; must verify shader binding indices match command-chain `BindingLocation` values |
| Phase 3 | Medium | Uniform names must match exactly; one typo = silent rendering regression. Validate with A/B screenshot comparison |
| Phase 4 | Low | ConditionalRender is well-tested; conditionals already work runtime (see MSAA branching) |
| Phase 5 | Very Low | Annotations are no-ops; GPU timers are non-invasive |

---

## 6. Implementation Priority

| Priority | Phase | Scope | Effort |
|----------|-------|-------|--------|
| **P0** | 1 | Decompose `CreateViewportTargetCommands()` into named sub-methods | Small — pure refactor |
| **P0** | 5 | Add `VPRC_Annotation` to all major pipeline stages | Small — additive only |
| **P1** | 2.2 | Migrate PPLL buffers to `VPRC_CacheOrCreateBuffer` + `VPRC_BindBuffer`, eliminate `VPRC_Manual` | Medium |
| **P1** | 3.3 | Migrate simple SettingUniforms (FXAA, debug, OIT resolve) to `VPRC_PushShaderGlobals` | Small |
| **P2** | 2.1 | Migrate light probe SSBOs to `VPRC_CacheOrCreateBuffer` + `VPRC_BindBuffer` | Large — most complex handler |
| **P2** | 3.3 | Migrate complex SettingUniforms (TSR, DoF, motion blur, temporal) | Medium |
| **P2** | 4 | Replace build-time debug viz conditionals with `VPRC_ConditionalRender` | Small |
| **P3** | 3.3 | Migrate `PostProcessFBO_SettingUniforms` (~50+ uniforms) | Large — most uniforms |
| **P3** | 1 | Extract `DefaultRenderPipeline.CommandChain.cs` partial file | Small — file move |

---

## 7. Success Criteria

After full migration:
- **Zero** `VPRC_Manual` commands in the default pipeline
- **Zero** `SettingUniforms +=` subscriptions in FBO factory methods
- **Zero** raw SSBO `BindTo()` calls outside the command chain
- All pipeline SSBOs visible via `VPRC_CacheOrCreateBuffer` in the chain
- All shader state visible via `VPRC_PushShaderGlobals` / `VPRC_BindTexture` / `VPRC_BindBuffer`
- `CreateViewportTargetCommands()` ≤ 80 lines (currently ~500)
- Every major pipeline stage annotated for GPU debugger visibility
- Debug visualization toggleable at runtime without command chain regeneration
- No rendering regressions (validated by A/B screenshot comparison)

---

## 8. Non-Goals

- **Shader refactoring**: This plan does not change any GLSL/HLSL shaders. All shader binding names and indices remain identical.
- **Render pass reordering**: The logical order of passes is correct and well-commented. This plan only restructures the C# code that builds the command chain.
- **New rendering features**: No new visual features. This is a pure architecture/readability improvement.
- **Stereo/mono code deduplication in texture factories**: While the `Textures.cs` file has significant stereo/mono duplication, this is a separate concern. Consider it a follow-up after the command chain migration is stable.

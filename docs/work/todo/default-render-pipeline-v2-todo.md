# DefaultRenderPipeline2 — Phased Implementation Todo

> **Audit note (2026-04-19):** This is now a historical phased plan. The current condensed backlog lives in [default-render-pipeline-follow-up-2026-04-20.md](./default-render-pipeline-follow-up-2026-04-20.md). The corrections below keep this file aligned with the current V1/V2 source layout so it can still be read safely.

> **Strategy:** Create a parallel `DefaultRenderPipeline2` (with matching partial files) alongside the existing `DefaultRenderPipeline`. No changes to the original — it remains the active pipeline until V2 is validated and swapped in via a preference or launch flag.

**Reference:** [design/default-render-pipeline-improvement-plan.md](../design/default-render-pipeline-improvement-plan.md)

---

## File Plan

New files to create (all under `XRENGINE/Rendering/Pipelines/Types/`):

| File | Copied From | Purpose |
|------|-------------|---------|
| `DefaultRenderPipeline2.cs` | `DefaultRenderPipeline.cs` | Class declaration, constants, fields, constructor, settings helpers, and runtime invalidation hooks |
| `DefaultRenderPipeline2.CommandChain.cs` | — (new) | Decomposed command-chain builder with named `Append*` sub-builders plus grouped `CacheTextures()` helpers |
| `DefaultRenderPipeline2.Textures.cs` | `DefaultRenderPipeline.Textures.cs` | Texture/view factory helpers |
| `DefaultRenderPipeline2.FBOs.cs` | `DefaultRenderPipeline.FBOs.cs` | FBO factories plus the current `SettingUniforms` callbacks that later phases aim to remove |
| `DefaultRenderPipeline2.PostProcessing.cs` | `DefaultRenderPipeline.PostProcessing.cs` | Post-process schema and parameter metadata helpers; shader-global migration is still future work |
| `DefaultRenderPipeline2.ExactTransparency.cs` | `DefaultRenderPipeline.ExactTransparency.cs` | Current PPLL/depth-peel implementation; still uses `VPRC_Manual` and material/FBO `SettingUniforms` hooks |

Audit note: V1 now also has `DefaultRenderPipeline.ResourceLogging.cs`. V2 does not currently mirror that diagnostics-only partial.

---

## Phase 0 — Scaffolding ✅
> Copy the existing pipeline to V2 files, rename the class, verify it compiles and can be hot-swapped.

- [x] **0.1** Copy the then-current 5 `DefaultRenderPipeline*.cs` source partial files to `DefaultRenderPipeline2*.cs` (audit note: V1 later gained `DefaultRenderPipeline.ResourceLogging.cs`, which V2 does not mirror)
- [x] **0.2** Rename class to `DefaultRenderPipeline2 : RenderPipeline` across all partials
- [x] **0.3** Add `DefaultRenderPipeline2` to the pipeline registry / factory so the engine can instantiate it
- [x] **0.4** Add env var `XRE_USE_PIPELINE_V2=1` to swap between V1 and V2 at startup (in `NewRenderPipeline()` + `ApplyRenderPipelinePreference()` + `ApplyGlobalIlluminationModePreference()`)
- [x] **0.5** Build — confirmed zero new errors (0 Error(s), build succeeded)
- [ ] **0.6** Run editor with V2 active — confirm identical visual output to V1 (screenshot A/B)
- [x] **0.7** Centralize all pipeline creation through `NewRenderPipeline(bool stereo)` factory overload
- [x] **0.8** Route VR two-pass path (`Engine.VRState.InitTwoPass`) through factory — V2-aware
- [x] **0.9** Route VR single-pass stereo path (`Engine.VRState.InitSinglePass`) through factory — V2-aware
- [x] **0.10** Route OpenXR foveated multi-view path (`OpenXRAPI.GetOrCreateOpenXrPipeline`) through factory — V2-aware, handles `DefaultRenderPipeline2` as source type
- [x] **0.11** Route unit test pipeline creation (`UnitTestingWorld.Pawns`) through factory
- [x] **0.12** Update `RenderPipelineInspector` `is` checks to recognize V2 (`is DefaultRenderPipeline or DefaultRenderPipeline2`)
- [x] **0.13** Build — confirmed zero errors after stereo/foveated/multi-view wiring

---

## Phase 1 — Structural Decomposition (Readability)
> Break the then-~500-line `CreateViewportTargetCommands()` into named sub-builders. V1 has continued evolving since this was written and is now roughly 820 lines again. No behavioral changes.

### 1A: Create CommandChain partial file
- [x] **1A.1** Create `DefaultRenderPipeline2.CommandChain.cs`
- [x] **1A.2** Move `GenerateCommandChain()`, `CreateViewportTargetCommands()`, `CreateFBOTargetCommands()`, `CreateFinalBlitCommands()`, and `CreateVendorUpscaleCommands()` into it

### 1B: Decompose CreateViewportTargetCommands
- [x] **1B.1** Extract `AppendVoxelConeTracingPass(c)` — VCT cache + dispatch
- [x] **1B.2** Extract `AppendAmbientOcclusionSwitch(c, enableCompute)` — AO mode VPRC_Switch with all 8 cases
- [x] **1B.3** Extract `AppendDeferredGBufferPass(c)` — GBuffer geometry + MSAA GBuffer FBO caching + conditional resolve
- [x] **1B.4** Extract `AppendForwardDepthPrePass(c)` — Forward pre-pass IfElse (shared vs separate)
- [x] **1B.5** Extract `AppendAmbientOcclusionResolve(c)` — AO resolve VPRC_Switch (HBAO+ / GTAO / default)
- [x] **1B.6** Extract `AppendLightingPass(c)` — LightCombine FBO, MSAA mark, MSAA/non-MSAA lighting branch
- [x] **1B.7** Extract `AppendForwardPass(c, enableCompute)` — MSAA FBO caching, bind forward pass, opaque/masked/GI/debug shapes
- [x] **1B.8** Extract `AppendTransparencyPasses(c)` — WB-OIT accum/resolve + exact transparency
- [x] **1B.9** Extract `AppendVelocityPass(c)` — velocity FBO caching, clear, motion vector render
- [x] **1B.10** Extract `AppendBloomPass(c)` — bloom dispatch
- [x] **1B.11** Extract `AppendMotionBlurAndDoF(c)` — conditional motion blur + DoF sub-chains
- [x] **1B.12** Extract `AppendTemporalAccumulation(c)` — TAA accumulate + pop jitter
- [x] **1B.13** Extract `AppendPostTemporalForwardPasses(c)` — transparent + on-top forward after temporal
- [x] **1B.14** Extract `AppendPostProcessResourceCaching(c)` — PostProcess FBO caching
- [x] **1B.15** Extract `AppendDebugVisualizationCaching(c)` — 5 conditional debug FBO blocks
- [x] **1B.16** Extract `AppendAntiAliasingResourceCaching(c)` — FXAA/TSR texture + FBO caching
- [x] **1B.17** Extract `AppendFxaaTsrUpscaleChain(c)` — FXAA/TSR upscale IfElse
- [x] **1B.18** Extract `AppendExposureUpdate(c)` — auto-exposure compute dispatch
- [x] **1B.19** Extract `AppendTemporalCommit(c)` — temporal commit phase
- [x] **1B.20** Extract `AppendFinalOutput(c, bypassVendorUpscale)` — output FBO bind, debug viz / vendor upscale / AA output selection

### 1C: Group CacheTextures
- [x] **1C.1** Split `CacheTextures()` into: `CacheGBufferTextures`, `CacheMsaaDeferredTextures`, `CacheLightingTextures`, `CacheTemporalTextures` (including velocity/history), `CachePostProcessTextures`, `CacheTransparencyTextures`, `CacheGITextures` (`CacheExactTransparencyTextures` still lives in the ExactTransparency partial)

### 1D: Validate
- [x] **1D.1** Build — zero new errors/warnings
- [ ] **1D.2** Run editor — identical visual output to Phase 0 baseline (screenshot A/B)

---

## Phase 2 — GPU Profiling Annotations
> Add VPRC_Annotation markers to all major pipeline stages. Purely additive — no behavioral changes.

- [ ] **2.1** Add annotation: `"Texture Caching"` around CacheTextures
- [ ] **2.2** Add annotation: `"AO Compute"` around AO switch
- [ ] **2.3** Add annotation: `"Deferred GBuffer"` around geometry render
- [ ] **2.4** Add annotation: `"MSAA GBuffer Resolve"` around conditional resolve
- [ ] **2.5** Add annotation: `"Forward Pre-Pass"` around depth+normal pre-pass
- [ ] **2.6** Add annotation: `"AO Resolve"` around AO blur/resolve
- [ ] **2.7** Add annotation: `"Lighting"` around LightCombine + light volumes
- [ ] **2.8** Add annotation: `"Forward Render"` around opaque + masked forward
- [ ] **2.9** Add annotation: `"GI Composite"` around ReSTIR / LV / RC / Surfel
- [ ] **2.10** Add annotation: `"Transparency"` around WB-OIT + PPLL + depth peel
- [ ] **2.11** Add annotation: `"Velocity"` around motion vector pass
- [ ] **2.12** Add annotation: `"Bloom"` around bloom downsample/upsample
- [ ] **2.13** Add annotation: `"Motion Blur / DoF"` around conditional passes
- [ ] **2.14** Add annotation: `"Temporal Accumulation"` around TAA/TSR resolve
- [ ] **2.15** Add annotation: `"Post-Temporal Forward"` around transparent + on-top
- [ ] **2.16** Add annotation: `"Post-Processing"` around tonemapping/grading
- [ ] **2.17** Add annotation: `"AA Upscale"` around FXAA / TSR
- [ ] **2.18** Add annotation: `"Final Output"` around swapchain present
- [ ] **2.19** Build + run — confirm annotations visible in RenderDoc capture

---

## Phase 3 — Migrate PPLL Buffers Into Command Chain
> Eliminate all 3 VPRC_Manual usages. Move PPLL SSBO lifecycle and binding into the command chain.

- [ ] **3.1** Add const string names: `PpllNodeBufferName`, `PpllCounterBufferName` (pipeline resource names)
- [ ] **3.2** Create factory methods: `CreatePpllNodeBuffer()`, `CreatePpllCounterBuffer()` that return `XRDataBuffer`
- [ ] **3.3** Create predicate: `NeedsPpllNodeBufferResize()` — returns true when pixel count exceeds current capacity
- [ ] **3.4** Replace `EnsureExactTransparencyBuffers()` call with `VPRC_CacheOrCreateBuffer` for both PPLL buffers
- [ ] **3.5** Replace `VPRC_Manual(ResetPpllResources)` with `VPRC_DispatchCompute` for head pointer clear + `VPRC_SetVariable` for counter reset (or keep compute path, whichever works for atomic counter)
- [ ] **3.6** Replace `PpllResolveMaterial_SettingUniforms` SSBO binding with `VPRC_BindBuffer` scopes around PPLL forward pass
- [ ] **3.7** Replace `VPRC_Manual(() => _activeDepthPeelLayerIndex = capture)` with `VPRC_SetVariable("ActiveDepthPeelLayer", capture)`
- [ ] **3.8** Replace `VPRC_Manual(() => _activeDepthPeelLayerIndex = -1)` with `VPRC_SetVariable("ActiveDepthPeelLayer", -1)`
- [ ] **3.9** Update `PreviousDepthPeelDepthTexture` accessor to read from pipeline variable instead of `_activeDepthPeelLayerIndex` field
- [ ] **3.10** Remove `_ppllNodeBuffer`, `_ppllCounterBuffer`, `_activeDepthPeelLayerIndex` fields
- [ ] **3.11** Remove `EnsureExactTransparencyBuffers()` method
- [ ] **3.12** Build — zero new errors/warnings
- [ ] **3.13** Run editor with exact transparency enabled — confirm PPLL + depth peeling still render correctly

---

## Phase 4 — Migrate Simple SettingUniforms to Command Chain
> Replace the low-complexity SettingUniforms callbacks with VPRC_PushShaderGlobals / VPRC_BindTexture scopes.

> Audit note: the current V1/V2 `SettingUniforms +=` surface is broader than the bullets below. V2 still has additional hooks for `MotionVectorsMaterial`, standard/MSAA `LightCombine`, `RestirComposite`, `SurfelGIComposite`, `LightVolumeComposite`, `RadianceCascadeComposite`, `PpllResolveMaterial`, `DepthPeelingResolveMaterial`, and `DepthPeelingDebugMaterial`. If the end goal remains zero `SettingUniforms +=`, expand the checklist before treating 9.2 as complete.

### 4A: FXAA (2 uniforms)
- [ ] **4A.1** Add `VPRC_PushShaderGlobals` before FXAA quad render with `FxaaTexelStep` vector2
- [ ] **4A.2** Remove `FxaaFBO_SettingUniforms` event handler
- [ ] **4A.3** Remove `SettingUniforms +=` subscription in `CreateFxaaFBO()`

### 4B: TransformId Debug (1 sampler + 2 uniforms)
- [ ] **4B.1** Add `VPRC_BindTexture` + `VPRC_PushShaderGlobals` before TransformId debug quad render
- [ ] **4B.2** Remove `TransformIdDebugQuadFBO_SettingUniforms`
- [ ] **4B.3** Remove subscription in `CreateTransformIdDebugQuadFBO()`

### 4C: Transparent Resolve (3 samplers)
- [ ] **4C.1** Add 3× `VPRC_BindTexture` scopes (scene copy, accum, revealage) before transparent resolve
- [ ] **4C.2** Remove `TransparentResolveFBO_SettingUniforms`
- [ ] **4C.3** Remove subscription in `CreateTransparentResolveFBO()`

### 4D: Debug Overlay Handlers (×3, 1 sampler each)
- [ ] **4D.1** Add `VPRC_BindTexture` before each debug FBO quad render (accum debug, revealage debug, overdraw debug)
- [ ] **4D.2** Remove `TransparentAccumulationDebugFBO_SettingUniforms`, `TransparentRevealageDebugFBO_SettingUniforms`, `TransparentOverdrawDebugFBO_SettingUniforms`
- [ ] **4D.3** Remove subscriptions in debug FBO factory methods

### 4E: PPLL Resolve (2 samplers + 3 uniforms)
- [ ] **4E.1** Add `VPRC_BindTexture` ×2 + `VPRC_PushShaderGlobals` before PPLL resolve quad render
- [ ] **4E.2** Remove `PpllResolveMaterial_SettingUniforms`
- [ ] **4E.3** Remove `PpllResolveFBO_SettingUniforms`
- [ ] **4E.4** Remove `PpllFragmentCountDebugFBO_SettingUniforms`
- [ ] **4E.5** Add bindings/globals for depth-peeling resolve/debug materials; remove `DepthPeelingResolveMaterial_SettingUniforms` and `DepthPeelingDebugMaterial_SettingUniforms`
- [ ] **4E.6** Remove subscriptions in PPLL/depth-peeling factories

### 4F: BrightPass / Bloom (~5 uniforms)
- [ ] **4F.1** Add `VPRC_PushShaderGlobals` before bright pass quad render
- [ ] **4F.2** Remove `BrightPassFBO_SettingUniforms`

### 4G: Validate
- [ ] **4G.1** Build — zero new errors/warnings
- [ ] **4G.2** Run editor — confirm FXAA, transparency, bloom, debug overlays all render correctly

---

## Phase 5 — Migrate Medium-Complexity SettingUniforms
> TSR, DoF, motion blur, and temporal accumulation handlers.

### 5A: Motion Blur (~8 uniforms)
- [ ] **5A.1** Add `VPRC_PushShaderGlobals` before motion blur quad render with velocity scale, samples, texel size, etc.
- [ ] **5A.2** Remove `MotionBlurFBO_SettingUniforms` + subscription

### 5B: Depth of Field (~12 uniforms)
- [ ] **5B.1** Add `VPRC_PushShaderGlobals` before DoF quad render with mode, focus, aperture, CoC, bokeh, etc.
- [ ] **5B.2** Remove `DepthOfFieldFBO_SettingUniforms` + subscription

### 5C: Temporal Accumulation (~10 uniforms)
- [ ] **5C.1** Add `VPRC_PushShaderGlobals` before temporal accumulation quad render with jitter, feedback, history readiness
- [ ] **5C.2** Remove `TemporalAccumulationFBO_SettingUniforms` + subscription

### 5D: TSR Upscale (~15 uniforms)
- [ ] **5D.1** Add `VPRC_PushShaderGlobals` before TSR upscale quad render with texel sizes, jitter UV, feedback, variance, catmull, depth reject, etc.
- [ ] **5D.2** Remove `TsrUpscaleFBO_SettingUniforms` + subscription

### 5E: Validate
- [ ] **5E.1** Build — zero new errors/warnings
- [ ] **5E.2** Run editor — confirm motion blur, DoF, TAA, TSR all render correctly

---

## Phase 6 — Migrate Light Probe SSBOs Into Command Chain
> Move the ~300 lines of probe buffer management under VPRC_CacheOrCreateBuffer + VPRC_BindBuffer.

- [ ] **6.1** Add const string names for probe pipeline resources: `LightProbePositionBufferName`, `LightProbeParamBufferName`, `LightProbeTetraBufferName`, `LightProbeGridCellBufferName`, `LightProbeGridIndexBufferName`, `LightProbeIrradianceArrayName`, `LightProbePrefilterArrayName`
- [ ] **6.2** Refactor `BuildProbeResources()` to register buffers with the pipeline resource registry instead of storing as private fields
- [ ] **6.3** Refactor `ClearProbeResources()` to remove pipeline resources instead of manual dispose
- [ ] **6.4** Add `VPRC_CacheOrCreateBuffer` commands for each probe SSBO in `AppendLightingPass`
- [ ] **6.5** Add `VPRC_CacheOrCreateTexture` commands for irradiance + prefilter arrays
- [ ] **6.6** Add `VPRC_BindBuffer` scopes (bindings 0–4) + `VPRC_BindTexture` scopes (units 7–8) around LightCombine pass
- [ ] **6.7** Add `VPRC_PushShaderGlobals` for AO uniforms (UseAmbientOcclusion, Power, MultiBounce, SpecularOcclusion) + probe uniforms (ProbeCount, UseProbeGrid, GridOrigin, GridCellSize, GridDims, TetraCount)
- [ ] **6.8** Remove `LightCombineFBO_SettingUniforms` event handler
- [ ] **6.9** Remove `SettingUniforms +=` subscriptions in `CreateLightCombineFBO()` and `CreateMsaaLightCombineFBO()`
- [ ] **6.10** Remove private SSBO fields: `_probePositionBuffer`, `_probeParamBuffer`, `_probeTetraBuffer`, `_probeGridCellBuffer`, `_probeGridIndexBuffer`, `_probeIrradianceArray`, `_probePrefilterArray`
- [ ] **6.11** Build — zero new errors/warnings
- [ ] **6.12** Run editor with light probes in scene — confirm GI lighting renders correctly
- [ ] **6.13** Confirm probe tetrahedra debug visualization still works

---

## Phase 7 — Debug Visualization as Runtime Conditionals
> Replace build-time `if (Enable*Visualization)` FBO caching with VPRC_ConditionalRender.

- [ ] **7.1** Replace `if (EnableTransformIdVisualization)` block with `VPRC_ConditionalRender` keyed on `"DebugViz_TransformId"` pipeline variable
- [ ] **7.2** Replace `if (EnableTransparencyAccumulationVisualization)` with `VPRC_ConditionalRender`
- [ ] **7.3** Replace `if (EnableTransparencyRevealageVisualization)` with `VPRC_ConditionalRender`
- [ ] **7.4** Replace `if (EnableTransparencyOverdrawVisualization)` with `VPRC_ConditionalRender`
- [ ] **7.5** Replace `if (EnableDepthPeelingLayerVisualization)` with `VPRC_ConditionalRender`
- [ ] **7.6** Replace `if (EnablePerPixelLinkedListVisualization)` with `VPRC_ConditionalRender`
- [ ] **7.7** Wire debug viz editor preferences to set pipeline variables when toggled (instead of triggering chain rebuild)
- [ ] **7.8** Remove `HandleRenderingSettingsChanged()` chain regeneration for debug viz toggles (chain rebuild only for AA mode changes, resolution changes, etc.)
- [ ] **7.9** Build — zero new errors/warnings
- [ ] **7.10** Run editor — toggle each debug viz on/off at runtime without frame hitch from chain rebuild

---

## Phase 8 — Migrate PostProcessFBO_SettingUniforms
> The largest handler (~50+ uniforms). Decompose into per-stage shader globals helpers.

- [ ] **8.1** Create `ApplyVignetteGlobals(VPRC_PushShaderGlobals)` helper
- [ ] **8.2** Create `ApplyColorGradingGlobals(VPRC_PushShaderGlobals)` helper
- [ ] **8.3** Create `ApplyBloomGlobals(VPRC_PushShaderGlobals)` helper
- [ ] **8.4** Create `ApplyTonemappingGlobals(VPRC_PushShaderGlobals)` helper
- [ ] **8.5** Create `ApplyFogGlobals(VPRC_PushShaderGlobals)` helper
- [ ] **8.6** Create `ApplyChromaticAberrationGlobals(VPRC_PushShaderGlobals)` helper
- [ ] **8.7** Create `ApplyLensDistortionGlobals(VPRC_PushShaderGlobals)` helper
- [ ] **8.8** Wrap `PostProcessFBO` quad render in nested `VPRC_PushShaderGlobals` scopes (one per stage)
- [ ] **8.9** Add `VPRC_PushShaderGlobals` for `OutputHDR` uniform
- [ ] **8.10** Remove `PostProcessFBO_SettingUniforms` event handler
- [ ] **8.11** Remove `SettingUniforms +=` subscription in `CreatePostProcessFBO()`
- [ ] **8.12** Build — zero new errors/warnings
- [ ] **8.13** Run editor — confirm post-processing (tonemapping, color grading, bloom, fog, lens, chromatic) all render correctly

---

## Phase 9 — Final Cleanup & Validation

- [ ] **9.1** Verify zero `VPRC_Manual` commands remain in V2 pipeline
- [ ] **9.2** Verify zero `SettingUniforms +=` subscriptions remain in V2 FBO/material factories (including GI composite and exact-transparency helpers)
- [ ] **9.3** Verify zero raw `BindTo()` SSBO calls remain outside the command chain
- [ ] **9.4** Verify `CreateViewportTargetCommands()` is ≤80 lines
- [ ] **9.5** Full build of solution — zero new errors/warnings
- [ ] **9.6** A/B screenshot comparison: V1 vs V2 across all major rendering paths (deferred, forward, MSAA, FXAA, TSR, transparency, exact transparency, bloom, motion blur, DoF, light probes, debug viz)
- [ ] **9.7** Run unit tests — no regressions
- [ ] **9.8** RenderDoc capture — confirm all 18 annotations visible and correctly scoped
- [ ] **9.9** Update `docs/work/design/default-render-pipeline-improvement-plan.md` to mark all phases complete
- [ ] **9.10** Document `XRE_USE_PIPELINE_V2` env var in README or relevant docs

---

## Swap-In Checklist (post-validation, separate task)
> Once V2 is fully validated, promote it to the default.

- [ ] Make `DefaultRenderPipeline2` the default pipeline in the registry
- [ ] Rename `DefaultRenderPipeline` → `DefaultRenderPipelineLegacy` (or archive it)
- [ ] Rename `DefaultRenderPipeline2` → `DefaultRenderPipeline`
- [ ] Remove the `XRE_USE_PIPELINE_V2` swap mechanism
- [ ] Delete legacy files

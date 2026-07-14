# DefaultRenderPipeline2 — Phased Implementation Todo

> **Resource lifecycle completion (2026-07-10):** V2 is retained as the
> cleaned-up, better-organized successor pipeline. It now implements the same
> immutable resource-profile and generation model as V1. Its `Append*` command
> decomposition and GPU annotations remain V2-owned; only resource allocation
> moved out of executable commands.

> **Audit note (2026-04-19):** This is now a historical phased plan. The current condensed backlog lives in [default-render-pipeline-follow-up-2026-04-20.md](./default-render-pipeline-follow-up-2026-04-20.md). The corrections below keep this file aligned with the current V1/V2 source layout so it can still be read safely.

> **Audit refresh (2026-05-06):** V1 has continued evolving since this plan was written. Important deltas to keep in mind before continuing any phase below:
>
> - **V1 has been refactored to match V2's `Append*` decomposition.** Phase 1's premise (V1 is a ~500-line monolith, V2 decomposes it) is obsolete: V1's `CreateViewportTargetCommands()` is now ~62 lines and uses the same `AppendVoxelConeTracingPass`, `AppendAmbientOcclusionSwitch`, …, `AppendFinalOutput` helpers as V2, plus `AppendDebugOverlay` and `AppendVolumetricFog`. Phase 1 is effectively complete in both pipelines and the line-count goal in 9.4 is already met for V2.
> - **V2 volumetric fog re-sync (2026-05-06): COMPLETE.** Ported from V1 to V2: 5 texture factories, 9 FBO factories (with `.UseLifetime(RenderResourceLifetime.Transient)` on the quad FBOs), 9 `NeedsRecreateVolumetricFog*Fbo` predicates, half-internal size helpers (`GetDesiredFBOSizeHalfInternal` / `NeedsRecreateTextureHalfInternalSize` / `ResizeTextureHalfInternalSize`), 13 const FBO/texture names, `InvalidateVolumetricFogHistory(XRCamera?)` static, the `AppendVolumetricFog(c)` chain (4 stages: half-depth downsample → scatter → temporal reproject → bilateral upscale, plus `VPRC_VolumetricFogHistoryPass` Begin/Commit and the reproject→history blit), and the 3 `SettingUniforms` handlers (`VolumetricFogHalfScatterFBO_SettingUniforms`, `VolumetricFogReprojectFBO_SettingUniforms`, `VolumetricFogUpscaleFBO_SettingUniforms`) plus the shared `VolumetricFog_SetFragmentCameraUniforms` helper and the 3 diagnostic statics. V2's `CreatePostProcessFBO` and `NeedsRecreatePostProcessFbo` were also updated to bind `VolumetricFogColor` at sampler index 5 in the mono path (stereo still binds 5 textures and skips the scatter chain). V2 now matches V1's volumetric fog feature surface; the remaining Phase 4/5/9 work below applies to both pipelines symmetrically.
> - **V1 also has `DefaultRenderPipeline.ResourceLogging.cs`** (diagnostics-only partial). V2 does not mirror it. Low priority but noted for completeness.
> - **`SettingUniforms +=` handler counts (current):**
>   - V1: ~20 in `FBOs.cs` + 4 in `ExactTransparency.cs` = **24** subscriptions to remove for the Phase 9.2 goal.
>   - V2: ~22 in `FBOs.cs` + 4 in `ExactTransparency.cs` = **26** subscriptions after the volumetric fog re-sync (V2 now mirrors V1's surface and adds the 3 volumetric fog handlers — both pipelines should drop these together in Phase 4/5).
>   - The Phase 4/5 sub-tasks below cover only a subset of these. The remaining `MotionVectorsMaterial`, `LightCombine` (standard + MSAA), `RestirComposite`, `SurfelGIComposite`, `LightVolumeComposite`, `RadianceCascadeComposite`, `DepthPeelingResolveMaterial`, `DepthPeelingDebugMaterial`, and `VolumetricFog*` handlers must be folded into Phase 4/5 (or a new phase) before 9.2 can be ticked.
> - **`VPRC_Manual` count:** still 3 in V1 and 3 in V2 (all in `ExactTransparency.cs` for PPLL reset + depth-peel layer index). Phase 3 is still relevant.
> - **`CreateViewportTargetCommands()` line count:** V1 ~62 lines, V2 ~62 lines. Phase 9.4 (≤80 lines) is already satisfied for V2; the remaining audit work is the SettingUniforms / VPRC_Manual / probe-SSBO removals, not line count.
>
> Treat the original phase descriptions as historical context. Concrete remaining V2 work is captured in the condensed follow-up doc linked above.

> **Implementation refresh (2026-05-07):** V2 now uses command-scoped program bindings instead of V2-local FBO/material `SettingUniforms` subscriptions, has no V2 `VPRC_Manual` commands, and has no raw V2 `BindTo()` SSBO calls in `DefaultRenderPipeline2*.cs`. PPLL reset/depth-peel state moved into typed commands and pipeline variables. Debug visualization FBO materialization/presentation now uses per-frame `DebugViz_*` pipeline variables and runtime command branches. Light probe resources are registered with the pipeline resource registry and bound through command-chain `VPRC_BindBuffer` scopes; the private CPU-side probe handles remain for asynchronous tetrahedralization/content refresh bookkeeping and debug access, but direct shader binding is gone.

> **Strategy:** Create a parallel `DefaultRenderPipeline2` (with matching partial files) alongside the existing `DefaultRenderPipeline`. No changes to the original — it remains the active pipeline until V2 is validated and swapped in via a preference or launch flag.

**Reference:** [design/rendering/default-render-pipeline-improvement-plan.md](../design/rendering/default-render-pipeline-improvement-plan.md)

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
>
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

## Phase 1 — Structural Decomposition (Readability) ✅ (now also done in V1)
>
> **2026-05-06 audit:** V1 has been refactored to use the same `Append*` decomposition. V1's `CreateViewportTargetCommands()` is now ~62 lines and calls the identical helpers V2 introduced. This phase is no longer about V1 being a monolith — both pipelines share the same structural shape. The remaining V2-vs-V1 gap is `AppendVolumetricFog` (V1-only) and `AppendDebugOverlay` was added in V1 too (V2 has it). Treat the per-task checklist below as historical record.

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
>
> Add VPRC_Annotation markers to all major pipeline stages. Purely additive — no behavioral changes.

- [x] **2.1** Add annotation: `"Texture Caching"` around CacheTextures
- [x] **2.2** Add annotation: `"AO Compute"` around AO switch
- [x] **2.3** Add annotation: `"Deferred GBuffer"` around geometry render
- [x] **2.4** Add annotation: `"MSAA GBuffer Resolve"` around conditional resolve
- [x] **2.5** Add annotation: `"Forward Pre-Pass"` around depth+normal pre-pass
- [x] **2.6** Add annotation: `"AO Resolve"` around AO blur/resolve
- [x] **2.7** Add annotation: `"Lighting"` around LightCombine + light volumes
- [x] **2.8** Add annotation: `"Forward Render"` around opaque + masked forward
- [x] **2.9** Add annotation: `"GI Composite"` around ReSTIR / LV / RC / Surfel
- [x] **2.10** Add annotation: `"Transparency"` around WB-OIT + PPLL + depth peel
- [x] **2.11** Add annotation: `"Velocity"` around motion vector pass
- [x] **2.12** Add annotation: `"Bloom"` around bloom downsample/upsample
- [x] **2.13** Add annotation: `"Motion Blur / DoF"` around conditional passes
- [x] **2.14** Add annotation: `"Temporal Accumulation"` around TAA/TSR resolve
- [x] **2.15** Add annotation: `"Post-Temporal Forward"` around transparent + on-top
- [x] **2.16** Add annotation: `"Post-Processing"` around tonemapping/grading
- [x] **2.17** Add annotation: `"AA Upscale"` around FXAA / TSR
- [x] **2.18** Add annotation: `"Final Output"` around swapchain present
- [ ] **2.19** Build + run — confirm annotations visible in RenderDoc capture

---

## Phase 3 — Migrate PPLL Buffers Into Command Chain
>
> Eliminate all 3 VPRC_Manual usages. Move PPLL SSBO lifecycle and binding into the command chain.

- [x] **3.1** Add const string names: `PpllNodeBufferName`, `PpllCounterBufferName` (pipeline resource names)
- [x] **3.2** Create factory methods: `CreatePpllNodeBuffer()`, `CreatePpllCounterBuffer()` that return `XRDataBuffer`
- [x] **3.3** Create predicate: `NeedsPpllNodeBufferResize()` — returns true when pixel count exceeds current capacity
- [x] **3.4** Replace `EnsureExactTransparencyBuffers()` call with `VPRC_CacheOrCreateBuffer` for both PPLL buffers
- [x] **3.5** Replace `VPRC_Manual(ResetPpllResources)` with a typed command for head pointer clear + counter reset
- [x] **3.6** Replace `PpllResolveMaterial_SettingUniforms` SSBO binding with `VPRC_BindBuffer` scopes around PPLL forward pass
- [x] **3.7** Replace `VPRC_Manual(() => _activeDepthPeelLayerIndex = capture)` with `VPRC_SetVariable("ActiveDepthPeelLayer", capture)`
- [x] **3.8** Replace `VPRC_Manual(() => _activeDepthPeelLayerIndex = -1)` with `VPRC_SetVariable("ActiveDepthPeelLayer", -1)`
- [x] **3.9** Update `PreviousDepthPeelDepthTexture` accessor to read from pipeline variable instead of `_activeDepthPeelLayerIndex` field
- [x] **3.10** Remove `_ppllNodeBuffer`, `_ppllCounterBuffer`, `_activeDepthPeelLayerIndex` fields
- [x] **3.11** Remove `EnsureExactTransparencyBuffers()` method
- [x] **3.12** Build — editor project builds successfully; existing repo warnings remain
- [ ] **3.13** Run editor with exact transparency enabled — confirm PPLL + depth peeling still render correctly

---

## Phase 4 — Migrate Simple SettingUniforms to Command Chain
>
> Replace the low-complexity SettingUniforms callbacks with VPRC_PushShaderGlobals / VPRC_BindTexture scopes.

> Audit note: the current V1/V2 `SettingUniforms +=` surface is broader than the bullets below. V2 still has additional hooks for `MotionVectorsMaterial`, standard/MSAA `LightCombine`, `RestirComposite`, `SurfelGIComposite`, `LightVolumeComposite`, `RadianceCascadeComposite`, `PpllResolveMaterial`, `DepthPeelingResolveMaterial`, and `DepthPeelingDebugMaterial`. If the end goal remains zero `SettingUniforms +=`, expand the checklist before treating 9.2 as complete.

### 4A: FXAA (2 uniforms)

- [x] **4A.1** Add command-scoped FXAA bindings before FXAA quad render with `FxaaTexelStep` vector2
- [x] **4A.2** Remove `FxaaFBO_SettingUniforms` event handler
- [x] **4A.3** Remove `SettingUniforms +=` subscription in `CreateFxaaFBO()`

### 4B: TransformId Debug (1 sampler + 2 uniforms)

- [x] **4B.1** Add `VPRC_BindTexture` + command-scoped program bindings before TransformId debug quad render
- [x] **4B.2** Remove `TransformIdDebugQuadFBO_SettingUniforms`
- [x] **4B.3** Remove subscription in `CreateTransformIdDebugQuadFBO()`

### 4C: Transparent Resolve (3 samplers)

- [x] **4C.1** Add 3× `VPRC_BindTexture` scopes (scene copy, accum, revealage) before transparent resolve
- [x] **4C.2** Remove `TransparentResolveFBO_SettingUniforms`
- [x] **4C.3** Remove subscription in `CreateTransparentResolveFBO()`

### 4D: Debug Overlay Handlers (×3, 1 sampler each)

- [x] **4D.1** Add `VPRC_BindTexture` before each debug FBO quad render (accum debug, revealage debug, overdraw debug)
- [x] **4D.2** Remove `TransparentAccumulationDebugFBO_SettingUniforms`, `TransparentRevealageDebugFBO_SettingUniforms`, `TransparentOverdrawDebugFBO_SettingUniforms`
- [x] **4D.3** Remove subscriptions in debug FBO factory methods

### 4E: PPLL Resolve (2 samplers + 3 uniforms)

- [x] **4E.1** Add `VPRC_BindTexture` ×2 + command-scoped program bindings before PPLL resolve quad render
- [x] **4E.2** Remove `PpllResolveMaterial_SettingUniforms`
- [x] **4E.3** Remove `PpllResolveFBO_SettingUniforms`
- [x] **4E.4** Remove `PpllFragmentCountDebugFBO_SettingUniforms`
- [x] **4E.5** Add bindings/globals for depth-peeling resolve/debug materials; remove `DepthPeelingResolveMaterial_SettingUniforms` and `DepthPeelingDebugMaterial_SettingUniforms`
- [x] **4E.6** Remove subscriptions in PPLL/depth-peeling factories

### 4F: BrightPass / Bloom (~5 uniforms)

- [x] **4F.1** Current V2 bloom path no longer uses a V2 BrightPass FBO subscription
- [x] **4F.2** Remove `BrightPassFBO_SettingUniforms`

### 4G: Validate

- [x] **4G.1** Build — editor project builds successfully; existing repo warnings remain
- [ ] **4G.2** Run editor — confirm FXAA, transparency, bloom, debug overlays all render correctly

---

## Phase 5 — Migrate Medium-Complexity SettingUniforms
>
> TSR, DoF, motion blur, and temporal accumulation handlers.
>
> **2026-05-06 audit:** This phase must also cover the volumetric fog handlers introduced in V1 (`VolumetricFogHalfScatterFBO_SettingUniforms`, `VolumetricFogReprojectFBO_SettingUniforms`, `VolumetricFogUpscaleFBO_SettingUniforms`). V2 picked up the volumetric fog subsystem on the same date and now exposes the same 3 handlers, so a `5F: Volumetric Fog` group should be added when this phase is executed.

### 5A: Motion Blur (~8 uniforms)

- [x] **5A.1** Add command-scoped motion blur bindings before motion blur quad render with velocity scale, samples, texel size, etc.
- [x] **5A.2** Remove `MotionBlurFBO_SettingUniforms` + subscription

### 5B: Depth of Field (~12 uniforms)

- [x] **5B.1** Add command-scoped DoF bindings before DoF quad render with mode, focus, aperture, CoC, bokeh, etc.
- [x] **5B.2** Remove `DepthOfFieldFBO_SettingUniforms` + subscription

### 5C: Temporal Accumulation (~10 uniforms)

- [x] **5C.1** Add command-scoped temporal accumulation bindings before temporal accumulation quad render with jitter, feedback, history readiness
- [x] **5C.2** Remove `TemporalAccumulationFBO_SettingUniforms` + subscription

### 5D: TSR Upscale (~15 uniforms)

- [x] **5D.1** Add command-scoped TSR upscale bindings before TSR upscale quad render with texel sizes, jitter UV, feedback, variance, catmull, depth reject, etc.
- [x] **5D.2** Remove `TsrUpscaleFBO_SettingUniforms` + subscription

### 5F: Volumetric Fog

- [x] **5F.1** Add command-scoped bindings before half-scatter, reprojection, and upscale quad renders
- [x] **5F.2** Remove `VolumetricFogHalfScatterFBO_SettingUniforms`, `VolumetricFogReprojectFBO_SettingUniforms`, and `VolumetricFogUpscaleFBO_SettingUniforms` subscriptions

### 5E: Validate

- [x] **5E.1** Build — editor project builds successfully; existing repo warnings remain
- [ ] **5E.2** Run editor — confirm motion blur, DoF, TAA, TSR all render correctly

---

## Phase 6 — Migrate Light Probe SSBOs Into Command Chain
>
> Move the ~300 lines of probe buffer management under VPRC_CacheOrCreateBuffer + VPRC_BindBuffer.

- [x] **6.1** Add const string names for probe pipeline resources: `LightProbePositionBufferName`, `LightProbeParamBufferName`, `LightProbeTetraBufferName`, `LightProbeGridCellBufferName`, `LightProbeGridIndexBufferName`, `LightProbeIrradianceArrayName`, `LightProbePrefilterArrayName`
- [x] **6.2** Refactor `BuildProbeResources()` to register buffers with the pipeline resource registry
- [x] **6.3** Refactor `ClearProbeResources()` to remove pipeline resources before falling back to local disposal
- [x] **6.4** Add `VPRC_SyncLightProbeResources` to materialize probe SSBOs before lighting
- [x] **6.5** Register irradiance + prefilter arrays as pipeline texture resources
- [x] **6.6** Add `VPRC_BindBuffer` scopes (bindings 0–4) around LightCombine pass; probe textures are bound by command-scoped program bindings at units 7–8
- [x] **6.7** Add command-scoped AO uniforms (UseAmbientOcclusion, Power, MultiBounce, SpecularOcclusion) + probe uniforms (ProbeCount, UseProbeGrid, GridOrigin, GridCellSize, GridDims, TetraCount)
- [x] **6.8** Remove `LightCombineFBO_SettingUniforms` event handler
- [x] **6.9** Remove `SettingUniforms +=` subscriptions in `CreateLightCombineFBO()` and `CreateMsaaLightCombineFBO()`
- [ ] **6.10** Remove private SSBO fields: `_probePositionBuffer`, `_probeParamBuffer`, `_probeTetraBuffer`, `_probeGridCellBuffer`, `_probeGridIndexBuffer`, `_probeIrradianceArray`, `_probePrefilterArray`
- [x] **6.11** Build — editor project builds successfully; existing repo warnings remain
- [ ] **6.12** Run editor with light probes in scene — confirm GI lighting renders correctly
- [ ] **6.13** Confirm probe tetrahedra debug visualization still works

---

## Phase 7 — Debug Visualization as Runtime Conditionals
>
> Replace build-time `if (Enable*Visualization)` FBO caching with VPRC_ConditionalRender.

- [x] **7.1** Replace `if (EnableTransformIdVisualization)` block with `VPRC_ConditionalRender` keyed on `"DebugViz_TransformId"` pipeline variable
- [x] **7.2** Replace `if (EnableTransparencyAccumulationVisualization)` with `VPRC_ConditionalRender`
- [x] **7.3** Replace `if (EnableTransparencyRevealageVisualization)` with `VPRC_ConditionalRender`
- [x] **7.4** Replace `if (EnableTransparencyOverdrawVisualization)` with `VPRC_ConditionalRender`
- [x] **7.5** Replace `if (EnableDepthPeelingLayerVisualization)` with `VPRC_ConditionalRender`
- [x] **7.6** Replace `if (EnablePerPixelLinkedListVisualization)` with `VPRC_ConditionalRender`
- [x] **7.7** Wire debug viz editor preferences to per-frame pipeline variables instead of build-time command selection
- [x] **7.8** Remove debug-viz dependence on command-chain regeneration
- [x] **7.9** Build — editor project builds successfully; existing repo warnings remain
- [ ] **7.10** Run editor — toggle each debug viz on/off at runtime without frame hitch from chain rebuild

---

## Phase 8 — Migrate PostProcessFBO_SettingUniforms
>
> The largest handler (~50+ uniforms). Decompose into per-stage shader globals helpers.

- [x] **8.1** Route vignette uniforms through command-scoped post-process bindings
- [x] **8.2** Route color grading uniforms through command-scoped post-process bindings
- [x] **8.3** Route bloom uniforms through command-scoped post-process bindings
- [x] **8.4** Route tonemapping uniforms through command-scoped post-process bindings
- [x] **8.5** Route fog uniforms through command-scoped post-process bindings
- [x] **8.6** Route chromatic aberration uniforms through command-scoped post-process bindings
- [x] **8.7** Route lens distortion uniforms through command-scoped post-process bindings
- [x] **8.8** Wrap `PostProcessFBO` quad render in command-scoped program binding
- [x] **8.9** Add command-scoped binding for `OutputHDR` uniform
- [x] **8.10** Remove `PostProcessFBO_SettingUniforms` event handler
- [x] **8.11** Remove `SettingUniforms +=` subscription in `CreatePostProcessFBO()`
- [x] **8.12** Build — editor project builds successfully; existing repo warnings remain
- [ ] **8.13** Run editor — confirm post-processing (tonemapping, color grading, bloom, fog, lens, chromatic) all render correctly

---

## Phase 9 — Final Cleanup & Validation

- [x] **9.1** Verify zero `VPRC_Manual` commands remain in V2 pipeline
- [x] **9.2** Verify zero `SettingUniforms +=` subscriptions remain in V2 FBO/material factories (including GI composite and exact-transparency helpers)
- [x] **9.3** Verify zero raw `BindTo()` SSBO calls remain in `DefaultRenderPipeline2*.cs`
- [x] **9.4** Verify `CreateViewportTargetCommands()` is ≤80 lines
- [ ] **9.5** Full build of solution — zero new errors/warnings
  - 2026-05-07 validation: `XREngine.Editor` and `XREngine.Runtime.Rendering` build successfully after the V2 command-chain migration. Full solution build now gets past the V2/Forward+ compile blocker, then fails in unrelated project-wide areas: `XREngine.Benchmarks` cannot resolve `AnimationClipComponent`, `XREngine.UnitTests` has many pre-existing `Engine` type ambiguities between `XREngine` and `XREngine.Runtime.Rendering`, and Audio2Face CSV parser tests reference APIs no longer present.
- [ ] **9.6** A/B screenshot comparison: V1 vs V2 across all major rendering paths (deferred, forward, MSAA, FXAA, TSR, transparency, exact transparency, bloom, motion blur, DoF, light probes, debug viz)
- [ ] **9.7** Run unit tests — no regressions
  - 2026-05-07 validation: unit tests were not runnable from the full solution because `XREngine.UnitTests` does not currently compile for unrelated `Engine` ambiguity and stale Audio2Face parser API errors.
- [ ] **9.8** RenderDoc capture — confirm all 18 annotations visible and correctly scoped
- [x] **9.9** Update `docs/work/design/rendering/default-render-pipeline-improvement-plan.md` with V2 implementation status
- [x] **9.10** Document `XRE_USE_PIPELINE_V2` env var in README or relevant docs

---

## Phase 10 — Declarative GPU Resource Lifecycle

- [x] **10.1** Add `DefaultRenderPipeline2.Resources.cs` with immutable
  profile-selected texture, view, renderbuffer, buffer, framebuffer,
  fullscreen-material, history, and external-target declarations.
- [x] **10.2** Include output size, internal size, HDR, effective AA/MSAA,
  stereo/view shape, feature mask, capture policy, backend-safe path, and
  imported target class in the generation key.
- [x] **10.3** Remove all texture/FBO/buffer/renderbuffer cache authoring from
  the V2 `Append*` command builders without collapsing their organization or
  profiling annotations.
- [x] **10.4** Move V2 PPLL, depth-peeling, atmosphere, fog, bloom, AO,
  temporal, debug, GI, MSAA, FXAA, SMAA, and TSR resources into declarations.
- [x] **10.5** Make SMAA consume declared edge, blend, output, and FBO resources
  and report a missing/incompatible generation instead of allocating in-frame.
- [x] **10.6** Remove the four cache-or-create command types and serialized
  fixtures that referenced them.
- [x] **10.7** Add layout and assembly-level source contracts preventing the
  removed command family from returning.
- [ ] **10.8** Deduplicate the V1/V2 declaration catalog behind shared helpers
  once V2 feature parity is frozen; preserve separate command-chain structure.
- [ ] **10.9** Replace mutable light-probe registry publication with an explicit
  scene-resource/import binding generation shared by V1 and V2.
- [ ] **10.10** Complete V2 OpenGL/Vulkan/VR visual validation across all
  profile variants before promotion.

## Swap-In Checklist (post-validation, separate task)
>
> Once V2 is fully validated, promote it to the default.

- [ ] Make `DefaultRenderPipeline2` the default pipeline in the registry
- [ ] Rename `DefaultRenderPipeline` → `DefaultRenderPipelineLegacy` (or archive it)
- [ ] Rename `DefaultRenderPipeline2` → `DefaultRenderPipeline`
- [ ] Remove the `XRE_USE_PIPELINE_V2` swap mechanism
- [ ] Delete legacy files

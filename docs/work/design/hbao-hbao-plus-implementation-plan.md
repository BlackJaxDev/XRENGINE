# HBAO And HBAO+ Implementation Plan

Last Updated: 2026-03-11
Current Status: `AmbientOcclusionSettings.EType.HorizonBased` and `HorizonBasedPlus` exist, but both currently alias to the Multi-View AO path rather than having dedicated implementations.
Primary Goal: implement real HBAO+ as the default high-quality screen-space AO path in the default deferred pipeline, with optional classic HBAO as a reference/debug mode.

## Recommendation

Implement `HorizonBasedPlus` first and make it the intended default AO mode for the deferred renderer once stable.

Keep classic `HorizonBased` only if we want a reference implementation for algorithm comparison, visual debugging, or regression testing. It should not be the default shipping path.

Reasoning:

- HBAO+ is a better fit for the current renderer's single-channel AO visibility contract.
- HBAO+ is designed around full-resolution depth, interleaved rendering, and edge-aware blur, which aligns better with a modern deferred pipeline than the original horizon-scan formulation.
- The current pipeline already consumes AO as a scalar visibility term during deferred ambient combine, so the integration boundary is already correct.

## Current Engine Reality

The current AO plumbing is partially in place, but the Horizon-Based modes are not real yet.

- `AmbientOcclusionSettings.EType` already includes `HorizonBased` and `HorizonBasedPlus`.
- `DefaultRenderPipeline` already switches among AO branches before deferred light combine.
- AO output is consumed as a single-channel visibility term in the deferred combine shader.
- The schema-driven post-process UI already supports per-parameter visibility with `visibilityCondition`.

The parts that are currently wrong for HBAO/HBAO+ are:

- `HorizonBased` and `HorizonBasedPlus` are remapped to `MultiViewAmbientOcclusion` in settings uniform binding.
- `DefaultRenderPipeline.MapAmbientOcclusionMode(...)` also routes both modes to the MVAO branch.
- The AO schema treats `HorizonBased` and `HorizonBasedPlus` as part of the Multi-View AO settings family.
- The current blur model used by the SSAO path is a simple box blur, not the cross-bilateral blur HBAO+ needs.

Relevant code entry points:

- `XREngine/Rendering/Camera/AmbientOcclusionSettings.cs`
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs`
- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_SSAOPass.cs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs`
- `XREngine/Rendering/PostProcessing/RenderPipelinePostProcessSchemaBuilder.cs`

## Target Architecture

AO should continue to be generated before deferred light combine and consumed as one scalar visibility texture. That integration point is already correct and should not move.

Target AO modes:

| AO Type | Intended Role | Backing Pass | Notes |
|---|---|---|---|
| `ScreenSpace` | cheap legacy SSAO fallback | `VPRC_SSAOPass` | keep simple and low-risk |
| `HorizonBased` | optional classic reference HBAO | new `VPRC_HBAOPass` | not default |
| `HorizonBasedPlus` | default high-quality AO | new `VPRC_HBAOPlusPass` | full internal resolution |
| `MultiViewAmbientOcclusion` | current experimental/custom AO | `VPRC_MVAOPass` | keep separate semantics |
| `ScalableAmbientObscurance` / `MultiScaleVolumetricObscurance` | separate obscurance family | existing MSVO path | keep independent |
| `SpatialHashRaytraced` | compute/ray-marched experimental AO | existing spatial hash path | keep independent |

## Settings Must Be De-Entangled

This is the most important design requirement besides the shader work.

Each AO type needs an explicit parameter family. Do not keep sharing one bucket of fields and then selectively reinterpret them across unrelated algorithms.

### Problem In The Current Model

Today the following are coupled incorrectly:

- `HorizonBased` and `HorizonBasedPlus` reuse the Multi-View AO settings group.
- UI visibility conditions treat them as `IsMVAO(...)`.
- Uniform binding remaps them to the Multi-View AO implementation.

That produces three failures:

1. The wrong parameters are shown in the ImGui render-pipeline editor.
2. The wrong shader constants are bound for Horizon-Based modes.
3. The code structure encourages more aliasing instead of real implementations.

### Required Settings Split

Keep `AmbientOcclusionSettings` as the stage backing object if that is the least disruptive route, but split its semantics into algorithm-specific parameter groups.

Suggested grouping:

#### Shared AO parameters

- `Enabled`
- `Type`
- `Radius`
- `Bias`
- `Power`

These can remain common only if they mean the same thing across the algorithms that expose them.

#### SSAO-only parameters

- kernel sample count
- sample radius tuning
- optional legacy noise options if SSAO keeps using the noise texture path

#### HBAO-only parameters

- direction count
- steps per direction
- tangent bias or horizon bias
- optional fallback blur controls if classic HBAO is retained

#### HBAO+-only parameters

- `Radius`
- `Bias`
- `Power`
- `DetailAO`
- `BlurEnable`
- `BlurRadius`
- `BlurSharpness`
- `UseInputNormals`
- `MetersToViewSpaceUnits` or equivalent scene scale control
- optional `ForegroundSharpness` / sharpness profile only if we actually implement that path

#### MVAO-only parameters

- `SecondaryRadius`
- `MultiViewBlend`
- `MultiViewSpread`
- `DepthPhi`
- `NormalPhi`

#### MSVO-only parameters

- `ResolutionScale`
- `SamplesPerPixel`
- `Intensity`

#### SpatialHash-only parameters

- `SpatialHashCellSize`
- `SpatialHashMaxDistance`
- `SpatialHashSteps`
- `SpatialHashJitterScale`
- `Thickness`

### Refactor Guidance

The cleanest medium-term design is to split the current AO settings class into per-algorithm backing types and keep one selector stage that owns the active type.

Possible end state:

- `AmbientOcclusionSettings` contains only shared controls and algorithm selection.
- `SsaoSettings`
- `HbaoSettings`
- `HbaoPlusSettings`
- `MvaoSettings`
- `MsvoSettings`
- `SpatialHashAoSettings`

If that is too much churn for the first pass, Phase 1 can keep one backing class but must still add explicit `IsHBAO(...)`, `IsHBAOPlus(...)`, `IsMVAO(...)`, `IsMSVO(...)`, and `IsSpatialHash(...)` predicates and stop reusing one family's visibility rules for another.

## ImGui Render Pipeline Editor Requirement

The ImGui editor should show only the settings that apply to the selected AO type.

This should be solved at the schema layer first, not with ad hoc ImGui branching.

Reasoning:

- The render-pipeline editor already consumes `RenderPipelinePostProcessSchema` metadata.
- `RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder.AddParameter(...)` already supports `visibilityCondition`.
- Other post-process stages already use visibility predicates successfully.

Therefore the AO implementation work must include updating `DefaultRenderPipeline.PostProcessing.cs` so that AO parameter visibility is keyed to the actual AO type family.

### Required UI Behavior

When the user selects an AO type in the ImGui render pipeline panel:

- only shared AO controls remain always visible
- only the selected algorithm's parameter group becomes visible
- no HBAO+ controls appear for SSAO, MVAO, MSVO, or SpatialHash modes
- no Multi-View controls appear for HBAO or HBAO+
- no stale values are shown as if they affect the active algorithm when they do not

### Implementation Notes For UI Gating

`DefaultRenderPipeline.PostProcessing.cs` should define dedicated predicates such as:

- `IsSSAO(object o)`
- `IsHBAO(object o)`
- `IsHBAOPlus(object o)`
- `IsMVAO(object o)`
- `IsMSVO(object o)`
- `IsSpatialHash(object o)`

Do not include `HorizonBased` or `HorizonBasedPlus` inside `IsMVAO(...)`.

Every AO parameter added to the schema should use one of these predicates or a clearly named shared predicate such as:

- `UsesSharedRadius(object o)`
- `UsesSharedBias(object o)`
- `UsesSharedPower(object o)`

This keeps the ImGui editor behavior correct even before custom per-type editor polish exists.

## HBAO Implementation Plan

Only do this if we decide classic HBAO is worth keeping.

### Phase H0 - Pass Scaffolding

- [ ] Add `VPRC_HBAOPass`.
- [ ] Add dedicated HBAO shader files.
- [ ] Add a branch in `DefaultRenderPipeline` for `HorizonBased`.
- [ ] Stop mapping `HorizonBased` to `MultiViewAmbientOcclusion`.

### Phase H1 - Correct Algorithm

- [ ] Reconstruct view-space position and view-space normal.
- [ ] March along multiple azimuth directions.
- [ ] Track maximum horizon angle per direction.
- [ ] Integrate occlusion from the horizon delta rather than using an SSAO-style kernel approximation.
- [ ] Add bias handling to suppress self-occlusion and low-tessellation artifacts.

### Phase H2 - Filtering

- [ ] Replace box blur with edge-aware blur.
- [ ] Use depth-aware blur at minimum.
- [ ] Consider normal-aware weighting if depth-only blur is insufficient.

## HBAO+ Implementation Plan

This is the primary path to implement.

### Phase P0 - Pipeline Separation

- [ ] Add `VPRC_HBAOPlusPass`.
- [ ] Add `HorizonBasedPlus` branch selection in `DefaultRenderPipeline`.
- [ ] Stop remapping `HorizonBasedPlus` to MVAO in `AmbientOcclusionSettings.SetUniforms(...)`.
- [ ] Stop routing `HorizonBasedPlus` to MVAO in `MapAmbientOcclusionMode(...)`.

### Phase P1 - Depth/Normal Preparation

- [ ] Use full internal-resolution depth as the source.
- [ ] Use G-buffer normals by default.
- [ ] Add a preparation stage for view-space depth or another representation that avoids repeated expensive reconstruction in the inner sampling loops.
- [ ] Keep the AO result single-channel `R16F` unless quality testing proves another format is needed.

### Phase P2 - Interleaved Rendering

- [ ] Implement 4x4 interleaved or deinterleaved AO sampling.
- [ ] Assign deterministic per-pass jitter rather than using the current random noise texture model.
- [ ] Add internal transient resources for deinterleaved depth and AO accumulation.
- [ ] Reinterleave the AO result back to the full internal-resolution AO texture consumed by deferred light combine.

### Phase P3 - HBAO+ Gather

- [ ] Implement coarse AO gather.
- [ ] Implement optional detail AO gather.
- [ ] Keep detail AO separately weightable so foliage-heavy scenes can disable it.
- [ ] Use radius in world/view-space terms rather than arbitrary screen-space-only tuning.

### Phase P4 - Proper Blur

- [ ] Replace the current AO box blur with separable cross-bilateral blur.
- [ ] Blur against depth at minimum.
- [ ] Add blur radius selection matching the intended HBAO+ quality modes.
- [ ] Add blur sharpness control.
- [ ] Consider a foreground sharpness profile only after the base path is correct.

### Phase P5 - Shader And Resource Cleanup

- [ ] Share common AO helpers where possible.
- [ ] Do not force SSAO-specific naming into HBAO+ internals if the resources become algorithm-specific.
- [ ] Keep the public output contract stable: one AO visibility texture sampled by deferred light combine.

## Pipeline Work Items

- [ ] Add new AO render-command containers in `DefaultRenderPipeline`.
- [ ] Add `CreateHBAOPassCommands()`.
- [ ] Add `CreateHBAOPlusPassCommands()`.
- [ ] Update the AO switch in `CreateViewportTargetCommands()`.
- [ ] Ensure the compute-disabled fallback path is still well-defined.
- [ ] Preserve stereo support expectations when creating textures and FBOs.

## Settings And Schema Work Items

- [ ] Add explicit HBAO predicates to `DefaultRenderPipeline.PostProcessing.cs`.
- [ ] Add explicit HBAO+ predicates to `DefaultRenderPipeline.PostProcessing.cs`.
- [ ] Remove HBAO and HBAO+ from the MVAO predicate family.
- [ ] Reassign every AO parameter to the correct visibility condition.
- [ ] Add HBAO+ parameters to the schema.
- [ ] Keep shared parameters visible only where their semantics are truly shared.
- [ ] Rename UI labels where needed so they match the actual algorithm meaning.

## Shader Work Items

- [ ] Add new shader files for HBAO and HBAO+ rather than mutating `SSAOGen.fs` into an overloaded mega-shader.
- [ ] Add a shared include for view-space reconstruction, depth conversion, and bilateral weighting if it reduces duplication without obscuring behavior.
- [ ] Replace `SSAOBlur.fs` reuse for HBAO+ unless it is upgraded into a proper bilateral blur shared by multiple AO paths.

## Validation Plan

### Functional Validation

- [ ] Verify `HorizonBased` no longer executes the MVAO branch.
- [ ] Verify `HorizonBasedPlus` no longer executes the MVAO branch.
- [ ] Verify AO still reaches `DeferredLightCombine.fs` through the same output texture contract.
- [ ] Verify AO disable mode still produces full visibility (`1.0`).

### ImGui / Schema Validation

- [ ] In the ImGui render-pipeline editor, switch between every AO type and verify only the relevant settings are visible.
- [ ] Verify no MVAO-only settings appear for HBAO or HBAO+.
- [ ] Verify no HBAO+-only settings appear for SSAO, MSVO, or SpatialHash.
- [ ] Verify default values are sensible when changing AO type for the first time.

### Visual Validation

- [ ] Test indoor contact shadowing.
- [ ] Test large-radius outdoor occlusion.
- [ ] Test alpha-tested vegetation with `DetailAO` on and off.
- [ ] Test camera motion for flicker and shimmer.
- [ ] Test thin geometry and screen-border false occlusion behavior.

### Performance Validation

- [ ] Compare SSAO, HBAO, and HBAO+ GPU cost at the same internal resolution.
- [ ] Confirm HBAO+ remains viable at the renderer's default internal resolution.
- [ ] Profile the interleaved/deinterleaved passes for avoidable allocations or transient resource churn.

## Acceptance Criteria

This work is complete when all of the following are true:

- `HorizonBased` and `HorizonBasedPlus` no longer alias to the MVAO path.
- `HorizonBasedPlus` has a real implementation with full-resolution depth, interleaved sampling, and edge-aware blur.
- The AO settings model is no longer semantically entangled across unrelated AO algorithms.
- The ImGui render-pipeline editor shows only the settings relevant to the selected AO type.
- Deferred light combine still consumes one AO visibility texture without requiring broader lighting-pipeline changes.

## Suggested Execution Order

1. Fix AO type routing and schema visibility conditions first.
2. Add `HorizonBasedPlus` pass scaffolding and shader/resource setup.
3. Implement the HBAO+ gather and bilateral blur.
4. Validate the ImGui editor behavior for all AO types.
5. Decide whether classic `HorizonBased` is still worth implementing afterward.

## Explicit Non-Goals

- Do not rewrite the deferred light combine contract.
- Do not hide AO type differences behind vague shared settings if the algorithms do not actually share semantics.
- Do not solve the ImGui editor visibility problem with a one-off UI hack while leaving the schema incorrect.
- Do not rename MVAO into HBAO/HBAO+ as a shortcut.
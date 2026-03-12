# HBAO And HBAO+ Implementation TODO

Last Updated: 2026-03-11
Current Status: AO settings/schema de-entangling is in place, classic `HorizonBased` is explicitly deferred behind a neutral-AO stub branch, `HorizonBasedPlus` has a dedicated full-resolution HBAO+ gather path plus separable cross-bilateral blur, the shared AO resource contract no longer uses the old `SSAO*` naming, and lit forward shaders now sample the shared AO visibility texture.
Scope: Default render pipeline AO generation and consumption. This work covers AO settings, render-pipeline branch selection, AO passes, deferred/lighted forward shader consumption, and editor-facing schema visibility for AO settings.

## Current Reality

What is already true:

- `AmbientOcclusionSettings.EType` includes `HorizonBased` and `HorizonBasedPlus`.
- The deferred pipeline already has the correct AO insertion point: AO is generated before deferred light combine and consumed as a scalar visibility term.
- The post-process schema supports per-parameter `visibilityCondition` gating.
- AO settings/schema de-entangling is implemented so HBAO and HBAO+ no longer inherit MVAO-only editor controls.

What is still incomplete:

- There is no `VPRC_HBAOPass`.
- There are no dedicated classic-HBAO shaders.
- The SSAO path still uses its older simple box blur; the new cross-bilateral blur is currently HBAO+-specific.

## Target Outcome

At the end of this work:

- `HorizonBasedPlus` is a real implementation and becomes the intended default high-quality AO mode.
- `HorizonBased` is either implemented as a reference/debug mode or explicitly left unsupported and hidden until implemented.
- Each AO type has clearly scoped settings with no editor leakage from unrelated algorithms.
- The ImGui render-pipeline editor shows only the settings that affect the currently selected AO type.
- Deferred light combine continues to consume a single AO visibility texture without broader lighting-pipeline churn.

## Non-Goals

- Do not rewrite deferred light combine around a new AO contract.
- Do not rename the current MVAO path into HBAO or HBAO+.
- Do not solve editor visibility with one-off ImGui branching while leaving the schema entangled.
- Do not introduce a vendor-library dependency for AO.

## Phase 0 - Lock The AO UI And Settings Contract

Outcome: AO types are no longer semantically entangled in the settings/UI layer.

- [x] Split AO schema visibility so `HorizonBased` and `HorizonBasedPlus` no longer inherit MVAO-only controls.
- [x] Add explicit HBAO/HBAO+ settings fields to `AmbientOcclusionSettings`.
- [x] Add dedicated schema predicates for `IsHBAO(...)` and `IsHBAOPlus(...)`.
- [x] Keep shared controls visible only where the semantics are actually shared.
- [x] Ensure the ImGui render-pipeline editor can hide unrelated AO controls using schema visibility metadata.

Implementation notes:

- `XREngine/Rendering/Camera/AmbientOcclusionSettings.cs` now contains dedicated HBAO/HBAO+ properties.
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs` now uses explicit AO-family predicates instead of treating HBAO/HBAO+ as MVAO.

Acceptance criteria:

- Switching AO types in the editor no longer shows MVAO-only controls for HBAO or HBAO+.
- HBAO+-only controls are hidden for SSAO, MVAO, MSVO, and SpatialHash.

## Phase 1 - Separate Pipeline Routing From MVAO

Outcome: HBAO and HBAO+ stop pretending to be MVAO at the render-pipeline routing layer.

- [x] Add explicit `HorizonBased` and `HorizonBasedPlus` branch cases to the AO switch in `DefaultRenderPipeline`.
- [x] Add `CreateHBAOPassCommands()`.
- [x] Add `CreateHBAOPlusPassCommands()`.
- [x] Update `MapAmbientOcclusionMode(...)` so these modes stop routing to `MultiViewAmbientOcclusion`.
- [x] Decide temporary fallback behavior until the real passes are fully implemented:
  - preferred: route to dedicated stub passes that log clearly and output neutral AO
  - avoid: silently reusing MVAO forever

Acceptance criteria:

- Selecting `HorizonBased` no longer executes the MVAO pass.
- Selecting `HorizonBasedPlus` no longer executes the MVAO pass.
- Incomplete implementations now log clearly and render neutral AO instead of silently rendering the wrong algorithm.

## Phase 2 - Implement HBAO+ Core Path

Outcome: `HorizonBasedPlus` becomes a real AO implementation suitable for the default deferred pipeline.

- [x] Add `VPRC_HBAOPlusPass`.
- [x] Use full internal-resolution depth as the source domain.
- [x] Use G-buffer normals by default.
- [x] Add depth/normal preparation as needed so the inner gather avoids wasteful repeated reconstruction.
- [x] Implement interleaved or deinterleaved sampling rather than the current noise-texture SSAO pattern.
- [x] Implement coarse AO gather.
- [x] Implement optional detail AO gather.
- [x] Reinterleave or compose the result back into the standard AO visibility texture.

Quality requirements:

- full internal-resolution depth input
- deterministic sampling pattern
- no dependency on the existing AO noise texture path
- stable camera-motion behavior compared to the SSAO path

Acceptance criteria:

- `HorizonBasedPlus` now uses a dedicated gather path and no longer shares MVAO or SSAO shader logic.
- AO still reaches deferred light combine through the existing scalar AO texture contract.

## Phase 3 - Implement Proper HBAO+ Blur

Outcome: HBAO+ no longer depends on the legacy box blur.

- [x] Replace the current AO box blur for the HBAO+ path with a separable cross-bilateral blur.
- [x] Blur against depth at minimum.
- [x] Add blur radius support matching the exposed HBAO+ settings.
- [x] Add blur sharpness support.
- [x] Verify blur respects thin geometry and object boundaries well enough to avoid haloing.

Acceptance criteria:

- HBAO+ blur now runs as a separable depth-aware cross-bilateral filter.
- The editor-exposed HBAO+ blur controls affect only the HBAO+ path.

## Phase 4 - Decide Whether Classic HBAO Is Worth Keeping

Outcome: classic `HorizonBased` is either implemented deliberately or explicitly deferred.

If we keep it:

- [ ] Add `VPRC_HBAOPass`.
- [ ] Implement directional horizon search with horizon-angle accumulation.
- [ ] Add classic-HBAO-specific uniforms and shader code.
- [ ] Add at least a minimal edge-aware filter.

If we defer it:

- [x] Document that `HorizonBased` remains unimplemented.
- [x] Keep it hidden, disabled, or clearly labeled until the pass exists.

Acceptance criteria:

- The engine does not imply that classic HBAO is functional unless it actually is.

## Phase 5 - Resource And Shader Cleanup

Outcome: AO implementation details are coherent instead of accreted onto SSAO naming.

- [x] Add dedicated HBAO/HBAO+ shader files.
- [x] Move shared AO reconstruction and bilateral helpers into a small shared include if it improves clarity.
- [x] Avoid overloading `SSAOGen.fs` into a multi-algorithm shader.
- [x] Generalize the shared AO resource names instead of keeping legacy `SSAO*` naming internally.

Acceptance criteria:

- The AO code paths are readable and each algorithm has an obvious home.

## Phase 6 - Validation And Default Selection

Outcome: HBAO+ is production-ready enough to be the intended default AO path.

- [ ] Validate editor visibility for every AO type.
- [ ] Validate indoor contact shadow quality.
- [ ] Validate outdoor large-radius occlusion.
- [ ] Validate alpha-tested foliage with detail AO on and off.
- [ ] Validate screen-border behavior and self-occlusion biasing.
- [ ] Measure GPU cost against SSAO and MVAO.
- [ ] Decide whether to switch the default AO type from `ScreenSpace` to `HorizonBasedPlus`.

Acceptance criteria:

- HBAO+ is stable, visibly higher quality than the SSAO path, and performant enough for the default internal-resolution pipeline.

## Implementation Notes

- The AO schema/UI layer is now clean enough that future pass work can proceed without confusing the editor.
- `HorizonBasedPlus` now routes through `VPRC_HBAOPlusPass` and dedicated HBAO+ shaders instead of the neutral stub branch.
- Classic `HorizonBased` is intentionally deferred for now and exposed as `HBAO (Deferred)` so the editor does not imply that a real classic-HBAO implementation exists.
- Shared AO FBO, texture, and shader sampler names now use ambient-occlusion terminology instead of leaking old SSAO-only naming into every AO path.
- The next highest-value code change is refining HBAO+ quality/perf rather than splitting effort across a second horizon-search implementation.

## Key Files

- `XREngine/Rendering/Camera/AmbientOcclusionSettings.cs`
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs`
- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_HBAOPlusPass.cs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs`

## Suggested Next Step

The next implementation phase should focus on HBAO+ validation and tuning unless there is a concrete need for a separate classic-HBAO debug/reference path.
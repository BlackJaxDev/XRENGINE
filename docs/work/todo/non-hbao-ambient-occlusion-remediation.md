# Non-HBAO Ambient Occlusion Status And Remediation TODO

Last Updated: 2026-03-11
Current Status: non-HBAO AO modes have now been audited against the current code and external references. The MSVO path had its dead UI controls removed, the misleading SAO selector entry is gone, the live AO API now exposes canonical `MVAO` and `MSVO` names while retaining compatibility aliases, GTAO has a first real gather plus denoise path, legacy `HorizonBased` now normalizes to `HBAO+`, and VXAO remains an explicit scaffold plus dedicated shared-voxel design/TODO item instead of silently pretending to work.
Scope: current non-HBAO AO modes plus roadmap support planning for GTAO and VXAO. HBAO/HBAO+ are handled in separate docs.

This document now consolidates the prior non-HBAO AO audit and the follow-up remediation backlog into one canonical work doc.

## Current Reality

What is already true:

- `ScreenSpace` is a recognizable legacy SSAO implementation.
- `MultiViewAmbientOcclusion` is internally coherent as a real multi-view AO family.
- `MultiScaleVolumetricObscurance` has a dedicated multi-scale obscurance pass.
- `SpatialHashRaytraced` is implemented as an experimental compute AO path.
- Dead MSVO editor controls for `ResolutionScale` and `SamplesPerPixel` have been removed because the runtime pass does not consume them.
- The AO method dropdown now labels the current non-HBAO families canonically: `MVAO`, `MSVO`, `HBAO+`, `GTAO`, `VXAO`, and `Spatial Hash AO`.

What remains problematic:

- `MultiScaleVolumetricObscurance` still needs deeper validation against canonical MSVO expectations.
- `SpatialHashRaytraced` still needs stronger validation before it can be treated as production-stable.
- GTAO now has an early runtime implementation, while VXAO is still only a deliberate scaffold and planning target.

## Consolidated Audit Summary

Current classification of the non-HBAO AO families:

- `ScreenSpace` is an honestly named legacy SSAO path.
- `MultiViewAmbientOcclusion` is treated as a canonical multi-view AO family with its own dedicated pass.
- `ScalableAmbientObscurance` remains only a compatibility alias; `MultiScaleVolumetricObscurance` is the live multi-scale obscurance mode.
- `SpatialHashRaytraced` is an experimental compute AO path and should remain documented as such until stronger validation exists.
- GTAO is the strongest modern canonical screen-space follow-on for this area.
- VXAO is a longer-range shared-voxel roadmap item, not a small extension of the current screen-space pass set.

Recommended current readiness order:

1. `ScreenSpace`
2. `MultiViewAmbientOcclusion`
3. `SpatialHashRaytraced`
4. `ScalableAmbientObscurance` / `MultiScaleVolumetricObscurance`

## Target Outcome

At the end of this work:

- every non-HBAO AO mode is honestly named relative to its implementation
- the editor shows only controls that actually affect the active pass
- published and experimental AO modes are documented honestly
- canonical AO names and compatibility aliases are separated cleanly
- GTAO and VXAO are tracked as deliberate future families with the right implementation expectations

## Non-Goals

- Do not expand this TODO into HBAO/HBAO+ work.
- Do not rewrite the deferred AO output contract.
- Do not chase speculative AO variants beyond the now-explicit GTAO and VXAO roadmap additions.

## Phase 0 - Keep The Editor Honest

Outcome: the AO editor stops implying capabilities the runtime does not have.

- [x] Remove dead MSVO settings from the schema.
- [x] Keep only MSVO settings that the runtime currently consumes.
- [x] Audit other AO modes for any remaining editor controls that do not affect the shader/runtime path.
- [x] Make the AO method selector use canonical day-to-day names for MVAO, MSVO, HBAO+, GTAO, VXAO, and Spatial Hash AO.

Acceptance criteria:

- no AO mode shows controls that are definitely dead in the current runtime implementation
- the AO method selector does not present legacy aliases as if they were distinct implementations

## Phase 1 - Classify And Rename Where Needed

Outcome: AO mode names align with reality.

- [x] Remove `ScalableAmbientObscurance` from the editor selector until a canonical SAO implementation exists.
- [x] Keep `MultiScaleVolumetricObscurance` as the primary live API name for the current MSVO pass.
- [x] Remove the old prototype/custom selector wording from the live API surface.
- [x] Document the multi-view path explicitly as MVAO.
- [x] Document the spatial-hash path explicitly as experimental.
- [x] Replace the temporary selector labels with honest live enum/API names while retaining compatibility aliases.

Acceptance criteria:

- the names shown in the editor and docs use the canonical AO family names
- old enum values still map cleanly to the current runtime behavior

## Phase 2 - Decide The Fate Of Current MVAO

Outcome: MVAO has an explicit supported role instead of lingering ambiguously.

- [ ] Decide whether MVAO remains a supported custom mode after HBAO+ lands.
- [ ] If kept, document its intent and tuning guidance.
- [ ] If not kept, mark it experimental/deprecated and plan migration.

Acceptance criteria:

- MVAO has a deliberate product position: supported mode, experimental mode, or deprecation path

## Phase 3 - Repair Or Replace The SAO/MSVO Slot

Outcome: the engine either validates the current MSVO implementation against its references or narrows its claims further.

Option A: strengthen the current MSVO implementation

- [ ] validate the current MSVO gather and blur against its published reference expectations
- [ ] expose any additional MSVO settings only if the runtime actually consumes them
- [ ] document the exact algorithmic compromises if the implementation remains approximate

Option B: narrow claims further if validation fails

- [ ] hide the current MSVO entry from non-experimental use
- [ ] or rename it again if the implementation proves too far from canonical MSVO behavior

Acceptance criteria:

- the engine no longer presents the MSVO pass more confidently than its validated implementation quality supports

## Phase 4 - Validate SpatialHashRaytraced As Experimental

Outcome: the spatial-hash AO path has an explicit validation bar.

- [ ] test stability under camera motion and changing geometry
- [ ] test failure cases at screen edges, thin geometry, and low sample counts
- [ ] test whether cached cell reuse causes objectionable ghosting or lagging artifacts
- [ ] document expected quality/performance tradeoffs

Acceptance criteria:

- the spatial-hash path either earns an experimental supported status or is clearly marked as research-only

## Phase 5 - Add GTAO Deliberately

Outcome: the engine gains a modern canonical non-HBAO screen-space AO target.

- [x] add a dedicated GTAO research/design note under `docs/work/design`
- [x] add an explicit GTAO enum/schema/pipeline stub so future work lands on a real AO family slot instead of overloading another path
- [x] define GTAO-specific inputs and outputs without reusing misleading SSAO/MSVO labels
- [x] add a first GTAO gather plus edge-aware denoise path
- [ ] decide whether GTAO is exposed beside HBAO+ or becomes the preferred modern non-HBAO screen-space path

Acceptance criteria:

- GTAO is treated as a first-class algorithm family with its own settings, pass design, and product positioning

## Phase 6 - Evaluate VXAO On Shared Voxel Infrastructure

Outcome: VXAO is handled as a world-space voxel AO project built on the same shared voxelization path as voxel cone tracing GI instead of a post-process tweak.

- [x] add a dedicated VXAO design note under `docs/work/design`
- [x] add an explicit VXAO enum/schema/pipeline scaffold so future work lands on a real AO family slot instead of overloading another path
- [x] add a dedicated shared-voxel VCT/VXAO implementation TODO under `docs/work/todo`
- [ ] lock the shared voxel ownership, coverage, and transform contract with VCT
- [ ] define voxel coverage, memory, payload, and update strategy expectations for the shared volume
- [ ] define how VXAO would blend with a short-range screen-space fallback for fine detail
- [ ] decide whether VXAO belongs in the default pipeline, as an advanced renderer option, or as research-only work

Acceptance criteria:

- VXAO is either rejected cleanly or tracked as a real shared-voxel feature with explicit scope and dependency on working VCT infrastructure

## Phase 7 - Publish The AO Readiness Matrix

Outcome: the team has one agreed ranking for non-HBAO AO modes.

- [ ] mirror the readiness ranking from the design audit into a stable doc if these modes remain user-facing
- [ ] ensure editor labels and docs use the same quality/readiness language

Recommended current readiness order:

1. `ScreenSpace`
2. `MultiViewAmbientOcclusion`
3. `SpatialHashRaytraced`
4. `ScalableAmbientObscurance` / `MultiScaleVolumetricObscurance`

Recommended future non-HBAO target order if new families are approved:

1. GTAO
2. `ScreenSpace`
3. `MultiViewAmbientOcclusion`
4. VXAO
5. `SpatialHashRaytraced`
6. `ScalableAmbientObscurance` / `MultiScaleVolumetricObscurance`

## Key Files

- `XREngine/Rendering/Camera/AmbientOcclusionSettings.cs`
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs`
- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_SSAOPass.cs`
- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_MVAOPass.cs`
- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_MSVO.cs`
- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_SpatialHashAOPass.cs`

## Suggested Next Step

The next concrete code task after this TODO should be Phase 3: validate the current MSVO implementation quality against its references now that the selector and API use the canonical MSVO/MVAO naming.

## Research Sources

- McGuire, Mara, Luebke, `Scalable Ambient Obscurance`, HPG 2012
- McGuire and Mara, `Efficient GPU Screen-Space Ray Tracing`, JCGT 2014
- Jimenez, Wu, Pesce, Jarabo, `Practical Real-Time Strategies for Accurate Indirect Occlusion`
- NVIDIA, `VXAO: Voxel Ambient Occlusion`
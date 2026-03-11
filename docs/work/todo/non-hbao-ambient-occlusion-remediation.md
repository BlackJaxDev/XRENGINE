# Non-HBAO Ambient Occlusion Remediation TODO

Last Updated: 2026-03-11
Current Status: non-HBAO AO modes have now been audited against the current code and external references. The MSVO path was confirmed to expose dead UI controls, those controls have been removed from the post-process schema, the misleading SAO selector entry has been removed, the live AO API now uses honest names with compatibility aliases for old enum values, GTAO now has a first real gather plus denoise path, and VXAO now has an explicit enum/schema/pipeline scaffold plus dedicated shared-voxel design and TODO docs instead of existing only as a research note.
Scope: current non-HBAO AO modes plus roadmap support planning for GTAO and VXAO. HBAO/HBAO+ are handled in separate docs.

## Current Reality

What is already true:

- `ScreenSpace` is a recognizable legacy SSAO implementation.
- `MultiViewAmbientOcclusion` is internally coherent as a custom AO mode.
- `SpatialHashRaytraced` is implemented as an experimental compute AO path.
- Dead MSVO editor controls for `ResolutionScale` and `SamplesPerPixel` have been removed because the runtime pass does not consume them.
- The AO method dropdown now labels the current non-HBAO families in a way that calls out legacy, custom, prototype, alias, and experimental status.

What remains problematic:

- `ScalableAmbientObscurance` and `MultiScaleVolumetricObscurance` both point at one simplified pass that does not match canonical SAO closely enough.
- `MultiViewAmbientOcclusion` is still a custom local name with no confirmed canonical external algorithm family.
- `SpatialHashRaytraced` still needs stronger validation before it can be treated as production-stable.
- GTAO now has an early runtime implementation, while VXAO is still only a deliberate scaffold and planning target.

## Target Outcome

At the end of this work:

- every non-HBAO AO mode is honestly named relative to its implementation
- the editor shows only controls that actually affect the active pass
- custom and experimental AO modes are clearly documented as such
- mislabeled AO modes are either implemented properly or renamed/de-emphasized
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
- [x] Make the AO method selector label current non-HBAO modes honestly enough for day-to-day editor use.

Acceptance criteria:

- no AO mode shows controls that are definitely dead in the current runtime implementation
- the AO method selector does not present custom, aliased, or experimental modes as if they were all equally canonical

## Phase 1 - Classify And Rename Where Needed

Outcome: AO mode names align with reality.

- [x] Remove `ScalableAmbientObscurance` from the editor selector until a canonical SAO implementation exists.
- [x] Stop exposing `MultiScaleVolumetricObscurance` as the primary live API name for the current simplified pass.
- [x] Rename the current simplified pass to a non-canonical live API name.
- [x] Document the multi-view path explicitly as custom.
- [x] Document the spatial-hash path explicitly as experimental.
- [x] Replace the temporary selector labels with honest live enum/API names while retaining compatibility aliases.

Acceptance criteria:

- the names shown in the editor and docs do not overclaim algorithm correctness
- old enum values still map cleanly to the current runtime behavior

## Phase 2 - Decide The Fate Of Current MVAO

Outcome: MVAO has an explicit role instead of lingering ambiguously.

- [ ] Decide whether MVAO remains a supported custom mode after HBAO+ lands.
- [ ] If kept, document its intent and tuning guidance.
- [ ] If not kept, mark it experimental/deprecated and plan migration.

Acceptance criteria:

- MVAO has a deliberate product position: supported custom mode, experimental mode, or deprecation path

## Phase 3 - Repair Or Replace The SAO/MSVO Slot

Outcome: the engine either has a real SAO-style implementation or stops pretending it does.

Option A: implement a canonical SAO path

- [ ] add a dedicated SAO implementation with depth prefiltering / depth hierarchy
- [ ] give `ScalableAmbientObscurance` its own true backing pass
- [ ] expose only settings that map to the actual SAO implementation

Option B: defer canonical SAO and stop overclaiming

- [ ] hide the current SAO/MSVO entry from non-experimental use
- [ ] or rename it to reflect that it is a simplified multi-radius obscurance prototype

Acceptance criteria:

- the engine no longer presents the current simplified pass as canonical SAO/MSVO unless it really becomes one

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

The next concrete code task after this TODO should be Phase 1: decide whether the current SAO/MSVO entries stay visible at all before HBAO+ and any future GTAO work redefine the engine's modern AO lineup.
# Default Pipeline GPU Hotspots TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
Validation: [Default And Advanced Pipeline Validation](../../../testing/rendering/default-and-advanced-pipeline-validation.md)

## Current State

`GroundTruthAmbientOcclusionSettings` defaults to 3 slices, 4 steps per slice, half resolution, normal-weighted blur, and visibility-bitmask GTAO. `DefaultRenderPipeline.Resources.cs` encodes GTAO full, half, and quarter resolution in the resource feature mask and scales scratch extents from that mode. GPU hotspot work remains for quality profiles, exposure skip policy, light-combine cost, VR defaults, diagnostics, and profiler metadata.

## Open Code Items

### Quality Profiles

- [ ] Add a VR performance quality profile for AO, exposure, bloom, TSR, and post-processing. Camera post-process settings and default profile selection. Done when: the active profile is visible in GPU dumps and logs.
- [ ] Make expensive full-resolution screen-space effects opt-in or dynamically quality-scaled for XR. Default render pipeline settings. Done when: the standard VR profile avoids full-resolution cost unless the user requests it.
- [ ] Add pass-level output resolution, sample count, view count, and quality settings to GPU profiler dumps. Profiler output code. Done when: hotspot rows include enough settings to reproduce the cost.

### Exposure And Effect Skips

- [ ] Ensure auto exposure does not run when the active exposure policy skips or cannot consume the result. Auto exposure commands and settings. Done when: skipped exposure removes the compute pass cost.
- [ ] Add diagnostics for effect enabled state, disabled state, and skip reason. Render logs and profiler metadata. Done when: a disabled effect explains why its producer did not run.
- [ ] Keep graph-topology removal of disabled producers in the resource-lifecycle work. Done when: this todo owns only quality policy and shader-side skip logic.

### Ambient Occlusion

- [ ] Tune GTAO resolution, denoise radius, slice count, and step count for VR. `GroundTruthAmbientOcclusionSettings`, `GTAOGen.fs`, `GTAOGenStereo.fs`. Done when: the standard VR profile does not spend 5 to 8 ms on GTAO.
- [ ] Add a debug view comparing AO quality and performance modes. Debug visualization code. Done when: AO quality regressions are visible without external tooling.
- [ ] Feed redundant AO intermediate-copy or aliasing findings into the resource-lifecycle work. Done when: shader kernel benchmarks use the accepted minimal graph.

### Lighting And Post

- [ ] Add light-combine profiler breakdown by light count, tile or cluster count, G-buffer format, and MSAA state. Profiler output code. Done when: the pass cost is attributed to specific inputs.
- [ ] Add stereo light-combine view-count metadata. Profiler output code. Done when: profiler rows identify per-eye or whole-frame cost.
- [ ] Reduce full-resolution post passes in VR where the effect can run at lower resolution or be disabled. Post-process settings and command conditions. Done when: the VR profile meets the selected headset budget or lists remaining tradeoffs.
- [ ] Add diagnostics that report masked foliage depth, overdraw, and shader variant selection. Material and forward-depth paths. Done when: variant selection is visible without manual shader inspection.

## Decisions Needed

- [ ] Choose the standard VR GPU budget for this tracker. Owner: rendering lead.
- [ ] Decide which AO mode is the preferred modern screen-space path after validation. Owner: rendering lead.

## Out Of Scope

- Redesigning default-pipeline graph topology.
- Treating render-thread CPU stalls as shader hotspot proof.
- Silent CPU fallback for a requested GPU path.

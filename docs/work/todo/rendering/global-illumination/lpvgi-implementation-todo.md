# LPV GI Implementation TODO

Last Updated: 2026-10-06
Status: Planned (no LPV code exists)
Architecture: [Global Illumination Ownership And Selection](../../../../architecture/rendering/global-illumination-ownership.md)  Design: [LPV Global Illumination Design](../../../design/global-illumination/lpv-global-illumination-design.md)
Validation: [Global Illumination Validation](../../../testing/rendering/global-illumination-validation.md#light-propagation-volumes)

## Current State

`EGlobalIlluminationMode.LightVolumes` is an unavailable descriptor in `GlobalIlluminationProviderRegistry`. `LightVolumeComponent`, `VPRC_LightVolumesPass`, `LightVolumes.comp`, and `LightVolumeComposite.fs` sample authored light volumes. They do no RSM capture, injection, or propagation. No host allocates LPV resources or schedules LPV work. The first target is OpenGL 4.6 in the ImGui editor; Vulkan follows.

## Open Code Items

### Provider And Settings

- [ ] Add an LPV GI module with provider-owned settings (`Off`, `Low`, `Medium`, `High`, `DebugReference` presets), resources, passes, debug surfaces, invalidation, and release. Register it as unsupported until the validation checks pass. Done when: the module declares only neutral host inputs and outputs, and inactive LPV allocates nothing.
- [ ] Add resource types for RSM outputs, LPV ping-pong volumes, fixed-point injection temporaries, optional geometry volume (GV), optional history, and debug counters. Reuse allocated arrays, constants, and query handles per frame. Done when: the per-frame path has no managed allocations.
- [ ] Add cascade placement types (snapped bounds, shader constants) and shader program lookup for clear, inject, resolve, propagate, debug, and lighting passes. Done when: the types compile and are used by the module.

### Directional RSM

- [ ] Add a directional-light reflective shadow map pass with position, world normal, and diffuse flux outputs, and an RSM resolution tied to the preset. Done when: the pass writes the three outputs.
- [ ] Add RSM debug views (depth, normal, flux) and GPU timing for the pass. Done when: the views are selectable in the editor.

### Injection And Propagation (OpenGL)

- [ ] Inject RSM samples into fixed-point signed SH accumulators with a configurable normal bias, then resolve to FP16 LPV volumes. Add GPU clears and debug-build saturation counters. Done when: the inject and resolve passes run with counters.
- [ ] Implement six-face gather propagation with ping-pong volumes, an iteration count from the preset, and named OpenGL barriers for clear, inject, resolve, propagate, and lighting. Done when: the propagation pass runs the configured iterations.
- [ ] Sample LPV in scene lighting as diffuse indirect only, with an intensity setting. Keep direct diffuse, direct specular, emissive, and LPV diffuse separate in HDR. Done when: the lighting shader adds one LPV diffuse term.
- [ ] Add debug views for injected and propagated slices and dominant SH direction, and GPU timing for resolve, propagation, and sampling. Done when: the views and timings exist.

### Cascades

- [ ] Enable four 32x32x32 cascades for the medium preset, with one-cell origin snapping, preset-driven extents, finest-containing-cascade selection, boundary blending, and a bounds overlay. Done when: the shader selects and blends cascades.

### Leak Reduction

- [ ] Add an optional per-cascade geometry volume with half-cell offset blocker injection, propagation attenuation, and optional derivative or wall-thickness damping. Add GV slice and leak heatmap debug views. Done when: propagation reads the GV.

### Temporal And Editor

- [ ] Add optional history with blending after propagation and rejection on camera or cascade changes, plus a temporal delta debug view. Done when: history rejects on cascade snap.
- [ ] Add ImGui controls for quality, cascades, cell count, iterations, intensity, injection bias, GV, history, and debug mode. Done when: the controls change the live provider settings.
- [ ] Add Unit Testing World toggles and LPV validation scenes (Cornell-box bounce, large atrium, outdoor directional bounce, thin wall, moving hero spotlight). Run `Tools/Generate-UnitTestingWorldSettings.ps1` after settings type changes. Done when: each scene loads from a setting.

### Performance

- [ ] Add LPV memory-footprint reporting, per-stage GPU timing in the render diagnostics UI, and CPU instrumentation for cascade placement and dispatch setup. Done when: the values show in diagnostics.

### Local Lights (Optional)

- [ ] Add a top-N budget for LPV-injected local lights and spotlight RSM injection. Keep other local lights direct-only. Done when: a spotlight injects into LPV within the budget.

### Vulkan

- [ ] Mirror LPV resources in Vulkan: images, views, descriptors, layouts, allocation lifetimes, and debug names. Done when: every LPV resource has a Vulkan resource with a debug name.
- [ ] Implement Vulkan compute clear, inject, resolve, propagate, GV, history, and debug passes with sync2 barriers that match the OpenGL transitions, and storage formats that match the shader declarations. Done when: the Vulkan passes record with no validation-layer errors in code review.

### Tests (Owner Clearance Required)

- [ ] Add unit tests for SH basis evaluation, cascade snapping, world-to-cell transforms, fixed-point conversion and packing, saturation, cascade selection and blending, propagation indexing, and GV half-cell alignment. Done when: each behavior has a deterministic test.

## Decisions Needed

- [ ] Decide whether the first RSM pass reuses the shadow-map path or is LPV-specific. Owner: rendering lead.
- [ ] Decide whether LPV stays opt-in or joins a default dynamic GI preset after profiling. Owner: rendering lead.
- [ ] Decide whether point-light injection (cubemap or multi-face) is affordable. Owner: rendering lead.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/rendering/global-illumination/lpvgi-implementation-todo.md`

- [ ] Add derivative or wall-thickness damping where it reduces leaks without over-darkening.

# LPV GI Implementation Todo

Source design: `docs/work/design/global-illumination/lpv-global-illumination-design.md`

Goal: implement Light Propagation Volumes as an optional dynamic diffuse global illumination feature for XRENGINE, proving the OpenGL path first and leaving Vulkan integration as the final backend phase.

## Phase 0: Branch and planning setup

- [ ] Create a dedicated branch for this todo list.
- [ ] Confirm the first implementation target is the ImGui editor with the OpenGL 4.6 renderer.
- [ ] Confirm LPV GI remains disabled by default until the feature has debug views and validation scenes.
- [ ] Decide where renderer-facing LPV settings should live.
- [ ] Decide whether the first RSM pass reuses the shadow-map path directly or starts as an LPV-specific light pass.

## Phase 1: Settings, scaffolding, and resource ownership

- [ ] Add LPV GI settings with `Off`, `Low`, `Medium`, `High`, and `DebugReference` presets.
- [ ] Add renderer feature scaffolding for LPV GI without changing default rendering behavior.
- [ ] Add resource ownership types for RSM outputs, LPV ping-pong volumes, fixed-point injection temps, optional GV, optional history, and debug counters.
- [ ] Add cascade placement data structures for snapped cascade bounds and shader constants.
- [ ] Add shader-program lookup scaffolding for LPV clear, inject, resolve, propagate, debug, and lighting integration passes.
- [ ] Ensure LPV per-frame paths reuse allocated arrays, lists, constants, and query handles.
- [ ] Add focused unit tests for SH basis evaluation, cascade snapping, and world-to-cell transforms.

## Phase 2: OpenGL directional RSM input

- [ ] Add a directional-light Reflective Shadow Map pass for LPV GI.
- [ ] Output or reconstruct light-space/world-space position data needed for injection.
- [ ] Output world-space normals in a format suitable for injection.
- [ ] Output diffuse reflected flux from directly lit surfels.
- [ ] Add an RSM resolution setting tied to LPV quality presets.
- [ ] Add RSM debug views for depth, normal, and flux in the ImGui editor.
- [ ] Add GPU timing instrumentation for the RSM pass.

## Phase 3: OpenGL LPV injection and resolve

- [ ] Allocate fixed-point signed integer injection accumulators for RGB SH coefficients.
- [ ] Add GPU clear for injection temp resources and initial LPV volumes.
- [ ] Implement compute injection from RSM samples into fixed-point SH accumulators.
- [ ] Apply configurable normal/injection bias to reduce self-lighting.
- [ ] Add saturation or overflow counters for debug builds.
- [ ] Resolve fixed-point injection accumulators into FP16 LPV source volumes.
- [ ] Add debug visualization for raw injected LPV slices.
- [ ] Add unit tests for fixed-point conversion, coefficient packing, and saturation behavior.

## Phase 4: OpenGL propagation and scene lighting

- [ ] Implement 6-face gather propagation with LPV ping-pong volumes.
- [ ] Add propagation iteration count from quality settings.
- [ ] Add named OpenGL backend barriers for clear, inject, resolve, propagate, and lighting transitions.
- [ ] Sample LPV during scene lighting as diffuse indirect only.
- [ ] Add LPV intensity control in renderer/editor settings.
- [ ] Add GPU timing for resolve, propagation, and scene-lighting LPV sampling.
- [ ] Add debug visualization for propagated LPV slices and dominant SH direction.
- [ ] Verify direct diffuse, direct specular, emissive, and LPV diffuse indirect remain separately composed in HDR.

## Phase 5: Cascades and quality baseline

- [ ] Enable four cascades with 32x32x32 cells as the default medium preset.
- [ ] Add one-cell cascade origin snapping.
- [ ] Add cascade scale controls or preset-driven world extents.
- [ ] Add finest-containing-cascade selection during shading.
- [ ] Add smooth blending near cascade boundaries.
- [ ] Add cascade bounds debug overlay.
- [ ] Add validation scenes for Cornell-box-style color bounce, large atrium coverage, and outdoor directional light bounce.
- [ ] Add unit tests for cascade boundary blending and cascade selection.

## Phase 6: Geometry volume and leak reduction

- [ ] Add optional geometry volume resources per cascade.
- [ ] Inject coarse blocker data with the required half-cell offset from LPV cells.
- [ ] Add GV sampling during propagation to attenuate transport through blockers.
- [ ] Add derivative or wall-thickness damping where it reduces leaks without over-darkening.
- [ ] Add GV slice debug visualization.
- [ ] Add leak heatmap debug visualization.
- [ ] Add thin-wall validation scene coverage.
- [ ] Add unit tests for GV half-cell alignment and propagation attenuation indexing.

## Phase 7: Temporal stability and editor workflow

- [ ] Add optional LPV history resources.
- [ ] Add history blending after propagation.
- [ ] Add basic history rejection for camera/cascade changes.
- [ ] Add temporal delta debug visualization.
- [ ] Add ImGui controls for LPV quality, cascades, cell count, propagation iterations, intensity, injection bias, GV, history, and debug mode.
- [ ] Add Unit Testing World toggles for LPV GI validation scenes if needed.
- [ ] Regenerate Unit Testing World settings and schema if new toggles are added.
- [ ] Update docs for any new editor preferences, launch flags, environment variables, or validation workflows.

## Phase 8: Performance hardening and production presets

- [ ] Add quality preset values for `Low`, `Medium`, `High`, and `DebugReference`.
- [ ] Add LPV memory-footprint reporting.
- [ ] Add per-stage GPU timing display in the render diagnostics UI.
- [ ] Add CPU-side instrumentation for cascade placement and LPV draw/dispatch setup.
- [ ] Remove or pool any hot-path allocations discovered during implementation.
- [ ] Profile representative scenes and tune default budgets.
- [ ] Decide whether LPV GI remains opt-in or becomes part of a default dynamic GI preset.
- [ ] Document known artifacts and recommended content usage.

## Phase 9: Optional local-light support

- [ ] Add a budget model for top-N LPV-injected local lights.
- [ ] Add spotlight RSM injection if the directional-light path is stable.
- [ ] Evaluate point-light cubemap or multi-face injection cost before implementation.
- [ ] Add editor controls for local-light LPV participation.
- [ ] Add validation scene for one moving hero spotlight.
- [ ] Keep long-tail local lights direct-only unless profiling proves broader injection is affordable.

## Phase 10: Vulkan integration

- [ ] Mirror LPV resources with Vulkan images, image views, descriptors, and allocation lifetimes.
- [ ] Add Vulkan descriptor layouts for RSM inputs, injection temps, LPV volumes, GV, history, and constants.
- [ ] Implement Vulkan compute clear, inject, resolve, propagate, GV, history, and debug extraction passes.
- [ ] Add sync2 barriers matching the logical OpenGL transitions.
- [ ] Validate storage-image formats exactly match shader declarations.
- [ ] Add Vulkan debug names for all LPV resources and passes.
- [ ] Match OpenGL debug output behavior in Vulkan.
- [ ] Run Vulkan validation layers through LPV scenes once the backend path is functional.
- [ ] Update rendering docs with Vulkan LPV backend notes and any remaining backend differences.

## Phase 11: Final validation and merge-back

- [ ] Run targeted unit tests for LPV math, cascade placement, injection conversion, propagation indexing, and GV alignment.
- [ ] Run targeted editor/render validation scenes for OpenGL LPV GI.
- [ ] Run Vulkan LPV validation if Phase 10 is complete and Vulkan is enabled in the local environment.
- [ ] Review docs for user-facing settings, debug views, and known limitations.
- [ ] Capture remaining risks and follow-up issues.
- [ ] Merge the completed LPV GI branch back into `main` after implementation and validation are complete.


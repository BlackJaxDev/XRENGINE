# 08 - Transparency, Special Passes, And Post-Processing TODO

Last Updated: 2026-07-28
Owner: Rendering
Status: Proposed
Depends On: [07 - Native Material, Lighting, Decal, And GI Shading](07-native-material-lighting-decals-and-gi-todo.md)
Next: [09 - Stereo, XR, Capture, And Editor Integration](09-stereo-xr-capture-and-editor-integration-todo.md)

## Goal

Reconnect material classes that cannot use opaque visibility shading and rebuild
the temporal/post-processing chain around the native opaque HDR result. These
passes must remain explicit exceptions; they must not restore an ordinary
opaque forward renderer or deferred/forward full-frame composite.

## Pass Categories

Every late draw must declare one category:

- transparent and temporally participating;
- refractive and scene-color dependent;
- exact-transparency/OIT;
- volumetric or atmospheric;
- post-temporal overlay;
- editor/debug/on-top;
- UI/presentation.

Opaque or masked materials cannot select one of these categories merely because
their advanced kernel is unavailable.

## TODO

### 1. Late-Pass Eligibility

- [ ] Add explicit material/pass metadata for blend, refraction, order
  dependence, temporal participation, depth-write behavior, and scene-color
  dependency.
- [ ] Remove advanced-pipeline use of `OpaqueForward` and `MaskedForward`.
- [ ] Reject compatible opaque work that attempts to enter a late path.
- [ ] Render unsupported required-mode opaque work with an observable error
  material or fail pipeline selection.
- [ ] Report late-pass counts and reasons per category.

### 2. Scene Color And Depth Contract

- [ ] Publish native opaque HDR, final visibility depth, optional normal/AO
  sidecars, and exposure state under advanced resource names.
- [ ] Create a dedicated scene-color snapshot only when a refractive or
  scene-color-dependent pass is visible.
- [ ] Never sample an attachment while writing to the same image without a
  supported feedback-loop contract.
- [ ] Preserve depth testing against final visibility depth.
- [ ] Define internal/output resolution and stereo layer policy for every
  scene-color consumer.

### 3. Transparency And OIT

- [ ] Port weighted blended OIT to consume native opaque HDR and advanced
  depth.
- [ ] Port PPLL and depth peeling through declared resources and typed commands.
- [ ] Define which transparent materials use sorted alpha, weighted OIT, PPLL,
  or depth peeling.
- [ ] Preserve shadow, froxel-light, probe, fog, and texture-table access
  through shared GPU records.
- [ ] Define current/previous transform and reactive-mask behavior for
  transparent motion.
- [ ] Add capacity/overflow diagnostics for OIT buffers without same-frame
  readback.

### 4. Special Material Families

- [ ] Classify water, hair, particles, trails, beams, portals, mirrors, and
  custom effects as native visibility, transparent, refractive, volumetric, or
  unsupported.
- [ ] Give geometry-displacing opaque effects a specialized visibility writer
  plus native material kernel where production support is required.
- [ ] Keep simulation/update work outside the pipeline command-chain builder.
- [ ] Share global tables and avoid per-object descriptor reconstruction.
- [ ] Add an editor-visible reason for every unsupported special effect.

### 5. Atmosphere And Volumetric Fog

- [ ] Define sky, aerial-perspective, volumetric-fog, transparency, and
  refraction ordering.
- [ ] Adapt atmosphere and fog providers to final visibility depth and native
  HDR.
- [ ] Preserve half-resolution and temporal histories through declared
  resources.
- [ ] Ensure transparent objects receive consistent fog rather than relying on
  a legacy light-combine output.
- [ ] Validate camera cuts, underwater/interior cases, stereo, and disabled
  providers.

### 6. Dense Motion And Temporal Inputs

- [ ] Consume visibility-reconstructed opaque velocity directly.
- [ ] Merge transparent/special velocity only for participating pixels.
- [ ] Generate disocclusion, reactive, transparency, and invalid-history masks.
- [ ] Preserve exact jitter and motion-vector conventions required by TAA,
  TSR, DLSS, FSR, XeSS, and other active upscalers.
- [ ] Define history reset for resize, pipeline switch, camera cut, view-count
  change, render-scale change, HDR change, and shader/resource generation
  replacement.

### 7. Temporal And Post Chain

- [ ] Reconnect temporal accumulation at the correct point relative to
  participating transparency and fog.
- [ ] Reconnect motion blur, depth of field, bloom, exposure, tone mapping,
  color grading, vignette, FXAA/SMAA, TSR, and vendor upscale paths against
  advanced resource names.
- [ ] Skip disabled passes before resolving their resources or shaders.
- [ ] Preserve HDR/SDR output encoding and alpha behavior.
- [ ] Keep post-temporal overlays and UI outside temporal history.
- [ ] Remove legacy post-process bindings that assume GBuffer or light-combine
  attachment names.

### 8. Debugging And Validation

- [ ] Add a pass-category overlay and counts.
- [ ] Add views for scene-color snapshot, transparency accumulation/revealage,
  PPLL/depth-peel occupancy, reactive mask, velocity, history validity, fog,
  bloom, exposure, and final output.
- [ ] Validate no-transparency frames allocate or execute no transparency
  resources/work.
- [ ] Capture sorted alpha, OIT, refraction, particles, water, fog, atmosphere,
  temporal, HDR, and upscale scenes.
- [ ] Add tests proving advanced opaque material classes cannot enter ordinary
  forward passes.

## Acceptance Criteria

- [ ] Transparent and special content composes correctly over native opaque
  HDR and final visibility depth.
- [ ] Late paths are explicit, observable exceptions.
- [ ] The temporal/post chain consumes dense advanced depth, velocity, and
  reactive inputs.
- [ ] Disabled late/post features do no material work and resolve no unused
  resources.
- [ ] No classic deferred/ordinary opaque-forward composite is restored.


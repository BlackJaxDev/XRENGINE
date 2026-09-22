# Radiance Cascades runtime completion

Created: 2026-09-21
Status: unsupported; screen resolve exists, radiance production does not

## Current boundary

`RadianceCascadeComponent` owns up to four externally authored `XRTexture3D`
radiance volumes. `RadianceCascadesGlobalIlluminationModule` declares mono/stereo
screen output and history, and `VPRC_RadianceCascadesPass` samples those volumes,
applies diffuse material response, accumulates history, and uses neutral GI
composition. This is a consumer of prepared radiance, not a complete Radiance
Cascades GI implementation.

The provider remains deliberately unsupported in
`GlobalIlluminationProviderRegistry`. Do not enable it by changing the static
support result until all exit checks below pass.

## Missing implementation

- [ ] Define the cascade representation: direction/angular bins, spatial layout,
  encoding, mip/level relationship, world transforms, and precision budget.
- [ ] Implement scene-radiance injection from geometry, emissive materials, direct
  lights, shadow visibility, and the environment.
- [ ] Implement cascade propagation/merge and near-to-far update scheduling with
  explicit barriers and renderer-owned submission receipts.
- [ ] Add provider-owned persistent runtime state, invalidation revisions, resize
  and scene-replacement handling, disposal, and renderer-owner recovery.
- [ ] Define authored/baked asset serialization and validation. Reject incompatible
  dimensions, formats, transforms, and versions with actionable diagnostics.
- [ ] Replace first-active selection with the same deterministic single-volume or
  explicit multi-volume policy used by supported providers.
- [ ] Confirm temporal history validity across camera cuts, movement, dynamic
  resolution, normal/reverse depth, mono/stereo, and view-owner replacement.
- [ ] Validate the material-response contract for diffuse/metallic/AO behavior and
  prove baseline diffuse is suppressed only after a valid replacement is published.
- [ ] Exercise Default and Advanced through the shared module boundary on OpenGL
  and Vulkan; inspect screen output, each cascade, history, and final composition.
- [ ] Measure update cost and verify no per-frame managed allocations. The active
  cascade list is already cached on authoring changes rather than materialized in
  the render pass.

## Exit gate

The provider may become experimental/supported only when live radiance is produced
without pre-populated textures, Default and Advanced show equivalent output
semantics, mono and stereo history are correct, interruption/lifetime evidence is
clean, and the result is applied exactly once through neutral composition.

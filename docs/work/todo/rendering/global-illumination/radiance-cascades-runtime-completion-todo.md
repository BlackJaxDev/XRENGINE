# Radiance Cascades Runtime Completion TODO

Last Updated: 2026-10-06
Status: Active (provider rejected at runtime)
Architecture: [Global Illumination Ownership And Selection](../../../../architecture/rendering/global-illumination-ownership.md), [Radiance Cascades guide](../../../../developer-guides/gi/radiance-cascades.md)
Validation: [Global Illumination Validation](../../../testing/rendering/global-illumination-validation.md#radiance-cascades)

## Current State

`RadianceCascadeComponent` owns up to four authored `XRTexture3D` radiance volumes. `RadianceCascadesGlobalIlluminationModule` declares mono and stereo screen output and history. `VPRC_RadianceCascadesPass` samples the volumes, applies diffuse material response with `1/pi`, accumulates history, and uses neutral GI composition. The active cascade list is cached on authoring changes. No injection, propagation, or live update exists, so the registry rejects the selection. Volume selection uses `RadianceCascadeComponent.Registry.TryGetFirstActive`.

## Open Code Items

### Representation

- [ ] Define the cascade representation: angular bins, spatial layout, encoding, level relationship, world transforms, and precision budget. Done when: the layout is in code constants and in the Radiance Cascades guide.

### Radiance Production

- [ ] Inject scene radiance from geometry, emissive materials, direct lights, shadow visibility, and the environment. Done when: a cascade fills from an empty texture with no pre-populated data.
- [ ] Implement cascade propagation and merge with near-to-far update scheduling, explicit barriers, and renderer-owned submission receipts. Done when: the passes declare graph accesses and a rejected submission does not advance history.

### Lifetime

- [ ] Add provider-owned persistent runtime state with invalidation revisions, resize and scene-replacement handling, disposal, and renderer-owner recovery. Done when: state is keyed by physical pipeline and renderer owner, as in [Global Illumination Ownership](../../../../architecture/rendering/global-illumination-ownership.md#lifetime-and-invalidation-domains).
- [ ] Define authored and baked asset serialization and validation. Reject wrong dimensions, formats, transforms, and versions with clear diagnostics. Done when: a load of a bad asset gives a diagnostic and no contribution.
- [ ] Replace `TryGetFirstActive` with a deterministic single-volume policy (priority plus tie rejection, as DDGI) or an explicit multi-volume policy. Done when: registration order no longer selects the volume.

### Provider Status

- [ ] Change the registry descriptor to supported only after the validation checks pass. `GlobalIlluminationProviderRegistry.cs`. Done when: the static support result changes in the same change that records the passed checks.

## Decisions Needed

- [ ] Choose single-volume or multi-volume blending for v1. Owner: rendering lead.

## Out Of Scope

- ReSTIR radiance-cache reuse.
- Specular radiance-cascade sampling before diffuse production is valid.

# Sky And Atmosphere Follow-Ups TODO

Last Updated: 2026-10-06
Status: Planned
Architecture: [Atmospheric Scattering Component](../../../developer-guides/components/atmospheric-scattering.md)  Design: [Atmospheric Scattering Component Design](../../design/rendering/atmospheric-scattering-component-design.md)
Validation: [Default And Advanced Pipeline Validation](../../testing/rendering/default-and-advanced-pipeline-validation.md)

## Current State

`AtmosphericScatteringComponent`, `AtmosphericScatteringSettings`, and the shaders in `Build/CommonAssets/Shaders/Scene3D/Atmosphere/` implement direct single scattering for OpenGL mono rendering. `DefaultRenderPipeline` and `AdvancedRenderPipeline` both own the aerial-perspective chain and composite `AtmosphereColor` in `PostProcess.fs`. The atmosphere resources are 2D textures only. No transmittance or inscatter LUT exists.

## Open Code Items

### Stereo

- [ ] Add stereo texture-array variants for the aerial-perspective resources and shaders. `DefaultRenderPipeline.Resources.cs`, `AdvancedRenderPipeline` resource setup, `AtmosphereHalfDepthDownsample.fs`, `AtmosphereAerialPerspective.fs`, `AtmosphereReproject.fs`, `AtmosphereUpscale.fs`. Done when: stereo cameras allocate layered atmosphere textures and a shader contract test covers the array variants.

### Quality

- [ ] Add an optional transmittance and inscatter LUT quality mode. `AtmosphericScatteringSettings`, `AtmosphereCommon.glsl`, new LUT passes. Done when: a setting selects the LUT path and a unit test covers the setting default and the LUT resource contract.

### Materials

- [ ] Add optional material hooks so that objects outside the atmosphere receive atmosphere transmittance. `AtmosphereCommon.glsl`, forward material snippets. Done when: a material can opt in to the hook and a shader contract test covers the include.

## Decisions Needed

- [ ] Decide whether to sync ambient light from sky luminance and sun elevation. Owner: rendering lead.
- [ ] Decide whether to add physically scaled editor gizmos for large-radius atmospheres. Owner: editor lead.
- [ ] Decide the scope of Vulkan and DX12 resource and shader parity for the atmosphere path. Owner: rendering lead.

## Out Of Scope

- Multiple scattering, clouds, ozone, rainbows, fogbows, and weather.

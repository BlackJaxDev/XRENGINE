# Surface Detail Import And Forward Shadows

XREngine supports imported surface-detail textures and forward directional shadow receiving through shared shader helpers and explicit material parameters.

This feature doc promotes the implemented rendering note from `docs/work/design/rendering/shadows/surface-detail-and-forward-shadow-debugging.md`.

## Imported Surface Detail

Imported materials can provide either a normal map or a height map for surface detail.

The importer resolves a single surface-detail texture slot:

- `TextureType.Normals` is preferred when present.
- `TextureType.Height` is used as a fallback when no normal map exists.

The material receives shader parameters that describe how the texture should be interpreted:

- `NormalMapMode = 0` for RGB tangent-space normal maps.
- `NormalMapMode = 1` for grayscale height maps reconstructed into normals.
- `HeightMapScale` for finite-difference height reconstruction strength.

Forward and deferred shaders use the shared `SurfaceDetailNormalMapping.glsl` snippet so normal-map and height-map behavior stays consistent across render paths.

Imported height maps also compile their surface-detail shaders with `XRENGINE_HEIGHTMAP_MODE`. That makes the height-map reconstruction path unconditional and keeps Vulkan from accidentally decoding grayscale bump maps as RGB tangent-space normal maps if a runtime material uniform is missing or delayed. Material transparency normalization must preserve existing deferred shader variants so this import-time surface-detail mode is not stripped after the material's transparency state is resolved.

## Forward Shadow Casting

Shadow caster variants are used during shadow map rendering instead of drawing arbitrary source materials directly. The implemented variant factory forces shadow caster variants to render with no face culling, so one-sided imported surfaces can still cast shadows when viewed from the light's back side.

Opaque materials that are eligible for the shared opaque shadow material can use that faster path. Materials with alpha discard, custom shadow behavior, or incompatible shader hooks keep specialized shadow variants.

## Forward Directional Shadow Receive

Forward lighting now uses the engine-owned directional shadow bias controls instead of hardcoded large compare-bias constants.

The forward path consumes:

- `ShadowBiasParams`,
- `ShadowBiasProjectionParams`,
- per-cascade `CascadeBiasMin`,
- per-cascade `CascadeBiasMax`,
- and `CascadeReceiverOffsets`.

The shader computes receiver-plane slope bias and normal offset from shadow texel size. This makes the forward receive path line up with the rest of the engine's directional shadow tuning and avoids washing out real occluders with oversized fixed bias.

## Cascade Diagnostics

Directional cascades expose the effective cascade range and per-cascade bias values in editor diagnostics. The effective range is clamped from the source camera far plane, camera shadow-collection distance, and the light's cascade distance override.

This makes common failures easier to see, especially scenes where practical shadow coverage is much shorter than the camera far plane.

## Implementation References

- `XRENGINE/Core/ModelImporter.cs`
- `Build/CommonAssets/Shaders/Snippets/SurfaceDetailNormalMapping.glsl`
- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- `XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs`
- `XREngine.Runtime.Rendering/Shaders/ShadowCasterVariantFactory.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.cs`
- `XREngine.UnitTests/Rendering/AlphaToCoveragePhase2Tests.cs`
- `XREngine.UnitTests/Rendering/DeferredOpacityShaderContractTests.cs`
- `XREngine.UnitTests/Rendering/ImportedDeferredMaterialTests.cs`
- `XREngine.UnitTests/Rendering/UberShaderForwardContractTests.cs`
- `XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs`
- `XREngine.UnitTests/Rendering/ForwardDepthNormalVariantTests.cs`

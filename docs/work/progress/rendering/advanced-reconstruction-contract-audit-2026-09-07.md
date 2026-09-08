# Advanced reconstruction contract audit — 2026-09-07

Revision: `8b104bf7a` plus working changes. Source audit for ARP-A08; runtime
comparisons remain ARP-V56–V60. No new tests were added.

## Executable kernel inventory

`AdvancedGpuMaterialPublisher.TryTranslateLayout` admits only OpaqueDeferred,
ForwardOpaque and MaskedForward layouts. `CreateKernelRecord` interns a kernel
by layout, coverage and render-state class. These are dispatch identities for
the shared `Advanced/Shading/ShadeNativeOpaque.comp`, not arbitrary custom
shader programs. `AdvancedStandardMaterialShaderContract` verifies the shared
BaseColor/RMSE/AlphaCutoff/Flags word offsets, sizes and layout hashes. The GPU
`XR_ADV_IsStandardMaterial` repeats the layout and constant-range checks.

| Family | Reconstruction / consumer |
|---|---|
| Textured or constant opaque and masked | `ShadeNativeOpaque.comp` resolves generation-checked material/kernel handles and calls `XR_ADV_TryReconstructSurface` from `ReconstructSurface.glslinc`. Texture sampling uses the returned UV and gradients. Masked coverage is owned by visibility rasterization. |
| Unlit and emissive | The same reconstructed surface, base-color sampling and material constants; the eligibility/constant flags select unlit/emissive contributions in the common shading kernel. |
| Normal-mapped | The same surface's tangent, bitangent, normal and UV gradients feed tangent-space normal sampling. |
| Pending/invalid/custom layout | Layout/handle validation produces the explicit diagnostic color/reactive output. Unsupported translations return a publisher reason; custom shader code is not dispatched under a standard kernel ID. |
| Reconstruction reference/debug/validation | All include the same `ReconstructSurface.glslinc`. These optional resources are gated by reconstruction diagnostic features and `EnableReconstructionReferenceOutput`; they are not a production classic GBuffer. |

The shared decoder validates surface identity, frame slot, generations, meshlet
or indexed primitive ranges, packed current/previous geometry, view identity,
finite clip coordinates and perspective-correct barycentrics. It handles
flat-qualified attributes separately and computes UV derivatives analytically
from the same primitive. Temporal validity gates previous-frame reconstruction.
Both backend shader families use this include; API-specific buffer access and
view/layer indexing do not introduce separate interpolation implementations.

## Tangent-space convention

`AdvancedPackedVertexCodec.Pack` carries the source vertex's `BitangentSign`
with its octahedral tangent. Reconstruction interpolates normal/tangent data,
applies inverse-transpose normal transformation, projects the transformed
tangent off the normal, and multiplies source handedness by the world
transform's determinant sign before forming the bitangent. The CPU reference
is `AdvancedReconstructionTangentSpace.TryCreate`. This preserves the supplied
MikkTSpace tangent/sign convention; it does not prove that an importer generated
the correct source tangents. Missing packed input defaults and singular or
degenerate tangent frames need explicit consideration in the runtime fixtures.

UV1, Color1, flat attributes, deformation and analytical derivatives have
named mask bits. The admitted standard material shader does not implement an
arbitrary Custom0 shading interface. Geometry-displacing custom shader stages
are outside the admitted visibility variant contract. Static/skinned and
mirrored/normal-mapped image and numeric comparisons remain open.

## Gap found: ARP-I51

The canonical publisher initialized layout, kernel and material requirements
to zero, suppressing UV/color/normal/tangent reconstruction. ARP-I51 now publishes
Position, Normal, Tangent, TexCoord0, Color0 and AnalyticalDerivatives consistently
on all three records and the expected material header. Database unions remain
idempotent; classification receives the same derivative requirement consumed by
reconstruction. Shader normalization uses the CPU reference's finite geometric
normal / orthogonal tangent fallbacks for missing or cancelling attributes.
The restarted editor recreates all variants and schemas; this is not a live
repair of pre-existing zero-mask database rows.

Build 44 passes with zero warnings/errors. Both changed compute shaders compile
for OpenGL array and Vulkan 1.3 mono/array; the OVR fullscreen vertex also
compiles. OpenGL PID38032 renders an authored UV-varying blue/white checker on
both stereo panels, with correct eye parallax. Both 1920x1080 FXAA captures
contain zero nonfinite samples and alpha 1. The fresh log has no OpenGL errors.
This closes the implementation defect, not the broader reconstruction matrix.

ARP-A08 is closed as an inventory with the discovered defect assigned to I51.
Normal-map importer parity, mirrored/skinned frames, derivative numerics,
coverage edges and the other material families retain their separate V rows.

# Material Binding Policy

Last updated: 2026-06-17

## Production Rule

General scene materials use the GPU material table:

- `MaterialTable` stores compact material rows.
- Each material row stores base color/opacity, roughness/metallic/specular/emission constants, and texture handle or descriptor indices.
- `MaterialTextureHandleTable` stores OpenGL bindless texture handles. Vulkan bindless rows store descriptor-array indices directly in the material row.

The current material row is the standard opaque-deferred row. Its canonical contract is `MaterialBindingLayouts.OpaqueDeferred`; `GPUMaterialTable`, the generated material-table draw shader, and the shared GLSL material table header must agree on that layout hash and 48-byte row shape.

The broader upgrade for additional pass-declared layouts and shader annotation-driven conversion is tracked in [Dynamic Indirect Material Bindings](../../work/design/rendering/dynamic-indirect-material-bindings.md), with runtime ladder work tracked by the material-table TODOs.

Texture arrays are not the fallback for arbitrary material diversity. They are allowed only for genuinely homogeneous resource classes where every layer has the same semantic, dimensions, format, sampling policy, and lifetime pattern:

- decals
- terrain splat layers
- light cookies and shadow/cookie atlases
- render-target arrays used by stereo, cascades, probes, or post-processing

## Enforcement

`RenderingParameters.TextureArrayPolicy` defaults to `ArbitraryMaterialTextures`. Generic material-table draws reject texture-array inputs unless a material explicitly opts into `HomogeneousClassOnly`. That keeps one-off imported materials on bindless/descriptor indexing and prevents unbounded texture-array variants.

OpenGL `BindlessMaterialTable` uses `GL_ARB_bindless_texture` handles made resident by the renderer. The material table is two-level:

`MaterialEntry -> handle index -> 64-bit OpenGL handle`

Vulkan `BindlessMaterialTable` uses renderer-owned descriptor slots. The material row stores descriptor indices into `XR_BindlessMaterialTextures` at `set = 2`, `binding = 31`; descriptor index `0` is reserved for null/fallback and causes generated shaders to use the material fallback constant instead of sampling. Shaders that use the Vulkan path must read through `nonuniformEXT`.

The two modes are intentionally distinct:

- OpenGL rows store compact indices into `MaterialTextureHandleTable`; that second table stores resident 64-bit handles.
- Vulkan rows store descriptor indices directly; no OpenGL handle table is bound for the Vulkan descriptor-indexed variant.

The runtime texture reference contract is `GPUMaterialTextureReference`, with backend payloads for `OpenGLBindlessHandle` and `VulkanDescriptorIndex`.

## Per-Draw Texture Binding Ladder

Classic per-draw material binding is a compatibility rung under the same logical
material contract as the material table. Vulkan and OpenGL resolve material
textures in this order:

1. Program-bound sampler name, for render-target, engine, or FBO bindings that
   are supplied directly to the active program.
2. Material texture sampler name from `XRTexture.ResolveSamplerName(...)`.
3. Indexed `TextureN` alias from the stable material texture slot.
4. Numeric descriptor binding index plus array index for legacy shaders.
5. Bindless or descriptor-indexed material arrays, where the array index is a
   logical material texture index and must not be mixed with current per-draw
   sampler state.

Null material texture slots remain stable. A missing slot may use a visible
placeholder/fallback descriptor, but later texture indices must not shift.
Program-bound samplers participate in Vulkan descriptor fingerprints so FBO or
engine texture changes rewrite affected descriptor sets.

## Dynamic Layout Compatibility

Shaders can opt into material-table conversion with `//@binding(...)` directives or `indirect=...` keys on `//@property(...)` metadata. The resolver has three outcomes:

- `MaterialTableCompatible` when pass layout, shader metadata, and material state agree.
- `PerMaterialRequired` when a shader has no usable binding metadata or needs a conservative fallback.
- `Invalid` when the shader claims compatibility but uses missing or incompatible material semantics.

Unknown material uniforms must never be silently packed into the material table.

## PSO Families

The GPU-driven path batches by coarse material state, not by material identity. The built-in families are:

- opaque deferred
- opaque forward
- alpha-tested
- shadow
- transparent

Custom PSOs must be deliberate exceptions. Adding a texture-only material must not increase the graphics program or pipeline count.

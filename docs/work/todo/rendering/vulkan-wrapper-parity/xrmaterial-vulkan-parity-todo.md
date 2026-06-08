# XRMaterial Vulkan Parity TODO

Last Updated: 2026-06-08
Status: Active; per-material Vulkan wrapper/runtime parity pass implemented,
with material-table and hardware-validation work still open.

## Goal

Make `VkMaterial` provide the same engine-facing material behavior as
`GLMaterial`: parameter binding, texture binding, render options, engine
uniforms, shadow binding sources, material hooks, diagnostics, and descriptor
fallback behavior.

## Source Inventory

OpenGL:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs`

Vulkan:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMaterial.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Descriptors.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Uniforms.cs`

## Current Parity Already Present

- Vulkan has a material wrapper.
- `VkMaterial` tracks texture collection changes and material property changes.
- Descriptor sets are created per Vulkan program and frame.
- Material-owned uniform buffers are supported for a limited set of scalar,
  vector, and matrix parameter types.
- Image descriptors can use placeholders instead of failing the whole set.
- `VkMaterial` and `VkMeshRenderer.Descriptors` now share
  `MaterialTextureBindingResolver` for sampler-name, `TextureN`, numeric-slot,
  and bindless material-array texture resolution.
- Vulkan material uniform upload now supports shadow binding source filtering,
  active engine uniform auto-detection, AO uniforms, sampler aliases, and scoped
  bindings through the shared shadow resolver.
- Vulkan material-owned UBO serialization now covers bool vectors, doubles,
  dvecs, mat3 packing, and fixed-stride shader arrays in addition to the older
  scalar/vector/mat4 set.
- Vulkan shader/material contract source tests cover the implemented parity
  hooks.

## Generation Contract

`IsGenerated` must not be used as a proxy for descriptor-set readiness or valid
material data. OpenGL generation only means the API object exists and has an ID
where an API object exists; material uniforms/textures are made valid by later
binding calls. Vulkan should mirror that split: wrapper/cache generation is
separate from descriptor allocation, descriptor writes, uniform uploads, and
per-program material readiness.

## Missing Parity TODO

1. Define ownership between `VkMaterial` and `VkMeshRenderer`.
   - [x] Decide which wrapper owns engine uniforms, material parameters,
         textures, shadow source material binding, and custom setting hooks.
   - [x] Remove duplicated or divergent descriptor-resolution rules between
         `VkMaterial` and `VkMeshRenderer.Descriptors`.
   - [x] Document the final binding flow in `vulkan-renderer.md` or a material
         binding policy doc.

2. Match OpenGL texture binding semantics.
   - [x] Resolve textures by shader sampler name before numeric binding index.
   - [x] Support `XRTexture.ResolveSamplerName(...)` and indexed `TextureN`
         aliases consistently.
   - [x] Keep null texture slots stable so material texture index and descriptor
         index do not shift.
   - [x] Fold program-bound samplers into descriptor resource fingerprints.
   - [x] Keep bindless material array behavior explicitly separate from classic
         sampler-name binding.
   - [ ] When the material-table path is active, resolve texture references as
         material-row descriptor indices, not as per-draw current sampler state.
   - [ ] Require `nonuniformEXT` in Vulkan shader variants that index
         descriptor arrays from per-draw or per-material values.

3. Port shadow binding plans.
   - [x] Support `ShadowBindingSourceMaterial` for shadow-pass parameters.
   - [x] Support `ShadowBindingSourceMaterial` for shadow-pass textures.
   - [x] Match OpenGL's active-uniform filtering so only parameters/textures
         required by the shadow program are bound.
   - [x] Invalidate cached plans when source material parameters, textures, or
         active program bindings change.

4. Port shadow/custom material hooks.
   - [x] Call `ShadowUniformSourceMaterial.OnSettingShadowUniforms` when set.
   - [x] Call `ShadowBindingSourceMaterial.OnSettingShadowUniforms` when it has
         shadow handlers.
   - [x] Fall back to `OnSettingUniforms` only when OpenGL would do the same.
   - [x] Call `Data.OnSettingShadowUniforms` during shadow passes when the
         material itself supplies shadow handlers.
   - [x] Apply scoped program bindings after hooks if Vulkan keeps an
         equivalent program-level hook.

5. Match render option handling.
   - [x] Ensure material `RenderOptions` affect Vulkan pipeline state and
         dynamic state exactly once per draw.
   - [ ] Verify local render-options overrides take priority over material
         options like OpenGL.
   - [ ] Include render-option state in pipeline keys when it affects immutable
         Vulkan pipeline state.

6. Expand supported material parameter types.
   - [x] Audit `ShaderVar.SetUniform` coverage in OpenGL.
   - [x] Add Vulkan serialization for every shader var type that OpenGL can
         bind and that Vulkan shaders can legally consume.
   - [x] Include arrays, non-`mat4` matrices, booleans, integer vectors,
         unsigned vectors, and future struct/block-backed parameters.
   - [ ] Align std140/std430 layout packing with the reflected descriptor block
         layout instead of hardcoding only type sizes.

7. Match engine uniform behavior.
   - [x] Compare OpenGL `SetEngineUniforms` requirements with Vulkan auto
         uniform block generation.
   - [x] Bind camera, stereo, lighting, AO, viewport, render-time, and material
         required engine uniforms through one consistent Vulkan path.
   - [x] Ensure material required engine uniforms are honored even when the
         program does not expose a reflected auto-uniform block.

8. Keep Vulkan-native material-table behavior explicit.
   - [ ] Use pass-declared material binding layouts and layout hashes for Vulkan
         descriptor-indexed material paths.
   - [ ] Keep material table row uploads dirty-range based; editing one material
         must not rewrite unrelated rows.
   - [ ] Report active texture binding rung, descriptor-indexing availability,
         material-table layout hash, and fallback reason in renderer diagnostics.
   - [ ] Keep OpenGL bindless handle tables and Vulkan descriptor-indexed
         texture arrays on the same logical material/texture index contract.

9. Match finalization diagnostics.
   - [x] Add Vulkan equivalent of fallback sampler binding diagnostics.
   - [ ] Warn when a material program has no parameter or sampler bindings after
         descriptor resolution.
   - [ ] Add rate-limited texture-risk diagnostics equivalent to OpenGL's
         runtime material texture checks.

## Vulkan-Native Acceptance Additions

- [ ] Make the material binding layout a first-class Vulkan artifact derived
      from pass intent, shader reflection, material parameters, texture slots,
      engine uniforms, shadow binding source, descriptor-indexing availability,
      and material-table policy.
- [ ] Have `VkMaterial` populate a prepared binding plan instead of rediscovering
      descriptor rules during draw recording. The plan should include descriptor
      set layout, descriptor writes, uniform/storage block offsets, push
      constants, texture array indices, fallback resources, and material row
      layout hash.
- [ ] Treat descriptor layout signature, material row layout hash, texture
      binding rung, render options, shader artifact identity, and shadow source
      material as pipeline/material readiness inputs.
- [ ] Validate std140/std430/scalar layout offsets from reflection data before
      serializing material parameters into Vulkan uniform/storage buffers.
- [ ] Require `nonuniformEXT` or an equivalent validated shader variant whenever
      material or draw data indexes descriptor arrays.
- [ ] Keep material table updates dirty-range based and report the exact rows,
      byte ranges, texture indices, and descriptor writes touched by a material
      edit.
- [ ] Make placeholder texture use visible by role and reason: missing asset,
      not resident, unsupported format, descriptor allocation failure,
      incompatible sampler/view, or warmup not complete.

## OpenGL Backfill Additions

- [ ] Give OpenGL bindless and material-table paths the same pass-declared
      material layout hash used by Vulkan descriptor-indexed material paths.
- [ ] Treat classic OpenGL sampler binding as one texture binding rung under the
      shared material contract, not as the conceptual source of material
      texture identity.
- [ ] Update OpenGL material tables with dirty rows and dirty byte ranges so
      editing one material does not rewrite unrelated rows.
- [ ] Report OpenGL bindless handle table state, residency, fallback role,
      material row index, texture logical index, and binding rung in profiler
      counters comparable to Vulkan descriptor diagnostics.
- [ ] Keep OpenGL shader uniform/block packing diagnostics comparable to Vulkan
      reflection-based material parameter serialization diagnostics.

## Validation

- [x] Source test: sampler-name texture binding wins over numeric texture slot.
- [x] Source test: `TextureN` aliases resolve consistently for OpenGL and
      Vulkan.
- [x] Source test: shadow binding source material supplies parameters and
      textures for a shadow program.
- [x] Source test: unsupported material parameter types produce diagnostics,
      not silent zeros.
- [ ] Hardware: compare ordinary, shadow, depth-normal, FBO/post, and
      bindless-material paths against OpenGL.

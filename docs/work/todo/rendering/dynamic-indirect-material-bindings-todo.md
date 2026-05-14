# Dynamic Indirect Material Bindings TODO

Last Updated: 2026-05-14
Owner: Rendering
Status: Opaque-deferred dynamic layout/generator/packer/resolver slice
implemented; runtime smoke, forward+ promotion, Vulkan runtime validation, and
inspector UI remain.
Target Branch: `rendering-dynamic-indirect-material-bindings`

Design source:

- [Dynamic Indirect Material Bindings](../../design/rendering/dynamic-indirect-material-bindings.md)
- [Material Binding Policy](../../../architecture/rendering/material-binding-policy.md)
- [Bindless Deferred Texturing Plan](../../design/texturing/bindless-deferred-texturing-plan.md)

## Goal

Replace the current hardcoded zero-readback material-table path with a
layout-driven material binding system. Render passes declare the material row
they need, shaders opt in with explicit annotations or known engine metadata,
and compatible materials render through generated indirect variants. Shaders
that cannot be classified stay on the per-material tier path.

This TODO is the execution tracker. The design doc above remains the rationale
and architecture source.

## Current Problem

The OpenGL material-table path has a fixed opaque-deferred row in C# and two
parallel GLSL definitions for `MaterialEntry`:

- `HybridRenderingManager.CreateMaterialTableFragmentShader` inlines the
  active 12-uint / 48-byte row.
- `Build/CommonAssets/Shaders/Common/MaterialTable.glsl` still declares a
  stale 4-uint row at SSBO binding 11.
- `Build/CommonAssets/Shaders/Graphics/BindlessMesh.frag` consumes the stale
  header and can therefore read the wrong row shape.

The fix is not another hand-edited row. The row layout, GLSL struct, shader
aliases, and C# packer should come from one pass-declared layout.

## Current Validation

Validated on 2026-05-14:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
- `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~MaterialBinding|FullyQualifiedName~GPUMaterialTable|FullyQualifiedName~ShaderUiManifest|FullyQualifiedName~GpuIndirectPhaseDMaterialBindlessTests" --no-restore /p:UseSharedCompilation=false`
- Re-ran runtime and unit-test builds after layout validation, pass metadata,
  layout-driven row packing, resolver diagnostics, and flat material-id
  forwarding were added.
- Focused rendering/material binding test filter passed 26 tests.

Known validation still needed: editor scene smoke with `MaterialTable` and
`BindlessMaterialTable`, RenderDoc/visual parity against the previous opaque
deferred material-table path, forward+ material-table smoke, and Vulkan
descriptor-indexing runtime smoke.

## Scope

- `XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection*.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderUiManifest.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/UberShaderVariantBuilder.cs`
- `Build/CommonAssets/Shaders/Common/MaterialTable.glsl`
- `Build/CommonAssets/Shaders/Graphics/BindlessMesh.frag`
- `Build/CommonAssets/Shaders/Uber/uniforms.glsl`
- `docs/architecture/rendering/material-binding-policy.md`

## Non-Goals

- Do not infer arbitrary unannotated user uniforms into material rows.
- Do not require every shader to support material-table rendering.
- Do not use texture arrays as the generic fallback for material diversity.
- Do not mutate shader source at draw time.
- Do not add per-frame shader parsing, layout synthesis, LINQ, closure, or
  heap allocation to render submission hot paths.

## Phase 0 - Branch, Baseline, And Audit

- [x] Create dedicated branch
  `rendering-dynamic-indirect-material-bindings`.
- [x] Capture a build baseline for runtime rendering and editor:
  `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
  and
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`.
- [x] Inventory every current `MaterialEntry` / `GPUMaterialEntry` consumer.
- [x] Inventory all binding slot assumptions for material rows and texture
  handles. Preserve SSBO binding 11 for material rows and binding 17 for the
  texture handle table unless a broader runtime migration explicitly changes
  them.
- [x] Inventory the current stale-header consumers (`MaterialTable.glsl` and
  `BindlessMesh.frag`) so the Phase 3/6 work has a concrete removal list.

Acceptance criteria:

- [x] Baseline build status is recorded in this TODO.
- [x] Current material-table row consumers and shader include consumers are
  known before implementation begins.

## Phase 1 - Material Binding Layout Model

- [x] Add descriptor types for pass-declared layouts:
  `MaterialBindingLayout`, `MaterialBindingField`, `MaterialTextureBinding`,
  and an output contract model.
- [x] Add a binding scope enum covering `frame`, `camera`, `pass`, `draw`,
  `instance`, `material`, `texture`, and `static`.
- [x] Include deterministic layout hashing over pass id, output contract,
  fields, texture slots, default values, and backend-relevant feature flags.
- [x] Add validation for duplicate fields, incompatible semantic/type pairs,
  invalid defaults, and unsupported texture dimensionality.
- [x] Expose material binding layouts from render pipeline/pass metadata.
- [x] Define the opaque deferred layout equivalent to the current hardcoded
  row: base color/opacity, RMSE, texture handle indices, and flags.

Acceptance criteria:

- [x] Layout descriptors are immutable or treated as immutable after creation.
- [x] Layout hash changes deterministically when any material row field,
  texture binding, output, or backend-relevant option changes.
- [x] Opaque deferred can describe today's `GPUMaterialEntry` shape without a
  hand-authored GLSL struct.

## Phase 2 - Shader Annotation And Manifest Support

- [x] Extend `ShaderUiManifest` / `ShaderUiManifestParser` to parse
  `//@binding(...)` directives.
- [x] Extend `//@property(...)` metadata with optional indirect-binding keys:
  `indirect`, `semantic`, and backend-safe defaults.
- [x] Record binding scope, storage kind, semantic, default literal, and source
  line in manifest data.
- [x] Validate annotation-to-uniform name matches and type compatibility.
- [x] Treat missing scope on user-authored material uniforms as a compatibility
  warning, not as implicit material-table conversion.
- [x] Add parser tests for material fields, texture samplers, static
  properties, camera/pass uniforms, bad annotations, and default literals.

Acceptance criteria:

- [x] Engine-owned annotated shaders produce structured binding metadata.
- [x] Unannotated arbitrary material uniforms force a fallback-compatible
  diagnostic instead of a generated material-table variant.

## Phase 3 - Generated GLSL Material Records

- [x] Add a generator that emits `XR_MaterialRecord`,
  `XR_MaterialTableBuffer`, `XR_LoadMaterial(...)`, and value aliases from a
  `MaterialBindingLayout`.
- [x] Generate std430-safe packing using `vec4` / `uvec4` lanes for fields
  that can share lanes.
- [x] Lift the current bindless sampling helper into a shared `XR_`-prefixed
  helper API that generated variants can target.
- [x] Replace the inlined material-table fragment shader row in
  `HybridRenderingManager.CreateMaterialTableFragmentShader` with generated
  output.
- [x] Replace the stale `Build/CommonAssets/Shaders/Common/MaterialTable.glsl`
  definition with generated or generated-compatible shared source.
- [x] Keep binding 11 for the material row table and binding 17 for texture
  handle lookup in generated OpenGL variants.
- [x] Optionally emit `flat in uint XR_FragMaterialId` from the generated
  vertex stage when the fragment shader needs the material id directly, and
  fall back to the existing `floatBitsToUint(FragTransformId)` ->
  `Draws[drawID].MaterialID` lookup against the `DrawMetadata` SSBO when it
  does not.
- [ ] Cache generated shader source by source hash, pass layout hash, backend
  feature mask, and static property hash.

Acceptance criteria:

- [x] There is one source of truth for material row layout.
- [x] `BindlessMesh.frag` cannot include a row shape that disagrees with the
  C# material packer.
- [x] Generated code uses `XR_` helper names and avoids colliding with user
  shader symbols.

## Phase 4 - Layout-Driven Material Packer

- [x] Add a generated or cached material row packer from the same layout used
  by shader generation.
- [x] Share lane-packing rules with the existing `GPUMaterialEntryWords`
  `PackMaterialEntry` path until the hardcoded row can be deleted.
- [~] Pack scalar/vector/matrix values, flags, texture indices, and defaults
  into std430-compatible rows.
- [x] Keep dirty-row upload semantics. Editing one material value should not
  rewrite the full table.
- [x] Keep texture handle table uploads separate from material rows.
- [x] Add tests for row offsets, default material values, dirty-row updates,
  texture handle index packing, and resize behavior.

Acceptance criteria:

- [x] C# row bytes match generated GLSL offsets for the opaque deferred layout.
- [ ] Dirty material updates remain allocation-free in the per-frame path.
- [x] Missing optional material values resolve through layout defaults.

## Phase 5 - Resolver And Draw-Path Integration

- [x] Add a resolver that combines pass layout, shader manifest/known engine
  metadata, material state, and backend features.
- [x] Model resolver outcomes as `MaterialTableCompatible`,
  `PerMaterialRequired`, and `Invalid`.
- [x] Route compatible shaders to generated material-table indirect variants.
- [~] Route unclassified uniforms, unsupported sampler usage, and incompatible
  output contracts to per-material tier rendering.
- [~] Surface resolver diagnostics in render diagnostics and material/shader
  inspector surfaces.
- [ ] Cache resolver results outside the hot draw loop.
- [x] Preserve `EZeroReadbackMaterialDrawPath.ActiveBucketList`,
  `MaterialTable`, and `BindlessMaterialTable` behavior while replacing the
  hardcoded material-table shader and row.
- [x] Do not weaken the existing `C-GPU-2` zero-readback invariants enforced
  by `AssertZeroReadbackProductionInvariants` and
  `AssertZeroReadbackUsesGpuCountPath` in `HybridRenderingManager` while
  routing through generated variants.

Acceptance criteria:

- [x] Unknown material uniforms never silently render with missing table data.
- [~] Draw submission does not parse shaders, synthesize layouts, allocate, or
  generate variants per frame.
- [x] Per-material tier rendering remains available for arbitrary shaders.

## Phase 6 - Opaque Deferred Conversion

- [x] Convert the standard opaque deferred material path to the dynamic opaque
  deferred layout.
- [~] Remove the current hardcoded `GPUMaterialEntry` row dependency from the
  material-table shader path once the generated row is active.
- [x] Preserve the existing `MaterialTable` and `BindlessMaterialTable`
  settings and diagnostics.
- [ ] Validate base color, opacity, roughness, metallic, specular, emission,
  albedo, normal, metallic/roughness texture, and missing-texture fallback
  cases.
- [x] Add focused tests that adding a new annotated opaque deferred field
  changes the layout hash, generated shader source, and packer layout.

Acceptance criteria:

- [ ] Standard opaque deferred materials render correctly through
  `MaterialTable` and `BindlessMaterialTable`.
- [x] The stale 4-uint row is gone or impossible to use.
- [ ] Opaque deferred material-table output matches the previous hardcoded path
  for equivalent materials.

## Phase 7 - Forward+ Layout Support

- [~] Declare a forward+ material binding layout with its own outputs, light
  list bindings, and material fields.
- [x] Generalize `RenderZeroReadbackMaterialTableBuckets` so the material-table
  path is not hardcoded to `EDefaultRenderPass.OpaqueDeferred`.
- [x] Keep forward+ shaders on the per-material tier path until their material
  and pass inputs are classified.
- [ ] Validate forward+ material table rendering with at least one standard
  lit material and one texture-diverse scene.

Acceptance criteria:

- [ ] Forward+ material-table rendering is possible through a pass-declared
  layout.
- [x] Deferred-only assumptions are removed from the material-table dispatch
  path or guarded by explicit compatibility checks.

## Phase 8 - Uber And Custom Shader Support

- [~] Map existing Uber `//@property(...)` metadata into material fields,
  texture bindings, and static variants.
- [x] Add indirect-binding metadata to representative Uber properties in
  `Build/CommonAssets/Shaders/Uber/uniforms.glsl`.
- [x] Introduce a stable texture helper such as `XR_TEXTURE2D(...)` that can
  lower to regular samplers, bindless handles, or descriptor-indexed textures.
- [x] Defer arbitrary `texture(Texture0, uv)` rewriting until a real GLSL
  transform exists.
- [x] Keep hand-authored custom shaders on per-material tier rendering unless
  they opt in through `//@binding(...)` or approved helper macros.

Acceptance criteria:

- [ ] Uber variants can choose material-table compatibility from authored
  state and annotations.
- [x] Custom shaders remain correct by default.
- [x] No ad hoc draw-time string replacement is required for sampler access.

## Phase 9 - Backend Mapping

- [x] Preserve the OpenGL SSBO row-table path.
- [x] Preserve the OpenGL bindless texture handle table path.
- [ ] Define the non-bindless OpenGL fallback: value-only rows plus
  per-material texture binding, with explicit limits on texture diversity.
- [ ] Map Vulkan material rows to storage buffers.
- [ ] Map Vulkan textures to descriptor indexing with `nonuniformEXT` where
  required.
- [ ] Include material row and descriptor-indexed texture bindings in Vulkan
  pipeline layout validation.

Acceptance criteria:

- [~] Backend-specific code consumes the same logical material layout.
- [~] Unsupported backend features produce resolver fallback or validation
  diagnostics instead of incorrect rendering.

## Phase 10 - Diagnostics, Tests, And Docs

- [x] Add resolver and packer unit tests under `XREngine.UnitTests`.
- [x] Add shader manifest parser tests for new binding metadata.
- [ ] Add runtime rendering smoke coverage for opaque deferred material-table
  and bindless material-table paths.
- [~] Add diagnostics showing layout hash, resolver outcome, fallback reason,
  and generated variant cache key.
- [x] Update
  [material-binding-policy.md](../../../architecture/rendering/material-binding-policy.md)
  with the dynamic layout policy and fallback rules.
- [x] Cross-reference the dynamic layout work from the D1 / W5 row of
  [production-rendering-pipeline-roadmap.md](gpu/production-rendering-pipeline-roadmap.md)
  so the bindless/material-table follow-up status does not drift.
- [x] Update the design doc with implementation status when major phases land.
- [x] Run targeted validation:
  `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
  and
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`.
- [x] Run targeted unit tests once added:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~MaterialBinding|FullyQualifiedName~GPUMaterialTable|FullyQualifiedName~ShaderUiManifest" --no-restore /p:UseSharedCompilation=false`.

Acceptance criteria:

- [ ] Standard opaque deferred materials render correctly through
  `MaterialTable` and `BindlessMaterialTable` without hardcoded
  `GPUMaterialEntry` fields.
- [x] Adding a new annotated material field changes the layout hash, generated
  shader, and packer deterministically.
- [x] Unknown material uniforms force `PerMaterialRequired`.
- [ ] Forward+ material-table rendering is possible with a pass-declared
  forward+ layout.
- [~] Uber shader variants can opt into material-table compatibility from
  authored annotations.
- [~] No shader parsing, layout synthesis, or allocation occurs in the
  per-frame hot draw path.
- [x] Documentation describes compatibility, fallback, and backend behavior.

## Risk And Rollback

- Keep per-material tier rendering as the correctness fallback throughout the
  migration.
- Keep the current hardcoded opaque deferred row behind a temporary guard only
  until generated opaque deferred parity is validated.
- If generated variants regress rendering, route affected shader/pass pairs to
  `PerMaterialRequired` while preserving the rest of the material-table path.
- Do not promote forward+ or Uber material-table paths until opaque deferred
  parity is validated.

## Final Task

- [ ] Merge `rendering-dynamic-indirect-material-bindings` back into `main`
  after implementation, validation, and documentation updates are complete.

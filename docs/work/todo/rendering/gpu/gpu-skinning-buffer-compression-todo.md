# GPU Skinning Buffer Compression TODO

Last Updated: 2026-05-06
Current Status: design captured, implementation not started
Scope: implement the architecture defined in
[GPU Skinning Buffer Compression Plan](../../../design/rendering/gpu/gpu-skinning-buffer-compression-plan.md).

## Goal

Replace the current mesh-wide fixed-4 vs variable skinning layout split with a
single compact `Core4 + Spill` influence format, then replace renderer-owned
dual-`mat4` bone palettes with precomposed affine skin palettes.

The intended result is lower mesh influence bandwidth, lower palette upload
bandwidth, one logical shader decode contract across compute and direct vertex
skinning, and a cleaner v1 API surface for GPU-driven animation sources.

This is a pre-v1 rendering cleanup. Cooked mesh payloads, internal buffer names,
shader contracts, and rendering settings may break when the result is simpler
and easier to validate.

## Current Reality

What exists now:

- `XRMesh` chooses a mesh-wide skinning influence layout from
  `OptimizeSkinningTo4Weights`, `OptimizeSkinningWeightsIfPossible`, and
  `MaxWeightCount`.
- The fixed-4 path stores four 32-bit indices plus four 32-bit weights per
  vertex, or 32 bytes per vertex.
- The variable path stores one 32-bit offset and one 32-bit count per vertex,
  plus 32-bit index and 32-bit weight entries per influence.
- One vertex with more than four influences can force the whole mesh onto the
  variable path.
- `XRMeshRenderer` publishes `BoneMatricesBuffer` and
  `BoneInvBindMatricesBuffer`, both as full `mat4` streams.
- Shaders compose `BoneInvBindMatrices[idx] * BoneMatrices[idx]` per influence
  instead of consuming a precomposed final skin matrix.
- Compute and direct vertex skinning both carry old fixed-vs-variable decode
  branches.
- Cooked mesh payloads persist the legacy skinning buffer fields and
  `MaxWeightCount`.

What does not exist yet:

- compact `Core4x8` and `Core4x16` influence packing,
- spill header and spill entry buffers,
- deterministic quantized packing tests,
- one shared shader decode path for compute and direct skinning,
- cooked payload versioning for the compressed skinning layout,
- final affine skin palette buffers,
- global final-skin palette packing,
- cleanup of the legacy skinning settings and buffer names.

## Target Outcome

At the end of this work:

- every skinned vertex stores four compact core influence lanes plus an
  overflow header,
- vertices with more than four non-zero influences append only the extra tail
  entries to a compact spill list,
- meshes choose only core index width, `Core4x8` or `Core4x16`, for the common
  compressed path,
- weights are packed as normalized 8-bit values and sum to exactly 255 per
  vertex after packing,
- compute and direct vertex skinning use one logical decode contract,
- renderer palettes expose current final skin matrices as affine `vec4[3]`
  records,
- previous-frame skin palettes are present only when temporal consumers need
  them,
- `GlobalAnimationInputBuffers` is replaced by or renamed to
  `GlobalSkinPaletteBuffers`,
- legacy cooked skinning payloads are rejected and recooked instead of silently
  translated,
- obsolete fixed-vs-variable settings and float-backed integer skinning decode
  are removed.

## Non-Goals

- Do not redesign blendshape buffer layouts as part of this work.
- Do not compress skinned output buffers such as positions, normals, or
  tangents.
- Do not solve skinned bounds readback in this implementation series.
- Do not add support for more than 65535 utilized bones in the compressed path;
  use an explicit fallback for rare oversized skeletons.
- Do not keep long-lived legacy cooked mesh compatibility.
- Do not introduce per-frame heap allocations in skinning dispatch, visible
  collection, render submission, or shader binding hot paths.

---

## Phase 0 - Branch, Baseline, And Guard Rails

Outcome: the implementation branch has measurable baselines, deterministic
packing fixtures, and reviewable contracts before the runtime format changes.

### 0.1 Branch And Baseline Capture

- [ ] Create a dedicated branch for this todo list and implementation series.
- [ ] Capture current mesh influence bytes per vertex for representative fixed
  and variable meshes.
- [ ] Capture current palette bytes per bone for CPU-driven renderers and
  global animation input buffers.
- [ ] Capture current compute skinning dispatch timing on a representative
  skinned scene.
- [ ] Capture current direct vertex skinning frame timing on a representative
  skinned scene.
- [ ] Identify or create representative content buckets:
  - rigid-ish meshes with 0 to 2 influences,
  - typical character meshes with up to 4 influences,
  - dense facial, cloth-like, or test meshes with 5 or more influences,
  - GPU-driven bone source cases such as physics-chain-driven skinning.
- [ ] Add instrumentation that reports current and compressed skinning bytes per
  vertex without requiring a GPU debugger.
- [ ] Add instrumentation that reports current and compressed palette bytes per
  bone.
- [ ] Document the baseline numbers in the PR notes or a benchmark artifact.

Acceptance criteria:

- [ ] Reviewers can compare old and new memory costs per mesh and per palette.
- [ ] Performance and memory baselines are captured before Phase 1 changes land.
- [ ] The dedicated branch exists before implementation work begins.

### 0.2 Deterministic Packing Contract

- [ ] Define a canonical logical influence list for tests and format migration.
- [ ] Sort influences by descending weight with ascending bone-index tiebreaks.
- [ ] Drop zero and negative weights before packing.
- [ ] Preserve the existing `0` no-bone sentinel and `boneIndex + 1` indexing
  contract.
- [ ] Define the quantization rule for `UNorm8` weights.
- [ ] Normalize packed weights so the sum is exactly 255 for every non-empty
  vertex.
- [ ] Absorb rounding residue into the largest retained influence.
- [ ] Drop tail influences that round below `1 / 255` instead of emitting
  zero-weight spill entries.
- [ ] Define zero-influence vertices as all-zero core indices, all-zero core
  weights, and a zero spill header.
- [ ] Record little-endian cooked payload and shader bit-order expectations.

Acceptance criteria:

- [ ] Packing rules are deterministic from source `Vertices[i].Weights`.
- [ ] Tests can decode a packed vertex without depending on legacy buffer names.
- [ ] Zero-influence and tiny-tail behavior is specified before shader work
  starts.

### 0.3 Test Fixtures And Baseline Tests

- [ ] Add unit fixtures for 0, 1, 2, 3, 4, 5, and 8 influence vertices.
- [ ] Add fixtures for equal-weight ties to prove bone-index tiebreak behavior.
- [ ] Add fixtures for `Core4x8` boundary behavior at 255 utilized bones.
- [ ] Add fixtures for `Core4x16` boundary behavior above 255 utilized bones.
- [ ] Add a very-large-skeleton fixture that proves the compressed path refuses
  or falls back when utilized bones exceed 65535.
- [ ] Add CPU decode tests that compare logical weights against packed decode
  results.
- [ ] Add tests for sentinel lanes and zero-influence vertices.
- [ ] Add a transition test where a fifth influence rounds below `1 / 255` and
  produces no spill entry.

Acceptance criteria:

- [ ] Packing and decode tests exist before GPU shader migration begins.
- [ ] Boundary cases are covered for both core index widths.
- [ ] Oversized skeleton behavior is explicit and test-covered.

### 0.4 Backend Capability Audit

- [ ] Confirm `XRDataBuffer` can represent compact integer vertex attributes and
  raw `uint` SSBO views needed by the new format.
- [ ] Confirm `EComponentType` and buffer format plumbing can express
  `R8G8B8A8_UINT`, `R16G16B16A16_UINT`, and `R8G8B8A8_UNORM` equivalents.
- [ ] Confirm OpenGL uses integer core-index attributes through
  `glVertexAttribIPointer`.
- [ ] Confirm OpenGL uses normalized `UNorm8` core-weight attributes through
  the existing attribute path or a small extension to it.
- [ ] Confirm Vulkan binding descriptors can expose the same logical buffers as
  vertex attributes and SSBOs.
- [ ] Decide whether migration needs temporary duplicate GPU views for buffers
  consumed as both attributes and SSBOs.
- [ ] Document any temporary backend binding limitations before Phase 1.

Acceptance criteria:

- [ ] Backend binding requirements are known before buffer layout changes land.
- [ ] Any temporary duplicate-view strategy is explicit and removable.

---

## Phase 1 - Influence And Shader Refactor

Outcome: `XRMesh`, cooked payloads, compute shaders, and direct vertex shaders
consume one compressed `Core4 + Spill` influence representation, with legacy
layout retained only as a temporary fallback.

This phase intentionally keeps compute and direct vertex migration together so
the engine does not enter a state where the two paths disagree on the mesh
influence format.

### 1.1 Runtime Metadata And API Surface

- [ ] Add `SkinningInfluenceEncoding` metadata to `XRMesh`.
- [ ] Add `SkinningCoreIndexFormat` metadata for `Core4x8` and `Core4x16`.
- [ ] Add `HasSpillInfluences` metadata.
- [ ] Add `MaxSpillInfluenceCount` metadata.
- [ ] Retain `MaxWeightCount` only as descriptive inspection, telemetry, and
  import diagnostic data.
- [ ] Ensure no new runtime branch chooses layout, shader variant, or upload
  format from `MaxWeightCount`.
- [ ] Add a temporary `SkinningInfluenceEncoding.Legacy` fallback value.
- [ ] Gate the temporary legacy fallback behind one engine setting for rollback
  through Phase 3 only.
- [ ] Add assertions or review-time tests that prevent new compressed-path code
  from consulting the old fixed-vs-variable switches.

Acceptance criteria:

- [ ] Runtime layout selection is based on explicit compressed metadata, not
  `MaxWeightCount`.
- [ ] Legacy fallback is available but clearly temporary.

### 1.2 Core4 + Spill Mesh Packing

- [ ] Implement `Core4x8` packing when `UtilizedBones.Length <= 255`.
- [ ] Implement `Core4x16` packing when `UtilizedBones.Length <= 65535`.
- [ ] Implement explicit fallback or refusal when utilized bones exceed 65535.
- [ ] Pack four core indices as `u8` or `u16` lanes with `0` reserved as the
  sentinel.
- [ ] Pack four core weights as `UNorm8`.
- [ ] Pack one 32-bit spill header per vertex.
- [ ] Encode spill header bits 0..23 as spill entry offset.
- [ ] Encode spill header bits 24..31 as extra influence count.
- [ ] Pack spill entries as one `uint` each:
  - bits 0..15: `boneIndexPlusOne`,
  - bits 16..23: `weightUNorm8`,
  - bits 24..31: reserved and zero.
- [ ] Keep the universal spill-header allocation for the first implementation.
- [ ] Ensure all runtime packing and rebuild paths are allocation-free outside
  import, cook, or explicit mesh rebuild work.
- [ ] Add telemetry for chosen encoding, spill entry count, and max spill count.

Acceptance criteria:

- [ ] Dominant 0-to-4 influence vertices do not allocate spill entries.
- [ ] Vertices with more than four retained influences pay only for the extra
  spill entries.
- [ ] Packed data decodes to normalized logical weights within documented
  tolerance.

### 1.3 Mesh Buffer Ownership And Upload

- [ ] Introduce logical mesh influence buffers:
  `BoneInfluenceCoreIndices`, `BoneInfluenceCoreWeights`,
  `BoneInfluenceSpillHeaders`, and `BoneInfluenceSpillEntries`.
- [ ] During Phase 1, keep the existing `ECommonBufferType` names where needed
  to avoid dragging a repository-wide rename into the first format change.
- [ ] Map legacy `BoneWeightOffsets` and `BoneWeightCounts` meanings to the new
  core-index and core-weight buffers only inside the temporary migration layer.
- [ ] Add spill header and spill entry buffer creation in
  `XRMesh.Skinning.cs`.
- [ ] Update cached references in `XRMesh.Core.cs`.
- [ ] Ensure mesh rebuilds from source weights produce stable byte-identical
  buffers for stable source data.
- [ ] Add debug inspection for core index format and spill counts.

Acceptance criteria:

- [ ] Runtime upload creates all buffers needed by compute and direct skinning.
- [ ] Temporary legacy naming is isolated and scheduled for Phase 3 removal.
- [ ] Rebuilding the same mesh twice produces identical compressed payloads.

### 1.4 Cooked Payload Versioning

- [ ] Bump the `XRMesh.CookedBinary` payload version for the compressed
  skinning layout.
- [ ] Reject old cooked payloads with a version mismatch and force reimport from
  source assets.
- [ ] Store `SkinningInfluenceEncoding` metadata.
- [ ] Store `SkinningCoreIndexFormat` metadata.
- [ ] Store `HasSpillInfluences` and `MaxSpillInfluenceCount`.
- [ ] Store core index, core weight, spill header, and spill entry buffer plans.
- [ ] Preserve bone utilization metadata.
- [ ] Remove silent cooked-to-cooked translation from the initial delivery path.
- [ ] Add loader tests for version mismatch and recook/refusal behavior.

Acceptance criteria:

- [ ] Legacy cooked skinning payloads do not load as if they were compressed
  payloads.
- [ ] New cooked payloads fully describe the compressed influence format.

### 1.5 Compute Skinning Shader Contract

- [ ] Update `SkinningPrepass.comp` to decode packed core lanes.
- [ ] Update `SkinningPrepassInterleaved.comp` to decode packed core lanes.
- [ ] Add shared GLSL helpers for core index unpacking, `UNorm8` weight decode,
  spill header decode, and spill entry decode.
- [ ] Decode four core lanes unconditionally.
- [ ] Decode spill entries only when `extraInfluenceCount > 0`.
- [ ] Remove old compute branches for fixed-4 vs variable mesh-wide storage
  from the compressed path.
- [ ] Remove skinning-specific dependence on float-backed integer metadata in
  the compressed path.
- [ ] Preserve `boneMatrixBase` or its replacement base-offset behavior.
- [ ] Add shader contract tests that prove the old mesh-wide storage branches
  are not generated or selected for compressed meshes.

Acceptance criteria:

- [ ] Compute skinning consumes one compressed logical influence contract.
- [ ] Float-backed integer skinning decode is gone from the new path.
- [ ] Spill decode is skipped for core-only vertices.

### 1.6 Direct Vertex Shader Contract

- [ ] Update `DefaultVertexShaderGenerator` to declare compact core-index
  attributes.
- [ ] Update `DefaultVertexShaderGenerator` to declare normalized compact
  core-weight attributes.
- [ ] Bind spill header and spill entry SSBOs for overflow-capable meshes.
- [ ] Generate one shared direct-skinning decode sequence for compressed meshes.
- [ ] Preserve direct vertex skinning behavior for core-only meshes.
- [ ] Preserve base palette offset behavior.
- [ ] Add source-generation tests that assert compact attributes and spill SSBO
  bindings are emitted when required.
- [ ] Add tests or shader snapshots proving direct and compute decode use the
  same bit layout.

Acceptance criteria:

- [ ] Direct vertex skinning no longer depends on mesh-wide fixed-vs-variable
  input declarations for compressed meshes.
- [ ] Overflow-capable meshes bind spill buffers consistently across backends.

### 1.7 Phase 1 Validation

- [ ] Run packing and decode unit tests.
- [ ] Run shader generation or shader contract tests for direct skinning.
- [ ] Run targeted compute skinning tests for compressed meshes.
- [ ] Compare CPU logical skinning output against compressed compute output.
- [ ] Compare CPU logical skinning output against compressed direct vertex
  output where a headless-safe path exists.
- [ ] Capture visual diffs for at least one core-only character and one spill
  character.
- [ ] Measure mesh skinning bytes per vertex against Phase 0 baselines.
- [ ] Measure compute skinning dispatch time against Phase 0 baselines.
- [ ] Measure direct vertex path frame time against Phase 0 baselines.

Acceptance criteria:

- [ ] Mesh skinning bytes per vertex are strictly lower than baseline for every
  measured asset.
- [ ] Compute skinning dispatch time is within +/- 5% of baseline at equivalent
  vertex count, unless the PR explicitly waives and explains the regression.
- [ ] Direct vertex path frame time is within +/- 5% of baseline on the skinned
  regression scene, unless the PR explicitly waives and explains the
  regression.
- [ ] Golden-image diffs meet the documented threshold or have explicit owner
  waiver.

---

## Phase 2 - Palette Compression

Outcome: renderers and GPU animation producers publish final affine skin
palettes instead of paired world and inverse-bind `mat4` streams.

### 2.1 Renderer Palette Abstraction

- [ ] Add `ActiveSkinPaletteBuffer` to the renderer-side skinning surface.
- [ ] Add optional `ActivePreviousSkinPaletteBuffer`.
- [ ] Add `ActiveSkinPaletteBase`.
- [ ] Add `ActiveSkinPaletteCount`.
- [ ] Define the affine skin matrix record as three independent `vec4` rows.
- [ ] Store `(linear.row_i.xyz, translation_i)` in each row.
- [ ] Preserve the identity sentinel at palette index 0.
- [ ] Compose CPU-driven final skin matrices once per dirty bone before upload.
- [ ] Keep previous-frame palette publication explicit for temporal consumers.
- [ ] Add renderer diagnostics for palette source, palette count, and current
  vs previous availability.

Acceptance criteria:

- [ ] CPU-driven renderers can publish final affine skin matrices without
  exposing inverse-bind buffers as the hot-path contract.
- [ ] Temporal paths can request previous-frame data without forcing every path
  to carry it.

### 2.2 Shader Palette Consumption

- [ ] Update compute skinning shaders to consume final affine skin palette
  records.
- [ ] Update generated direct vertex skinning to consume final affine skin
  palette records.
- [ ] Replace per-influence `inverseBind * world` matrix composition with one
  final palette fetch per influence.
- [ ] Add helper functions for transforming position, normal, and tangent from
  the affine row representation.
- [ ] Preserve base palette offset behavior across CPU and GPU palette sources.
- [ ] Validate normal and tangent behavior under non-uniform scale.
- [ ] Add shader contract tests for the affine `vec4[3]` palette layout.

Acceptance criteria:

- [ ] Shaders no longer need paired current world and inverse-bind palette
  streams for the compressed path.
- [ ] Position, normal, and tangent output matches the old path within
  documented tolerance.

### 2.3 Global Palette Packing

- [ ] Refactor global animation input packing toward one final skin palette
  stream.
- [ ] Add optional previous-frame final skin palette stream only for temporal
  consumers.
- [ ] Preserve palette base semantics for packed global buffers.
- [ ] Support GPU-produced palette sources writing final affine records
  directly.
- [ ] Keep global packing allocation-free in per-frame paths.
- [ ] Measure global palette copy volume before and after the refactor.
- [ ] Defer the public type rename to Phase 3 unless the implementation becomes
  clearer with an immediate replacement type.

Acceptance criteria:

- [ ] Global palette copy volume per frame is strictly lower than baseline.
- [ ] GPU-produced animation sources fit the same final palette abstraction as
  CPU-driven bones.

### 2.4 Phase 2 Validation

- [ ] Run unit tests for affine palette composition.
- [ ] Run shader contract tests for the final skin palette layout.
- [ ] Compare CPU-composed final palettes against legacy `inverseBind * world`
  results.
- [ ] Validate representative CPU-driven skinned meshes.
- [ ] Validate at least one GPU-driven bone source path.
- [ ] Capture visual diffs for posed characters at fixed animation keyframes.
- [ ] Measure palette bytes per bone against Phase 0 baselines.
- [ ] Measure global palette copy volume against Phase 0 baselines.
- [ ] Measure compute and direct skinning timing against Phase 0 baselines.

Acceptance criteria:

- [ ] Palette bytes per bone are strictly lower than baseline.
- [ ] Global palette copy volume is strictly lower than baseline.
- [ ] Compute and direct skinning timings remain within +/- 5% of baseline, or
  the PR includes explicit measured waiver notes.
- [ ] Visual diff thresholds pass for the representative content buckets.

---

## Phase 3 - Cleanup And Removal

Outcome: temporary migration aliases, legacy layout paths, and obsolete settings
are removed after compressed influence and palette validation succeeds.

### 3.1 Rename Mesh Influence Buffers

- [ ] Rename `BoneMatrixOffset` or equivalent legacy buffer identifiers to
  `BoneInfluenceCoreIndices`.
- [ ] Rename `BoneMatrixCount` or equivalent legacy buffer identifiers to
  `BoneInfluenceCoreWeights`.
- [ ] Add `BoneInfluenceSpillHeaders` as a first-class common buffer type.
- [ ] Add `BoneInfluenceSpillEntries` as a first-class common buffer type.
- [ ] Remove temporary `ECommonBufferType` aliases.
- [ ] Update OpenGL bindings for the renamed buffers.
- [ ] Update Vulkan bindings for the renamed buffers.
- [ ] Update compute dispatch bindings for the renamed buffers.
- [ ] Update editor, debug, and tooling references to the new names.

Acceptance criteria:

- [ ] The compressed influence buffers have one clear name everywhere.
- [ ] No legacy offset/count naming remains in active compressed skinning code.

### 3.2 Rename Global Palette Infrastructure

- [ ] Rename or replace `GlobalAnimationInputBuffers` with
  `GlobalSkinPaletteBuffers`.
- [ ] Rename public diagnostics, profiler labels, and logs to describe skin
  palettes rather than raw animation inputs where appropriate.
- [ ] Update shader binding names for final skin palettes.
- [ ] Update documentation references for global palette packing.
- [ ] Remove any temporary pair-of-`mat4` global packing paths from the
  compressed skinning route.

Acceptance criteria:

- [ ] Global palette infrastructure names match the final affine palette
  contract.
- [ ] Logs and diagnostics explain skin palette sources without exposing old
  implementation details.

### 3.3 Remove Legacy Runtime Paths

- [ ] Remove `SkinningInfluenceEncoding.Legacy`.
- [ ] Remove the engine setting that enables the temporary legacy fallback.
- [ ] Remove fixed-4 vs variable mesh-wide decode branches from runtime skinning
  code.
- [ ] Remove legacy `BoneInvBindMatricesBuffer` hot-path usage.
- [ ] Remove float-backed skinning integer decode.
- [ ] Remove or repurpose `OptimizeSkinningTo4Weights`.
- [ ] Remove or repurpose `OptimizeSkinningWeightsIfPossible`.
- [ ] Remove skinning-specific use of `UseIntegerUniformsInShaders`.
- [ ] Delete old cooked payload read paths once recook validation is complete.

Acceptance criteria:

- [ ] The active runtime path has no fixed-4 vs variable mesh-wide storage
  switch.
- [ ] The old rollback path is gone after validation.
- [ ] Obsolete settings no longer imply a user-facing rendering mode.

### 3.4 Documentation And Tooling Cleanup

- [ ] Update rendering architecture docs that describe mesh skinning buffers.
- [ ] Update OpenGL renderer docs for compact skinning attributes and spill
  SSBOs.
- [ ] Update Vulkan renderer docs for the same buffer contract.
- [ ] Update GPU-driven animation docs to reference final skin palettes.
- [ ] Update any editor or debug UI labels that expose skinning buffer names.
- [ ] Regenerate cooked assets or document the exact recook workflow.
- [ ] Update dependency or build docs only if this work changes workflows,
  launch flags, tasks, or generated files.

Acceptance criteria:

- [ ] Docs describe the final v1 contract, not the migration state.
- [ ] Recook or cache invalidation behavior is clear to contributors.

### 3.5 Phase 3 Validation

- [ ] Run the full targeted skinning unit test suite.
- [ ] Run shader contract tests for compute and direct skinning.
- [ ] Run a targeted editor build.
- [ ] Boot the ImGui editor with representative skinned content.
- [ ] Validate the Unit Testing World skinning scenarios.
- [ ] Run visual diffs after all legacy paths are removed.
- [ ] Re-measure memory, copy-volume, and frame-time budgets after cleanup.
- [ ] Confirm no new compiler warnings are introduced.

Acceptance criteria:

- [ ] Removing the legacy path does not change compressed-path output.
- [ ] Targeted builds and tests pass, except for unrelated failures that are
  documented with file/error context.
- [ ] Final measured numbers still satisfy the design regression budgets.

---

## Deferred Or Optional Follow-Ups

These are explicitly not required for the first implementation series.

- [ ] Evaluate sparse spill-header tables only if profiling proves the universal
  32-bit header matters.
- [ ] Evaluate `UNorm16` weights only if real content shows unacceptable
  quantization artifacts.
- [ ] Evaluate derived-fourth-weight packing after the baseline compressed
  format is validated.
- [ ] Evaluate dual-quaternion skinning as a deformation-quality feature, not as
  part of storage compression.
- [ ] Add a direct cooked-to-cooked migration tool only if source-asset recook
  becomes impractical.

## Validation Commands

Add or adjust filters as tests land. Expected useful targets:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Skinning
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~GpuAnimation
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
```

Use the existing `Build-Editor` task and ImGui editor run profiles for runtime
validation after shader or renderer integration changes.

## Code Touchpoints

- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Skinning.cs`: influence
  packing, buffer construction, deterministic rebuilds.
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Core.cs`: cached skinning
  metadata and buffer references.
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.CookedBinary.cs`: cooked
  payload versioning and serialized compressed buffers.
- `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`: active skin palette
  abstraction and CPU-driven final palette upload.
- `XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher.cs`:
  compute binding selection and palette source routing.
- `XREngine.Runtime.Rendering/Rendering/Compute/GlobalAnimationInputBuffers.cs`:
  global final-skin palette packing and eventual rename.
- `XREngine.Runtime.Rendering/Rendering/Shaders/Generator/DefaultVertexShaderGenerator.cs`:
  direct vertex skinning attribute and spill binding generation.
- `Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepass.comp`: compute
  decode and affine palette consumption.
- `Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepassInterleaved.comp`:
  interleaved compute decode and affine palette consumption.
- `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs`: compact attribute and
  SSBO buffer capability plumbing.
- `XREngine.Data/Rendering/Enums/EComponentType.cs`: compact integer and
  normalized component formats.

## Finalization

- [ ] Re-run all Phase 1 through Phase 3 targeted validation.
- [ ] Re-run the narrowest useful full build, usually
  `dotnet build XRENGINE.slnx`, unless unrelated repository errors block it.
- [ ] Capture final memory and performance numbers:
  - mesh influence bytes per vertex,
  - palette bytes per bone,
  - compute skinning dispatch time,
  - direct vertex path frame time,
  - global palette copy volume,
  - shader instruction impact from spill decode.
- [ ] Capture final visual diff results for core-only, spill-heavy, and
  GPU-driven palette scenes.
- [ ] Include validation results, remaining risks, fallback removals, and cooked
  payload migration notes in the PR summary.
- [ ] Merge the dedicated branch back into `main` after completion and
  validation.

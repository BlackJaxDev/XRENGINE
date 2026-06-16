# Uber Shader Variant Builder Remaining TODOs

Last updated: 2026-06-16
Status: Implementation work completed; only operational follow-through remains.

This TODO tracks follow-up optimization and validation work for
[UberShaderVariantBuilder.cs](../../../XREngine.Runtime.Rendering/Resources/Shaders/UberShaderVariantBuilder.cs).
The final implemented architecture is documented in
[Uber Shader Varianting](../../architecture/rendering/uber-shader-varianting.md).

## Implemented Baseline

The builder now has the following landed contracts:

- Dependency-aware canonical source resolution is cached by source identity and
  resolver options, and concurrent misses share one resolve.
- Vertex permutation hashes are memoized by shader identity plus validated
  source/dependency stamp.
- Manifest-derived feature, property, sampler, and macro data is cached next to
  `ShaderUiManifest`.
- `VariantHash`, `SourceVersion`, stable source/path hashes, and vertex
  permutation hashes use `System.IO.Hashing.XxHash64`.
- Generated variant cache keys include `VariantHash`, `SourceVersion`,
  normalized source path, and precomputed source path hash.
- Material axes are built with explicit loops and sorted arrays instead of
  per-call LINQ chains.
- Generated source emits only residual feature/pipeline defines that survived
  known-conditional pruning.
- Static property values are inlined as token-aware GLSL literals after static
  uniform declarations are removed.
- Generated `XRShader` instances carry structured generated-variant metadata,
  a bounded-header legacy recovery marker, and a parseable `variant-hash`
  comment for backend telemetry.
- Static uniform stripping preserves original newline style, and final source
  cleanup collapses blank-line runs.
- Recognized `#define` stripping and pipeline macro detection use single scans
  instead of one regex pass per macro.
- Stale generated-source eviction is driven by observed source-version changes
  instead of per-prepare filesystem probes.
- The generated define list is pre-sized before emission.

The broader shader-source optimization boundary remains tracked in
[Resolved Shader Source Optimization Remaining Todos](rendering/resolved-shader-source-optimization-todo.md).

## Completed Follow-Up Work

- Request construction is split into pre-resolve material axes and post-resolve
  source/pipeline axes.
- The generated-source preprocessing path now strips recognized defines,
  removes static uniform declarations, collapses blank-line runs, and feeds
  known conditional pruning without materializing an intermediate stripped
  source string.
- `InsertDefinesAfterVersion` builds the final source with a single
  `StringBuilder` and fixes the CRLF `#version` insertion edge case that could
  produce `\r\r\n`.
- `FormatFloatLiteral` has a common-value fast path for `0`, `1`, `-1`, and
  `0.5` while preserving finite-value validation and `-0.0` normalization.
- Vertex permutation hashing now sorts a pre-sized shader list instead of using
  per-call LINQ ordering.
- Baseline coverage captures minimal, common, and maximal generated-source
  contracts including `VariantHash`, `SourceVersion`, generated length,
  animated count, sampler count, and escaped generated source.
- Tests cover direct source invalidation, include invalidation, vertex include
  invalidation, concurrent resolve miss coalescing, deterministic hashes,
  static literal token safety, common float literal formatting, and cleanup
  after define/static-uniform stripping.

## Remaining Operational Work

- [ ] Profile with `Build-Editor` plus a shader-prep storm scene, then compare
  `PrepareVariant` total time and allocations in
  `profiler-main-thread-invokes.log`.
- [ ] Merge the dedicated work branch back into `main` after final validation.
- [ ] Include what changed, why, validation performed, risks, and follow-ups in
  the PR notes.

## Out Of Scope

- Changing the public `PreparedUberVariant` shape or
  `UberMaterialVariantBindingState`.
- GLSL semantics changes inside `GlslSnippetDeadCodeEliminator` beyond merging
  compatible passes into the streaming rewrite.
- Backing-store changes to `UberAuthoredState` or
  `UberMaterialPropertyState`.

# Uber Shader Variant Builder Optimizations — TODO

Tracks optimization opportunities in
[XREngine.Runtime.Rendering/Resources/Shaders/UberShaderVariantBuilder.cs](../../../XREngine.Runtime.Rendering/Resources/Shaders/UberShaderVariantBuilder.cs).

Goals:

- **Smaller generated variants** — minimize the size of the GLSL text we hand
  to the driver per material/pipeline permutation.
- **Faster preparation** — reduce CPU cost of `PrepareVariant`, especially on
  cache misses and during shader-prep storms (scene load, hot reload).

Items are ordered by expected impact after the setup phase. Keep each phase
small enough to validate independently.

## Phase 0 - Contracts, baselines, and branch setup

- [x] **Create a dedicated branch for this TODO before code changes.**
  Use an imperative branch name tied to the work, then keep each phase diff
  small enough to review and validate on its own.

- [ ] **Capture the current source/hash contract before refactors.**
  Record representative generated-source fixtures plus `VariantHash`,
  `SourceVersion`, `GeneratedSourceLength`, animated uniform count, and sampler
  count for minimal/common/maximal feature masks. Behavior-preserving phases
  should keep generated source identical unless the task explicitly says
  otherwise.

- [x] **Decide and document the hash-version boundary before replacing FNV.**
  Replacing FNV with xxHash intentionally changes `SourceVersion` and
  `VariantHash`; validate deterministic new hashes and identical generated
  source instead of expecting hash equality. If old serialized/status hashes
  need migration or display treatment, handle it in the same phase.

- [x] **Restore and lock the generated-variant metadata contract.**
  Generated variants must carry both a legacy recovery signal for
  `IsGeneratedVariant` / canonical-source restoration and a stable
  `// variant-hash: 0x...` telemetry comment. Do not move entirely to an
  `XRShader` flag until serialized legacy generated fragments remain detectable
  without scanning the full source.

## Phase 1 - Localized, low-risk caching wins (largest CPU win)

- [x] **Memoize `ResolveCanonicalVariantSource` per canonical source identity
  and resolver-options key.** `ShaderSourceResolver.ResolveSourceDetailed` runs
  include/snippet resolution on every `PrepareVariant` call, *before* the
  variant cache is consulted. Cache a structured result containing resolved
  source, resolved dependencies, source path/name, and computed source version.
  Validate hits with source text/path equality plus
  `ShaderSourceResolver.AreDependenciesCurrent(...)`; include resolver options
  such as `EmitIncludeDeadCodeMarkers` in the key. Concurrent misses should
  share one resolve.

- [ ] **Split `BuildRequest` into pre-resolve and post-resolve parts before
  reordering cache checks.** Build material axes (features, property modes,
  static literals, render pass, vertex permutation, and pipeline macros) without
  the resolved source, then combine with the cached resolved-source version.
  Only skip source resolution when the dependency-aware resolved-source cache
  can prove the version is current; otherwise resolve first so include/snippet
  edits cannot reuse stale variants.

- [x] **Memoize `ComputeVertexPermutationHash` per shader identity + validated
  source/dependency stamp.** Today it calls `GetResolvedSource()` + FNV over
  every non-fragment shader per variant prep. For materials with stable vertex
  shaders this should be O(1) after the first call. Do not use a bare
  `ConditionalWeakTable<XRShader, long>` unless it invalidates on source text,
  source path, and include/snippet dependency changes.

- [x] **Cache manifest-derived data on / next to `ShaderUiManifest`.**
  Compute once per manifest and reuse:
  - `featureGuardMacros` HashSet
  - `disabledFeatureMacros` precomputed bitmap keyed by enabled-feature set
  - `IsAuthorableUberProperty` filtered property list
  - `IsStaticPropertySupported` + `ShaderVar.GlslTypeMap` lookups
  - sampler property list (for `CountReferencedSamplers`)
  - `ResolvePipelineMacros` result keyed by source identity + dependency stamp

- [x] **Replace FNV-1a with `System.IO.Hashing.XxHash64` as an intentional hash
  migration.** Use xxHash for both `ComputeStableHash` and
  `ComputeVariantHash`; this is not behavior-preserving because it changes
  `SourceVersion`, `VariantHash`, cache keys, inspector/status values, and any
  tests that assert exact hashes. Validate deterministic new hashes and
  identical generated source for unchanged inputs.

- [x] **Normalize and pre-hash `UberVariantCacheKey.SourcePath`.** Today
  equality does an ordinal string compare every dictionary lookup. Prefer a
  normalized path plus precomputed `ulong` path hash, retaining the string for
  collision checks and diagnostics. Avoid `string.Intern` for asset/import
  paths because interned arbitrary paths are process-lifetime allocations.

- [x] **Replace LINQ chains in `BuildRequest` with explicit pre-sized loops.**
  `manifest.Features.Select(...).Where(...).Select(...).OrderBy(...).ToArray()`
  allocates 4–5 intermediates per call. Use `List<T>` with capacity +
  `List<T>.Sort` (already done for animated/static; extend to features).

## Phase 2 — Generated-source size reduction (largest size win)

- [x] **Stop emitting `#define <macro> 1` for guards/pipeline macros the
  pruner already resolved.** Have `PruneKnownConditionalBlocks` return the set
  of macros it could *not* statically eliminate; only emit defines for that
  residual set. Today every disabled feature + every active pipeline macro
  contributes a dead define line to the variant output.

- [x] **Inline static-literal substitution instead of emitting
  `#define _Foo vec4(...)`.** You already strip the static uniform
  declaration. In the same streaming pass, replace identifier references to
  `_Foo` in function bodies with the literal value using a token-aware scanner.
  Do not rewrite comments, preprocessor directives, string literals, struct
  field names, swizzle-like suffixes, or longer identifiers such as
  `_FooTint`. The GLSL compiler then sees constants directly (better constant
  folding, smaller GPU binary) and the variant text loses the static-define
  block.

- [x] **Replace the full-source generated-marker scan without breaking legacy
  detection.** `IsGeneratedVariant` does a `Contains` over the full source every
  check. Preferred path:
  - add a structured flag/property on newly generated `XRShader` instances,
  - keep a bounded header scan for serialized legacy generated fragments,
  - emit `// variant-hash: 0x...` for backend telemetry,
  - keep the existing marker or a compatible header signal until all consumers
    and saved assets can rely on structured metadata.

- [x] **Preserve original newlines in `StripStaticUniformDeclarations`.**
  `AppendLine` writes `Environment.NewLine` even when the source uses `\n`,
  which can inflate Windows variant size and create mixed line endings.

- [x] **Strip trailing blank-line runs** introduced by include-resolution and
  the various strip passes. After all elimination, collapse 2+ consecutive
  blank lines to one in the final streaming pass.

## Phase 3 — Single-pass streaming rewrite (CPU + size win, larger refactor)

- [ ] **Fold `StripRecognizedDefines`, `StripStaticUniformDeclarations`,
  `GlslSnippetDeadCodeEliminator.Trim`, and
  `GlslSnippetDeadCodeEliminator.StripRegionMarkers` into one streaming pass.**
  Currently the source is materialized 4–5 times. A single line walker that
  drops:
  - recognized `#define` lines (against a `HashSet<string>`),
  - static uniform declarations whose name is in the static set,
  - region markers,
  - blank-line runs,

  in one pass eliminates per-pass `Regex.Replace` allocations and large
  intermediate strings.

- [x] **Replace per-macro `Regex.Replace` in `StripRecognizedDefines` with a
  single `[GeneratedRegex]` that captures any `#define <name>` and intersects
  the captured name with the macro set.** Today the regex pipeline runs N
  passes over the entire source (one per macro).

- [x] **Replace per-macro `Regex.IsMatch` in `ResolvePipelineMacros` with a
  single scan.** Combine with the manifest cache (Phase 1) so this only runs
  once per source identity + dependency stamp.

- [ ] **`InsertDefinesAfterVersion`: use a single `StringBuilder` with known
  capacity** (`source.Length + defines.Sum(d => d.Length + 2)`) instead of
  `string.Join` + concat. Becomes one allocation.

- [ ] **Fold `PruneKnownConditionalBlocks` into the same streaming pass.**
  Together with the strip passes above, this gives one walk of the source
  from raw → final variant text.

## Phase 4 — Cache hygiene + micro-wins

- [x] **Drop per-call `File.Exists` in `PruneStaleSourceEntries`.** It runs on
  every `PrepareVariant`. After Phase 1, dependency freshness should be owned by
  the resolved-source cache; stale generated-source entries can be evicted when
  a validated source identity reports a new version. If active eviction is
  required, drive it from a single `ConcurrentDictionary<string, long>` of
  last-known versions and only enumerate the cache when a version changes.

- [ ] **Small-value fast path in `FormatFloatLiteral`.** Common values
  (`0`, `1`, `-1`, `0.5`) should hit a lookup table; today every static
  property pays `value.ToString("0.0################")` + a
  `StartsWith("-0.0")` scan.

- [x] **Pre-size the `defines` list in `GenerateVariantSource`** to
  `pipelineMacros.Count + disabledFeatureMacros.Count + staticProperties.Count + 1`
  to avoid the implicit `List<string>` growth allocations.

## Validation

- Add `XREngine.UnitTests` coverage for:
  - cache hit/miss accounting (no redundant resolve on hit; concurrent misses
    share one resolve),
  - dependency-aware source cache invalidation when direct source, include, or
    snippet content changes,
  - identical generated source after behavior-preserving refactors (golden
    file); exact hash equality is required only outside the explicit hash
    migration phase,
  - deterministic `VariantHash` / `SourceVersion` values after the xxHash
    migration,
  - generated variants include the legacy recovery marker/header signal and
    `// variant-hash: 0x...`, and backend telemetry can parse the hash,
  - token-aware static-literal replacement does not rewrite comments,
    directives, string literals, longer identifiers, or field/suffix accesses,
  - generated source size regression check (snapshot byte counts for a
    representative material/feature matrix),
  - vertex permutation hash memoization invalidates when vertex shader source,
    include, or snippet content changes.

- Profile with `Build-Editor` + a scene that triggers a shader-prep storm
  (scene load, hot reload). Capture `profiler-main-thread-invokes.log` and
  compare `PrepareVariant` total time + allocations before/after each phase.

## Completion

- [ ] **Merge the dedicated branch back into `main` after final validation.**
  Include what changed, why, validation performed, risks, and follow-ups in the
  PR notes.

## Out of scope (track separately if needed)

- Changing the public `PreparedUberVariant` shape or `UberMaterialVariantBindingState`.
- GLSL semantics changes inside `GlslSnippetDeadCodeEliminator` beyond merging
  it into the streaming pass.
- Backing-store changes to `UberAuthoredState` / `UberMaterialPropertyState`.

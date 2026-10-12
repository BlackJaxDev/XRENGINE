# Uber Shader Varianting

This document is the implemented architecture for XRENGINE's Uber material
variant system. It consolidates the completed variant builder optimization
work and the shader-source optimization boundary described in
[Resolved Shader Source Optimization Remaining Todos](../../work/todo/rendering/resolved-shader-source-optimization-todo.md).

The short version: Uber materials store author intent as material state, build a
deterministic variant request from that state, generate a smaller fragment
shader source off the render path, and let the renderer compile/link the new
program while the previous known-good program remains visible.

## Design Goals

- Keep `XRMaterial` authored state as the source of truth for Uber feature and
  property choices.
- Generate the smallest safe fragment source for a material/pass permutation.
- Keep `PrepareVariant` cheap on cache hits and predictable during shader-prep
  storms.
- Make shader identity explicit: no hidden axes, no text mutation as state.
- Preserve legacy generated-fragment recovery until serialized assets no longer
  depend on source-header detection.
- Keep generic shader-source optimization backend-neutral and separate from
  Uber material intent.

## Ownership

`XRMaterial`
: Owns serialized author intent in `UberAuthoredState`, the latest desired
  request in `RequestedUberVariant`, the currently adopted binding state in
  `ActiveUberVariant`, and editor/runtime progress in `UberVariantStatus`.

`ShaderUiManifest`
: Parses `//@feature(...)`, `//@property(...)`, mutability, defaults,
  dependencies, conflicts, and validation issues from the canonical shader
  source. The manifest describes the editor surface; it does not own material
  state.

`UberShaderVariantBuilder`
: Converts material intent into `UberMaterialVariantRequest`, resolves and
  caches canonical source, specializes Uber feature/pass conditionals, inlines
  eligible static material values, emits generated-fragment metadata, and caches
  generated `XRShader` instances.

`ResolvedShaderSourceOptimizer`
: Owns generic source cleanup after source resolution. It is renderer-neutral
  and can remove snippet regions, fold static literals when supplied, prune dead
  whole-source code, preserve keep annotations and stage interfaces, strip
  region markers, and collapse blank-line runs.

Renderer backends
: Own backend transforms, binary-cache lookup, compile/link work, program
  adoption, and backend telemetry. They consume generated source; they do not
  rebuild Uber material intent.

## Native Advanced Material Rows

Recognized Unity Poiyomi imports carry `EngineMaterialSemanticIdentity.NativeUberV1`.
`NativeUberMaterialSchema` defines separate opaque and masked layouts. It does
not change the bounded WebGPU `AdvancedUberBaseSurfaceRecord` ABI. Both native
layouts use the canonical material constant and texture binding tables. Numeric
properties occupy four-word slots. Integer selectors retain their integer bits.
Texture roles and feature bits have fixed offsets generated into shader source.
Material values do not create a shader program for each imported material.

`NativeUberMaterialSourceEncoder` checks the authored feature state and supported
operations before publication. Unsupported active operations return a reason.
`AdvancedGpuScenePublisher` selects the native layout before standard material
encoding. The native source contract must not enter standard PBR shading.

`ShadeNativeUber.glslinc` supplies the shared native evaluator for mono, MSAA,
and overflow repair. It implements the supported toon lighting, normals, material
AO, specular controls, cubemap reflections, layered matcaps, rim, emission,
glitter, color themes, and the supported decal operations.
Reconstructed UV, normal, tangent, and position changes supply compute texture
footprints. The current evaluator visits all lights that affect the view.

Active Mochie imports use the source metallic and smoothness multipliers.
Packed green is smoothness. The source invert control determines inversion.
Packed blue controls probe reflection and packed alpha controls direct specular.
Their strengths and invert controls remain independent. The opt-in packed-mask
mode leaves generic Uber materials on their existing path. Probe specular enters
toon ambient before the brightness cap and monochromatic adjustment. Custom
stylized specular uses the primary directional light once.

`NativeUberCoverage.glslinc` is shared by masked visibility, directional shadows,
and native shading. Active face discard, pixel UDIM cells, dissolve, or source
cutout require masked publication before depth writes. Missing UV1 uses UV0 in
both raster and reconstruction. UV2 and UV3 use the two former reserved words
in the 64-byte packed vertex. Missing UV2 and UV3 remain zero, as in the forward
shader. Active vertex UDIM discard and alpha-to-coverage
remain explicit unsupported operations. A view stores frozen render-time bits
at byte offset 940 without changing its 944-byte stride.

The Unity importer preserves transparent source blend and depth state and sets
`AdvancedLatePassMetadata`. Supported order-dependent materials use sorted alpha.
The late lane uses the existing filtered direct submission path. Imported Uber
materials do not claim temporal replay until matching velocity and reactive
variants exist. Unsupported scene-color dependencies return a diagnostic.

Runtime and image acceptance are tracked in the
[avatar validation matrix](../../work/testing/rendering/jax2031-advanced-parity-validation.md).

### Source Dissolve And Detail Color

Poiyomi Basic dissolve maps to Uber mode 5. The importer binds this mode after
exact-name parameters, so the source enum cannot replace the converted value.
It sums the saved dissolve amount channels into `_DissolveProgress`. Progress
is independent of alpha cutoff. Other source dissolve modes still require
separate parity work.

`Uber/source_dissolve.glsl` owns the Basic threshold and edge calculation for
canonical Uber and native coverage. The amount remaps from `-edgeWidth` to 1;
noise stays inside the endpoints. Zero progress has no edge or alpha change.
The source mask, noise inversion, detail subtraction, smoothing, hardness, and
continuous speed retain their separate roles. Basic texture pan uses source
time in seconds divided by 20. The dissolved texture changes color and alpha
before the material cutout test. It does not force an opaque material to clip.

`SerializedMaterialImporter.SourceToonDetailColor` converts multiplicative
source brightness into `_DetailTint` and clears the engine additive brightness.
It selects multiply blending and retains intensity and mask. An absent detail
texture contributes the source gray value, which cancels the source color-space
factor at brightness 1 and white tint. Unity project color space selects that
factor; standalone material imports without project settings assume linear.
The existing native and canonical detail evaluators consume these normalized
values. Detail-normal composition follows the separate normal contract below.

### Source Normals And Matcaps

The canonical and native Uber evaluators decode base and detail normals in
tangent space. Detail mask green controls the detail scale. They combine XY by
addition and Z by multiplication, then apply TBN and normalize once.
`SerializedMaterialImporter.SourceToonNormals` binds each texture's imported
green-channel inversion as material state. It does not change shared texture
bytes. Raw engine normal textures retain their RG decode convention.
Source backface tangent-basis controls still need a separate adapter.

Imported matcaps use `_SourceMatcap` and per-layer source controls. Smoothness
selects an explicit mip from the texture size. Mask channel, mask inversion,
mask UV, optional mask-driven smoothness, texture transform, and source pan
rate remain separate. Supported projections are source modes 0, 1, and 2.
Replace, multiply, add, and screen operations change base color before lighting.
The source Add to Light value and emission use separate contributions. Generic
engine-authored matcaps retain their existing evaluator path.

The importer rejects enabled matcap controls that the source adapter cannot
represent, including custom normal, rotation, circle mask, audio controls,
alpha overrides, and UV1 blend. A hue toggle is accepted only when shift and
speed are zero in shift mode. Animated matcap hue is not supported. These
failures use `SourceVisualImportException`.

### Source Glitter

`SerializedMaterialImporter.SourceToonGlitter` binds enabled Poiyomi glitter
with `_SourceGlitter = 1`. Frequency, degree range, contrast, layer count,
normal blend, surface color, and brightness retain their source values.
Integer modes remain integers in native material rows. Mask UV selection,
transform, pan, channel, and inversion remain independent of flake UVs.

`Uber/source_glitter.glsl` supplies the shared canonical and native evaluator.
Cell locations remain fixed. Time changes flake orientation, with the source
time scale and rotation order. Circle coverage uses adjacent surface samples
to estimate the source derivative across cell boundaries. It does not claim
pixel-identical hardware derivatives. The evaluator supports angle, pulse,
and light modes, with one to eight layers. Add blending contributes through
emission once. Replace blending changes base color before lighting and adds
only the source excess-brightness emission. Engine-authored glitter keeps its
existing evaluator, with one emission contribution.

The importer rejects enabled shape/color textures, square shapes, random
size/color/texture rotation, hue, theme, global mask, and AudioLink options.
The current lighting ramp supplies the shadow mask. Its lighting model still
differs from Poiyomi, so matched-lighting acceptance remains separate. The
two-layer circle evaluator checks 54 candidate sites per shaded sample,
including its derivative samples. Production GPU timing is still required.

## Authored And Active Variant State

The material keeps requested and active variant state separate so the editor can
show the desired future shape while the renderer continues using the previous
ready program.

- `UberAuthoredState` stores feature enablement, property modes, and preserved
  static literals.
- `RequestedUberVariant` records the latest request snapshot prepared from
  authored state.
- `ActiveUberVariant` records the generated fragment variant currently bound to
  the material.
- `UberVariantStatus` reports stage, requested hash, active hash, cache hit,
  preparation/adoption/backend timings, uniform count, sampler count, generated
  source length, and failure text.

On first inspection or rebuild, `EnsureUberStateInitialized()` infers missing
authored state from the canonical fragment shader and parsed manifest. If a
legacy material serialized a generated Uber fragment as its active shader, the
material restores the canonical `UberShader.frag` source before inference. That
keeps migration based on authoritative shader text instead of baked defines.

## Variant Build Flow

`RequestUberVariantRebuild()` captures the canonical fragment shader, debounces
rapid edits, supersedes stale requests for the same material, snapshots authored
state, prepares the variant on a worker path, and adopts the result when ready.

`PrepareVariant(...)` performs the CPU-side variant work:

1. Resolve canonical source through the dependency-aware resolved-source cache.
2. Build material axes from authored feature state, property modes, static
   literals, render pass, detected pipeline macros, and vertex permutation hash.
3. Compute `VariantHash` from those axes.
4. Look up the generated source and generated `XRShader` caches.
5. Generate source on a miss.
6. Create or reuse a generated fragment `XRShader` with structured metadata.
7. Return binding state, counts, generated size, timing, and cache-hit status.

The render thread only performs unavoidable backend work and final adoption.
Manifest parsing, dependency checks, request construction, hashing,
static-literal formatting, source emission, and cache lookup stay outside
draw-time code.

## Variant Axes And Hashing

`UberMaterialVariantRequest.VariantHash` is the authoritative material variant
identity. It is computed from explicit ordered axes:

- `EnabledFeatures`: enabled `//@feature(...)` ids.
- `PipelineMacros`: active pipeline-flavor defines detected from source plus
  render-option-derived forward-path disables.
- `AnimatedProperties`: non-sampler properties that remain runtime uniforms.
- `StaticProperties`: `name=literal` pairs baked into generated source.
- `RenderPass`: material render pass id.
- `SourceVersion`: stable hash of the resolved canonical fragment source.
- `VertexPermutationHash`: hash of non-fragment shader stages the fragment must
  pair with.

`VariantHash`, `SourceVersion`, stable path/source hashes, and
`VertexPermutationHash` use `System.IO.Hashing.XxHash64`. This was an
intentional hash-version boundary; legacy FNV values are not stable across it.
Tests should assert deterministic xxHash values and generated-source behavior,
not equality with pre-migration FNV hashes.

Every new variant axis must be added to `UberMaterialVariantRequest`,
`UberMaterialVariantBindingState`, and `ComputeVariantHash(...)` in the same
change. Hidden axes make cache reuse unsafe.

## Source Resolution Cache

Canonical source resolution is cached before generated-variant cache lookup.
The cache key contains source text, normalized source path/name, and resolver
options such as `EmitIncludeDeadCodeMarkers`.

Each cache entry stores:

- resolved source text
- normalized source path/name
- precomputed source-path hash
- `SourceVersion`
- file dependencies
- pipeline macros detected in the resolved source

Hits are accepted only while `ShaderSourceResolver.AreDependenciesCurrent(...)`
reports every dependency current. Concurrent misses share one
`Lazy<ResolvedUberShaderSource>` so shader-prep storms do not fan out duplicate
include/snippet resolution work.

The generated-source and generated-shader caches are keyed by
`(VariantHash, SourceVersion, SourcePathHash, SourcePath)`. `SourcePathHash`
keeps dictionary lookup cheap while retaining the normalized string for
collision checks and diagnostics.

Stale generated entries are not pruned by probing `File.Exists(...)` on every
prepare. `LastKnownSourceVersionsByPath` records the last seen version for each
source path and enumerates the generated caches only when that version changes.

## Vertex Permutation Hash

The fragment variant also depends on the non-fragment shader stages attached to
the material. `ComputeVertexPermutationHash(...)` resolves each non-fragment
shader through the same dependency-aware source cache, then memoizes the
per-stage hash by shader type, source text, normalized source path, and
validated `SourceVersion`.

This makes stable vertex-stage permutations O(1) after the first prepare while
still invalidating when source text, path, includes, or snippets change.

## Manifest-Derived Data Cache

`ShaderUiManifest` is parsed once, then the builder caches derived data in a
`ConditionalWeakTable<ShaderUiManifest, ManifestDerivedData>`.

Cached data includes:

- known conditional macros from pipeline axes and feature guards
- authorable Uber properties
- sampler properties
- property names that can safely be emitted as static literals
- disabled-feature macro sets by enabled-feature key

The cache keeps request construction and sampler counting from rediscovering the
same manifest facts on every prepare.

## Preparation Performance Contract

`PrepareVariant(...)` is allowed to allocate on cache misses, but the steady
cache-hit path should avoid avoidable whole-source work and repeated
intermediate collections.

The landed builder follows these rules:

- Material axes are gathered with explicit pre-sized lists and sorted arrays,
  not LINQ chains that allocate multiple intermediate sequences per prepare.
- Pipeline macro detection uses one scan of the resolved source and is stored
  with the resolved-source cache entry.
- Recognized `#define` stripping uses one regex pass over define lines and
  checks captured names against the known macro set.
- Generated metadata/define emission sizes the define list from the residual
  macro count plus required metadata lines.
- Source-path hashing is precomputed for cache keys; the normalized path string
  remains available for collision checks and diagnostics.
- Stale generated-source eviction is driven by observed source-version changes,
  not by per-call filesystem probes.
- Concurrent source-resolution misses share a single lazy resolve operation.

New code in this path should preserve those constraints unless profiling shows
a better shape.

## Source Generation

Generated source is derived from resolved canonical source. The builder first
removes old recognized feature/pipeline `#define` lines with a single regex pass
that captures define names and intersects them with the known macro set.

Known feature and pipeline conditional blocks are then evaluated using only
known macros. A branch is removed only when every condition in the group is
known; unknown preprocessor regions are preserved. Fully pruned feature/pass
guard macros are not re-emitted. Only residual feature or pipeline macros that
still appear in the final source are emitted after `#version`.

Static material properties are lowered in two steps:

1. Remove matching uniform declarations while preserving the source's original
   newline style.
2. Replace identifier references with deterministic GLSL literals using a
   token-aware scanner.

The literal scanner skips comments, preprocessor directives, string literals,
struct declarations, struct field names, longer identifiers, and suffix/field
accesses that would change semantics. When a static literal is followed by field
or swizzle access, the literal is parenthesized.

After Uber-specific specialization, the source passes through
`ResolvedShaderSourceOptimizer` with the diagnostic label `UberVariant`.
Generic optimization can trim snippet regions, whole-source dead code, region
markers, and blank-line runs. This means Uber varianting owns material intent;
generic dead-code cleanup belongs to the shared optimizer.

Generated variants insert a small header after `#version`:

```glsl
// XRENGINE_UBER_GENERATED_VARIANT
// variant-hash: 0x0123456789abcdef
```

Residual `#define <macro> 1` lines follow only when the final source still needs
them.

## Property Modes

Uber properties have two specialization modes:

- `Static`: the current material value is embedded as a GLSL literal. Editing
  the value changes the variant identity and requests a rebuild.
- `Animated`: the property remains a runtime uniform and can update without
  rebuilding while feature membership and other static axes stay unchanged.

Samplers always remain runtime-bound resources. When a feature is disabled, its
sampler declarations are compiled out and excluded from `SamplerCount`, but the
authored material texture values remain available for later re-enable.

Explicit manifest mutability constrains mode selection. Properties annotated as
runtime stay animated. Static-compatible scalar/vector values can be baked when
their type is supported by `ShaderVar.GlslTypeMap`; arrays and samplers are not
static-literal candidates.

If a property is compiled out because its feature is disabled or because it was
converted to a static literal, the material parameter is preserved on
`XRMaterial`. That preserves authored values across `Static -> Animated ->
Static` round trips and across feature toggles.

## Feature Gating And Family Shape

Feature membership is compile-time only. There are no runtime
`_Enable<Family>` or `_<Family>Toggle` uniforms that decide whether a feature
family exists in a draw. Sub-option selectors inside an already compiled feature
can remain runtime controls when they describe content mode rather than family
membership.

The canonical `UberShader.frag` source contains no unconditional feature
disables. Optional families live behind `#ifndef XRENGINE_UBER_DISABLE_*`
guards, and fresh hand-authored materials use the annotation default from
`//@feature(default=on|off)`.

Imported glTF/FBX materials author the same feature-state surface explicitly.
There is one canonical Uber family shape for imported and hand-authored
materials, so two materials with the same authored state can share generated
fragment cache entries.

Forward rendering path requirements are folded into pipeline macros. For
example, lighting, ambient occlusion, shadows, contact shadows, and PBR resource
paths add explicit disable macros when the material state or render options show
that those paths are not required.

## Sampler Layout Policy

Uber variants use stable sparse sampler bindings per feature module. A sampler
keeps its authored `layout(binding=N)` slot even when the owning feature is
disabled. Disabling a feature removes the declaration from generated source; it
does not repack sibling bindings.

This policy keeps texture-unit assignments stable across feature toggles and
lets the material binding path pre-bind authored textures without
variant-specific binding reflow. Disabled samplers do not contribute to
`SamplerCount`, so diagnostics still report live resource pressure accurately.

If a future feature family pushes total sampler usage past the backend limit,
revisit this policy with a written proposal before changing binding layout.

## Generated Metadata And Legacy Recovery

New generated `XRShader` instances carry:

- `IsGeneratedUberVariant = true`
- `GeneratedUberVariantHash = VariantHash`
- a bounded-header legacy marker: `XRENGINE_UBER_GENERATED_VARIANT`
- a parseable `// variant-hash: 0x...` telemetry comment

`UberShaderVariantBuilder.IsGeneratedVariant(...)` first checks the structured
flag, then scans only the first 2048 characters for the legacy marker. This
keeps live runtime checks cheap while still detecting serialized legacy
generated fragments without scanning full shader source.

The telemetry comment is intentionally kept separate from the legacy marker.
Backends can parse the variant hash from source strings that flow through
compile/link queues, while legacy recovery can continue to identify generated
fragments even if structured fields are missing from older assets.

## Program Cache Layering

Generated fragment shaders and linked backend programs are cached at different
layers.

Generated fragment cache
: Owned by `UberShaderVariantBuilder` and keyed by
  `(VariantHash, SourceVersion, SourcePathHash, SourcePath)`. The generated
  `XRShader` is immutable enough to share across materials with identical
  authored variant axes.

Program cache
: Owned by the renderer/hybrid rendering layer and keyed by renderer/material
  state, including material id, renderer key, shader state revision, and
  material variant hash where relevant. Linked programs carry backend and
  material-specific binding state, so they are not shared merely because two
  materials reuse the same generated fragment shader.

During a variant swap, the previous linked program remains the last-known-good
program until the replacement reports ready for the current renderer. If the
replacement stalls, fails, or hits queue backpressure, the old program remains
visible.

OpenGL uses clone-and-swap program handles. The replacement is loaded from the
binary cache or linked from source into a fresh handle, then adopted only after
it is ready. Vulkan carries the generated hash into shader/pipeline diagnostic
metadata for parity.

## Telemetry And Editor Contract

The ImGui Uber inspector edits `XRMaterial` state. It does not patch shader
source directly for normal feature/property changes.

- Feature toggles call material feature APIs and request a rebuild.
- Dependency/conflict prompts can run before authored state changes.
- Property mode switches rebuild when the active variant shape changes.
- Static value edits request a rebuild.
- Animated value edits stay live through uniform updates.

The inspector displays:

- requested and active variant hashes
- enabled feature count
- preparation stage and stale-state badges
- cache hit/miss status
- generated source size
- animated uniform count
- live sampler count
- worker preparation time
- material adoption time
- backend compile/link stage and timings
- failure text
- session telemetry from `UberShaderVariantTelemetry`

OpenGL backend telemetry parses the `variant-hash` comment from compile inputs
and reports compile start, link start, success, failure, compile milliseconds,
link milliseconds, and failure text keyed by variant hash. Material-side
preparation and adoption timing remain separate from driver compile/link timing.

## Regression Coverage

Primary coverage lives in
`XREngine.UnitTests/Rendering/UberMaterialVariantTests.cs`.

Covered behavior includes:

- authored-state inference from fragment source
- canonical-source restoration for legacy generated fragments
- generated metadata and parseable telemetry hash
- dependency-aware resolved-source cache hits and invalidation
- concurrent source-resolution miss coalescing
- deterministic `VariantHash` and `SourceVersion`
- variant-hash separation across feature mask, property mode, and static
  literal changes
- static literal inlining without rewriting comments, directives, longer
  identifiers, struct fields, or field/suffix accesses
- animated uniforms staying live
- static/animated/static value preservation
- disabled-feature sampler declarations not leaking into `SamplerCount`
- backend compile/link telemetry keyed by generated variant hash

`UberVariantPreparationBaseline_CapturesMinimalCommonMaximalTimings` is an
explicit CPU-side harness. It writes CSV output under
`Build/Logs/uber-variant-baselines/` with preparation milliseconds, adoption
milliseconds, generated source size, animated uniform count, and sampler count
for minimal, common, and maximal feature masks. GPU frame-cost baselines remain
a separate hardware validation workstream.

## Current Boundaries And Future Work

The current implementation is optimized but not a single-pass source rewriter.
`StripRecognizedDefines`, conditional pruning, static uniform stripping, static
literal inlining, static-if pruning, and generic optimizer passes still run as
staged transformations. A future rewrite may merge more of these into one
streaming pass, but that is not required for the current contract.

`BuildRequest(...)` still resolves canonical source before generated-variant
cache lookup. That is intentional until a pre-resolve request shape can prove
source dependency freshness without risking stale includes or snippets.

The small-value fast path for `FormatFloatLiteral(...)` is not part of the
landed architecture. Literal formatting is deterministic and validates finite
float values, but common-value lookup-table optimization remains a micro-win.

Generic source pruning is still evolving. Uber varianting should continue to
specialize material intent only. Broadly valid pruning, reflection integration,
backend-neutral diagnostics, and cross-family shader optimization belong in the
resolved-source optimization pipeline rather than in Uber-only helpers.

## Key Files

- `Build/CommonAssets/Shaders/Uber/UberShader.frag`
- `XREngine.Runtime.Rendering/Objects/Materials/UberMaterialState.cs`
- `XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.Uber.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/UberShaderVariantBuilder.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/UberShaderVariantBuilder.Preprocessor.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/UberShaderVariantTelemetry.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderUiManifest.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderSourceResolver.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/ResolvedShaderSourceOptimizer.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/XRShader.cs`
- `XREngine.Editor/AssetEditors/XRMaterialInspector.Uber.cs`
- `XREngine.UnitTests/Rendering/UberMaterialVariantTests.cs`

## Related Documentation

- [Uber Shader UI Annotations](uber-shader-ui-annotations.md)
- [Uber Shader Materials](../../developer-guides/rendering/uber-shader-materials.md)
- [OpenGL Program Linking](../../developer-guides/rendering/opengl-program-linking.md)
- [World Shader Prewarm Graph](../../work/design/rendering/world-shader-prewarm-graph-design.md)

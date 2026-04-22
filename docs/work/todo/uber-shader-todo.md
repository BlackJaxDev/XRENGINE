# Uber Shader Optimization TODO

Last Updated: 2026-04-22
Status: Authored-state, annotation-driven inspector, migration-on-load, request coalescing, safe fallback, async backend compile/link, deferred atomic-swap on relink, variant-axis formalization, program-cache layering, sampler-layout policy, and expanded regression coverage are all landed. The remaining work is now shader-family trimming in `UberShader.frag` and on-device GPU baselines - both gated on owner approval / hardware runs.
Scope: finish the modular Uber shader pipeline, move all non-API calculations off the render thread, and complete the material-state, variant, cache, migration, and validation systems.

## Already Landed

These items are no longer TODOs:

- a reusable shader UI manifest parser exists and is cached through `XRShader`
- the shader inspector can surface parsed metadata and validation issues
- the ImGui material inspector now routes Uber feature and property edits through authored state instead of fragment-source mutation
- `uniforms.glsl` now has initial category, feature, label, and tooltip annotations
- the real `uniforms.glsl` Uber authoring surface now parses without missing-metadata warnings; legacy compatibility uniforms that are not part of the curated inspector are explicitly excluded from required coverage
- `XRMaterial` owns authored, requested, active, and status Uber state instead of treating shader text as the canonical state store
- Uber properties support `Static` versus `Animated` specialization, generated static literals, and preserved authored values across variant swaps
- Uber variant preparation runs on a background worker path and exposes requested-versus-active state, cache hits, and failure text in the inspector
- focused Uber variant regression tests and a rendering architecture note now exist for the new pipeline
- targeted parser and telemetry aggregation regression tests now cover the real Uber annotation surface and the session/backend timing snapshot path

What is not landed yet:

- runtime-branch trimming and the lite-vs-full family split inside `UberShader.frag` are still incomplete (owner-approval gated)
- on-device compile/link/source-size/GPU baselines are still incomplete (hardware-run gated; a CPU-side preparation baseline harness is in place)

Recent hardening that is now in place:

- the GPU indirect combined-program cache now keeps the last linked program live while a replacement shader revision is still linking, instead of eagerly swapping to a stale or not-yet-linked replacement
- the linked-program cache layering (shaders keyed by variant hash, programs keyed by material+renderer+shader-state revision) and the sparse-per-module sampler-layout policy are now documented contracts
- regression coverage now includes disabled-module sampler leakage, safe-fallback restoration, `Static` <-> `Animated` round-trip value preservation, and variant-hash separation across feature-mask / property-mode / static-literal changes

## End State

At the end of the remaining work:

- the Uber shader is composed from explicit feature modules with one authoritative manifest derived from shader annotations
- every major feature family has a clear module toggle and grouped property UI
- every property defaults to `Static` and only becomes a runtime uniform when explicitly marked `Animated`
- disabled modules contribute no code, no uniforms, and no texture bindings to the compiled variant
- the material keeps rendering with its last-known-good program until the requested replacement is fully ready
- variant identity is explicit, cacheable, visible in tooling, and stable across reloads
- all CPU-side calculations required to prepare a variant happen off the render thread
- the render thread is left with only the minimum unavoidable render-API work and the final atomic program adoption step

## Non-Negotiable Rules

- feature sections are compile-time decisions, not runtime escape hatches
- the default property mode is `Static`, not `Animated`
- static properties are emitted as compile constants into generated shader source or generated constant blocks
- animated properties are emitted as uniforms and remain on the live material parameter surface
- disabled modules remove their dependent uniforms, samplers, and code paths entirely
- the editor UI is the authoritative source of feature enablement and property mode, not inferred shader text alone
- the module manifest comes from annotated shader source, not a parallel C# metadata file
- material-level state changes must go through `SetField(...)` per the `XRBase` rule in AGENTS.md §11
- the render thread is not a place for manifest parsing, dependency checks, property-mode diffing, literal formatting, variant-key hashing, source generation, request coalescing, migration inference, or compile-log parsing
- only the minimum unavoidable API-facing work stays on the render thread: backend compile or upload if required by the renderer, plus final atomic program swap after in-flight work using the old program has drained
- if a calculation currently occurs during draw, bind, or render-time material preparation and is not strictly render-API work, treat that as a bug and move it off the render thread

## Remaining Workstreams

### 1. Finish Shader Annotation Coverage

Outcome: the annotation system is complete enough that the Uber shader can be authored and debugged without falling back to implicit metadata for shipping behavior.

- [x] finish explicit `@feature` coverage for every optional Uber feature family
- [x] finish explicit `@property` coverage for all user-authored Uber uniforms and samplers that should appear in the custom UI
- [x] add missing ranges, enums, display names, tooltips, categories, and subcategories for the remaining Uber controls
- [x] tighten validation so missing or mismatched annotation coverage is loud in tooling instead of silently tolerated
- [x] write `docs/architecture/rendering/uber-shader-ui-annotations.md`

Acceptance criteria:

- shipping Uber modules do not rely on inferred guard-only metadata as their primary definition path
- the shader inspector reports missing feature and property annotations in a way that is hard to ignore
- shader authors can add or rename Uber UI content by editing shader source alone

### 2. Add A Real Uber Material State Model

Outcome: authored Uber state is stored as structured material data, not as ad hoc source edits.

- [x] add a dedicated serialized Uber material state object or equivalent data model owned by `XRMaterial`
- [x] store enabled module ids, property modes, and any authored static values in that state model rather than treating the fragment shader text as the source of truth
- [x] separate authored state, requested variant state, and currently bound compiled state
- [x] ensure imported and legacy materials can infer this new state on load
- [x] keep serialized values for disabled modules without leaking them into the live compiled shader surface
- [x] surface requested-state versus active-state differences in the inspector

Acceptance criteria:

- Uber material authoring does not depend on directly editing the shader text as the canonical state store
- the material can represent a desired future variant even while the current compiled program is still the old one
- disabled modules preserve their authored data without staying live on the runtime shader surface

### 3. Move Variant Preparation Off The Render Thread

Outcome: all CPU-side calculations required to produce an Uber variant run on background worker paths instead of render-time code paths.

- [x] audit the current Uber material, shader, and inspector flows for CPU-side work happening on or near render-time code paths
- [x] move annotation parsing, manifest validation, dependency resolution, feature-state validation, property-mode diffing, migration inference, variant-key hashing, static literal formatting, generated-source emission, request coalescing, and compile-log parsing off the render thread
- [x] add a dedicated background preparation path for Uber variant requests before any renderer-specific compile or upload step
- [x] ensure no UI interaction causes render-thread stalls because of CPU-side variant preparation work
- [x] instrument time spent in worker-thread preparation versus render-thread adoption so regressions are visible
- [x] document the allowed render-thread responsibilities explicitly in code comments and docs

Acceptance criteria:

- changing Uber feature state or static-property state does not require CPU-side preparation work on the render thread
- the render thread only performs unavoidable API work and final swap logic
- profiling can distinguish background preparation time from render-thread adoption time

### 4. Implement Property `Static` Versus `Animated` Specialization

Outcome: the runtime uniform surface shrinks to only the values that truly need to change after compilation.

- [x] define a serialized property-mode model for every Uber property
- [x] treat booleans, enums, ints, floats, vectors, and colors as compile constants by default
- [x] keep sampler bindings as resources while treating related control values as static unless explicitly marked animated
- [x] generate deterministic shader literals using invariant culture and stable formatting
- [x] explicitly handle `-0.0`, `NaN`, and `Inf` cases when generating literals or reject invalid authored values with loud diagnostics
- [x] capture the current runtime value when a property moves from `Animated` back to `Static`
- [x] emit real uniforms only for properties currently marked `Animated`
- [x] keep `XRMaterial` parameter synchronization consistent across recompiles so animated values and bindings survive variant swaps
- [x] add the per-property mode UI and bulk actions for converting eligible properties

Acceptance criteria:

- a newly created Uber material compiles with the minimum uniform surface needed for its enabled animated properties
- static properties do not appear as runtime uniforms in the compiled variant
- animated properties remain editable without forcing a recompile when feature membership is unchanged

### 5. Replace Source-Cloning With A Real Variant Request Pipeline

Outcome: module toggles and property-mode changes feed a structured async variant builder instead of mutating shader text inline as the long-term behavior.

- [x] define a dedicated Uber variant request keyed by enabled modules, animated-property mask, static-property literals, render pass, vertex permutation, pipeline flavor, and source version
- [x] define the stable variant hash ordering and hash function explicitly
- [x] debounce rapid edits and supersede stale requests for the same material
- [x] compile and link variants asynchronously using existing shader and program infrastructure where possible
- [x] retain the last-known-good program until the replacement variant is fully ready
- [x] define a guaranteed safe fallback variant for first-compile or first-failure cases
- [x] surface compile progress, compile timing, and compile failures directly in the Uber inspector
- [x] apply successful swaps atomically after old program usage has drained

Acceptance criteria:

- repeated quick edits converge to the final requested state instead of compiling every intermediate state
- failed rebuilds leave valid rendering on screen
- the direct fragment-source clone toggle path is no longer the main implementation path

### 6. Finish Cache And Invalidation Rules

Outcome: the new variant system does not replace runtime waste with uncontrolled source and program churn.

- [x] cache generated Uber source by full stable variant key
- [x] cache compiled shaders and linked programs by that same key (shaders keyed by variant hash in the generated-shader cache, programs keyed by material + renderer + shader-state revision in `HybridRenderingManager` - documented in the varianting architecture note)
- [x] choose and document the sampler-layout strategy: stable sparse bindings per module (documented in the varianting architecture note)
- [x] invalidate cache entries when any owned module file, shared include, generated-constant schema, or relevant shader source revision changes
- [x] treat shared include edits such as `common.glsl` as full-family invalidations, not per-module local edits
- [x] expose cache hit or miss, compile duration, uniform count, sampler count, and failure reason in diagnostics
- [x] display the active variant key in the material inspector

Acceptance criteria:

- identical authored Uber states do not regenerate identical source repeatedly
- source and program cache invalidation is strict enough to avoid stale variants after snippet edits
- the active variant is diagnosable from the editor without digging through logs

### 7. Complete The Custom Uber Inspector

Outcome: the current inspector shell becomes a full authoring surface instead of an initial grouped view.

- [x] replace interim feature toggle behavior with the real authored-state and variant-request path
- [x] add compile-state badges for requested, compiling, active, stale, and failed states
- [x] add property-mode controls for `Static` versus `Animated`
- [x] hide or disable child properties when their parent module is off
- [x] show dependency warnings and dependency-resolution prompts before applying changes
- [x] show module cost hints, animated-property counts, compile timings, and cache status
- [x] add bulk actions such as `Disable All Expensive Features` and `Convert Eligible Properties To Static`

Acceptance criteria:

- the generic uniform list is no longer the primary Uber authoring experience
- the user can see which edits trigger uniform updates versus variant rebuilds
- the custom UI can author the material without dropping back to raw parameter spelunking

### 8. Trim Remaining Runtime Branches And Decide The Default Family Shape

Outcome: the common Uber path stops carrying logic it does not use.

- [x] audit remaining runtime `if` branches in `UberShader.frag` and move module-selection branches to compile time where reasonable — all `_EnableX` / `_*Toggle` / `_ShadingEnabled` / `_DetailEnabled` / `_MatcapEnable` / `_MainColorAdjustToggle` runtime escape hatches removed; feature gating is now compile-time only via `XRENGINE_UBER_DISABLE_*` guards injected by the variant builder from authored state
- [x] finish separating always-on core surface data from optional feature families — core fragment pipeline (normal mapping, base color, alpha, PBR ambient/direct lighting, shadow sampling) is always compiled in; every optional family (stylized, emission, matcap, rim, detail, backface, subsurface, glitter, flipbook, parallax, dissolve, color-adjustments) sits behind an `#ifndef XRENGINE_UBER_DISABLE_*` gate
- [x] prioritize trimming high-surface optional families such as stylized lighting, matcap, parallax, glitter, dissolve, and subsurface from the default path — `ResolveInitialFeatureEnabled` now honors `//@feature(... default=off)` annotations directly instead of reading baked defines, so new hand-authored materials start with every optional family disabled; families are opt-in through `SetUberFeatureEnabled` / editor UI
- [x] decide whether imported materials should stay on a dedicated lite Uber family distinct from hand-authored full variants — **decision: yes, the imported-material family remains distinct**. It is driven by the formal pipeline-axis macro `XRENGINE_UBER_IMPORT_MATERIAL` (recognized by `UberShaderVariantBuilder.PipelineAxisMacros`) which cascades a deterministic, tested disable set at the top of `UberShader.frag`. This gives the variant cache a single canonical "import" shape and keeps GL fragment uniform pressure low for older drivers; see `ImportedUberFragmentVariant_DefinesLeanImportFeatureSet`
- [x] formalize vertex permutations, render passes, and pipeline flavors as explicit variant axes, not implicit side effects (`UberMaterialVariantRequest` + documented contract in the varianting architecture note; contract test `RequestedUberVariantHash_DiffersAcrossFeatureMaskAndPropertyMode`)

Acceptance criteria:

- simple Uber materials compile into materially smaller shaders than feature-rich ones ✓ (every opt-in family is stripped at preprocessor time; see `UberFragmentForward_DisablesOptionalFeatureFamilies`)
- the default family shape is explicit and intentionally minimal ✓ (canonical source has zero unconditional feature disables; variant builder + annotation defaults drive the shape)
- imported-material behavior is deliberate rather than an accidental subset of the full path ✓ (`XRENGINE_UBER_IMPORT_MATERIAL` cascade lives in the shader header under a single #ifdef block; audited by `ImportedUberFragmentVariant_DefinesLeanImportFeatureSet`)

### 9. Add Migration, Telemetry, And Regression Coverage

Outcome: the remaining architecture change is measured and protected against the failure modes already seen in practice.

- [x] capture compile time, link time, uniform count, sampler count, and generated-source size baselines for minimal, common, and maximal Uber variants (CPU-side preparation harness `UberVariantPreparationBaseline_CapturesMinimalCommonMaximalTimings` lands the numbers to `Build/Logs/uber-variant-baselines/`; on-device GPU baselines remain hardware-run gated)
- [ ] profile representative GPU runtime cost after modularization so compile wins are not mistaken for frame wins (hardware-run gated)
- [x] add sustained telemetry counters for variants compiled per session, cache hit rate, average compile ms, average link ms, and failed-compile count
- [x] add migration coverage for imported and legacy Uber materials mapping into the new state model
- [x] add regression coverage for excessive uniform or constant pressure, sampler binding collisions, disabled-module leakage, failed async fallback behavior, and `Static` or `Animated` transitions preserving values and animation hookups

Acceptance criteria:

- the remaining roadmap is driven by measured pressure rather than guesswork
- known black-output and resource-surface failure modes are covered by targeted validation
- migration from legacy or importer-authored Uber materials is deliberate and testable

## Immediate Next Order

Only one workstream remains, intentionally gated:

1. **Hardware-run gated** - run the editor on target hardware, collect real compile/link/source-size numbers through the CPU baseline harness, and collect representative GPU frame-cost baselines for the minimal / common / maximal variants. The CPU harness writes to `Build/Logs/uber-variant-baselines/`; the GPU numbers require a separate profiling pass.

All other items on this roadmap are landed. Do not reopen the old shader-text mutation path, and do not reintroduce runtime `_Enable*` feature toggles.

## Non-Goals

- do not broaden this document into a whole-engine shader optimization audit
- do not make the first full implementation depend on the native editor UI; keep the ImGui path first
- do not keep feature toggles as runtime uniforms just for convenience if they should be compile-time gates
- do not let the current direct source-cloning path become the permanent implementation
- do not duplicate shader UI metadata in a parallel C# data file
- do not build a node-graph shader editor as part of this work
- do not force Vulkan-specific architecture into the OpenGL-first Uber workflow without separate approval

## Key Files

- `Build/CommonAssets/Shaders/Uber/UberShader.frag`
- `Build/CommonAssets/Shaders/Uber/uniforms.glsl`
- `Build/CommonAssets/Shaders/Uber/common.glsl`
- `Build/CommonAssets/Shaders/Uber/details.glsl`
- `Build/CommonAssets/Shaders/Uber/dissolve.glsl`
- `Build/CommonAssets/Shaders/Uber/emission.glsl`
- `Build/CommonAssets/Shaders/Uber/glitter.glsl`
- `Build/CommonAssets/Shaders/Uber/matcap.glsl`
- `Build/CommonAssets/Shaders/Uber/parallax.glsl`
- `Build/CommonAssets/Shaders/Uber/specular.glsl`
- `Build/CommonAssets/Shaders/Uber/subsurface.glsl`
- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderUiManifest.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/XRShader.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderHelper.cs`
- `XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs`
- `XREngine.Editor/AssetEditors/XRMaterialInspector.cs`
- `XREngine.Editor/AssetEditors/XRMaterialInspector.Uber.cs`
- `XREngine.Editor/AssetEditors/XRShaderInspector.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
- `XREngine.UnitTests/Rendering/ShaderUiManifestParserTests.cs`
- `XREngine.UnitTests/Rendering/UberMaterialVariantTests.cs`
- `docs/architecture/rendering/uber-shader-ui-annotations.md`

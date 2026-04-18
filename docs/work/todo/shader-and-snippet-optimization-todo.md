# Shader And Snippet Optimization TODO

Last Updated: 2026-03-28
Status: Active audit and remediation planning.
Scope: shader runtime cost, shader compile/preprocess cost, shader-variant pressure, and shared snippet hot-path cleanup across OpenGL-first rendering paths.

## Current Reality

What is already true:

- the repository has a large shader surface area spread across forward, deferred, AO, terrain, UI/text, compute, and post-process families
- shader source resolution already has a per-asset resolved-source cache in `XRShader`
- several important passes already centralize reusable logic into snippets such as forward lighting, shadow sampling, AO helpers, and material functions
- the forward and deferred pipelines have enough test coverage that targeted changes can be validated incrementally

What remains problematic:

- the remaining matrix-inverse cleanup is mostly limited to non-primary or specialized paths rather than common full-screen/UI/text/renderer hot paths
- forward and deferred lighting still have expensive probe-resolution fallbacks with linear scans over tetrahedra and probes
- shadow filtering uses manual PCF/contact-shadow loops that leave hardware comparison paths and lower-tap alternatives on the table
- shader source preprocessing still performs recursive file reads, directory walks, and snippet discovery work that should be cached more aggressively
- some material variants are still synthesized at runtime from regex-heavy source rewriting instead of explicit or memoized variant paths
- the forward shader family remains highly duplicated across transparency technique variants, increasing compile/load pressure and maintenance cost
- the Uber forward path still carries enough optional feature logic that many materials likely overpay for flexibility they are not using

## Target Outcome

At the end of this work:

- per-draw and per-pass matrix data is precomputed on the CPU instead of inverted repeatedly in shaders
- forward and deferred probe lookup avoid fragment-stage O(`TetraCount`) and O(`ProbeCount`) fallback work in normal rendering
- shadow sampling uses the cheapest acceptable path for each shadow type and quality tier
- shader include and snippet resolution are cached with stable invalidation instead of repeated recursive disk work
- runtime-generated shader variants are either memoized or replaced with explicit variant assets / lower-cost rewrite paths
- duplicated shader families are reduced to a smaller, more intentional permutation set
- the most expensive shaders have a clear profiling and remediation order instead of ad hoc tuning

## Non-Goals

- do not redesign the entire material system just to reduce shader count
- do not force Vulkan-only architecture decisions into the current OpenGL-first render path without separate approval
- do not rewrite every shader family at once; prioritize shared wins and the hottest passes first
- do not trade correctness or visual stability for speculative micro-optimizations without validation

## Phase 0 - Establish Baselines

Outcome: the work is guided by measured hot spots, not only static inspection.

- [ ] capture compile/startup timings for shader source resolution, include/snippet expansion, and runtime variant generation
- [ ] capture GPU timings for AO, deferred light combine, forward lighting, shadowed directional light, TSR, and UI/text passes
- [ ] record shader counts by family and the number of unique forward/deferred variants currently compiled at startup
- [ ] document which passes are CPU-bound by preprocessing versus GPU-bound by execution cost

Acceptance criteria:

- a small baseline table exists for CPU-side shader preparation and GPU-side pass timings
- the first remediation phases are ordered by measured impact rather than intuition alone

## Phase 1 - Remove In-Shader Matrix Inverses

Outcome: expensive matrix inverse work is moved out of vertex/fragment hot paths.

- [x] audit all shaders using `inverse(ProjMatrix)`, `inverse(InverseViewMatrix)`, `inverse(InverseViewMatrix_VTX)`, or equivalent patterns
- [x] add pass uniforms for precomputed `ViewMatrix`, `InverseProjMatrix`, `ViewProjMatrix`, and any other repeated inverses actually needed
- [x] update AO, deferred, skybox, UI, and text shaders to consume the new uniforms instead of recomputing inverses
- [x] remove avoidable per-vertex normal-matrix reconstruction from unlit UI/text paths
- [x] validate parity for OpenGL editor rendering and unit tests covering forward/deferred shader contracts

Acceptance criteria:

- no common full-screen or batched UI/text shader computes matrix inverses in its main hot path
- pass setup code binds the precomputed matrices once per draw/pass

## Phase 2 - Fix Probe Lookup Complexity

Outcome: light probe selection stops doing linear fallback work in fragment shaders during normal rendering.

- [x] confirm where probe-grid misses still trigger `TetraCount` and `ProbeCount` scans in forward and deferred lighting paths
- [x] design a direct cell-to-probe or cell-to-tetra lookup representation that removes fragment-stage global scans
- [x] implement the new lookup path in shared forward-lighting snippets and deferred light combine shaders
- [x] keep an explicit debug-only or validation-only fallback if needed, but remove it from shipping hot paths
- [x] add targeted tests for probe lookup correctness, grid misses, and empty-probe edge cases

Acceptance criteria:

- normal forward and deferred shading never perform full probe/tetra linear scans per fragment
- probe lookup remains visually stable and functionally correct under existing lighting tests

## Phase 3 - Reduce Shadow Sampling Cost ✅

Outcome: directional/local shadow paths use lower-cost comparison and filtering strategies where supported.

- [x] audit current 2D, array, and cube shadow sampling paths by sample count and call frequency
- [x] evaluate `sampler2DShadow` / compare-sampler paths for 2D and array shadow maps
- [x] replace fixed manual PCF loops with hardware PCF or reduced-tap kernels where quality remains acceptable
- [x] make contact-shadow sample counts adaptive by distance, quality tier, or other cheap heuristics
- [x] keep a higher-quality path available behind explicit settings where needed

Implementation notes:

- Tiered manual filtering (simple → tent4 → PCF3x3 → Poisson soft) landed in ShadowSampling.glsl, ForwardLighting.glsl, and all deferred dir/spot/point shaders. Defaults lowered to ShadowSamples=4, ContactShadowSamples=4 in shaders and LightComponent.cs.
- Adaptive contact-shadow stepping via `ResolveContactShadowSampleCount()` reduces ray-march steps by depth.
- Compare-sampler plumbing evaluated and wired: `XRTexture2D.EnableComparison`/`CompareFunc` properties added and applied in both `GLTexture2D.SetParameters()` and `GLTexture2DArray.PushData()`. API conversion (`ETextureCompareFunc` → GL enum) added to `GLObjectBase`.
- Compare-sampler **activation** on directional shadow map textures is deferred: the Enhanced deferred shader (`DeferredLightingDir_Enhanced.fs`) performs PCSS blocker search which requires raw depth reads via `texture(sampler2D, ...)`. With `GL_TEXTURE_COMPARE_MODE` set, `sampler2DShadow` does not support `texelFetch` or raw depth access in GLSL, so PCSS would break. Activation requires either (a) GL sampler object per-pass overrides (GLSampler.LinkData pipeline), or (b) texture views (`GL_ARB_texture_view`) to vend a non-compare view for blocker search passes.

Acceptance criteria:

- default shadowed forward lighting uses fewer manual texture fetches in the common case
- quality/performance tradeoffs are explicit rather than hard-coded into one expensive path

## Phase 4 - Cache Shader Source Resolution Aggressively

Outcome: include/snippet expansion stops paying repeated disk and search costs after the first resolve.

- [x] add cached include expansion keyed by normalized path plus invalidation data such as last-write time
- [x] build a reusable snippet index instead of calling recursive `Directory.EnumerateFiles(..., SearchOption.AllDirectories)` during resolution
- [x] memoize snippet file contents and resolved snippet expansions where safe
- [x] preserve recursive-include detection and error reporting while reducing allocations and repeated file I/O
- [ ] benchmark shader startup/load paths before and after the cache changes

Acceptance criteria:

- repeated shader resolves do not perform recursive snippet directory scans
- include/snippet-heavy shaders show a measurable reduction in CPU-side resolve time

Implementation notes:

- `ShaderSourceResolver` now caches include expansion by normalized path and validates cached entries against dependency file stamps before reuse.
- shader-root file lookup and snippet discovery are indexed once per root and invalidated when directory timestamps change, so repeated resolves avoid recursive tree scans.
- snippet file contents are memoized in the shared text cache, and top-level snippet expansion results are memoized per expanded source plus registered-snippet version.
- `XRShader` now validates include/snippet dependency stamps before returning its per-asset resolved-source cache, preventing stale source after nested file edits.
- `ShaderSourcePreprocessor` and `ShaderSnippets` now route through the shared resolver so OpenGL/Vulkan preprocess paths use the same cache and invalidation behavior.

## Phase 5 - Tame Runtime Variant Generation

Outcome: runtime-created shader variants stop scaling linearly with regex-heavy source rewriting work.

- [x] hash and memoize generated forward depth-normal variants by source content
- [x] evaluate whether explicit companion shaders should replace regex-based rewriting for the most common variant families
- [x] keep any remaining source rewriting limited to narrow, deterministic transformations
- [ ] measure startup savings for scenes with many distinct forward materials

Acceptance criteria:

- the same material source is not repeatedly rewritten into identical variants
- variant generation cost is bounded and observable

Implementation notes:

- `ForwardDepthNormalVariantFactory` now memoizes fallback depth-normal rewrite results by hashed source-content buckets and caches both success and failure outcomes, so custom forward shaders do not repeat the regex-heavy rewrite path.
- the common named forward families continue to prefer `ShaderHelper.GetDepthNormalPrePassForwardVariant(...)`, and `ShaderHelper.CreateDefinedShaderVariant(...)` now memoizes define-injected variant source text by define name plus source content.
- the remaining runtime rewrite path is intentionally limited to custom or unmapped forward fragment sources, and its transformation stays narrow: strip forward-lighting outputs/snippets, preserve pre-light setup, and replace the lighting write with encoded normal output.

## Phase 6 - Reduce Forward Shader Duplication

Outcome: the forward family has a smaller, more intentional permutation surface.

- [x] inventory duplicated forward fragment files by feature and transparency technique
- [x] decide which differences should remain separate shaders versus preprocessor permutations or specialization flags
- [x] collapse the worst duplication clusters first, especially lit/unlit textured forward families
- [x] ensure transparent technique selection does not explode the number of near-identical source files unnecessarily

Acceptance criteria:

- the forward shader family count is materially lower without losing needed technique separation
- compile/load pressure drops for the common forward path

Implementation notes:

- the common textured forward families now generate `WeightedOit`, `PerPixelLinkedList`, and `DepthPeel` variants from the base forward shader source via explicit defines in `ShaderHelper`, instead of loading separate transparency-only shader files for those families.
- the base textured forward shaders now inline a small output-policy block gated by `XRENGINE_FORWARD_WEIGHTED_OIT`, `XRENGINE_FORWARD_PPLL`, and `XRENGINE_FORWARD_DEPTH_PEEL`, so the same fragment source handles opaque and transparent forward techniques without duplicating the lighting/material body.
- redundant common textured transparency shader files were removed for lit textured, lit normal/spec/alpha combinations, silhouette-POM, unlit textured, stereo unlit textured, and unlit alpha-textured families; the colored and decal paths remain separate for now where their migration payoff was lower.

## Phase 7 - Split Or Trim The Uber Path

Outcome: the Uber shader no longer forces every material through the same feature-heavy fragment path.

Audit status:

- the current repo state only partially implements this phase
- `UberShader.frag` now has a trimmed MVP-style compile-time path and `uniforms.glsl` gates several optional uniform blocks behind feature defines
- imported/default Uber materials still bind the same single fragment shader, and there is not yet a distinct "full rich path" family kept for feature-heavy materials versus a separate simpler material family for common cases
- because of that, the acceptance criteria are not fully met yet: the repo has trimming work, but not a complete family split with a reserved full path

- [ ] profile the Uber shader with representative simple and feature-heavy materials
- [ ] identify which features should become compile-time families instead of runtime branches
- [ ] carve out simpler material families for common cases that do not need parallax, glitter, matcap, dissolve, or similar features
- [ ] keep the full Uber path for materials that genuinely need it

Acceptance criteria:

- simple materials can bind a materially cheaper shader path than the full Uber fragment shader
- the remaining Uber path is reserved for truly feature-rich materials

## Phase 8 - Review Secondary Passes

Outcome: expensive secondary passes are optimized only where profiling justifies it.

- [x] review TSR texture-fetch count and simplify only if it remains a measured bottleneck
- [x] replace transcendental-heavy UI blur paths with separable or preweighted kernels where practical
- [x] review skybox, debug, and utility shaders for any remaining repeated inverse or reconstruction work

Acceptance criteria:

- secondary-pass work is driven by measured benefit
- low-value rewrites are avoided

Implementation notes:

- `GrabpassGaussian.frag` no longer evaluates gaussian weights or radial directions with `exp`, `sqrt`, `cos`, and `sin` in nested per-fragment loops; it now uses a truncated preweighted fixed-tap kernel with bounded sample count.
- the shared skybox vertex shader now reconstructs and rotates the world ray once per fullscreen-triangle vertex, and the equirectangular, octahedral, cubemap, cubemap-array, gradient, and dynamic skybox fragment shaders consume the interpolated `FragWorldDir` instead of repeating inverse-matrix reconstruction per pixel.
- the inline skybox fallback shader sources in `SkyboxComponent.cs` were updated to match the on-disk shader contract so runtime fallback behavior stays aligned.
- dynamic skybox stars and clouds now use a pole-safe octahedral direction mapping, and OpenGL mesh renderers explicitly invalidate cached programs when a live material swap changes the active skybox mode at runtime.
- TSR was reviewed and intentionally left unchanged in this pass: without a fresh measured bottleneck, the current `TemporalSuperResolution.fs` path avoids speculative fetch-count churn while the higher-confidence blur and skybox wins land first.

## Key Files

- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderSourceResolver.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/XRShader.cs`
- `XREngine.Runtime.Rendering/Shaders/ForwardDepthNormalVariantFactory.cs`
- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- `Build/CommonAssets/Shaders/Snippets/ShadowSampling.glsl`
- `Build/CommonAssets/Shaders/Scene3D/AOCommon.glsl`
- `Build/CommonAssets/Shaders/Scene3D/GTAOGen.fs`
- `Build/CommonAssets/Shaders/Scene3D/HBAOPlusGen.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs`
- `Build/CommonAssets/Shaders/Uber/UberShader.frag`
- `Build/CommonAssets/Shaders/Common/UITextBatched.vs`
- `Build/CommonAssets/Shaders/Common/Text.vs`
- `Build/CommonAssets/Shaders/Common/UIQuadBatched.vs`
- `Build/CommonAssets/Shaders/UI/GrabpassGaussian.frag`

## Suggested Next Step

The highest-confidence first implementation pass is Phase 1 plus the Phase 4 caching work:

1. move repeated inverse matrices out of shaders and into pass uniforms
2. add include/snippet caching so startup and variant preparation stop doing unnecessary recursive disk work

Those two changes have the best chance of producing measurable wins without forcing a risky render-architecture rewrite.

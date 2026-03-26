# Shader And Snippet Optimization TODO

Last Updated: 2026-03-25
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

- [ ] add cached include expansion keyed by normalized path plus invalidation data such as last-write time
- [ ] build a reusable snippet index instead of calling recursive `Directory.EnumerateFiles(..., SearchOption.AllDirectories)` during resolution
- [ ] memoize snippet file contents and resolved snippet expansions where safe
- [ ] preserve recursive-include detection and error reporting while reducing allocations and repeated file I/O
- [ ] benchmark shader startup/load paths before and after the cache changes

Acceptance criteria:

- repeated shader resolves do not perform recursive snippet directory scans
- include/snippet-heavy shaders show a measurable reduction in CPU-side resolve time

## Phase 5 - Tame Runtime Variant Generation

Outcome: runtime-created shader variants stop scaling linearly with regex-heavy source rewriting work.

- [ ] hash and memoize generated forward depth-normal variants by source content
- [ ] evaluate whether explicit companion shaders should replace regex-based rewriting for the most common variant families
- [ ] keep any remaining source rewriting limited to narrow, deterministic transformations
- [ ] measure startup savings for scenes with many distinct forward materials

Acceptance criteria:

- the same material source is not repeatedly rewritten into identical variants
- variant generation cost is bounded and observable

## Phase 6 - Reduce Forward Shader Duplication

Outcome: the forward family has a smaller, more intentional permutation surface.

- [ ] inventory duplicated forward fragment files by feature and transparency technique
- [ ] decide which differences should remain separate shaders versus preprocessor permutations or specialization flags
- [ ] collapse the worst duplication clusters first, especially lit/unlit textured forward families
- [ ] ensure transparent technique selection does not explode the number of near-identical source files unnecessarily

Acceptance criteria:

- the forward shader family count is materially lower without losing needed technique separation
- compile/load pressure drops for the common forward path

## Phase 7 - Split Or Trim The Uber Path

Outcome: the Uber shader no longer forces every material through the same feature-heavy fragment path.

- [ ] profile the Uber shader with representative simple and feature-heavy materials
- [ ] identify which features should become compile-time families instead of runtime branches
- [ ] carve out simpler material families for common cases that do not need parallax, glitter, matcap, dissolve, or similar features
- [ ] keep the full Uber path for materials that genuinely need it

Acceptance criteria:

- simple materials can bind a materially cheaper shader path than the full Uber fragment shader
- the remaining Uber path is reserved for truly feature-rich materials

## Phase 8 - Review Secondary Passes

Outcome: expensive secondary passes are optimized only where profiling justifies it.

- [ ] review TSR texture-fetch count and simplify only if it remains a measured bottleneck
- [ ] replace transcendental-heavy UI blur paths with separable or preweighted kernels where practical
- [ ] review skybox, debug, and utility shaders for any remaining repeated inverse or reconstruction work

Acceptance criteria:

- secondary-pass work is driven by measured benefit
- low-value rewrites are avoided

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
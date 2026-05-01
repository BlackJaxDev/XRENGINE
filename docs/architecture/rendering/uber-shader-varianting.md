# Uber Shader Varianting

The Uber material path no longer treats the editor as the source of truth for shader text mutations. `XRMaterial` now owns the authored Uber state, prepares requested variants from that state, and swaps in generated fragment variants once preparation completes.

## Runtime Model

- `XRMaterial.UberAuthoredState` stores the serialized author intent for feature enablement and per-property mode selection.
- `XRMaterial.RequestedUberVariant` captures the latest prepared request snapshot.
- `XRMaterial.ActiveUberVariant` captures the fragment variant currently bound to the material.
- `XRMaterial.UberVariantStatus` exposes preparation state, cache hit/miss, counts, and failure text for the editor.

The first time an Uber material is inspected or rebuilt, `EnsureUberStateInitialized()` infers missing authored state from the current fragment shader source and the parsed `ShaderUiManifest`. If a legacy material is carrying a previously generated Uber fragment as its serialized active shader, the material now reloads the canonical `UberShader.frag` source from disk before inference so migration starts from authoritative source rather than baked defines.

## Variant Generation

`RequestUberVariantRebuild()` performs three steps:

1. Capture the canonical fragment shader that future variants should be derived from.
2. Debounce rapid UI edits, supersede stale requests for the same material, and snapshot the authored state into a structured `UberMaterialVariantRequest`.
3. Prepare a generated fragment source on a worker thread and swap it onto the material when ready.

The generator strips previously recognized Uber feature defines, reapplies the current feature/pipeline macro set, and turns `Static` properties into compile-time `#define` literals. `Animated` properties remain uniforms and continue to update through the regular material parameter path. The request key now includes enabled features, animated/static property membership, static literals, render pass, vertex-permutation hash, pipeline macros, and resolved source version.

## Property Modes

- `Static`: the current material parameter value is embedded into the generated fragment source as a GLSL literal. Editing the value requires a variant rebuild.
- `Animated`: the property stays as a uniform and can be driven live without regenerating the shader.
- Samplers always remain runtime-bound.

Even when a property is compiled out because its feature is disabled or because it was converted to a static literal, the corresponding material parameter is preserved on the `XRMaterial`. That keeps authored values available for later re-enable or mode changes.

## Editor Contract

The ImGui Uber inspector now edits `XRMaterial` state instead of patching shader source directly.

- Feature toggles call `SetUberFeatureEnabled(...)` and batch a `RequestUberVariantRebuild()`.
- Feature toggles can surface dependency-resolution prompts before the authored state is changed when annotations declare dependencies or conflicts.
- Property mode switches call `SetUberPropertyMode(...)` and rebuild when the active variant shape changes.
- Static property value edits request a rebuild; animated edits stay live.
- The inspector displays requested hash, active hash, preparation stage, stale-state badges, cache status, generated-source size, and failure text from `UberVariantStatus`, plus session telemetry from `UberShaderVariantTelemetry`.
- Generated fragment variants embed a stable `variant-hash` comment, and the OpenGL backend uses that hash to report real backend compile-stage, link-stage, compile milliseconds, link milliseconds, and failure text back into the inspector.
- The inspector also keeps material-side handoff timing separate from worker-thread preparation timing, so variant generation cost and final swap cost are visible independently.

## Render-Thread Boundary

The allowed render-thread responsibilities remain intentionally small:

- backend-facing shader or program compile work when required by the renderer
- final shader/program adoption after background preparation has already completed

Renderer-specific compile and link timing is tracked separately from worker-thread variant preparation. That keeps backend telemetry tied to the real driver work without moving request building, annotation parsing, or generated-source emission back onto the render thread.

For the GPU indirect combined-program path, the renderer now treats shader-set changes as a cache invalidation boundary and keeps the last linked combined program active until the replacement backend program reports linked for the current renderer. That hardens Uber variant swaps without collapsing back to shader-text mutation or forced synchronous relinking.

Manifest parsing, annotation validation, request coalescing, source hashing, static-literal formatting, canonical-source recovery, and generated-source emission stay on the CPU-side worker or material-management path instead of draw-time code.

## Regression Coverage

`XREngine.UnitTests/Rendering/UberMaterialVariantTests.cs` covers:

- inferring authored Uber state from fragment source
- lowering static properties into generated defines
- keeping animated properties as uniforms during variant rebuilds
- capturing the current runtime value when a property moves from `Animated` back to `Static`
- migrating legacy generated-fragment materials back to the canonical Uber source before state inference
- excluding samplers owned by disabled features from `SamplerCount` and emitting the corresponding `XRENGINE_UBER_DISABLE_*` guard define
- restoring the canonical Uber source when a material was serialized with a previously generated variant (safe fallback for legacy or failed-prep states)
- preserving authored values across `Static` -> `Animated` -> `Static` -> `Animated` round trips including the restored uniform hookup
- separating variant hashes across feature-mask, property-mode, and static-literal changes so cache keys stay unambiguous
- aggregating preparation, adoption, and backend compile/link timing through `UberShaderVariantTelemetry`
- tracking backend compile/link stage transitions and failure reasons keyed by `variant-hash`

An explicit baseline harness test (`UberVariantPreparationBaseline_CapturesMinimalCommonMaximalTimings`) is marked `[Explicit]` so it only runs on demand. It produces a CSV under `Build/Logs/uber-variant-baselines/` with per-variant preparation milliseconds, adoption milliseconds, generated-source size, animated uniform count, and sampler count, covering a minimal / common / maximal feature mask. The harness is CPU-side only; on-device GPU cost capture is a separate workstream that requires hardware validation.

## Variant Axes

The authoritative Uber variant key is `UberMaterialVariantRequest.VariantHash`. It is computed from the following explicit axes:

- `EnabledFeatures` - ordered list of enabled `//@feature(...)` ids
- `AnimatedProperties` - ordered list of animated property names
- `StaticProperties` - ordered list of `name=literal` pairs for static properties
- `PipelineMacros` - ordered list of pipeline-flavor defines detected in the canonical source
- `RenderPass` - material render pass id
- `SourceVersion` - stable hash of the resolved canonical shader source text
- `VertexPermutationHash` - hash of the vertex-stage permutation the fragment variant must pair with

These axes are not implicit side effects of shader-text state. `UberShaderVariantBuilder.BuildRequest(...)` serializes each axis into the hash, and the generated fragment variant embeds `// variant-hash: 0x...` so backend telemetry can correlate driver compile/link events with the exact authored state. Any new axis must be added to both `UberMaterialVariantRequest` and `ComputeVariantHash(...)` in the same change to keep cache keys unambiguous.

## Program Cache Layering

Program caching is split into two explicit layers that share the variant hash as their join key:

1. Generated fragment shaders are cached inside `UberShaderVariantBuilder` keyed by `(VariantHash, SourceVersion, SourcePath)`. Identical authored state reuses the same `XRShader` instance without regenerating source.
2. Linked GL programs are cached by `HybridRenderingManager` keyed by `(materialId, rendererKey, ShaderStateRevision)`. `ShaderStateRevision` bumps whenever `XRMaterial.SetShader(...)` swaps in a new fragment variant, so a variant change invalidates the program cache entry without sharing linked-program state across materials that happen to reuse the same fragment shader.

This split is intentional. Shader instances are idempotent and safely sharable across materials, but linked programs carry material-specific uniform bindings and renderer-specific state and must not be shared between materials even if their fragment shaders match. The layering avoids stale uniform layouts while still letting the shader cache do the expensive work once per unique variant.

During an Uber variant swap, the program cache keeps the previously linked program as the last-known-good fallback until the replacement program reports `IsLinked` for the current renderer (`HybridRenderingManager.IsProgramReadyForCurrentRenderer`). That gives atomic swap semantics from the draw-path perspective: the old program stays live until the new program has drained its own link/compile work, at which point the cache replaces the entry in a single lookup step.

## Sampler Layout Policy

Uber variants use **stable sparse sampler bindings per module**. Each sampler keeps its authored `layout(binding=N)` slot even when its owning feature is disabled. Guard defines only compile out the sampler's *declaration*, not its binding slot reservation.

Rationale:

- toggling one module never forces sibling modules' samplers to recompile against different texture units
- the material binding path can safely pre-bind textures for all authored samplers without reasoning about variant-specific repacking
- backend texture-unit reuse is still bounded: disabled-module samplers contribute nothing to `SamplerCount`, so the inspector accurately shows which units are live
- adding a new optional module only requires reserving one new sparse slot, not reflowing existing slots

This is the opposite of a "dense repack per variant" strategy, which would minimise the smallest-variant sampler count at the cost of churning neighbour modules' slot assignments on every toggle. Dense repack is not worth the added binding-path complexity for the sampler counts the Uber shader actually uses today.

If a future Uber feature family pushes the combined sampler count past the driver-reported `GL_MAX_TEXTURE_IMAGE_UNITS`, the policy should be revisited with a written proposal before changing the layout strategy.

## Feature Gating And Family Shape

Feature gating is **compile-time only**. There are no runtime `_Enable*` / `_*Toggle` uniforms that can turn a feature on or off per draw. A feature is either compiled into the variant or it is not; that decision is owned by the material's authored state and materialized by `UberShaderVariantBuilder` as an `XRENGINE_UBER_DISABLE_<FAMILY>` define injected ahead of the canonical source.

The canonical `UberShader.frag` source contains **no unconditional** feature disables. Every optional family sits behind an `#ifndef XRENGINE_UBER_DISABLE_*` guard. Default feature enablement for newly instantiated hand-authored materials is driven by the `//@feature(id="...", default=on|off)` annotations parsed into `ShaderUiManifest`; `XRMaterial.ResolveInitialFeatureEnabled` honors `feature.DefaultEnabled` directly. This means the annotation surface is the source of truth for "what does a fresh Uber material start with enabled," and the shader text never silently overrides it.

There is **one canonical family shape** for hand-authored and imported materials.

Fresh hand-authored materials start with every optional feature at its annotation default (typically `default=off`) and opt in through the editor. Imported glTF / FBX materials author the same feature-state surface explicitly, so the variant builder trims the compiled shape from material state rather than from a separate import-only pipeline axis.

Keeping imported materials on the same canonical family means two materials that produce the same authored-state mask share cache entries. OpenGL uniform pressure is controlled by generated per-material disables plus the forward lighting SSBO path, not by a second shader-source cascade.

### Rules

- Do not reintroduce unconditional `#define XRENGINE_UBER_DISABLE_*` into the canonical source. All hand-authored feature state flows through authored state → variant builder.
- Do not reintroduce `_Enable<Family>` / `_<Family>Toggle` runtime uniforms. They were redundant with the compile-time guards and actively undermined the "authored state is the sole source of truth" rule.
- Sub-option selectors inside an already-compiled feature (e.g. `_MainHueShiftToggle`, `_LightingMode`, `_MainAlphaMaskMode`, `_AlphaForceOpaque`) are not feature toggles — they are content/mode controls — and may remain runtime uniforms.
- Imported-material behavior must flow through authored feature state and generated variants, not through import-only shader-source defines.

# Uber Shader Materials

Last updated: 2026-04-28

XRENGINE's Uber shader material path is a modular, annotation-driven authoring system for forward-rendered materials. It keeps the editor-facing material controls in shader source, stores material intent as structured `XRMaterial` state, and compiles only the feature families and animated properties a material actually needs.

The current implementation is stable for the ImGui editor path. The remaining profiling work is hardware validation: representative GPU frame-cost baselines for minimal, common, and maximal variants should be captured on target devices when those runs are available.

## What The Feature Provides

- Shader-source annotations define the curated material inspector surface.
- `XRMaterial` owns authored Uber state instead of mutating fragment source as the source of truth.
- Feature families are compile-time variant decisions, not runtime `_Enable*` uniforms.
- Properties default to `Static` compile-time literals and can be promoted to `Animated` uniforms when live updates are needed.
- Disabled modules preserve authored values but contribute no compiled code, live uniforms, or live sampler declarations.
- Variant requests prepare CPU-side work off the render thread, then adopt the compiled result atomically when ready.
- The last-known-good program remains live while replacement variants compile or link.
- The inspector exposes requested and active variant hashes, cache status, timings, counts, and failure text.

## Authoring Model

The canonical authoring surface lives in `Build/CommonAssets/Shaders/Uber/`.

Use `//@feature(...)` annotations to describe optional feature families, their default state, cost, dependencies, and conflicts. Use `//@property(...)` and `//@tooltip(...)` annotations to expose editable controls in the custom material inspector. Shader authors should update annotations in the same change as any shader property rename, addition, or feature split.

The ImGui material inspector reads this manifest through `XRShader` and edits the material's structured Uber state. It does not patch shader source directly for normal feature and property edits. Missing feature or user-facing property annotations are surfaced as validation warnings in tooling instead of being silently accepted.

For the annotation contract and directive reference, see [Uber Shader UI Annotations](../architecture/rendering/uber-shader-ui-annotations.md).

## Material State

`XRMaterial` separates three pieces of Uber state:

- `UberAuthoredState`: serialized author intent for enabled features, property modes, and preserved constant values.
- `RequestedUberVariant`: the latest prepared request snapshot that the material wants to use.
- `ActiveUberVariant`: the generated variant currently bound to the material.

This separation lets the editor represent a desired future material shape while the renderer is still using the previous known-good program. Imported and legacy materials infer missing authored state on load. If a material was serialized with a generated Uber fragment, migration recovers the canonical `UberShader.frag` source before building authored state.

Material-level state changes must continue to use the `XRBase.SetField(...)` mutation path so change notifications and invalidation stay intact.

## Feature Families

The default hand-authored material shape is intentionally lean. Optional families are disabled by annotation default unless the author opts in through the inspector or calls the material feature API.

The always-on core covers the base fragment surface: normal mapping, base color, alpha, PBR ambient and direct lighting, and shadow sampling. Optional families sit behind `XRENGINE_UBER_DISABLE_*` compile-time guards, including stylized lighting, emission, matcap, rim, detail, backface, subsurface, glitter, flipbook, parallax, dissolve, and color adjustments.

Imported materials use the same canonical Uber family as hand-authored materials. Importers author feature state directly, and the generated variant trims unused feature families from that state.

Do not reintroduce runtime `_Enable<Family>` or `_<Family>Toggle` uniforms for feature membership. Sub-option selectors inside an already compiled feature can remain runtime controls when they describe content mode rather than feature inclusion.

## Property Modes

Uber properties have two specialization modes:

- `Static`: the current value is emitted as a deterministic GLSL compile-time literal. Editing the value changes the variant shape and requests a rebuild.
- `Animated`: the property remains a runtime uniform and can update without rebuilding when feature membership is unchanged.

Samplers remain runtime-bound resources. When a feature is disabled, its sampler declarations are compiled out and excluded from live sampler counts, but authored texture values stay on the material for later re-enable.

When a property moves from `Animated` back to `Static`, the current runtime value is captured so authored values survive round trips. Literal generation uses invariant formatting and handles invalid values loudly instead of allowing ambiguous shader source.

## Variant Pipeline

Uber material edits feed a structured `UberMaterialVariantRequest` rather than direct source cloning. The request key includes enabled features, animated-property membership, static literals, render pass, vertex permutation hash, pipeline macros, and source version.

Rapid edits are debounced and stale requests are superseded so the system converges on the final authored state. CPU-side work such as manifest validation, dependency checks, property-mode diffing, migration inference, variant hashing, literal formatting, generated-source emission, request coalescing, and compile-log parsing stays off render-time paths.

The render thread is responsible only for unavoidable backend API work and final adoption after preparation completes. Renderer-specific compile and link timing is tracked separately from worker-thread preparation and material-side handoff timing.

For cache layering, variant axes, sampler layout policy, and render-thread boundaries, see [Uber Shader Varianting](../architecture/rendering/uber-shader-varianting.md).

## Inspector Workflow

The ImGui Uber inspector is the primary authoring experience:

- Toggle feature families from the grouped manifest-driven UI.
- Resolve declared dependencies or conflicts before applying feature changes.
- Switch eligible properties between `Static` and `Animated`.
- Use bulk actions to disable expensive features or convert eligible properties to static literals.
- Review requested, compiling, active, stale, and failed states directly in the material panel.
- Inspect generated-source size, animated uniform count, sampler count, cache hit or miss state, compile and link timings, and failure reason.

The generic uniform list remains useful for lower-level inspection, but it is not the canonical Uber authoring surface.

## Diagnostics And Validation

Regression coverage lives primarily in `XREngine.UnitTests/Rendering/UberMaterialVariantTests.cs` and `XREngine.UnitTests/Rendering/ShaderUiManifestParserTests.cs`. The suite covers authored-state inference, legacy migration, static literal lowering, animated uniform preservation, feature-mask hashing, disabled-module sampler leakage, safe fallback restoration, backend compile/link telemetry, and static/animated round-trip value preservation.

The explicit CPU-side baseline harness `UberVariantPreparationBaseline_CapturesMinimalCommonMaximalTimings` writes CSV output under `Build/Logs/uber-variant-baselines/`. It records preparation milliseconds, adoption milliseconds, generated-source size, animated uniform count, and sampler count for minimal, common, and maximal variants.

GPU frame-cost baselines are intentionally captured outside the unit-test harness because they require representative target hardware and editor profiling runs.

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

## Related Documentation

- [Uber Shader Varianting](../architecture/rendering/uber-shader-varianting.md)
- [Uber Shader UI Annotations](../architecture/rendering/uber-shader-ui-annotations.md)
- [ImGui Shader Editor](shader-editor.md)

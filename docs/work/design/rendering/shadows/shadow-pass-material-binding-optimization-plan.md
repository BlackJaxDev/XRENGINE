# Shadow Pass Material Binding Optimization Plan

> Status: **active design**

## Goal

Reduce CPU time spent in `GLMeshRenderer.Render.SetMaterialUniforms`, with the largest gains targeted at the `Lights3DCollection.RenderShadowMaps` path, while preserving correct shadow casting for masked and custom-shadow materials.

## Motivation

Recent profiling shows a disproportionate amount of shadow-pass CPU time being spent in material uniform setup rather than in the actual draw submission.

The current OpenGL shadow path does select a `ShadowCasterVariant` during shadow rendering, but that variant still inherits most of the source material binding work:

- source material parameters
- source material textures
- source material `SettingUniforms` callback forwarding
- generic fallback-sampler and empty-binding diagnostics

That means many shadow draws still pay nearly the same CPU-side material setup cost as forward rendering, even when the shadow shader only needs a minimal depth-only binding set.

## Current State Summary

### Effective shadow material selection

During shadow rendering, `GLMeshRenderer.GetRenderMaterial()` switches to `ShadowCasterVariant` when the render state reports `ShadowPass = true`.

Current relevant path:

- `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Rendering.cs`
- `XREngine.Runtime.Rendering/Shaders/ShadowCasterVariantFactory.cs`

### Current shadow variant behavior

`ShadowCasterVariantFactory` currently creates a material variant that:

- swaps the fragment shader to a shadow-caster variant
- preserves the source material parameter array
- preserves the source material texture list
- forwards `sourceMaterial.OnSettingUniforms(program)` into the variant
- disables engine-uniform requirements

This is correct for masked casters and any material whose shadow output depends on source material state, but it is unnecessarily expensive for ordinary opaque casters.

### Current OpenGL binding behavior

`GLMaterial.SetUniforms()` currently performs the same broad binding procedure for shadow and non-shadow draws:

1. apply render parameters
2. iterate all `ShaderVar` parameters
3. bind all textures in `Data.Textures`
4. run material `SettingUniforms`
5. apply scoped pipeline bindings
6. bind fallback samplers for any remaining active sampler uniforms
7. emit empty-binding diagnostics

This is robust, but it is not specialized for the shadow pass.

## Problem Statement

The engine currently lacks a cheap shadow-material binding path for the common case where a caster is:

- opaque
- unmasked
- not dependent on material-specific shadow uniforms
- not dependent on material-specific shadow textures

As a result, shadow rendering pays for material flexibility even when the active shadow shader does not need that flexibility.

## Design Objectives

1. Keep masked and custom-shadow materials correct.
2. Make ordinary opaque shadow casters cheap.
3. Avoid per-draw work that can be decided once per material or once per material-program pair.
4. Preserve the current generic material path for non-shadow passes.
5. Keep diagnostics available, but avoid paying their full cost in hot shadow paths unless explicitly enabled.

## Non-Goals

This plan does not attempt to:

- redesign the full material system
- change Vulkan behavior as part of phase 1
- alter shadow-map generation order or light traversal
- change visual shadow bias or filtering behavior
- introduce bindless texturing as a prerequisite

## Proposed Design

### Phase 1: Opaque shadow fast path

Introduce a minimal shadow material path for common opaque casters.

#### Summary

If the source material does not require alpha masking or custom shadow bindings, shadow rendering should use a shared or cheaply cached minimal shadow caster material instead of the full `ShadowCasterVariant`.

#### Eligibility rules

A material is eligible for the opaque fast path when all of the following are true:

- `RenderPass` is compatible with opaque or masked-forward usage, but the material is not actively using masked discard in the shadow shader
- `TransparencyMode` is effectively opaque for shadow-caster purposes
- no shadow-specific texture sampling is required
- no shadow-specific custom uniform callback is required

Initial conservative rule set:

- opaque materials with no alpha-cutoff dependency use the fast path
- anything masked, alpha-tested, or custom-shadow-bound continues using the current shadow variant path

#### Expected effect

This removes most of the following per shadow draw for common opaque casters:

- parameter iteration
- texture iteration and sampler binding
- source-material callback forwarding
- fallback sampler binding

This should produce the largest immediate CPU reduction in `RenderShadowMaps`.

### Phase 2: Separate shadow-specific material binding intent

Decouple generic material callbacks from shadow-only callback needs.

#### Problem

The current shadow variant forwards `sourceMaterial.OnSettingUniforms(program)` into the shadow material. That makes the shadow pass pay for every custom forward-material binding path, even when the shadow shader does not consume those uniforms.

#### Proposal

Add an explicit shadow-binding opt-in at the material level. Viable shapes include:

- a dedicated `SettingShadowUniforms` event
- a boolean capability such as `RequiresShadowMaterialBindings`
- a small enum describing shadow binding mode

Recommended direction:

- add a shadow-specific callback/event
- only attach it for materials that genuinely need it

This keeps masked foliage, terrain, impostors, or other custom casters correct without making every opaque material pay the same cost.

### Phase 3: Shadow binding-plan cache per material-program pair

Precompute the subset of shadow bindings that are actually relevant to a linked shadow program.

#### Summary

For any material that still uses the full shadow variant path, cache a compact shadow binding plan containing:

- active parameter entries that exist in the shadow program
- active sampler bindings actually referenced by the shadow program
- pre-resolved uniform locations or cached name lookups where useful
- whether fallback sampler binding can be skipped safely

#### Why this matters

The current path still iterates all parameters and all textures every draw. For masked casters, the engine usually needs only a small subset of those bindings.

#### Cache invalidation inputs

Invalidate the cached shadow binding plan when any of the following change:

- source material shaders
- source material parameter array shape
- source material texture list shape
- linked GL program identity
- shader reload or relink

#### Expected effect

This phase reduces per-draw CPU cost without changing material semantics.

### Phase 4: Cheap diagnostics mode for hot shadow passes

Retain diagnostics, but avoid their full hot-path cost during normal shadow rendering.

#### Current expensive safety work

The generic binding path currently performs:

- binding-batch bookkeeping
- fallback sampler binding for unbound active samplers
- empty-binding warning checks

These are useful during shader development, but the shadow pass should avoid them when the engine already knows the chosen shadow path is texture-free.

#### Proposal

For shadow-pass materials that declare no sampler use:

- skip fallback sampler binding entirely
- skip empty-binding diagnostics unless an explicit render-diagnostics setting enables them

For shadow-pass materials that do use samplers:

- preserve diagnostics behind the existing debug-oriented path
- keep the default runtime behavior conservative until the binding-plan cache proves complete

## Detailed Behavioral Rules

### Opaque fast path

Behavior:

- use a minimal shadow material variant with no copied parameters, no copied textures, and no forwarded generic material callbacks
- preserve render-state requirements needed for shadow correctness, including current culling policy

Expected supported materials:

- ordinary opaque meshes
- static environment geometry
- most non-masked rigid props

### Full shadow variant path

Behavior:

- preserve current semantics for materials that depend on alpha testing or shadow-specific callbacks
- remain the fallback for anything the engine cannot yet prove safe for the fast path

Expected supported materials:

- foliage
- cutout decals used as shadow casters
- terrain layers using alpha discard in the shadow path
- any future material with explicit shadow uniform needs

## Implementation Plan

### Step 1: Instrument the current hot path

Before changing behavior, split `GLMaterial.SetUniforms()` into sub-scopes for:

- parameter upload
- texture binding
- `Data.OnSettingUniforms(...)`
- scoped pipeline bindings
- fallback sampler binding
- warning/diagnostic work

This will confirm which portions dominate in the current shadow pass.

### Step 2: Add minimal shadow caster material selection

Extend shadow material selection so `GLMeshRenderer.GetRenderMaterial()` can choose between:

- minimal opaque shadow material
- current full `ShadowCasterVariant`

This decision should be cheap and based on stable material properties.

### Step 3: Add explicit shadow-binding capability

Introduce a shadow-specific callback or capability marker and stop forwarding generic material callbacks into shadow variants by default.

### Step 4: Add cached shadow binding plans

For remaining full shadow variants, build and cache the actual shadow binding subset.

### Step 5: Reduce shadow-pass diagnostic overhead

Skip generic fallback and warning work for proven texture-free shadow materials, while preserving an opt-in debugging route.

## Alternatives Considered

### Alternative A: Keep the current shadow variant behavior and only optimize GL lookup internals

Rejected as the primary strategy.

Reason:

- uniform locations are already cached
- the larger cost appears to be repeated per-draw binding work, not only uniform-name lookup

### Alternative B: Make all shadow casters use the minimal path unconditionally

Rejected.

Reason:

- masked and alpha-tested casters would become incorrect
- future materials with explicit shadow-state requirements would have no escape hatch

### Alternative C: Solve this only with render-queue sorting

Insufficient by itself.

Reason:

- better sorting helps reduce forced uniform updates
- it does not remove unnecessary texture iteration or callback execution for common opaque casters

## Risks

### Risk 1: Incorrect shadow silhouettes for masked materials

Mitigation:

- keep masked materials on the full path until the shadow-binding opt-in is in place and validated

### Risk 2: Missing custom shadow uniforms for special materials

Mitigation:

- add an explicit shadow-specific callback path before removing generic forwarding broadly

### Risk 3: Variant selection becomes too heuristic

Mitigation:

- start with a conservative fast-path eligibility test
- expand only after validation against known scenes

### Risk 4: Diagnostics regress shader authoring workflows

Mitigation:

- gate reduced diagnostics behind shadow-pass specialization only
- preserve existing diagnostic behavior when an explicit debugging setting is enabled

## Validation Plan

### Performance validation

Compare before and after profiler captures for:

- `GLMeshRenderer.Render.SetMaterialUniforms`
- `Lights3DCollection.RenderShadowMaps`
- total frame time in scenes with many opaque shadow casters

Focus specifically on the shadow-map phase shown under:

- `XRWindow.GlobalPreRender`
- `WorldInstance.GlobalPreRender`
- `Lights3DCollection.RenderShadowMaps`

### Visual validation

Validate all of the following:

- opaque static geometry still casts correctly
- masked foliage still casts cutout shadows correctly
- point, spot, and directional shadow passes remain correct
- no obvious shader-binding warnings appear in normal scenes unless diagnostics are enabled

### Scene coverage

Validation scenes should include:

- dense opaque environment geometry
- alpha-tested vegetation or cutout assets
- mixed light types with active shadow maps

## Open Questions

1. Should the fast path use one global shared shadow material, or a small per-feature cache keyed by shadow-shader shape?
2. Should shadow-binding intent live on `XRMaterialBase`, `XRMaterial`, or the shader-variant factory layer?
3. Should Vulkan adopt the same shadow-binding intent model immediately, or only after the OpenGL path is validated?
4. Should render-queue sorting for shadow passes be tightened as part of this work item or treated as a separate follow-up?

## Recommended Initial Scope

The recommended first implementation slice is:

1. instrument `GLMaterial.SetUniforms()` sub-costs
2. add the conservative opaque fast path
3. keep masked and callback-heavy materials on the existing full path

That scope is small enough to land safely, and it should capture most of the expected CPU win.
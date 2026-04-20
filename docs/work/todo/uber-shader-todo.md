# Uber Shader Optimization TODO

Last Updated: 2026-04-19
Status: Active redesign and implementation planning.
Scope: optimize the Uber shader only by making it fully modular, editor-driven, and cheap by default through compile-time feature specialization, per-property static versus animated modes, and dynamic async recompilation.

## Current Reality

What is already true:

- the Uber shader already has real compile-time gating in `UberShader.frag` and `uniforms.glsl`
- the Uber shader source is already split into reusable module files under `Build/CommonAssets/Shaders/Uber/`
- `XRMaterial` already reacts to shader reloads and rebuilds its discovered uniform surface when shader source changes
- `XRShader.GenerateAsync` and renderer async program-compilation settings already provide a starting point for non-blocking rebuilds
- the ImGui-based material and shader inspectors are the correct first UI surface for iteration and validation

What remains problematic:

- imported and default Uber materials still behave too much like one large shader family instead of a curated set of feature-composed variants
- feature enablement is currently spread across compile-time defines, runtime uniforms, and shader-local branching instead of one authoritative material model
- too many values are treated like uniforms even when they are static for the lifetime of a material instance
- there is no dedicated Uber UI that lets the user toggle whole feature sections on or off, understand dependencies, and see compile state
- live edits are not yet designed around queued, cancellable, async recompilation with last-known-good program fallback
- the Uber path has already hit real failure modes from excessive shader surface area, including constant register pressure and texture binding collisions, so trimming it is also a correctness task

## Primary Goal

At the end of this work:

- the Uber shader is composed from explicit feature modules instead of behaving like a permanently maximal fragment path
- every major feature family has a master toggle in a custom Uber material UI
- toggling a feature section on or off queues an async shader variant rebuild without blocking the editor UI
- every user-editable property defaults to a compile constant
- a property becomes an actual uniform only when the user marks it as animated
- disabled features contribute no code, no uniforms, and no texture bindings to the compiled variant
- the current material keeps rendering with its last valid program until a newly requested variant compiles and links successfully
- variant identity is explicit, cacheable, and observable in tooling

## Core Design Rules

These rules are the center of the design and should override ad hoc convenience decisions:

- feature sections are compile-time decisions, not runtime escape hatches
- the default property mode is static, not animated
- static properties are emitted as compile constants into generated shader source or generated constant blocks
- animated properties are emitted as uniforms and remain on the live material parameter surface
- changing a static property triggers async recompilation of the owning material variant
- changing an animated property updates runtime uniforms without forcing a full recompile unless feature membership changed
- disabled feature sections remove their dependent uniforms, samplers, and code paths entirely
- the editor UI is the authoritative source of feature enablement and property mode, not inferred shader text alone

## Feature Model

The Uber shader should be reorganized around explicit modules with metadata, dependencies, and ownership of their properties.

Each feature module should define:

- a stable feature id
- the shader snippets or files it owns
- the compile-time define or defines that enable it
- the list of dependent features it requires
- the properties it owns
- which properties may legally be animated
- the textures and samplers it requires when enabled
- whether it is allowed in imported-material-lite variants, full variants, or both

Initial module families:

- Surface and Base Color
- Alpha and Cutout
- Normal Mapping
- Stylized Lighting
- Material AO and Shadow Masking
- Emission
- Matcap
- Rim Lighting
- Advanced Specular
- Detail Textures
- Outline and Backface Effects
- Glitter
- Flipbook
- Subsurface
- Dissolve
- Parallax

Acceptance criteria:

- every optional Uber feature is represented by a module definition instead of scattered one-off toggles
- disabling a module removes its uniforms block from `uniforms.glsl` and its logic from the compiled fragment path
- module dependencies are explicit and validated by tooling before compilation begins

## Custom Uber UI

Outcome: the user can author Uber variants intentionally instead of indirectly poking at shader internals.

- [ ] add a dedicated Uber material inspector section instead of relying only on generic uniform discovery
- [ ] expose a top-level toggle for each feature module with clear enabled, disabled, pending compile, and compile-failed states
- [ ] group properties underneath their owning feature section rather than a flat parameter list
- [ ] add a per-property mode control: `Static` or `Animated`
- [ ] hide or disable child properties when their parent feature is off
- [ ] show dependency badges and auto-enable required parent modules only when the user explicitly accepts that change
- [ ] show variant metadata such as feature count, animated property count, variant hash, last compile time, and cache hit or miss
- [ ] preserve serialized values for disabled sections without binding them to the live shader until re-enabled

Acceptance criteria:

- the user can toggle whole feature sections on and off from one custom UI
- the user can mark individual properties as animated without editing shader source or raw parameter lists
- the UI makes it obvious when a change will only update uniforms versus trigger async recompilation

## Static Versus Animated Property Policy

Outcome: the Uber shader pays runtime uniform cost only for values that genuinely need to change during playback.

- [ ] define a serialized property-mode model for every Uber property
- [ ] treat booleans, enums, ints, floats, vectors, and colors as compile constants by default
- [ ] keep sampler bindings as sampler resources, but treat related control values such as scales, pans, intensities, thresholds, and modes as static compile constants unless marked animated
- [ ] generate shader literals for static values using deterministic formatting so variant keys remain stable
- [ ] emit real uniforms only for properties currently marked animated
- [ ] keep animated-property discovery consistent with `XRMaterial` parameter synchronization so values survive recompiles and reloads
- [ ] ensure switching a property from `Static` to `Animated` or back is handled as a variant change and not as a silent partial mutation

Acceptance criteria:

- a newly created Uber material compiles with the minimum uniform surface necessary for its enabled animated properties
- a property marked static does not appear as a runtime uniform in the active variant
- a property marked animated does appear as a runtime uniform and can change without another compile when its feature set is unchanged

## Async Recompilation Pipeline

Outcome: the editor remains responsive while the material converges toward the newly requested Uber variant.

- [ ] define a dedicated Uber variant request object keyed by enabled modules, animated-property mask, render mode, and source version
- [ ] debounce rapid UI edits so repeated toggles do not enqueue redundant compile jobs
- [ ] cancel or supersede stale compile requests when newer edits arrive for the same material
- [ ] compile and link variants asynchronously using existing async shader and program-compilation infrastructure where available
- [ ] keep the last-known-good shader program bound until the replacement variant is ready
- [ ] surface compile progress and errors directly in the Uber UI instead of failing silently or dropping to black output
- [ ] apply successful program swaps atomically so the material never presents a partially updated shader state
- [ ] record compile timing and result metadata for later profiling and cache tuning

Acceptance criteria:

- toggling feature sections on or off does not stall the editor main loop
- compile failure leaves the old valid Uber variant rendering in place
- repeated quick edits converge to the final requested state instead of compiling every intermediate state

## Variant Caching And Invalidation

Outcome: modularity does not turn into uncontrolled variant churn.

- [ ] cache generated Uber source by module set plus static-property literals plus animated-property mask
- [ ] cache compiled shader variants and linked programs by the same stable key
- [ ] invalidate cached variants when any owned module file, shared Uber snippet, or generated constant schema changes
- [ ] keep cache entries separated for imported-material-lite, standard, and full-rich variants if those remain distinct families
- [ ] log cache hit or miss, compile duration, uniform count, sampler count, and compile-failure reason for each Uber variant build
- [ ] make the active variant key visible in the material inspector for diagnostics

Acceptance criteria:

- the same Uber feature and property configuration does not regenerate or recompile identical shader source repeatedly
- cache invalidation is strict enough that snippet edits never leave stale compiled variants behind

## Phase 0 - Establish Uber Baselines

Outcome: work is ordered by actual Uber cost and failure risk, not intuition.

- [ ] capture compile and link timings for representative minimal, medium, and full-feature Uber variants
- [ ] record uniform counts, sampler counts, and generated source size for those same variants
- [ ] profile GPU cost for representative materials after modularization begins so compile wins are not mistaken for frame wins
- [ ] record the current worst offenders in `UberShader.frag` and `uniforms.glsl`, including runtime branches that should become module gates
- [ ] create a small diagnostic table covering compile time, link time, variant count, and live uniform surface before any major refactor

Acceptance criteria:

- the roadmap has a measured baseline for minimal, common, and maximal Uber materials
- the first implementation phase is chosen from measured compile pressure, runtime cost, or failure frequency

## Phase 1 - Define The Uber Module Manifest

Outcome: the shader, UI, and caching code share one authoritative definition of Uber features.

- [ ] add a module manifest or equivalent metadata layer that maps feature ids to shader files, defines, dependencies, and properties
- [ ] move existing optional blocks in `uniforms.glsl` and `UberShader.frag` under module-owned compile gates driven by that manifest
- [ ] separate always-on core surface data from optional feature families
- [ ] keep one thin compatibility path for legacy materials while new Uber materials use the module manifest directly

Acceptance criteria:

- module membership is data-driven and not duplicated independently across shader code, editor UI, and cache keys
- turning a module off removes all of its owned compile and binding surface

## Phase 2 - Ship The Custom Uber Inspector

Outcome: Uber authoring becomes intentional and observable.

- [ ] implement the first ImGui inspector for Uber materials with collapsible feature sections
- [ ] add master feature toggles, per-property static or animated mode controls, and compile-state badges
- [ ] display dependency warnings and compile-impact hints before the user commits a change
- [ ] support bulk actions such as `Disable All Expensive Features`, `Convert Eligible Properties To Static`, and `Mark Selected Properties Animated`

Acceptance criteria:

- the generic uniform list is no longer the primary authoring experience for Uber materials
- users can reason about feature cost and property mode directly from the editor

## Phase 3 - Convert Static Properties To Generated Constants

Outcome: the common Uber path becomes materially cheaper by default.

- [ ] generate deterministic constant declarations or injected defines for static properties
- [ ] remove runtime toggle uniforms such as feature-enable booleans from the hot path once their owning module is compile-specialized
- [ ] keep only true time-varying properties on the uniform surface
- [ ] validate literal generation for floats and vectors so cache keys are stable and text churn is minimized

Acceptance criteria:

- common non-animated Uber materials compile with dramatically fewer uniforms than the legacy path
- runtime branching on optional feature enable flags is materially reduced or removed in the common case

## Phase 4 - Add Dynamic Async Recompilation And Safe Program Swap

Outcome: feature toggles are live and safe.

- [ ] wire the Uber inspector to request variant rebuilds automatically when feature membership or property mode changes
- [ ] ensure compile requests are debounced, supersedable, and cancellable
- [ ] preserve the last valid program until the next program is fully compiled and linked
- [ ] show compile logs in-editor when a requested variant fails
- [ ] retain material values and animation bindings across variant swaps

Acceptance criteria:

- toggling a feature section updates the material through async recompilation instead of forcing blocking manual reload flows
- the material never drops to an invalid partially rebound state during normal editor use

## Phase 5 - Trim The Uber Runtime Path Aggressively

Outcome: the default Uber variant stops carrying logic it is not using.

- [ ] audit all remaining runtime `if` branches in `UberShader.frag` and move feature-selection branches to compile time where reasonable
- [ ] prioritize removal of high-surface optional families such as stylized lighting variants, matcap, parallax, glitter, dissolve, and subsurface from the default common path
- [ ] decide whether imported materials should target a dedicated lite Uber family instead of the same full-rich manifest used by hand-authored materials
- [ ] keep a genuine full-rich path only for materials that need it, not as the default for ordinary content

Acceptance criteria:

- a simple Uber material compiles into a materially smaller shader than a feature-rich Uber material
- the full-rich variant is explicit and opt-in rather than the default shape of the system

## Phase 6 - Validate Known Uber Failure Modes

Outcome: the new architecture does not reintroduce already diagnosed Uber regressions.

- [ ] add regression coverage for shader compile failure caused by excessive uniform and constant pressure
- [ ] add regression coverage for sampler binding collisions when the logical material texture list is sparse
- [ ] validate that disabled modules do not leak unused samplers or uniforms into compiled variants
- [ ] validate that failed async recompiles keep the old valid program bound and visible
- [ ] validate that switching properties between `Static` and `Animated` preserves serialized values and animation hookups

Acceptance criteria:

- prior black-output classes of Uber failure are covered by targeted validation instead of manual rediscovery
- the modular Uber path fails loudly in tooling before it fails silently in-frame

## Non-Goals

- do not broaden this document back into a whole-engine shader optimization audit
- do not make the first implementation depend on the native editor UI; use the ImGui editor path first
- do not keep feature toggles as runtime uniforms just for implementation convenience if they should be compile-time module gates
- do not force Vulkan-specific architecture into the OpenGL-first Uber workflow without separate approval
- do not let a compatibility fallback become the permanent main path again

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
- `XREngine.Runtime.Rendering/Resources/Shaders/XRShader.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderHelper.cs`
- `XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs`
- `XREngine.Editor/AssetEditors/XRMaterialInspector.Enhanced.cs`
- `XREngine.Editor/AssetEditors/XRShaderInspector.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`

## Suggested Next Step

The first implementation pass should be Phase 1 plus the minimal Phase 2 shell:

1. define the authoritative Uber module manifest
2. build the custom ImGui Uber inspector around module toggles and per-property `Static` or `Animated` modes
3. only after that, wire async recompilation and generated compile constants to the new data model

That order keeps the shader architecture, UI, and variant cache keyed off the same source of truth instead of layering async recompilation on top of an unstable feature model.

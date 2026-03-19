# Runtime Modularization And Bootstrap Extraction Plan

## Overview

This document proposes a staged restructuring of the runtime assemblies so that:

1. `XREngine.Server` no longer depends on `XREngine.Editor`.
2. the current `XRENGINE` assembly stops acting as a dependency sink for unrelated subsystems.
3. runtime, rendering, animation, audio, input, and modeling integration code are split into explicit assemblies with one-way dependencies.
4. world/bootstrap/test-world composition lives in a runtime-safe assembly instead of inside the editor.

This is a design document for the refactor, not an implementation record.

Implementation companions:

- [Runtime Modularization Phase 2 TODO](../todo/runtime-modularization-phase2-todo.md) (complete)
- [Runtime Modularization Phase 3 TODO](../todo/runtime-modularization-phase3-todo.md) (physical code moves & XRENGINE deletion)

## Why This Refactor Exists

The current solution is already partially modularized at the project level, but the assembly boundaries do not fully represent architectural boundaries.

Key problems today:

- `XREngine.Server` directly references `XREngine.Editor`.
- `XRENGINE` directly references `XREngine.Animation`, `XREngine.Audio`, `XREngine.Input`, and `XREngine.Modeling`, so the main runtime assembly is both the engine core and the integration layer.
- shared startup and world-building utilities live under editor-owned `EditorUnitTests` code even when they are used by server and runtime flows.
- runtime types contain editor-facing metadata strings, which is acceptable short-term but confirms that the runtime model is still editor-aware.

The result is avoidable coupling:

- server startup is blocked on editor code movement,
- the core runtime cannot be reused without dragging in feature integrations,
- rendering and gameplay-facing scene types are mixed with subsystem bridges,
- future headless, tooling, and test harness work must route through the editor assembly.

## Current State Summary

### Existing Good Boundaries

The following projects are already reasonable lower-level libraries:

- `XREngine.Extensions`
- `XREngine.Data`
- `XREngine.Audio`
- `XREngine.Animation`
- `XREngine.Input`
- `XREngine.Modeling`
- `XREngine.Profiler`
- `XREngine.Profiler.UI`

These should remain leaf or near-leaf libraries in the target design.

### Current Architectural Problems

#### 1. Server depends on editor

`XREngine.Server` currently references `XREngine.Editor` and uses `EditorUnitTests` world/bootstrap helpers. That is the highest-value dependency to remove first.

This is a strict layering violation because:

- the server is a product/runtime host,
- the editor is a top-level tool application,
- shared startup helpers should flow downward into a runtime-safe assembly, not upward into the editor.

#### 2. XRENGINE is both core and adapter layer

The current `XRENGINE` project owns too many responsibilities at once:

- engine lifecycle and threading,
- world and scene runtime,
- rendering API and pipelines,
- animation-facing components,
- audio-facing components,
- input-facing controllers and VR transforms,
- modeling conversion bridges.

That shape makes the `XRENGINE` assembly a dependency sink. The project graph is acyclic, but the main runtime assembly is not a narrow core.

#### 3. Editor bootstrap code is mixed with editor UX code

`EditorUnitTests` currently mixes several concerns:

- runtime-safe world builders,
- startup toggles and settings,
- test scene composition,
- editor-specific world composition,
- editor UI helpers.

Only some of that belongs in the editor assembly.

## Goals

### Primary Goals

- Remove the `Server -> Editor` project reference.
- Introduce a runtime bootstrap assembly for shared world/startup/test-world composition.
- Split the current `XRENGINE` assembly into explicit layers.
- Preserve one-way dependencies with a clear bottom-up graph.
- Keep the repo buildable after each migration phase.

### Secondary Goals

- Make headless and server-specific runtime paths easier to isolate later.
- Reduce incidental rebuild scope when editing rendering, animation, audio, input, or modeling integration code.
- Make ownership of runtime code clearer by project.
- Prepare the codebase for future subsystem extraction without needing another broad sweep.

### Non-Goals

- This plan does not require preserving the current public API surface pre-ship.
- This plan does not attempt to package projects for NuGet distribution.
- This plan does not require a physics split in the first wave.
- This plan does not require removing all editor metadata strings from runtime types immediately.

## Target Assembly Layout

The recommended end state is:

### Foundation

- `XREngine.Extensions`
  - extension methods and low-level helpers only.
- `XREngine.Data`
  - shared contracts, math/value types, assets, events, settings objects, serialization helpers, runtime metadata attributes, and general-purpose utilities.

### Feature Libraries That Stay As Standalone Libraries

- `XREngine.Animation`
  - pure animation data structures, clips, state machines, curves, IK math, importers, and animation-side runtime logic that does not require scene/component ownership.
- `XREngine.Audio`
  - audio core, transports, effects, audio runtime objects, and audio-side abstractions.
- `XREngine.Input`
  - input devices, input interfaces, VR/OpenVR device bindings, and device-layer abstractions.
- `XREngine.Modeling`
  - editable mesh structures, boolean operations, validation, and modeling documents.

### New Runtime Layers

- `XREngine.Runtime.Core`
  - pure engine/runtime loop and engine-owned runtime model.
- `XREngine.Runtime.Rendering`
  - rendering API, render thread, render pipelines, windows/viewports, materials/shaders/meshes, render sync, renderable scene pieces.
- `XREngine.Runtime.AnimationIntegration`
  - scene/components that bind `XREngine.Animation` into runtime world objects.
- `XREngine.Runtime.AudioIntegration`
  - scene/components that bind `XREngine.Audio` into runtime world objects.
- `XREngine.Runtime.InputIntegration`
  - controllers, pawn input, viewport/window input routing, VR action transforms, and runtime bridges to `XREngine.Input`.
- `XREngine.Runtime.ModelingBridge`
  - runtime mesh import/export/conversion code that bridges `XRMesh` and `XREngine.Modeling`.
- `XREngine.Runtime.Bootstrap`
  - startup profiles, shared world factories, unit-testing-world toggles/settings loading, sample/test composition helpers, and runtime-safe startup orchestration.

### Applications

- `XREngine.Editor`
  - editor application, editor UI, inspectors, editors, authoring tools, play-mode shell, editor-specific world overlays.
- `XREngine.Server`
  - server executable and HTTP/load-balancer host. Must not reference `XREngine.Editor`.
- `XREngine.VRClient`
  - VR-focused executable and runtime host.
- `XREngine.UnitTests`
  - tests and integration harnesses.

## Target Dependency Rules

### Allowed Dependencies

```text
XREngine.Extensions
    -> no project references

XREngine.Data
    -> XREngine.Extensions

XREngine.Animation
    -> XREngine.Data

XREngine.Audio
    -> XREngine.Data

XREngine.Input
    -> XREngine.Data, XREngine.Extensions

XREngine.Modeling
    -> XREngine.Data

XREngine.Runtime.Core
    -> XREngine.Data, XREngine.Extensions

XREngine.Runtime.Rendering
    -> XREngine.Runtime.Core, XREngine.Data, XREngine.Extensions

XREngine.Runtime.AnimationIntegration
    -> XREngine.Runtime.Core, XREngine.Runtime.Rendering, XREngine.Animation, XREngine.Data

XREngine.Runtime.AudioIntegration
    -> XREngine.Runtime.Core, XREngine.Runtime.Rendering, XREngine.Audio, XREngine.Data

XREngine.Runtime.InputIntegration
    -> XREngine.Runtime.Core, XREngine.Runtime.Rendering, XREngine.Input, XREngine.Data, XREngine.Extensions

XREngine.Runtime.ModelingBridge
    -> XREngine.Runtime.Rendering, XREngine.Modeling, XREngine.Data

XREngine.Runtime.Bootstrap
    -> XREngine.Runtime.Core, XREngine.Runtime.Rendering,
       XREngine.Runtime.AnimationIntegration,
       XREngine.Runtime.AudioIntegration,
       XREngine.Runtime.InputIntegration,
       XREngine.Runtime.ModelingBridge,
       XREngine.Animation, XREngine.Audio, XREngine.Input, XREngine.Modeling,
       XREngine.Data, XREngine.Extensions

XREngine.Editor
    -> may depend on all runtime assemblies and tooling libraries

XREngine.Server
    -> may depend on runtime assemblies, but never on XREngine.Editor

XREngine.VRClient
    -> may depend on runtime assemblies, but never on XREngine.Editor
```

### Forbidden Dependencies

- `XREngine.Server -> XREngine.Editor`
- `XREngine.Runtime.Core -> XREngine.Animation`
- `XREngine.Runtime.Core -> XREngine.Audio`
- `XREngine.Runtime.Core -> XREngine.Input`
- `XREngine.Runtime.Core -> XREngine.Modeling`
- `XREngine.Runtime.Rendering -> XREngine.Editor`
- `XREngine.Animation`, `XREngine.Audio`, `XREngine.Input`, or `XREngine.Modeling` depending on any runtime or editor assembly
- any production runtime project depending on `XREngine.UnitTests`

## Recommended Naming Strategy

Use `Runtime.*` suffixes for the new adapter layers so they do not collide with the existing feature libraries.

Recommended project names:

- `XREngine.Runtime.Core`
- `XREngine.Runtime.Rendering`
- `XREngine.Runtime.AnimationIntegration`
- `XREngine.Runtime.AudioIntegration`
- `XREngine.Runtime.InputIntegration`
- `XREngine.Runtime.ModelingBridge`
- `XREngine.Runtime.Bootstrap`

This is clearer than naming an adapter layer `XREngine.Animation`, because `XREngine.Animation` already exists and should remain the lower-level animation library.

## Ownership By Assembly

### XREngine.Runtime.Core

Owns:

- `Engine` lifecycle, threading, timing, job scheduling, and shutdown coordination.
- game startup state and runtime settings application.
- world/scene ownership models that do not require rendering-specific code.
- base scene graph/runtime objects that are not subsystem adapters.
- networking core and runtime orchestration that is not UI/editor specific.
- play-mode-independent runtime coordination.

Should not own:

- shader/material/program code,
- window/viewports/render context code,
- audio source/listener components,
- animation clip/state-machine scene components,
- input device adapters or VR action transforms,
- modeling import/export bridges.

### XREngine.Runtime.Rendering

Owns:

- `XRWindow`, `XRViewport`, render context, pipelines, render passes, materials, shaders, mesh runtime types, texture runtime types, render synchronization, world rendering publication.
- rendering-facing scene components and renderable runtime objects.
- render-thread state publication and render statistics.

Should not own:

- input device implementations,
- audio runtime objects,
- animation data structures,
- bootstrap/test-world composition.

### XREngine.Runtime.AnimationIntegration

Owns:

- animation scene components,
- humanoid runtime integration,
- animation-driven transform/component application,
- motion capture receivers that exist to drive runtime scene objects,
- animation diagnostics that require runtime scene bindings.

Should depend on `XREngine.Animation`, not vice versa.

### XREngine.Runtime.AudioIntegration

Owns:

- runtime scene audio components,
- world/listener integration,
- Steam Audio scene bridges that attach runtime geometry/components to audio-side APIs,
- video/audio bridge code that exists because runtime scene objects emit sound.

Should depend on `XREngine.Audio`, not vice versa.

### XREngine.Runtime.InputIntegration

Owns:

- local/remote player controllers,
- pawn input assemblies,
- runtime camera/pawn input bridges,
- VR action transforms and scene bindings,
- runtime window/viewport input routing.

Should depend on `XREngine.Input`, not vice versa.

### XREngine.Runtime.ModelingBridge

Owns:

- `XRMesh <-> ModelingMeshDocument` conversion,
- runtime boolean operation entry points,
- import/export helpers that bridge render/runtime mesh types and modeling types.

Should depend on `XREngine.Modeling`, not vice versa.

### XREngine.Runtime.Bootstrap

Owns:

- startup profiles,
- runtime-safe shared world factories,
- unit-testing-world settings loading and normalization,
- common scene composition helpers shared by Editor, Server, VRClient, and tests,
- server-safe and editor-safe bootstrap configuration objects.

Should not own:

- editor UI,
- inspectors,
- asset editors,
- editor shell behavior,
- IMGUI/native UI logic.

## Bootstrap Extraction Plan

### Problem Being Solved

The current `EditorUnitTests` surface is the immediate reason `XREngine.Server` references `XREngine.Editor`.

That codebase contains several categories of logic that should be separated:

1. runtime-safe world composition helpers,
2. unit-testing-world settings/toggles definitions,
3. startup helpers used by multiple executables,
4. editor-only UI and editor-shell behavior.

Only category 4 belongs in `XREngine.Editor`.

### Recommended Extraction Shape

Create `XREngine.Runtime.Bootstrap` and move the following into it:

- unit testing world toggle/settings types currently owned by `EditorUnitTests.Settings`
- settings loading and writing helpers for `Assets/UnitTestingWorldSettings.jsonc`
- world-kind selection and startup profile resolution
- runtime-safe world factories currently used by both editor and server startup
- composition helpers like player pawn, skybox, and lighting builders when they do not require editor UI types

### Recommended Names After Extraction

Replace the `EditorUnitTests` identity for shared runtime code with names that describe what the code actually is.

Recommended replacements:

- `EditorUnitTests.Settings` -> `RuntimeBootstrapSettings` or `UnitTestingWorldSettings`
- `EditorUnitTests.Toggles` -> `RuntimeBootstrap.CurrentSettings`
- `EditorUnitTests.CreateSelectedWorld(...)` -> `BootstrapWorldFactory.CreateSelectedWorld(...)`
- `EditorUnitTests.ApplyRenderSettingsFromToggles()` -> `BootstrapRenderSettings.Apply(...)`
- nested static groups such as `Pawns`, `Lighting`, `Models` -> `BootstrapWorldBuilders.Pawns`, `BootstrapWorldBuilders.Lighting`, `BootstrapWorldBuilders.Models`

The critical rule is that shared runtime builders must no longer present themselves as editor-only code.

### What Stays In XREngine.Editor

- editor windows and panels
- IMGUI and native editor UI code
- inspectors and asset editors
- editor state and selection systems
- editor-only tools, menus, and shortcuts
- editor-only shell behavior layered on top of runtime bootstrap settings

### Transitional Strategy

During migration, a temporary shim in `XREngine.Editor` is acceptable:

- `EditorUnitTests` may remain as a thin forwarding facade to `XREngine.Runtime.Bootstrap` for one migration phase.
- new production references must point at `XREngine.Runtime.Bootstrap` directly.
- once editor call sites are migrated, delete the shim.

This keeps the solution green while avoiding a single huge rename sweep.

## XRENGINE Split Plan

### Core Principle

`XRENGINE` should stop being both the runtime kernel and the feature integration layer.

The split should follow this rule:

- feature libraries own domain logic,
- runtime core owns engine lifecycle and runtime model,
- adapter assemblies own the binding between runtime world objects and feature libraries.

### Candidate Move Map

#### Move into XREngine.Runtime.Core

- engine lifecycle files under `Engine/` that do not require rendering/input/audio/animation/modeling concrete types
- timing, jobs, world orchestration, startup coordination, scene ownership, runtime settings glue
- networking core and host-independent multiplayer orchestration
- core scene graph/runtime object base types that can exist without render/audio/input adapters

#### Move into XREngine.Runtime.Rendering

- most of `Rendering/`
- `XRWindow`, viewport handling, render thread coordination
- render pipelines, materials, shaders, GPU resource abstractions
- rendering-facing scene components and render synchronization code

#### Move into XREngine.Runtime.AnimationIntegration

- `Scene/Components/Animation/**`
- humanoid animation bridge code that binds clips and runtime nodes/components
- runtime IK scene integration

#### Move into XREngine.Runtime.AudioIntegration

- `Scene/Components/Audio/**`
- audio listener/source scene bridge code
- runtime geometry bridges for Steam Audio scene integration
- runtime UI/video audio bridge code where appropriate

#### Move into XREngine.Runtime.InputIntegration

- runtime controller code under `XRENGINE/Input/**`
- VR scene transforms that wrap input action sources
- pawn input sets and camera/pawn control bridges
- viewport/window input glue that should not live in core

#### Move into XREngine.Runtime.ModelingBridge

- `Rendering/Modeling/**`
- all `XRMesh` import/export/boolean bridge logic

### Physics Scope In This Refactor

Physics is not part of the requested split and should remain where it best preserves buildability during the first wave.

Recommended treatment:

- keep physics in `XREngine.Runtime.Core` unless a rendering dependency forces a narrower move,
- defer a dedicated physics assembly until after the rendering/core split is stable.

## Transitional Migration Strategy

### Phase 0 - Guardrails And Inventory

Before moving code:

- capture the current project reference graph,
- list all `XRENGINE` folders by intended target assembly,
- identify editor-only versus runtime-safe `EditorUnitTests` files,
- identify all `Type.GetType("XREngine.Editor...`)` call sites and editor metadata attributes.

Deliverables:

- ownership spreadsheet or checklist,
- dependency report,
- compile-time no-cycle rules documented in the solution.

### Phase 1 - Introduce XREngine.Runtime.Bootstrap

Create `XREngine.Runtime.Bootstrap` first and move only the server/editor shared startup surface.

Move into it:

- unit-testing-world settings types,
- settings loader/writer for `Assets/UnitTestingWorldSettings.jsonc`,
- shared world factories,
- shared runtime-safe builder groups.

Update references:

- `XREngine.Server` -> reference `XREngine.Runtime.Bootstrap`
- `XREngine.Editor` -> reference `XREngine.Runtime.Bootstrap`
- `XREngine.UnitTests` -> reference `XREngine.Runtime.Bootstrap` if needed

Gate for completion:

- `XREngine.Server` builds without any project reference to `XREngine.Editor`.

### Phase 2 - Introduce Empty Runtime Layer Projects

Add the new projects with no or minimal code movement first:

- `XREngine.Runtime.Core`
- `XREngine.Runtime.Rendering`
- `XREngine.Runtime.AnimationIntegration`
- `XREngine.Runtime.AudioIntegration`
- `XREngine.Runtime.InputIntegration`
- `XREngine.Runtime.ModelingBridge`

At this phase, the objective is only to establish the target graph and solution structure.

Gate for completion:

- solution builds with empty or near-empty layer projects,
- no cycles exist in project references.

### Phase 3 - Carve Out Runtime.Core

Move the minimum viable core runtime surface out of `XRENGINE`.

Recommended order:

1. engine lifecycle and threading
2. time/job/runtime settings
3. world/scene ownership skeleton
4. networking core

Temporary measure:

- keep `XRENGINE` as a compatibility aggregator during this phase,
- forward types or re-export references temporarily if needed,
- delete the aggregator only after application projects consume the new layers directly.

### Phase 4 - Move Rendering

Move rendering code into `XREngine.Runtime.Rendering`.

During this phase, any type that currently forces `Engine` core code to know concrete rendering types should be inverted behind interfaces or moved wholly into the rendering assembly.

Important rule:

- `Runtime.Core` may know about render contracts defined in `Data` or a narrow shared contract surface,
- `Runtime.Core` must not depend on the full rendering implementation assembly.

### Phase 5 - Move Subsystem Adapters

Move runtime-facing integration code into the adapter assemblies:

- animation components to `Runtime.AnimationIntegration`
- audio components to `Runtime.AudioIntegration`
- input bridges/controllers/VR transforms to `Runtime.InputIntegration`
- mesh/modeling bridges to `Runtime.ModelingBridge`

At the end of this phase, the feature libraries remain leaf libraries and the runtime integration logic sits above them.

### Phase 6 - Delete Or Re-scope XRENGINE

Once apps reference the new projects directly, choose one of these end states:

#### Option A - Remove XRENGINE entirely

Recommended for the cleanest v1 architecture.

#### Option B - Keep XRENGINE as a thin facade

Only acceptable if there is a strong short-term migration reason. It should not continue to own real implementation.

Because the product is pre-ship, Option A is preferred.

## Required Dependency Inversions

The split will require a few deliberate inversion points.

### Runtime.Core <-> Rendering

Current risk:

- core engine lifecycle code knows concrete rendering types.

Needed change:

- move concrete rendering implementation to `Runtime.Rendering`,
- keep only narrow runtime contracts in `Data` or `Runtime.Core`-owned abstractions.

### Runtime.Core <-> InputIntegration

Current risk:

- `Engine.Input` and player-facing control code are mixed into the engine assembly.

Needed change:

- runtime core exposes player/world hooks,
- input integration supplies controller and device-binding behavior.

### Runtime.Core <-> AudioIntegration

Current risk:

- world/runtime code and scene components may directly assume audio integration is present.

Needed change:

- core owns world lifecycle,
- audio integration attaches listeners, sources, and scene/audio bridges.

### Runtime.Core <-> AnimationIntegration

Current risk:

- transforms and runtime animation-driven scene components are interwoven.

Needed change:

- core owns transform/runtime object model,
- animation integration owns applying animation state to those objects.

## Editor Metadata Strategy

Runtime assemblies currently include editor-facing metadata strings such as asset and component editor bindings.

Short-term policy during this refactor:

- allow these string-based metadata attributes to remain in place if they do not create assembly references,
- do not block the modularization on removing them.

Longer-term cleanup after the split is stable:

- move editor contract attributes to `XREngine.Data` if they are truly shared contracts,
- keep editor implementation type names in strings or registries,
- remove ad hoc `Type.GetType("XREngine.Editor,...")` lookups when a cleaner registration path exists.

This keeps the first wave focused on dependency direction rather than polishing every editor integration detail.

## Validation Strategy

Each phase should have narrow validation gates.

### Minimum Validation Per Phase

1. targeted project builds for the moved assemblies
2. targeted startup validation for Editor, Server, or VRClient depending on the phase
3. unit tests closest to changed runtime code
4. no new project-reference cycles

### Critical Gates

#### Gate A - Server/editor decoupling

- `XREngine.Server` builds with no `XREngine.Editor` project reference.
- server startup path still creates its world successfully.

#### Gate B - Runtime.Core independence

- `XREngine.Runtime.Core` does not reference `XREngine.Animation`, `XREngine.Audio`, `XREngine.Input`, or `XREngine.Modeling`.

#### Gate C - Adapter correctness

- moving a runtime adapter assembly does not require feature libraries to reference runtime code.

#### Gate D - App startup

- Editor launches.
- Server launches.
- VRClient launches.

## Risks

### Risk 1 - Namespace churn obscures ownership progress

If files move between projects without namespace cleanup or ownership tracking, the solution may compile while architecture remains muddy.

Mitigation:

- maintain an ownership map during migration,
- rename namespaces when the new boundary is meant to be permanent,
- do not leave long-lived code in the wrong project under a legacy namespace unless intentionally temporary.

### Risk 2 - Bootstrap project becomes a second junk drawer

If `Runtime.Bootstrap` absorbs editor UI or unrelated runtime logic, the server/editor split will be replaced by a different catch-all.

Mitigation:

- keep bootstrap limited to startup profiles, world factories, and shared runtime composition,
- reject editor shell and inspector code in that assembly.

### Risk 3 - Rendering/core split creates circular abstractions

Poorly placed interfaces can move the cycle into contracts instead of removing it.

Mitigation:

- prefer moving concrete code wholesale over inventing unnecessary abstractions,
- define only the minimum contracts required for lifecycle boundaries.

### Risk 4 - Partial moves leave apps referencing both old and new layers indefinitely

Mitigation:

- treat the current `XRENGINE` assembly as explicitly transitional once the new layers exist,
- schedule deletion or final reduction of the legacy aggregator as a tracked milestone.

## Recommended First Implementation Slice

The first slice should be intentionally narrow and high-confidence:

1. create `XREngine.Runtime.Bootstrap`
2. move unit-testing-world settings and shared world builders out of editor-owned code
3. update `XREngine.Server` to reference bootstrap instead of editor
4. keep a temporary `EditorUnitTests` forwarding shim in `XREngine.Editor` only if required to avoid a giant rename sweep
5. validate Editor and Server startup

This delivers immediate architectural value with relatively low blast radius.

## Recommended Follow-On Slice

After server/editor decoupling, the next slice should be:

1. create `XREngine.Runtime.Core` and `XREngine.Runtime.Rendering`
2. move the smallest coherent runtime-core set out of `XRENGINE`
3. move rendering implementation out of `XRENGINE`
4. keep the remaining subsystem bridges in the old assembly temporarily

Only after that should the animation, audio, input, and modeling adapter assemblies be carved out.

## Definition Of Done

This refactor is complete when all of the following are true:

- `XREngine.Server` does not reference `XREngine.Editor`.
- shared startup/test-world/world-factory code lives in `XREngine.Runtime.Bootstrap`.
- the old `XRENGINE` assembly is either removed or reduced to a thin temporary facade with no meaningful implementation.
- `XREngine.Runtime.Core` does not reference animation, audio, input, or modeling feature libraries.
- rendering, animation integration, audio integration, input integration, and modeling bridge code live in explicit assemblies.
- Editor, Server, VRClient, and targeted tests all build and launch through the new graph.

## Recommendation

Proceed with this refactor in two deliberate waves:

1. server/editor decoupling through `XREngine.Runtime.Bootstrap`
2. `XRENGINE` decomposition into `Runtime.Core`, `Runtime.Rendering`, and subsystem adapter assemblies

That sequence removes the most obvious bad dependency first, establishes the target graph early, and avoids trying to solve every architectural issue in a single move.
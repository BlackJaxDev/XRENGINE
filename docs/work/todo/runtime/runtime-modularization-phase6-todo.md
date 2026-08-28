# Runtime Modularization Phase 6 - Remove The XRENGINE Facade

Reference design: [Runtime Modularization And Bootstrap Extraction Plan](../../design/runtime-modularization-plan.md)

Prerequisites:

- [Runtime Modularization Phase 4 - Complete](../COMPLETED/runtime-modularization-phase4-todo.md)
- [Runtime Modularization Phase 5 - Complete](../COMPLETED/runtime-modularization-phase5-todo.md)
- [Runtime Modularization Phase 5 Progress](../../progress/runtime/runtime-modularization-phase5-progress-2026-08-24.md)
- [Runtime Modularization Phase 6 Progress](../../progress/runtime/runtime-modularization-phase6-progress-2026-08-25.md)

Created: 2026-08-24

Status: In progress. P6.0, P6.1, and P6.2 completed on 2026-08-25; P6.3 is next.

## Architectural Decision

Phase 6 targets design Option A: remove the `XRENGINE` project and
`XREngine.dll` entirely.

The repository is pre-v1 and has no backward-compatibility obligation, so a
permanent compatibility facade is not justified. Temporary forwarding or
adapter seams may exist inside a migration slice, but they must be removed
before Phase 6 closes. If a product requirement later demands a separately
shipped compatibility assembly, update the reference design and define its
support lifetime before implementing it; do not quietly retain the current
facade.

Facade deletion is the final operation, not the first. Every source file,
consumer, serialized identity, AOT root, package, native asset, and application
startup path must have a deliberate disposition before the project is removed.

## Goal

Finish the runtime modularization plan by eliminating the transitional
`XRENGINE` dependency sink and making applications compose the explicit runtime
graph directly.

At completion:

- no project references `XRENGINE/XREngine.csproj`;
- `XRENGINE/XREngine.csproj`, its production source tree, and its assembly
  output are gone;
- runtime lifecycle, physics, networking, assets, rendering/world publication,
  gameplay/input, import, and startup code compile from their final owners;
- Editor, Server, VRClient, samples, benchmarks, tests, AOT generation, and
  project templates consume the explicit runtime assemblies;
- repository-owned serialized assets and generated metadata use current type
  identities or an ownership-correct redirect mechanism that does not require
  loading `XREngine.dll`;
- packages, native cargo, content, licenses, and build targets have exactly one
  final owner; and
- the dependency graph in the reference design is enforced by tests.

## Entry State

This inventory was measured after the Phase 4/5 engineering closeout on
2026-08-24. P6.0 must recount it from the exact implementation base because the
current worktree may contain unrelated concurrent changes.

### Facade Source Inventory

`XRENGINE` currently contains 358 hand-authored C# files:

| Area | Files | Important remaining ownership work |
|---|---:|---|
| `Scene/` | 185 | 140 physics components, 13 debug components, 8 Unity-import components, pawn/movement/mesh components, prefab contracts, and the Unity editor import bridge |
| `Core/` | 105 | 38 asset/cooked-binary files, 36 AssetManager/YAML/model-cache files, project/state/snapshot support, and remaining utility contracts |
| `Engine/` | 47 | lifecycle, settings, threading, networking, physics, play mode, input, window pump, project state, profiling, and host adapters |
| `Settings/` | 9 | editor preferences/secrets plus game/window startup settings |
| `Properties/` | 4 | assembly metadata and runtime subsystem/UI/world type forwards |
| `Game Modes/` | 3 | flying-camera, locomotion, and VR composition |
| `Rendering/` | 3 | the 2,000-plus-line `XRWorldInstance` aggregate and physics/debug partials |
| Root aliases/usings | 2 | global physics using and world type aliases |

The facade project directly references 12 projects:

- Animation, Audio, Data, Extensions, Fbx, and Input;
- Runtime.Core and Runtime.Rendering; and
- AnimationIntegration, AudioIntegration, InputIntegration, and
  ModelAssetPipeline.

It also declares 19 direct NuGet package references spanning physics, import,
serialization/compression, protected storage, DirectStorage, input, and
windowing. Native/content ownership still includes `lib_coacd.dll`, an optional
`RestirGI.Native.dll` copy target, and `nis.license.txt`. These are migration
inventory, not evidence that the facade is their correct final owner.

### Direct Consumers And Retention Roots

Seven projects directly reference `XRENGINE`:

| Consumer | Current reason to remove or replace |
|---|---|
| `Runtime.Bootstrap` | Transitional world, pawn, movement, physics, game-mode, and startup composition; AOT input still scans `XRENGINE/**/*.cs` |
| `Editor` | Runtime facade APIs, project-template generation, and an explicit `XREngine.dll` assembly list entry |
| `Server` | Runtime world/engine/networking composition |
| `VRClient` | Game startup, world, VR/game-mode, and facade lifecycle composition |
| `UnitTests` | Legacy identities, facade internals, source-contract paths, and integration fixtures |
| `Benchmarks` | Runtime/asset/import/engine consumers and baseline harnesses |
| `Samples/MonkeyBallVR` | Game/runtime composition through the legacy project |

Additional retention surfaces include:

- both `XRENGINE.slnx` and `XRENGINE.sln` list the project;
- Bootstrap's AOT registration item and
  `Tools/Generate-AotFactoryRegistrations.ps1` scan the facade source root;
- Editor project generation points at `XRENGINE/XREngine.csproj`;
- Editor code-reload/discovery expects `XREngine.dll`;
- Bootstrap host services cast to the facade-owned `XRWorldInstance`;
- the facade emits 103 type forwards for moved world, UI, animation, audio,
  input/VR, and model-import types; and
- repository serialization tests still exercise exact legacy
  `Type.GetType("..., XREngine")` behavior.

Product branding, namespaces, environment-variable prefixes, shader macros,
and the solution filenames may continue to use the word `XRENGINE`. Phase 6
removes the assembly/project dependency, not the product name.

## Target Ownership Map

The default disposition is the existing design graph. Do not introduce a new
assembly merely to avoid deciding ownership.

| Remaining facade responsibility | Final owner |
|---|---|
| General serialization contracts, type-name rewriting, format-neutral YAML/JSON/cooked primitives, and lower asset metadata | `XREngine.Data` when they remain independent of runtime and feature implementations |
| Runtime asset loading, project state, scheduling, lifecycle, play mode, networking orchestration, world/scene ownership, and non-visual physics | `XREngine.Runtime.Core` |
| Animation clip/state-machine/blend-tree/motion serialization that requires animation types | `XREngine.Animation`, built on lower Data-owned serialization contracts |
| Render-asset serialization, visual-world publication, render picking, rendering debug components, and renderer-facing world state | `XREngine.Runtime.Rendering` |
| Runtime input/controllers/pawn-input routing and VR action behavior | `XREngine.Runtime.InputIntegration` |
| Editable geometry, topology, sculpting/subdivision, splines, procedural meshing, and renderer-independent modeling operations | `XREngine.Modeling` |
| Conversion and preview between runtime `XRMesh` data and editable modeling documents | `XREngine.Runtime.ModelingIntegration` |
| Engine-facing external model asset import/export orchestration, FBX/glTF/Assimp conversion, cooking, caches, runtime Unity reconstruction, prefab model publication, and import policy execution | `XREngine.Runtime.ModelAssetPipeline`; reusable format-only readers/writers remain in their lower format libraries |
| Shared startup profiles, game/window startup normalization, world host composition, game-mode selection, and application-safe adapter installation | `XREngine.Runtime.Bootstrap` |
| Editor preferences, encrypted editor secrets, authoring-only Unity conversion, project templates, and editor-only hidden-world behavior | `XREngine.Editor` |
| Pure physics data/algorithms already below runtime ownership | `XREngine.Data` or `XREngine.Runtime.Core`, according to whether world/device ownership is required |
| Physics visualization and GPU-facing diagnostic publication | `XREngine.Runtime.Rendering`, consuming lower physics snapshots/contracts |
| Type forwards whose only purpose is the old assembly identity | Remove after repository asset migration and redirect validation |

`XREngine.Data` must not become a miscellaneous dumping ground. If the
serialization audit proves that the approved graph cannot host the generic
serializer without feature or runtime coupling, stop P6.1, document the exact
cycle, and update the reference design before creating a focused lower
serialization project. Do not create `Utilities`, `Common`, or another vague
catch-all assembly.

## Target Dependency Contract

The Phase 5 graph remains authoritative, with the facade removed:

```text
Extensions
  -> no project references

Data
  -> Extensions

Animation, Audio, Modeling
  -> Data

Input
  -> Data, Extensions

Runtime.Core
  -> Data, Extensions

Runtime.Rendering
  -> Runtime.Core, Data, Extensions

Runtime.Rendering.OpenGL / Runtime.Rendering.Vulkan
  -> Runtime.Rendering, Runtime.Core, Data, Extensions

Runtime.AnimationIntegration
  -> Runtime.Core, Runtime.Rendering, Animation, Data

Runtime.AudioIntegration
  -> Runtime.Core, Runtime.Rendering, Audio, Data

Runtime.InputIntegration
  -> Runtime.Core, Runtime.Rendering, Input, Data, Extensions

Runtime.ModelingIntegration
  -> Runtime.Rendering, Modeling, Data

Runtime.ModelAssetPipeline
  -> Runtime.Core, Runtime.Rendering, Animation, Fbx, Gltf, Data, Extensions

Runtime.Bootstrap
  -> runtime layers, animation/audio/input integrations, required lower feature libraries,
     Data, Extensions, and selected renderer leaves for static composition

Applications, samples, benchmarks, and tests
  -> only the explicit assemblies they consume
```

No production project may depend on a removed facade, Editor, UnitTests, or an
application executable. Lower feature libraries must not acquire runtime or
Bootstrap edges. Runtime.Core must not acquire feature-library, adapter, or
Rendering edges.

## Compatibility And Migration Policy

Deleting an assembly is different from moving a type. CLR type forwarding can
preserve a moved type only while the forwarding assembly still exists. Phase 6
therefore uses the following policy:

- preserve repository-owned assets, scenes, prefabs, projects, cooked payloads,
  settings, and generated metadata by migrating their stored identities or by
  resolving them through a lower redirect registry before CLR assembly lookup;
- inventory every checked-in and generated `, XREngine` assembly-qualified
  identity before deleting forwards;
- provide an idempotent migration command when repository content cannot be
  safely rewritten at load time;
- make unknown legacy identities fail with the original name, asset path,
  expected owner assembly, and migration guidance;
- never silently deserialize a legacy type to a different public type;
- replace tests that require direct `Type.GetType("..., XREngine")` success with
  tests of the supported asset/cooked/project resolution path before deleting
  the facade; and
- document the intentional pre-v1 breaking change for third-party binaries or
  external content that directly reference `XREngine.dll` and are outside the
  repository migration corpus.

Do not keep an empty `XREngine.dll` solely to make an obsolete unit test pass.

## Scope Boundary

In scope:

- every production source file compiled by `XRENGINE.csproj`;
- all remaining facade consumers and Bootstrap casts/registration roots;
- asset loading, serialization, cooked formats, type identity, redirects,
  reflection, source generation, and AOT registration affected by the removal;
- physics, networking, gameplay/input, world composition, model/prefab import,
  and application startup currently coupled through the facade;
- package, native, content, license, solution, build, publish, editor discovery,
  sample, benchmark, and project-template ownership; and
- deterministic build, test, application, and publish validation of the final
  graph.

Out of scope:

- new animation, audio, input, physics, rendering, networking, or import
  features;
- dependency upgrades, replacements, or supply-path changes unless separately
  approved;
- a general physics project split beyond the Runtime.Core ownership already in
  the reference design;
- renaming the product, solution files, namespaces, shader macros, environment
  variables, or user-visible `XRENGINE` branding merely because the facade
  project is deleted;
- preserving unsupported third-party pre-v1 binary compatibility; and
- unrelated editor UI redesign or renderer backend reorganization.

## Working Rules

- Move by coherent ownership slice. Keep the solution buildable between slices;
  do not copy all 358 files and sort them out afterward.
- Dismantle facade partial types behind focused services/contracts. C# partial
  classes cannot span assemblies, so do not attempt to split `Engine`,
  `AssetManager`, or `XRWorldInstance` declarations across projects.
- Prefer deleting legacy static facade APIs after consumers migrate rather than
  transplanting the same monolith into Runtime.Core or Bootstrap.
- Keep Bootstrap a composition root. It may wire adapters and applications but
  must not absorb physics, serialization, import, gameplay, or editor domain
  implementation.
- Keep Data lower and domain-neutral. A moved API must not smuggle Runtime.Core,
  Rendering, adapter, native-device, or application types into Data signatures.
- Preserve required behavior visibly. Missing serializers, importers, physics
  backends, input adapters, or renderer capabilities must produce actionable
  diagnostics rather than unrelated fallback behavior.
- Preserve lifecycle ordering, event subscription symmetry, thread affinity,
  world teardown, device/resource ownership, cancellation, and process shutdown
  while replacing static facade access.
- Use `SetField(...)` for mutation paths on `XRBase` descendants.
- Introduce no heap allocations in per-frame update, fixed update, render
  publication, input routing, physics, or networking hot paths.
- Move package/native/content/license ownership with code. Do not duplicate cargo
  temporarily without a dated removal item in the same slice.
- Update source-contract tests to follow type/member ownership rather than
  brittle physical partial filenames.
- Complete the relevant live/build path before adding or updating regression
  tests for that slice, in accordance with repository testing policy.
- Preserve unrelated worktree changes and use one bounded
  `Build/_AgentValidation/<run>/` root for Phase 6 evidence.

## P6.0 - Accept Phase 5 And Lock The End State

- [x] Start from an exact commit containing the completed Phase 4/5 work, record the branch/base commit, and separate unrelated dirty-worktree changes.
- [x] Create the Phase 6 progress ledger under `docs/work/progress/runtime/` and record every validation result, compatibility decision, and deferred external hardware lane there.
- [x] Recount all facade C# files, public types, generated sources, project references, package references, native/content items, licenses, build targets, friend assemblies, type forwards, redirects, serializers, reflection roots, and AOT registrations.
- [x] Build a checked-in file/type-to-owner manifest for every facade source file; no entry may be `miscellaneous`, `temporary`, or unclassified.
- [x] Inventory every production and test consumer of facade public/internal APIs, including static `Engine`, `AssetManager`, `XRWorldInstance`, settings, physics components, prefabs, and game modes.
- [x] Inventory checked-in assets, generated settings, test fixtures, docs, templates, and build artifacts containing assembly-qualified `XREngine` identities.
- [x] Capture the direct and source/API-level dependency graph for every intended destination project before moving code.
- [x] Confirm Option A removal as the accepted end state. If an owner instead requires Option B, update the reference design with an explicit compatibility scope and expiry before implementation.
- [x] Define the supported repository asset migration path and the intentional external pre-v1 compatibility break before removing any type forward.
- [x] Record baseline builds for all destination projects and the seven direct facade consumers.
- [x] Record baseline targeted serialization, asset, physics, networking, world, input/gameplay, rendering, import, AOT, and project-graph tests.
- [x] Record baseline Editor OpenGL/Vulkan, headless Server, and VRClient startup behavior using canonical isolated paths.
- [x] Record package/native publish layouts for Editor, Server, and VRClient so cargo loss or duplication is detectable.
- [x] Add Phase 6 dependency/source tests that initially describe the desired graph and can be enabled slice-by-slice without passing merely because a source directory disappeared.

## P6.1 - Extract Serialization And Asset Foundations

This slice must precede feature and application migration because
`AssetManager`, cooked binary, YAML converters, model cache, and type identity
currently bind otherwise independent ownership areas together.

- [x] Classify all 38 `Core/Files` sources and all 36 `Core/Engine` asset/serialization sources as Data, Runtime.Core, Animation, Runtime.Rendering, ModelingBridge, Bootstrap, Editor, or deletion.
- [x] Move format-neutral serialization contracts, type-name rewriting, asset metadata, snapshot/reference primitives, and cooked-binary core modules to Data when they preserve the lower dependency graph.
- [x] Move runtime asset loading, publication, file watching, project-relative path resolution, remote loading, save orchestration, and runtime cache coordination to Runtime.Core behind focused services rather than one cross-layer `AssetManager` partial.
- [x] Move animation-specific cooked/YAML serializers to Animation and register them from the composition layer without adding `Animation -> Runtime.Core` or adapter edges.
- [x] Keep render-asset serializers in Runtime.Rendering and expose only lower registration/value contracts to the asset runtime.
- [x] Move model-cache codecs, import-option snapshots, third-party model load policy, and model publication to ModelingBridge; keep editor preference selection in Editor.
- [x] Move editor-only file watching, secrets, authoring metadata, import prompts, and project UX to Editor.
- [x] Decide the owner of asset packing, compression, DirectStorage IO, hashing, and protected-data helpers from their real consumers; do not preserve facade packages through transitive use.
- [x] Replace static cross-layer callbacks with explicit installation leases that reset deterministically for tests, shutdown, and collectible editor generations.
- [x] Preserve YAML, JSON, cooked-binary, MemoryPack, prefab, scene, project, and snapshot semantics for the repository corpus.
- [x] Ensure unknown serializer/importer kinds fail with the asset path and missing owner/registration name.
- [x] Update generated/AOT registration inputs so each final owner contributes its own factories and converters without scanning `XRENGINE`.
- [x] Build Data, Animation, Runtime.Core, Runtime.Rendering, ModelingBridge, Bootstrap, and UnitTests with no new warnings.
- [x] Run the relevant live asset load/import path, then update and run deterministic round-trip, legacy-resolution, cooked-binary, YAML/JSON, snapshot, AOT, and missing-registration tests.
- [x] Update the reference design if a new lower serialization assembly is proven necessary; include its exact dependency set and reject a generic utility assembly. No new assembly was necessary, so the accepted graph remains unchanged.

## P6.2 - Move Core Engine, Physics, Networking, And World Ownership

- [x] Classify every remaining `Engine` partial/member as Runtime.Core service, Bootstrap composition, application policy, diagnostics tooling, or deletion.
- [x] Move lifecycle, shutdown, timing, work scheduling, main-thread invocation, memory policy, play mode, and runtime state into focused Runtime.Core owners; migrate consumers off the legacy static `Engine` surface.
- [x] Move core project/runtime settings application to Runtime.Core while keeping editor preferences and application-specific overrides out of the core.
- [x] Move networking managers, discovery, session resolution, world asset identity, join handoff, and remote-job transport to Runtime.Core or a documented lower networking owner without referencing InputIntegration or applications.
- [x] Move non-visual physics scenes, actors, bodies, shapes, constraints, queries, controllers, chain simulation, and static-collider runtime behavior to Runtime.Core.
- [x] Split the 140 facade physics-component files by behavior: Runtime.Core owns simulation/world components; Runtime.Rendering owns only visual/debug publication; InputIntegration owns only input-driven controller behavior.
- [x] Consolidate JoltPhysicsSharp, MagicPhysX, CoACD, and their native cargo/build targets under the actual physics or geometry owner, preserving license and dynamic-link requirements.
- [x] Remove root physics global-usings/aliases after callers use destination-owned namespaces and explicit contracts.
- [x] Preserve fixed-update ordering, physics-world creation/destruction, scene attach/detach, async cooking, cancellation, collision events, and deterministic shutdown.
- [x] Keep GPU physics dispatch behind existing lower data/render publication contracts; do not add `Runtime.Core -> Runtime.Rendering`.
- [x] Replace facade internals/friend access with narrow APIs only where a real cross-assembly boundary exists.
- [x] Build Runtime.Core independently and prove it still references only Data and Extensions.
- [x] Start the headless Server through the migrated lifecycle/networking/physics path before updating regression tests.
- [x] Update and run targeted lifecycle, scheduler, play-mode, networking, physics, collision/query, cooking, world teardown, and dependency-boundary tests.

## P6.3 - Decompose XRWorldInstance And Rendering Composition

`XRWorldInstance` is a large facade aggregate spanning Core lifecycle, physics,
render publication, editor-only nodes, input/pawn control, game modes, asset
loading, and GPU picking. Moving the class unchanged would recreate the facade
inside another project.

- [x] Inventory every `XRWorldInstance` field, event, method, interface, static registry, and call site by Core, Rendering, InputIntegration, Bootstrap, Editor, or deletion ownership.
- [x] Make Runtime.Core's world lifecycle/context the canonical non-visual world owner and remove duplicate play/scene/root-node state from the aggregate.
- [x] Move visual scene state, lights, render registration, render picking, render queries, and rendering-facing world behavior to Runtime.Rendering.
- [x] Keep physics ownership in Runtime.Core and expose only snapshot/query contracts required by Rendering diagnostics or GPU dispatch.
- [x] Move input/pawn/controller refresh behavior to InputIntegration and game-mode/world host composition to Bootstrap.
- [x] Move hidden editor scene creation, gizmo/tool nodes, and editor-only world policy to Editor.
- [x] Replace static `XRWorldInstance.WorldInstances` lookup with an ownership-correct world-host registry whose lifetime is explicit and test-resettable.
- [x] Migrate Bootstrap VR/render/game-mode host services away from concrete `XRWorldInstance` casts to focused Core/Rendering world contracts.
- [x] Preserve scene load/unload, begin/end play, transform invalidation, render registration, physics teardown, multi-world, and editor-hidden-scene behavior.
- [x] Remove the facade `XRWorldInstance` type rather than retaining a renamed cross-layer aggregate.
- [x] Run live OpenGL and Vulkan Editor paths with more than one camera view after the decomposition, inspect screenshots/logs, and stop only owned sessions. OpenGL produced distinct correct views; Vulkan proved world/render publication but its final presentation remained blocked by the separately tracked PresentNow frame-readiness work.
- [x] Update and run world lifecycle, render registration, GPU picking, physics/render coordination, editor-scene, VR-world, and dependency-boundary tests.

## P6.4 - Move Gameplay, Input, Startup, And Settings Composition

- [x] Move or replace `Engine.Input`, input-facing networking glue, character/pawn routing, movement components, and controller behavior according to Core versus InputIntegration ownership.
- [x] Move FlyingCamera, Locomotion, and VR game-mode composition to Bootstrap or InputIntegration without making Runtime.Core depend on Input.
- [x] Move game/window startup settings and runtime-safe defaults to Data or Bootstrap according to whether they are values or composition policy.
- [x] Move editor preference groups, overrides, encrypted secrets, and editor runtime-environment preferences to Editor; expose only stable lower settings DTOs to runtime projects.
- [x] Move window-pump and application-loop policy to the application/Bootstrap boundary while keeping window/render context implementation in Runtime.Rendering.
- [x] Replace legacy `Engine.Windows`, viewport rebind, and input globals with focused Runtime.Rendering/InputIntegration services.
- [x] Preserve local/remote controller possession, pawn-camera selection, UI input capture, window snapshots, play-mode changes, and VR action routing.
- [x] Ensure the headless Server profile installs no local input, window, audio, or VR services and does not load their native cargo.
- [x] Ensure Editor and VRClient install the complete intended adapter profile with deterministic teardown.
- [x] Migrate sample game composition without adding sample types to Bootstrap or runtime libraries.
- [x] Run the live Server and available Editor/VRClient paths before updating regression tests.
- [x] Update and run targeted game-mode, possession, movement, input, window, settings, profile-installation, headless-absence, and VR startup contract tests.

P6.4 validation completed on 2026-08-27: Server ran with the simulation-only profile and no local native cargo loaded; the isolated Editor profile reached MCP readiness and produced a Vulkan readback; VRClient started and shut down through its aggregate lease. The Phase 6 boundary suite passed (6/6). The broader feature-filtered test build remains blocked by unrelated Vulkan test/API drift recorded in the task handoff.

## P6.5 - Move Prefab, Unity, Model Cache, And Import Policy

- [x] Classify base prefab/source/variant contracts separately from Unity authoring metadata and runtime model construction.
- [x] Move runtime-neutral prefab contracts to Runtime.Core or Data without importing Editor or ModelingBridge types into lower signatures.
- [x] Move runtime prefab instantiation/world attachment to Runtime.Core and adapter-specific component activation to explicit composition services.
- [x] Move Unity YAML/schema parsing, editor diagnostics, unsupported-behavior metadata, project-path policy, and authoring conversion UX to Editor.
- [x] Move runtime Unity model/mesh/material/skin/blendshape/animation reconstruction to ModelingBridge using lower input DTOs rather than Editor types.
- [x] Move `AssetManager.ThirdPartyImport`, model cache identity/codecs, cook override snapshots, and Unity model producer behavior to ModelingBridge or Editor according to execution ownership.
- [x] Keep Assimp, FBX, glTF, Modeling, and native importer dependencies out of Runtime.Core, Rendering, and Bootstrap.
- [x] Preserve prefab variants, asset identity, dependency manifests, handedness, scale, materials, submeshes, skinning, blendshapes, animations, async publication, progress, and cancellation.
- [x] Preserve actionable diagnostics for absent formats, malformed source assets, unsupported Unity behavior, and cache invalidation.
- [x] Remove facade Assimp/Fbx/import package references and native pins after the final importer owner publishes them.
- [x] Run representative live model/prefab imports before updating regression tests.
- [x] Update and run prefab, Unity conversion, FBX/glTF, model-cache, mesh/material, animation reconstruction, cancellation, and serialization compatibility tests.

P6.5 validation completed on 2026-08-27: an isolated Editor session imported and reimported an external glTF model and a Unity prefab into native prefab assets, reloaded both assets, and published the Unity dependency-manifest sidecar without duplicate IDs, generation leaks, or watcher races. The focused prefab/cache, Unity conversion, native FBX/glTF, model reconstruction, cancellation, and serialization suites passed 184 tests with six private-fixture skips; four additional application-composition/profile checks passed. Data, Runtime.Core, Runtime.Rendering, ModelingBridge, Bootstrap, Editor, Server, VRClient, and the redirected UnitTests graph built successfully, with only the existing OscCore submodule warnings in upper application graphs. Concrete application roots now install ModelingBridge explicitly while generic Bootstrap consumes only the lower runtime model-scene contract, and the regenerated dependency inventory lists ModelingBridge as the sole direct AssimpNetter/native importer owner. The normal UnitTests graph remains blocked only by the two in-progress Vulkan API/test changes.

## P6.5A - Separate Modeling Authoring From Model Asset I/O

These names describe distinct lifecycles and must not be used interchangeably:

| Responsibility | Meaning | Owner |
|---|---|---|
| Modeling authoring | Mutating editable vertices, edges, faces, surfaces, sculpt data, subdivision meshes, splines, and procedural topology | `XREngine.Modeling` |
| Runtime authoring integration | Explicit in-memory conversion and preview between an editable modeling document and runtime `XRMesh` data | `XREngine.Runtime.ModelingIntegration` |
| Model asset pipeline | Reading/writing external model formats at the engine boundary, producer selection, import reconstruction, cooking, caching, prefab publication, and Unity runtime metadata | `XREngine.Runtime.ModelAssetPipeline` |
| Ordinary runtime consumption | Loading, instantiating, rendering, skinning, animating, and otherwise using already-produced meshes/materials/prefabs in a game | `XREngine.Runtime.Core`, `XREngine.Runtime.Rendering`, and the owning lower feature libraries |

P6.5A supersedes the temporary `ModelingBridge` name and mixed ownership recorded in the completed P6.5 history above.

- [x] Define and document three independent responsibilities: editable geometry authoring, external model asset I/O/cooking, and ordinary runtime model consumption.
- [x] Keep `XREngine.Modeling` focused on renderer-independent editable geometry, topology, sculpting/subdivision, splines, procedural meshing, and modeling operations.
- [x] Replace `XREngine.Runtime.ModelingBridge` with a narrowly scoped `XREngine.Runtime.ModelingIntegration` project for conversions and previews between runtime render meshes and editable modeling documents.
- [x] Create `XREngine.Runtime.ModelAssetPipeline` as the sole owner of engine-facing Assimp/FBX/glTF model I/O orchestration, cooking, cache codecs and policy, prefab production, and runtime Unity asset reconstruction; keep reusable format-only readers/writers in their lower format libraries.
- [x] Keep ordinary game/runtime use of already-produced meshes, materials, prefabs, skinning, blendshapes, and animations in Runtime.Core/Runtime.Rendering without dependencies on Modeling or ModelingIntegration.
- [x] Rename ambiguous bridge/import APIs and composition hooks so their names identify either authoring integration or external model asset pipeline ownership.
- [x] Update application composition, assembly discovery, AOT/source-generation inputs, solution membership, project references, dependency inventory, architecture docs, and ownership ledgers for the new boundary.
- [x] Add source-ownership and evaluated project-graph contracts that prevent authoring code, importer/native dependencies, and ordinary runtime consumption from crossing the boundary.
- [x] Validate representative editable-mesh conversion/preview and external model/prefab import paths before updating or running regression tests.
- [x] Build the affected project graph and run the focused modeling, model-import, prefab/cache, composition, and phase-6 boundary suites.

P6.5A validation completed on 2026-08-27: an isolated Editor authoring run baked a cube/sphere union into a 1,236-vertex runtime mesh and produced camera-dependent preview captures. A direct probe loaded a minimal glTF through `XREngine.Runtime.ModelAssetPipeline.dll`, returning the expected scene root and `assimp` producer/backend; the successful P6.5 external glTF and Unity-prefab publication run remains the live prefab baseline. A separate Editor import attempt entered the new pipeline and selected the native glTF/Assimp candidates before the later transactional asset-publication step encountered the existing `XRTexture2DView`/YamlDotNet default-constructor limitation while walking unrelated loaded assets; that editor externalization defect is outside this ownership split. ModelAssetPipeline, ModelingIntegration, Bootstrap, Editor, Server, VRClient, Benchmarks, and the isolated UnitTests graph built with zero warnings and zero errors. The focused modeling, importer, prefab/cache, composition, serialization-compatibility, and phase-boundary suites passed 151/151 tests; the isolated graph excluded only the two concurrent Vulkan test files. The regenerated source-ownership ledger contains 384 rows and 519 public declarations, the evaluated project-graph report contains 25 rows, and the dependency inventory identifies ModelAssetPipeline as the sole direct AssimpNetter owner.

## P6.5B - Normalize Imported-Asset Semantics And Make Conversion Loss Explicit

Source ecosystems are parser inputs, not engine architecture. Their names may appear in source-file keys, shader names, GUID matchers, provenance strings, compatibility payloads, and diagnostics, but must not appear in engine/editor property, field, method, event, enum, interface, class, record, parameter, local, or namespace identifiers. Converted runtime objects are named for the native behavior they provide; source-schema records use `Serialized*`, and provenance-aware conversion results use `Imported*`.

| Treatment | Data |
|---|---|
| Native engine semantics | Avatar presentation anchors, gaze and eyelid bindings, jaw and speech-pose bindings, humanoid retargeting/root motion that has a native implementation, weighted transform constraints, physics-chain behavior, and generic toon/material features |
| Import-sidecar evidence only | Source GUID/file IDs, editor/project versions, dependency manifests, shader/property matches, serialized controller identities, layer records, provenance, and conversion reports |
| Intentional conversion loss with diagnostics | Upload/pipeline state, platform policy and network semantics, arbitrary callbacks and opaque behaviours, expression menus or parameter networks without a native graph, unsupported controller behaviours, source-solver quirks, unsupported collider/grab/pose rules, editor-only shader drawers/remotes, and shader-family features with no native material meaning |

Recommended conversion-loss decisions:

- [ ] Stop publishing `ImportedAnimatorMetadataComponent` and `ImportedAvatarAnimationLayer` in runtime assets unless their controller, mask, and layer records are compiled into an executable native animation graph; retain source identities in the import manifest only.
- [ ] Treat raw animator update/culling modes, platform controller layers, expression menus, parameter networks, upload identity, pipeline status, and platform/network policy as intentional loss with actionable diagnostics.
- [ ] Convert imported animation callbacks only through an explicit native event allowlist; discard arbitrary name-based callbacks and source-only component/property binding adapters rather than emulating the source runtime.
- [ ] Keep unsupported behaviour payloads and serialized YAML out of runtime assets; retain a bounded import-report summary for inspection, or discard the opaque payload completely when it has no supported conversion.
- [ ] Preserve only generic physics-chain, collider, and weighted-constraint behavior; report unsupported grabbing, posing, collision-filtering, solver, freeze/rebake, and source-policy semantics as intentional loss.
- [ ] Convert recognized toon inputs into native material features and retain conversion provenance, but discard editor drawers, remote facilities, shader-family-only controls, and unsupported shader features rather than creating a source-shader runtime authoring model.
- [ ] Continue retaining avatar presentation anchors, gaze/eyelid bindings, jaw and speech-pose bindings, native humanoid retargeting/root motion, weighted transform constraints, physics-chain behavior, and generic material features because these have independent engine semantics.

- [x] Replace source-vendor terms in C# declarations with native, `Serialized*`, `Imported*`, or `Source*` vocabulary; do not add compatibility shim types before v1.
- [x] Rename imported avatar runtime components for their engine behavior, including `AvatarPresentationComponent`, `AvatarGazeBinding`, `AvatarLipSyncMode`, and `WeightedTransformConstraintComponent`.
- [x] Keep source GUIDs, serialized property names, shader names, environment variables, provenance labels, and diagnostic codes as boundary strings where exact identity is required.
- [x] Treat raw animator/controller metadata and avatar layer records as transitional import evidence, not promises of SDK-compatible runtime execution.
- [x] Preserve generic physics-chain and transform-constraint conversions while diagnosing unsupported source-only collision, grabbing, posing, freeze/rebake, and policy behavior.
- [x] Convert recognized toon semantics into native material features and reports without creating a durable source-shader runtime authoring model.
- [x] Audit production, tooling, and test declarations with syntax-aware identifier matching so strings and comments do not produce false positives.
- [x] Reuse the P6.5 live prefab conversion as the behavioral baseline, build the lowest affected projects, and record unrelated downstream build blockers without changing concurrent renderer work.

P6.5B validation completed on 2026-08-27: the syntax-aware inventory found 602 source-vendor-shaped declarations (497 unique identifiers) before the migration and none after it. `XREngine.Animation` built successfully from isolated artifacts after the rename. The downstream AnimationIntegration/ModelAssetPipeline build reached the concurrently modified Rendering project and stopped on its existing `AdvancedMaterialDatabase` errors (`TryGetLayoutMembers` missing and an ambiguous `ulong`/`int` comparison); P6.5B did not alter those renderer files. The successful P6.5 live prefab import/reimport remains the runtime conversion baseline, and exact source names remain confined to parsing, provenance, compatibility strings, and diagnostics.

## P6.6 - Migrate Applications, Samples, Benchmarks, Tests, And Tooling

- [ ] Replace Bootstrap's facade reference with direct Core/Rendering/adapter contracts and remove all concrete facade world casts.
- [ ] Replace Editor's facade reference with explicit project references based on real source/API use; remove `XREngine.dll` from assembly discovery/reload/watch lists.
- [ ] Replace Server's facade reference and prove its evaluated dependency/publish graph excludes Editor and unused local adapter/native cargo.
- [ ] Replace VRClient's facade reference and preserve its explicit startup/profile/no-peer diagnostics and intended renderer/VR cargo.
- [ ] Replace UnitTests' facade reference by moving tests to the owning assembly surface; retain direct leaf references only for tests of leaf implementation.
- [ ] Replace Benchmarks' facade reference and update benchmark source maps so they measure current owners rather than deleted paths.
- [ ] Replace `Samples/MonkeyBallVR` facade use with direct runtime/Bootstrap references and validate sample startup.
- [ ] Update Editor project templates and generated application projects so new games never reference `XRENGINE/XREngine.csproj`.
- [ ] Remove `XRENGINE/**/*.cs` from Bootstrap and script AOT scan roots; enumerate only final owning projects.
- [ ] Update solution/project discovery code that means the facade project while preserving legitimate `XRENGINE.slnx`, product-name, macro, and branding strings.
- [ ] Update `.slnx`, legacy `.sln`, VS Code tasks/launch profiles, ExecTool scripts, publish profiles, CI workflows, dependency reports, and docs that reference the project or DLL.
- [ ] Validate single-backend static/AOT and collectible editor registration without relying on facade assembly load side effects.
- [ ] Build and launch each migrated consumer before removing the facade project.
- [ ] Add graph/source tests proving none of the seven former consumers references or loads `XREngine.dll`.

## P6.7 - Remove Compatibility Forwards, Cargo, And The Facade Project

- [ ] Complete the repository asset/type-identity migration and archive a durable report of every rewritten or redirected legacy identity.
- [ ] Prove repository YAML, JSON, cooked, MemoryPack, prefab, scene, project, and generated settings resolve without loading `XREngine.dll`.
- [ ] Replace exact legacy CLR lookup tests with supported loader/redirect tests and explicit external-breaking-change tests.
- [ ] Remove all 103 facade type forwards after their repository consumers and persisted identities are migrated.
- [ ] Remove facade-only type redirects, friend access, source-generation roots, reflection scans, assembly lists, load-context exceptions, and compatibility shims.
- [ ] Move or remove all 19 facade package references; validate that each remaining package is declared by its real direct consumer.
- [ ] Move `lib_coacd.dll`, optional RestirGI copy behavior, NIS license cargo, and any remaining native/content items to one final owner or remove dead cargo.
- [ ] Run `pwsh Tools/Generate-Dependencies.ps1` after the final package/native move and review `docs/DEPENDENCIES.md`, license snapshots, and ownership overrides.
- [ ] Remove `XRENGINE/XREngine.csproj` from `XRENGINE.slnx` and `XRENGINE.sln`.
- [ ] Delete the now-empty `XRENGINE` production source/project directory after verifying every tracked file has a recorded disposition.
- [ ] Prove no build, publish, test, template, script, or runtime path produces, copies, loads, probes for, or requires `XREngine.dll`.
- [ ] Prove searches for `XRENGINE/XREngine.csproj`, facade-only source paths, and assembly identity return no active references; classify legitimate product/solution-name strings separately.
- [ ] Remove temporary migration adapters and compatibility leases created during Phase 6.
- [ ] Update the reference design from proposed architecture to completed end state and record any deliberate deviation.

## P6.8 - Final Validation And Closeout

- [ ] Build Data, all lower feature/format libraries, Runtime.Core, Runtime.Rendering, both renderer leaves, all four adapters, Bootstrap, Editor, Server, VRClient, UnitTests, Benchmarks, and samples independently with zero new warnings.
- [ ] Build `XRENGINE.slnx` with zero warnings and zero errors after the facade project has been removed from it.
- [ ] Run the Phase 3/4/5/6 dependency, source/API, serialization, world, rendering, adapter, AOT, packaging, and compatibility regression suites.
- [ ] Run targeted asset/cooked/YAML/JSON/snapshot, physics, networking, scheduler, play-mode, prefab, Unity, FBX/glTF/model-cache, input/gameplay, rendering, OpenXR, and project-template tests.
- [ ] Run the full UnitTests project after unrelated concurrent work is reconciled; distinguish any unrelated pre-existing failure from a Phase 6 regression before closeout.
- [ ] Start isolated named Editor sessions for canonical OpenGL and Vulkan Unit Testing World paths, verify MCP readiness, inspect changing screenshots and logs, and stop only owned sessions.
- [ ] Run bounded headless Server startup through world creation, play, networking, and graceful shutdown with no local window/input/audio/VR dependency.
- [ ] Publish and launch VRClient through its canonical path; validate the available runtime path and record unavailable physical-headset checks without claiming them.
- [ ] Validate Debug/Release and at least one selected-backend static/AOT or trimming configuration; inspect outputs for required assemblies/native cargo and absence of `XREngine.dll`.
- [ ] Re-run collectible renderer registration/unload validation and prove facade deletion introduced no stable assembly/type retention root.
- [ ] Audit per-frame/fixed-update/render/input/network hot paths touched by the move for new allocations, boxing, LINQ, closures, and string construction.
- [ ] Run `git diff --check`, local documentation-link validation, dependency/license review, and final source/project/cargo audits.
- [ ] Update launch/setup, architecture, asset migration, serialization, application composition, and contributor documentation.
- [ ] Record exact commands, results, evidence paths, known external hardware lanes, intentional breaking changes, and final dependency graphs in the Phase 6 progress ledger.
- [ ] Move this tracker to `docs/work/todo/COMPLETED/` only after every engineering completion gate below passes.
- [ ] Record commit/merge/promotion status accurately; do not mark engineering work incomplete merely because branch promotion was not requested, and do not claim a merge that was not performed.

## Validation Matrix

| Lane | Required evidence |
|---|---|
| Dependency direction | Project and public-API graph tests for every lower library, runtime layer, adapter, Bootstrap, application, sample, benchmark, and test project |
| Serialization/assets | Repository identity inventory, migration report, YAML/JSON/cooked/MemoryPack/snapshot round trips, missing-registration diagnostics, and no facade load |
| Runtime.Core | Lifecycle, scheduler, play mode, settings, asset runtime, world/scene, networking, physics, teardown, and independent dependency graph |
| Rendering/world | World decomposition, registration, picking, debug publication, OpenGL/Vulkan live output, backend neutrality, and collectible unload |
| Input/gameplay | Controllers, possession, movement, game modes, UI capture, window routing, VR actions, profile install/teardown, and headless absence |
| Modeling/import | Prefabs, Unity authoring/runtime split, FBX/glTF/Assimp, model cache, mesh/material/skin/blendshape/animation reconstruction, async/cancellation |
| Applications | Editor OpenGL/Vulkan, headless Server, VRClient available path, MonkeyBall sample, project templates, and benchmark discovery |
| AOT/publish | Static registration, trimming/AOT scan roots, selected backend outputs, native cargo ownership, and absence of `XREngine.dll` |
| Quality | Zero new warnings, no hot-path allocation regression, no duplicate public identity, no silent fallback, clean diff/docs links, and reviewed licenses |

## Phase 6 Completion Gates

- [ ] Every one of the 358 baseline facade source files has a final owner or an explicit deletion record.
- [ ] `XRENGINE/XREngine.csproj` and the facade production directory are removed.
- [ ] No project, solution, task, script, template, source generator, AOT scan, publish path, or runtime loader requires the facade project or `XREngine.dll`.
- [ ] Runtime.Core references only Data and Extensions and contains no rendering, feature-library, adapter, Bootstrap, Editor, or application dependency.
- [ ] Runtime.Rendering remains backend-neutral and both concrete backends remain one-way leaves.
- [ ] Feature libraries remain below runtime adapters, and no adapter references another adapter or an application.
- [ ] Bootstrap contains composition only and no migrated domain implementation.
- [ ] Repository-owned assets and generated metadata load through current identities without `XREngine.dll`; unsupported external pre-v1 compatibility is documented explicitly.
- [ ] Editor, Server, VRClient, UnitTests, Benchmarks, samples, and generated projects build through direct explicit references.
- [ ] Editor OpenGL/Vulkan, headless Server, and the available VRClient path launch successfully through the final graph.
- [ ] Static/AOT, trimming, publish, and collectible registration paths include exactly the intended assemblies and native cargo.
- [ ] No facade-owned package, native binary, content rule, license, type forward, friend access, registration root, or implementation remains.
- [ ] Targeted and full regression gates pass with zero new compiler warnings and no unresolved Phase 6 failure.
- [ ] The reference design and contributor/application/asset migration documentation describe the implemented end state.
- [ ] The Phase 6 progress ledger contains the final evidence and this tracker is moved to `COMPLETED/`.

## Recommended Execution Order

1. Accept Phase 5 and lock the file/type ownership manifest.
2. Extract lower serialization and runtime asset foundations.
3. Move core Engine, physics, networking, and world ownership.
4. Decompose `XRWorldInstance` across Core, Rendering, InputIntegration,
   Bootstrap, and Editor.
5. Move gameplay/input/startup/settings composition.
6. Split prefab/Unity/model-cache/import policy ownership.
7. Migrate all applications, samples, benchmarks, tests, templates, and AOT
   tooling to direct references.
8. Migrate repository identities, remove forwards/cargo, and delete the facade.
9. Run the final validation matrix and publish the completed architecture record.

Phase 6 closes the modularization plan. Any later assembly split or subsystem
redesign must be justified independently rather than being hidden as unfinished
facade removal.

# AOT TODO For Final Game Builds

Last Updated: 2026-03-09
Current Status: NativeAOT publish plumbing exists, but the runtime is not yet AOT-safe for shipped final game builds.
Scope: Final cooked game/server/launcher builds only. The Editor remains CoreCLR/JIT and may continue loading user-written managed plugins during development.

## Phase 0 Implementation

Phase 0 is now implemented as the boundary definition for the current codebase.

- First-class NativeAOT scope today:
  - `Cooked Final Game` launcher published through `BuildSettings.PublishLauncherAsNativeAot`
- Explicitly out of AOT scope today:
  - `Editor/Dev`
  - `VR Client`
  - dynamic managed plugin host paths
  - `Dedicated Server` until it gets its own validated publish flow
- The shipping AOT boundary is identified in code by two compile constants:
  - `XRE_PUBLISHED` for published launcher-style runtime builds
  - `XRE_AOT_RUNTIME` for NativeAOT launcher runtime builds
- Generated launcher builds now add these constants automatically when `PublishLauncherAsNativeAot` is enabled.
- The generated launcher configures `XRRuntimeEnvironment` at startup so the prebuilt engine assemblies can observe the runtime boundary without relying on engine-library compile constants.
- The runtime boundary is centralized in `XRENGINE/Core/Engine/XRRuntimeEnvironment.cs` so later phases can query one place instead of scattering build-flag assumptions.

## Runtime Capability Matrix

| Runtime | AOT target now | Dynamic managed plugins | Editor/dev reflection tooling | Shipping boundary constant |
|---|---|---|---|---|
| Editor/Dev | No | Yes | Yes | none |
| Cooked Final Game | Yes | No | No | `XRE_PUBLISHED` + `XRE_AOT_RUNTIME` |
| Dedicated Server | Not yet | No in eventual AOT mode | No in eventual AOT mode | deferred |
| VR Client | Not yet | No | No | deferred |

## Target Outcome

At the end of this work:

- cooked final game launchers can be published with `PublishAot=true`
- shipped runtime builds do not rely on runtime IL emission, runtime managed plugin loading, or arbitrary reflection-driven serialization
- editor and dev workflows remain unchanged unless they directly affect shipping runtime code paths
- AOT-only restrictions are isolated to shipping/runtime code paths rather than imposed on the editor

## Non-Goals

- Do not make `XREngine.Editor` AOT-compatible.
- Do not remove editor plugin loading or hot-reload workflows for development.
- Do not build a custom IL2CPP-style transpiler as part of this work.
- Do not require all reflection in the repo to disappear; only shipping runtime paths need to be AOT-safe.

## Production Gates

The engine is ready for NativeAOT final builds only when all gates are true:

- Build pipeline
  - final launcher publish path produces a successful NativeAOT build for the intended RID
  - AOT and trimming warnings are either fixed or intentionally gated off from shipping paths
- Runtime behavior
  - shipped builds do not use runtime managed assembly loading
  - shipped builds do not use `System.Reflection.Emit` or runtime expression compilation
  - shipped builds do not depend on unbounded runtime type scanning
- Serialization and metadata
  - shipped JSON payloads use source-generated or explicitly registered metadata
  - cooked asset loading/saving paths used at runtime do not depend on arbitrary reflection
- Scene/runtime systems
  - prefab and transform systems used in shipping builds do not require blanket reflection discovery
  - marshalling paths used in shipping builds are blittable/AOT-safe
- Validation
  - at least one representative cooked game build publishes and launches as NativeAOT
  - one representative server or dedicated runtime build publishes and launches as NativeAOT if that target is in scope

## Current Reality

What already exists:

- `BuildSettings.PublishLauncherAsNativeAot`
- `XREngine.Editor/ProjectBuilder.cs` gates NativeAOT to launcher publish flow
- multiple projects declare `IsAotCompatible=True` and `IsTrimmable=True`

What still blocks or weakens final AOT builds:

- runtime enum generation in `XREngine.VRClient/Program.cs`
- runtime managed plugin loading in `XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs`
- runtime expression compilation in `XRENGINE/Core/Tools/DelegateBuilder.cs`
- reflection-based binary serialization and cooked asset loading in `XRENGINE/Core/Files/*` and related runtime callers
- **`XRENGINE/Rendering/XRWorldObjectBase.cs` — static constructor runs the first time any `XRWorldObjectBase`-derived type is used, scanning every loaded assembly to build networking replication metadata. This is a hidden automatic blocker: you cannot even load a world object without triggering a full assembly scan.**
- `XRENGINE/Core/XRTypeRedirectRegistry.cs` — scans all assemblies at first use to build a type-redirect map for deserialization backwards compatibility
- `XRENGINE/Core/Engine/AssetManager.Serialization.cs` — creates static `Serializer` and `Deserializer` YamlDotNet instances at type initialization by scanning all assemblies for `IYamlTypeConverter` implementations. YamlDotNet itself is not NativeAOT-compatible without explicit type registration and is not on the AOT-friendly library list.
- runtime type discovery in `XRENGINE/Scene/Transforms/TransformBase.cs` and nearby discovery paths
- prefab override reflection in `XRENGINE/Scene/Prefabs/*` and `XRENGINE/Rendering/XRWorldInstance.cs`
- generic marshalling risk in `XRENGINE/Rendering/API/Rendering/Objects/Buffers/XRDataBuffer.cs`
- networking game mode and controller type activation via reflection in `XRENGINE/Engine/Networking/Engine.ClientNetworkingManager.cs` and `XRENGINE/Engine/Subclasses/Engine.State.cs`
- rendering type activation via `Activator.CreateInstance` in camera, post-process, render command, and OpenXR pipeline paths

## Phase 0 - Define The Shipping AOT Boundary

Outcome: there is an explicit boundary between editor/dev behavior and shipping runtime behavior.

- [x] Define which executables are in scope for first-class NativeAOT support.
  - first target: cooked final game launcher
  - deferred: dedicated server if desired
  - out of scope for now: editor, VR dev tooling, dynamic script host
- [x] Add a documented runtime capability matrix:
  - `Editor/Dev`
  - `Cooked Final Game`
  - `Dedicated Server`
  - `VR Client`
- [x] Decide how shipping builds identify AOT mode.
  - current implementation: compile constants `XRE_PUBLISHED` and `XRE_AOT_RUNTIME`, stamped automatically by the launcher publish flow
  - supporting helper: `XRRuntimeEnvironment`
- [x] Ensure all AOT-only restrictions are keyed off the shipping runtime boundary, not global engine behavior.
  - current implementation: only NativeAOT launcher publish flow adds the AOT runtime constant; editor/dev builds remain unchanged

Acceptance criteria:

- every engineer can tell which code paths must be AOT-safe and which remain editor-only/dev-only
- no TODO in later phases depends on making the editor itself AOT-compatible

## Phase 1 - Remove Hard AOT Blockers From Shipping Paths

Outcome: shipped runtime paths no longer use features NativeAOT fundamentally does not support.

- [x] Replace runtime enum generation in `XREngine.VRClient/Program.cs`.
  - implemented: replaced runtime-generated enums with static compile-time enums
- [x] Disable or exclude runtime managed plugin loading for shipping builds.
  - `XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs`
  - implemented: NativeAOT runtime builds now throw immediately if dynamic managed assembly loading is attempted
  - editor/dev behavior remains unchanged
- [x] Remove runtime codegen from delegate-building paths used by shipping builds.
  - `XRENGINE/Core/Tools/DelegateBuilder.cs`
  - implemented: direct `MethodInfo.CreateDelegate(...)` fast path plus interpreted expression fallback when dynamic code is unavailable or the runtime is AOT
- [x] Audit for any remaining `System.Reflection.Emit`, `Expression.Compile()`, or collectible `AssemblyLoadContext` usage reachable from final game/runtime builds.
  - implemented for current Phase 1 scope: the main reachable runtime offenders now either use compile-time types, interpreted fallback, or explicit AOT runtime blocking

Acceptance criteria:

- NativeAOT publish no longer fails due to fundamental runtime code generation or dynamic managed loading in shipping code paths

## Phase 2 - Replace Reflection Discovery With Explicit Registries

Outcome: shipping builds stop depending on “discover everything in loaded assemblies.”

- [x] Fix `XRENGINE/Rendering/XRWorldObjectBase.cs` static constructor.
  - implemented: published AOT runtime now reconstructs world-object replication metadata from generated build metadata instead of scanning every loaded assembly on first use
- [x] Replace transform discovery with an explicit registry or source-generated list.
  - `XRENGINE/Scene/Transforms/TransformBase.cs`
  - implemented: published AOT runtime now resolves transform type lists and friendly names from generated build metadata
- [x] Replace `XRTypeRedirectRegistry` assembly scan with a build-time or startup-registration model.
  - `XRENGINE/Core/XRTypeRedirectRegistry.cs`
  - implemented: published AOT runtime now consumes generated redirect metadata instead of scanning for `[XRTypeRedirect]`
- [x] Audit and gate `AssetManager.Serialization.cs` assembly scan.
  - implemented: YAML converter discovery now uses generated metadata in published AOT runtime instead of assembly scanning
- [x] Resolve the Phase 2 registry side of YAML discovery.
  - full YAML runtime policy still belongs to Phase 3, but the assembly-scan portion is now replaced for published AOT runtime
- [x] Generate runtime metadata during build/cook and store it in the published config archive.
  - implemented: `AotRuntimeMetadata.bin` is generated during config archive creation for NativeAOT launcher builds
  - contents currently include:
    - known runtime type names
    - transform type list + friendly names
    - XR type redirects
    - world-object replication metadata
    - YAML converter type list
- [ ] Audit all `AppDomain.CurrentDomain.GetAssemblies()` call sites in engine runtime including:
  - `XRENGINE/Core/Engine/AssetManager.Serialization.cs`
  - `XRENGINE/Core/Files/CookedAssetBlob.cs`
  - `XRENGINE/Core/Files/CookedBinarySerializer.cs` (multiple call sites)
  - `XRENGINE/Core/Files/XRAsset.MemoryPack.cs`
  - `XRENGINE/Core/Engine/PolymorphicYamlNodeDeserializer.cs`
  - `XRENGINE/Core/Engine/XRAssetYamlTypeConverter.cs`
  - `XRENGINE/Engine/Networking/Engine.ClientNetworkingManager.cs`
- [x] For the implemented Phase 2 scope, replace scan-heavy runtime registries with generated metadata.
  - `XRWorldObjectBase`
  - `TransformBase`
  - `XRTypeRedirectRegistry`
  - `AssetManager.Serialization`
  - `PolymorphicYamlNodeDeserializer`
  - `XRAsset.MemoryPack`
- [x] Introduce generated registries for shipping runtime types.
  - implemented through generated `AotRuntimeMetadata.bin` rather than hand-maintained lists
- [ ] Keep broad reflection scanning only in editor/dev tooling where needed.

Acceptance criteria:

- the core Phase 2 registries no longer rely on `AppDomain.CurrentDomain.GetAssemblies()` in published AOT runtime
- generated runtime metadata is produced at build/cook time and loaded by published runtime from the config archive
- trimming roots are more explicit than accidental, though later phases still need to finish remaining scan-heavy systems

## Phase 3 - Make Serialization AOT-Safe

Outcome: runtime serialization used by shipped builds is explicit, bounded, and metadata-safe.

### 3a — YAML (YamlDotNet)

YamlDotNet is currently used as the primary format for saving/loading world state, settings, and assets. Its design assumes full runtime reflection and is not NativeAOT-compatible in its current usage.

- [x] Decide which YAML paths are reachable from shipped final builds.
  - final runtime policy: shipped game builds do not load YAML at runtime
  - world, scene, and asset loading must come from cooked published content instead
- [x] Gate the main YAML entry points out of published runtime.
  - implemented: `AssetManager.Serializer` and `AssetManager.Deserializer` are now lazy and throw if reached from published runtime
  - implemented: direct `.asset` YAML deserialization now fails fast in published runtime instead of initializing YamlDotNet
- [x] Clean up `SnapshotYamlSerializer`, `PolymorphicYamlNodeDeserializer`, and `XRAssetYamlTypeConverter` from remaining published-runtime reachable paths.
  - implemented: `SnapshotYamlSerializer` is now only an explicit YAML-runtime guard, and the active polymorphic/XRAsset YAML helpers now fail fast immediately if reached from published runtime
- [x] Remove YamlDotNet package/runtime linkage from published launcher outputs once the remaining call sites are gone.
  - implemented at the runtime-entry-point level: published launcher/runtime code paths no longer expose supported YAML entry points, leaving YamlDotNet trim eligibility to publish-time validation rather than runtime reachability

### 3b — System.Text.Json

- [x] Create a source-generated `System.Text.Json` context for engine-owned runtime DTOs.
  - implemented: generated runtime/pretty-print contexts now cover discovery announcements, VR input payloads, and launcher VR manifest documents
- [x] Convert shipping JSON call sites from open-ended generic serialization to typed `JsonTypeInfo` or generated context usage.
  - networking payload helpers
  - VR state DTOs if VR runtime is in AOT scope
  - any launcher/runtime config payloads
- [x] Separate editor/dev JSON flexibility from shipping runtime JSON requirements.
  - editor may still use looser reflection-based JSON paths if needed
  - shipping runtime should require registration or typed DTOs
- [x] Define policy for user/game-defined payloads in shipped builds.
  - registration at build time
  - `JsonNode`/raw JSON boundary
  - no arbitrary `Type`-driven serialization in shipping runtime

Implementation notes:

- REST and webhook helpers now keep raw `JsonNode` / string-body paths available for flexible editor/dev use.
- Published runtime now blocks generic `System.Text.Json` materialization for user-defined payloads unless the caller provides explicit `JsonTypeInfo` metadata.
- Shipping/runtime engine-owned JSON paths now use generated metadata instead of generic serializer entry points.

Acceptance criteria:

- shipping builds do not link YamlDotNet, or YamlDotNet is only used with explicitly registered types
- shipping JSON code paths compile without unresolved NativeAOT metadata/runtime code warnings

## Phase 4 - Replace Reflection-Based Cooked Asset Serialization

Outcome: shipped runtime asset loading paths stop relying on arbitrary reflection.

- [x] Audit the exact cooked asset loading paths exercised by final game launchers.
  - implemented: published launcher asset loads flow through `AssetManager.Published.cs` -> `CookedAssetReader` -> cooked blob format dispatch
- [x] Reduce `CookedBinarySerializer` usage in shipping runtime paths.
  - prefer explicit per-type readers/writers
  - or generator-based serializers such as MemoryPack where appropriate
  - implemented: registered published runtime asset types now cook as `RuntimeBinaryV1` and dispatch through `PublishedCookedAssetRegistry` instead of the generic reflection reader
  - expanded scope: published runtime registry now covers `XRMesh`, `XRTexture2D`, `AnimationClip`, `BlendTree1D`, `BlendTree2D`, `BlendTreeDirect`, and `AnimStateMachine`
- [x] Add a build-time registry for cooked asset/runtime-serializable types.
  - implemented: `AotRuntimeMetadata.PublishedRuntimeAssetTypeNames` now records the registered published runtime asset set and is validated by AOT runtime loads
- [x] Keep editor-side authoring/cooking flexibility separate from shipped runtime loading requirements.
  - implemented: editor/dev cooking still supports the generic cooked-binary fallback, while published runtime only opts into explicit registry-backed formats for bounded asset types
- [x] Revisit `AssetManager.Published.cs`, `CookedAssetBlob.cs`, and texture/mesh cooked-binary helpers currently annotated with dynamic-code warnings.
  - implemented: published runtime asset blobs now have an explicit runtime-only format for registered types, while editor/dev cooking and generic cooked-binary fallback remain available for non-registered assets

Implementation note:

- animation asset serializers now use MemoryPack-serialized model payloads for nested motions and static method arguments instead of embedding generic cooked-binary payloads
- remaining custom cooked-binary handlers that still require open-ended payloads stay off the published runtime registry until they gain bounded serializers

Acceptance criteria:

- final game/runtime asset loading path no longer depends on unrestricted reflection for core cooked content

## Phase 5 - Make Prefabs And Runtime Type Activation Bounded

Outcome: prefab instancing and runtime type creation work in shipping builds without open-ended reflection.

- [ ] Audit prefab override application paths used in shipped runtime.
- [ ] Replace reflection-heavy prefab apply/diff paths with generated or explicitly mapped handlers for shipping builds.
- [ ] Audit and fix concrete `Activator.CreateInstance` call sites reachable from final game/runtime startup:
  - `XRENGINE/Core/Attributes/RequiresTransformAttribute.cs` — creates transform instances by type; replace with a factory delegate or type-keyed registry
  - `XRENGINE/Engine/Subclasses/Engine.State.cs` — creates assets via `Activator.CreateInstance<T>()` and instantiates local player controllers by type; the controller path has a partial `[DynamicallyAccessedMembers]` annotation that still requires AOT publish to know about the concrete controller types
  - `XRENGINE/Engine/Networking/Engine.ClientNetworkingManager.cs` — creates game modes via `Activator.CreateInstance(gmType)` where the type is resolved from the network by string name; requires an explicit game mode registry
  - `XRENGINE/Rendering/Camera/XRCameraParameters.cs` — creates camera parameter instances by type; replace with a type-keyed factory
  - `XRENGINE/Rendering/PostProcessing/CameraPostProcessStateCollection.cs` — creates post-process state instances by type; replace with a factory or source-generated constructor call
  - `XRENGINE/Rendering/Pipelines/Commands/ViewportRenderCommandContainer.cs` — creates render commands by type using `[DynamicallyAccessedMembers(PublicParameterlessConstructor)]`; callers must pass only statically known types for this to be AOT-safe
  - `XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs` — clones render pipelines via `Activator.CreateInstance(sourcePipeline.GetType())`; replace with pipeline-type-specific clone logic or a registered factory
  - `XRENGINE/Core/Files/CookedBinarySerializer.cs` — multiple `Activator.CreateInstance` for events, tuples, hash sets, and other runtime constructed types; needs per-type registered factories
- [ ] Where unrestricted type activation is used, switch to:
  - explicit factory registration
  - source-generated constructors
  - constrained type maps keyed on statically known types
- [ ] Verify that any remaining `[DynamicallyAccessedMembers]`-annotated paths have their type sets fully knowable at publish time.

Acceptance criteria:

- prefab and runtime object creation used by shipped builds do not require broad metadata preservation guesses
- no `Activator.CreateInstance` in shipping runtime is called with a type unknown at publish time

## Phase 6 - Make Low-Level Marshalling AOT-Safe

Outcome: low-level buffer/memory paths avoid runtime-generated marshalling.

- [ ] Audit `XRDataBuffer` and similar generic marshalling APIs.
- [ ] Restrict shipping runtime marshalling helpers to `unmanaged` where possible.
- [ ] Replace `Marshal.PtrToStructure<T>` in hot/runtime-safe paths with `Unsafe` or `MemoryMarshal` based reads when valid.
- [ ] Validate blittable layout assumptions for runtime structs used in rendering/networking/native interop.

Acceptance criteria:

- NativeAOT runtime no longer depends on generic marshalling stubs for core buffer paths

## Phase 7 - Tighten Build, Publish, And Validation

Outcome: AOT publishing becomes a repeatable supported build mode rather than an experiment.

- [ ] Add one canonical publish workflow for the first supported AOT target.
  - target RID
  - self-contained setting
  - trimming setting
  - symbols/logging expectations
- [ ] Add a dedicated validation checklist for AOT publish output.
  - build success
  - startup success
  - world load
  - asset load
  - input/networking smoke test as applicable
- [ ] Ensure AOT warnings are surfaced clearly during publish.
- [ ] Add targeted tests or smoke tests for AOT-sensitive runtime systems.
- [ ] Document which features are intentionally unavailable in AOT shipping builds.

Acceptance criteria:

- a new cooked final build can be published and smoke-tested without rediscovering manual AOT steps

## Optional Phase 8 - Split Dynamic User Code Into A Separate Non-AOT Host

Outcome: if we ever need dynamic user gameplay code with an AOT engine runtime, the split is explicit rather than accidental.

- [ ] Decide whether a separate non-AOT `GameHost` process is needed for dynamic user code in shipped scenarios.
- [ ] If yes, keep the AOT engine runtime as an authority and move dynamic managed loading to the helper process.
- [ ] Use explicit DTO contracts and IPC rather than sharing arbitrary runtime object graphs.

This phase is optional and should not block a simpler “static game code in shipping builds” AOT milestone.

## Immediate Action List

If work starts now, the highest-leverage first slice is:

- [ ] gate `GameCSProjLoader` out of shipping AOT builds
- [ ] replace runtime enum generation in `XREngine.VRClient/Program.cs`
- [x] replace or isolate `DelegateBuilder` runtime compilation from shipping paths
- [ ] fix `XRWorldObjectBase` static constructor — it triggers automatically and scans every assembly
- [x] decide whether YAML is loaded at runtime in shipping builds; if not, gate `AssetManager.Serializer`/`Deserializer` and direct YAML asset loading out of the shipping runtime
- [x] implement source-generated JSON context for engine-owned runtime DTOs
- [ ] define a static registry strategy for transforms, prefabs, world objects, and runtime-created gameplay/rendering types
- [ ] prove one representative cooked final launcher can publish with NativeAOT
- [ ] remove the dead `using System.Reflection.Emit;` import from `XREngine.Animation/State Machine/Layers/AnimLayer.cs` (unused import, avoids false AOT scan hits)

## Tracking Checklist

- [ ] Shipping AOT target matrix written and agreed
- [ ] Hard blockers removed from shipping runtime paths
- [ ] Runtime type discovery replaced with registries
- [ ] Shipping JSON paths converted to generated metadata
- [ ] Cooked asset loading path made reflection-bounded
- [ ] Prefab/runtime activation path made AOT-safe
- [ ] Buffer marshalling path constrained to blittable/unmanaged types
- [ ] Canonical AOT publish flow documented and validated

## References

- `docs/work/audit/aot-incompatibilities.md`
- `XREngine.Editor/ProjectBuilder.cs`
- `XREngine.Data/Core/BuildSettings.cs`
- `XREngine.VRClient/Program.cs`
- `XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs`
- `XRENGINE/Core/Tools/DelegateBuilder.cs`
- `XRENGINE/Scene/Transforms/TransformBase.cs`
- `XRENGINE/Core/Files/CookedBinarySerializer.cs`
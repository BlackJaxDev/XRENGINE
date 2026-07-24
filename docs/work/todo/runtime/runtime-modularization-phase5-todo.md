# Runtime Modularization Phase 5 - Subsystem Adapter Cleanup

Reference design: [Runtime Modularization And Bootstrap Extraction Plan](../../design/runtime-modularization-plan.md)

Prerequisites:

- [Runtime Modularization Phase 3 - Complete](../runtime-modularization-phase3-todo.md)
- [Runtime Modularization Phase 4 - Remaining Rendering Move](../runtime-modularization-phase4-todo.md)
- [Runtime Modularization Phase 4 Progress](../../progress/runtime/runtime-modularization-phase4-progress-2026-07-23.md)

Created: 2026-07-24

Status: Planned; begin after Phase 4 P4.8 and P4.9 closeout

## Goal

Finish the design Phase 5 split by making animation, audio, input/VR, and
modeling integration explicit upper-layer assemblies with tested one-way
dependencies.

Most adapter implementation is already physically in its target project. Phase
5 therefore focuses on the remaining ownership and dependency cleanup:

- remove feature-specific runtime integration from the transitional `XRENGINE`
  facade,
- replace facade-owned host adapters and implicit global installation with
  explicit composition,
- resolve adapter-to-adapter and undocumented importer dependency edges,
- transfer package, native-content, serialization, reflection, and AOT
  registration ownership with the code,
- prove that feature libraries remain below the runtime adapter layer.

Phase 5 does not delete the complete `XRENGINE` facade. Migrating every
Bootstrap/application/tool/test consumer off that facade and deleting or
deliberately re-scoping it remains design Phase 6.

## Entry State

The following snapshot is an orientation baseline from the active Phase 4
worktree. Recount it in P5.0 after Phase 4 is integrated because P4.8 and P4.9
may still change project and packaging ownership.

| Adapter assembly | Production C# files | Current orientation |
|---|---:|---|
| `XREngine.Runtime.AnimationIntegration` | 69 | Animation components, humanoid/IK, motion capture, OSC, animation-driven transforms, and diagnostics are already present. |
| `XREngine.Runtime.AudioIntegration` | 34 | Scene audio, Steam Audio, microphone, lip-sync, Audio2Face, STT/TTS, and voice bridge components are already present. |
| `XREngine.Runtime.InputIntegration` | 31 | Controllers, input sets, window input routing, flyable camera/UI input, VR components, and VR transforms are already present. |
| `XREngine.Runtime.ModelingBridge` | 15 | `XRMesh` conversion/boolean code and the FBX/glTF runtime import pipeline are already present. |

Known Phase 5 cleanup candidates include:

- `XRENGINE/Engine/Engine.RuntimeAnimationHostServices.cs`,
  `Engine.RuntimeAudioIntegrationServices.cs`,
  `Engine.RuntimeInputServices.cs`, and
  `Engine.RuntimeModelImportServices.cs`;
- remaining facade animation/cooked/YAML serialization and third-party import
  code;
- remaining input/VR game-mode, startup-settings, pawn, lifecycle, and host
  adapters;
- facade-owned audio listener/world composition;
- facade AOT generation that scans only selected runtime roots;
- `openvr_api.dll`, `OVRLipSync.dll`, and feature-specific package/license
  content still published from `XRENGINE`;
- the current `Runtime.AudioIntegration -> Runtime.AnimationIntegration`
  project edge, which is not in the target design;
- the current ModelingBridge references to Animation, FBX, and glTF, which must
  either be represented by a deliberate final import-adapter design or removed
  from the documented ModelingBridge graph.

## Target Dependency Contract

Phase 5 starts from the reference design's graph:

| Assembly | Approved project dependencies |
|---|---|
| `Runtime.AnimationIntegration` | Runtime.Core, Runtime.Rendering, Animation, Data |
| `Runtime.AudioIntegration` | Runtime.Core, Runtime.Rendering, Audio, Data |
| `Runtime.InputIntegration` | Runtime.Core, Runtime.Rendering, Input, Data, Extensions |
| `Runtime.ModelingBridge` | Runtime.Rendering, Modeling, Data |
| `Runtime.Bootstrap` | Runtime layers, adapter assemblies, feature libraries, Data, Extensions |

External format, device, transport, or native dependencies are permitted only
when the owning adapter genuinely consumes them and their ownership is recorded.
They do not permit an upward project edge from a feature library.

The following rules are non-negotiable:

- Animation, Audio, Input, Modeling, FBX, and glTF feature/format libraries must
  not reference runtime, Bootstrap, Editor, an application, or `XRENGINE`.
- Runtime.Core must continue to reference only Data and Extensions.
- Runtime.Rendering must continue to reference only Runtime.Core, Data, and
  Extensions.
- No adapter may reference `XRENGINE`, Editor, Server, VRClient, UnitTests, or a
  concrete OpenGL/Vulkan backend.
- Adapters must not reference one another. Cross-feature behavior is composed
  above them or exchanged through a lower, ownership-correct value/capability
  contract.
- Bootstrap may compose all adapters but must not absorb feature
  implementation, editor UI, or subsystem domain logic.
- A transitive reference is not a substitute for declaring or removing a
  source-level dependency. Public API signatures must also obey the approved
  graph.

## Scope Boundary

In scope:

- runtime scene components, transforms, controllers, and bridges for the four
  subsystems;
- adapter-specific host capabilities and Bootstrap installation;
- runtime model conversion/import/export integration;
- adapter serialization, type identity, AOT/reflection registration, and
  editor discovery metadata;
- packages, submodule references, native binaries, content, and licenses whose
  final consumer is an adapter or lower feature library;
- deterministic tests and application startup validation for the new graph.

Out of scope:

- unfinished Phase 4 rendering/backend extraction or frame-loop work;
- a new physics assembly;
- general deletion of `XRENGINE` or migration of unrelated facade consumers;
- dependency upgrades or supply-path changes;
- removal of harmless string-only editor metadata when it creates no runtime
  reference;
- new animation, audio, input, VR, modeling, or importer features.

## Working Rules

- Move concrete implementation wholesale when ownership is clear. Add a lower
  contract only at a real lifecycle or cross-subsystem boundary.
- Do not move pure feature data, algorithms, or serialization into an adapter
  merely because the adapter already references the feature library.
- Prefer explicit Bootstrap installation and teardown over static
  initialization. Any transitional `Current` service must have narrow
  ownership, deterministic reset, and actionable missing-service diagnostics.
- Optional adapters may be absent. Core, Rendering, and headless startup must
  not silently instantiate them or fail because an unused adapter is missing.
- Required behavior must fail visibly; do not hide missing input, audio,
  animation, model-import, native, or accelerated paths behind an unrelated
  fallback.
- Change namespaces when they communicate final ownership. Update serialized
  identities, redirects, type forwards, reflection/AOT registrations, editor
  assembly lists, and source-contract tests in the same slice.
- Use `SetField(...)` in `XRBase` mutation paths and introduce no allocations in
  per-frame animation, audio, input, scene-update, or render-publication paths.
- Move package/native/content/license ownership with the final consumer and run
  the dependency report after the last ownership change.
- Validate each coherent adapter slice before proceeding to the next.

## P5.0 - Accept Phase 4 And Capture The Baseline

- [ ] Confirm Phase 4 P4.8 and P4.9 are closed and record the exact integration commit used for Phase 5.
- [ ] Create a dedicated `codex/` Phase 5 branch without discarding unrelated worktree changes.
- [ ] Create a Phase 5 progress ledger under `docs/work/progress/runtime/` and record pre-existing validation failures separately from Phase 5 regressions.
- [ ] Recount production source, generated code, packages, native assets, content, licenses, serializers, type redirects/forwards, reflection/AOT registrations, and application references for all four adapters.
- [ ] Capture the direct project-reference graph and source/API-level dependency graph for each adapter and each lower feature/format library.
- [ ] Enumerate every remaining `XRENGINE` source reference to Animation, Audio, Input, Modeling, FBX, glTF, OpenVR, adapter assemblies, or adapter host services.
- [ ] Classify each remaining facade file as lower feature logic, runtime adapter logic, Bootstrap composition, editor-only logic, or a documented Phase 6 facade concern.
- [ ] Capture targeted adapter/feature builds, the closest subsystem tests, full-solution build status, and canonical Editor/Server/VRClient startup status.
- [ ] Record the Phase 5 package and native-content baseline, including `openvr_api.dll`, `OVRLipSync.dll`, OpenVR.NET, OscCore, Assimp, FBX/glTF, OpenAL/Steam Audio, and any conflict-only package pins.

## P5.1 - Lock The Adapter Graph And Composition Contracts

- [ ] Add project-graph tests that enforce every forbidden upward edge and the approved dependency set for all four adapters.
- [ ] Add source/API-boundary tests that reject feature, adapter, facade, editor, application, and concrete-backend types from the wrong layer even when a transitive reference makes them compile.
- [ ] Prove Animation, Audio, Input, Modeling, FBX, and glTF remain leaf feature/format libraries with no runtime-facing project edge.
- [ ] Remove the `Runtime.AudioIntegration -> Runtime.AnimationIntegration` edge. Put shared lip-sync/blendshape values below both adapters or compose the two behaviors from Bootstrap without either adapter owning the other.
- [ ] Decide the final ownership of the ModelingBridge Animation/FBX/glTF importer edges before further moves. Either narrow the bridge to the reference design, or document and test a deliberately expanded/split import-adapter graph in the reference design.
- [ ] Audit direct Extensions, Core, native API, and third-party type use in public signatures; add only genuinely owned direct references and remove accidental/transitive coupling.
- [ ] Define narrow install/uninstall or registration contracts for animation, audio, input/VR, and model-import host capabilities.
- [ ] Define missing-adapter behavior per capability: optional no-op/absence, deferred component-level failure, or required fail-fast. Do not use one policy for every subsystem.
- [ ] Ensure adapter registration and teardown do not retain application, editor, world, renderer-backend, native-device, or collectible assembly generations.
- [ ] Update the reference design before implementation continues if the accepted graph differs from its dependency table.

## P5.2 - Finalize Animation Integration Ownership

- [ ] Audit all AnimationIntegration source and retain only scene/component binding, humanoid runtime integration, animation-driven application, motion-capture scene binding, runtime IK, and scene-bound diagnostics.
- [ ] Move any remaining facade-owned animation components, humanoid/IK bridges, animation-driven transforms, or motion-capture runtime bindings into AnimationIntegration.
- [ ] Rehome pure clip, property-animation, blend-tree, state-machine, motion, cooked-binary, YAML/JSON, and asset serialization in Animation or an ownership-correct lower serialization layer rather than leaving it in `XRENGINE`.
- [ ] Move `EngineRuntimeAnimationHostServices` out of the facade implementation and install its narrow capabilities from Bootstrap/application composition.
- [ ] Replace direct legacy Engine/networking/asset-manager/profiler access with Core, Rendering, feature-library, or Bootstrap-owned contracts at the actual boundary.
- [ ] Confirm OscCore and motion-capture transport ownership is explicit and does not pull networking composition into the Animation feature library.
- [ ] Keep render-only pose diagnostics and spline previews above Runtime.Rendering without leaking rendering types into Animation.
- [ ] Update namespaces, XML docs, serialized type identities, type redirects/forwards, component discovery, editor inspectors, reflection/AOT registration, and docs for moved animation types.
- [ ] Add or update deterministic tests for clip/state-machine component binding, humanoid application, IK lifecycle, motion-capture transport contracts, animation serialization compatibility, and missing-host diagnostics.
- [ ] Build Animation, AnimationIntegration, Bootstrap, `XRENGINE`, Editor, Server, VRClient, and the closest animation/serialization test projects with zero new warnings.

## P5.3 - Finalize Audio Integration Ownership

- [ ] Audit all AudioIntegration source and retain runtime source/listener components, world/listener attachment, Steam Audio scene geometry/probes, scene-bound microphone/voice/lip-sync behavior, and runtime media/audio bridges.
- [ ] Keep audio transports, effects, buffers, devices, OpenAL/Steam Audio abstractions, and audio-side runtime objects in Audio.
- [ ] Move `EngineRuntimeAudioIntegrationServices` out of the facade implementation and replace Engine asset/timing/project-path access with narrow installed capabilities.
- [ ] Remove audio-specific world/listener behavior from `XRWorldInstance` and other facade composition where an AudioIntegration-owned attachment or lower world contract can own it.
- [ ] Complete the P5.1 lip-sync/animation decoupling while preserving deterministic viseme/blendshape publication and update ordering.
- [ ] Transfer `OVRLipSync.dll`, its optional provisioning diagnostics, and any audio-specific native/content/license items from `XRENGINE` to AudioIntegration or Audio according to the final consumer.
- [ ] Audit STT/TTS provider dependencies and keep cloud/provider code optional; deterministic validation must not require credentials or network access.
- [ ] Make absent audio devices, Steam Audio, OVR LipSync, Audio2Face, and optional providers distinguishable through actionable capability diagnostics.
- [ ] Update namespaces, serialized identities, redirects/forwards, component discovery, editor metadata, AOT/reflection registration, publish rules, and docs.
- [ ] Add or update tests for source/listener lifecycle, world attachment, Steam Audio geometry binding, microphone conversion, lip-sync publication, provider selection, serialization compatibility, and missing-native behavior.
- [ ] Build Audio, AudioIntegration, Bootstrap, `XRENGINE`, Editor, Server, VRClient, and the closest audio tests with zero new warnings.

## P5.4 - Finalize Input And VR Integration Ownership

- [ ] Audit all InputIntegration source and retain controllers, pawn input, window/viewport routing, flyable-camera/UI input, VR action transforms, runtime device-model components, and scene-bound VR behavior.
- [ ] Keep devices, action manifests, bindings, OpenVR device abstractions, and input-side interfaces in Input; do not move scene/world ownership downward into the feature library.
- [ ] Classify and move the remaining facade `Engine.Input`, VR input/state services, `VRPlayerInputSet`, VR pawn/game-mode/startup glue, and input-facing character-pawn code to InputIntegration, Bootstrap, Core, or Phase 6 according to responsibility.
- [ ] Move `EngineRuntimeInputServices` and related VR input composition out of the facade implementation and install the final capabilities explicitly.
- [ ] Keep renderer/OpenXR presentation lifecycle in Runtime.Rendering or its backend modules; InputIntegration may consume stable VR/input contracts but must not reference a concrete renderer/backend.
- [ ] Give OpenVR.NET, `openvr_api.dll`, action-manifest content, and related copy/publish rules one final owner in Input or InputIntegration, then remove duplicate facade cargo.
- [ ] Verify window snapshots and UI-input capture cross the stable Rendering/InputIntegration boundary without device objects escaping into Core or Rendering.
- [ ] Ensure headless Server startup does not require local devices, a window, OpenVR, or installed InputIntegration behavior that the server does not use.
- [ ] Update namespaces, serialized identities, redirects/forwards, AOT/reflection registration, editor assembly discovery, manifests, publish rules, and docs.
- [ ] Add or update tests for local/remote controllers, possession, pawn camera routing, UI capture, window snapshot devices, VR action/device transforms, manifest selection, serialization compatibility, and absent-device/runtime diagnostics.
- [ ] Build Input, InputIntegration, Runtime.Rendering, Bootstrap, `XRENGINE`, Editor, Server, VRClient, and the closest input/VR tests with zero new warnings.

## P5.5 - Finalize Modeling And Import Bridge Ownership

- [ ] Audit all ModelingBridge source and retain `XRMesh` conversion, runtime boolean entry points, import/export bridges, and only the format/animation import integration approved in P5.1.
- [ ] Keep editable mesh structures, topology operations, validation, and modeling documents in Modeling with no runtime dependency.
- [ ] Move remaining facade model-import/third-party-import bridge code into ModelingBridge, Bootstrap composition, or Editor tooling according to whether it constructs runtime assets, selects application policy, or provides authoring UX.
- [ ] Move `EngineRuntimeModelImportServices` out of the facade implementation; pass import backend policy through explicit options/composition instead of reading editor preferences from runtime code.
- [ ] Keep job scheduling, app-thread publication, profiling, cancellation, and progress behind narrow runtime contracts without exposing legacy Engine or editor types.
- [ ] Resolve Unity conversion, Assimp, FBX, glTF, skinning, blendshape, animation-component activation, and material/texture import ownership without pulling those libraries into Runtime.Rendering.
- [ ] Preserve mesh import/export ordering, handedness, skinning, blendshape, material, submesh, and boolean-operation behavior through the move.
- [ ] Transfer package, native, generated, content, and license ownership from `XRENGINE` to ModelingBridge, lower format libraries, or Editor as appropriate.
- [ ] Update namespaces, serialized identities, redirects/forwards, asset/importer discovery, reflection/AOT registration, editor metadata, publish rules, and docs.
- [ ] Add or update tests for `XRMesh` round trips, booleans, FBX/glTF import, animation/skin/blendshape reconstruction, importer policy, async publication, cancellation, serialization compatibility, and missing-format capability diagnostics.
- [ ] Build Modeling, ModelingBridge, FBX, glTF, Bootstrap, `XRENGINE`, Editor, Server, VRClient, and the closest modeling/import tests with zero new warnings.

## P5.6 - Composition, Registration, Compatibility, And Cargo Cleanup

- [ ] Move all remaining subsystem-specific Engine host adapters out of `XRENGINE` and into Runtime.Bootstrap or the exact application composition root that owns them.
- [ ] Replace implicit adapter installation in `Engine` static initialization with explicit, ordered Bootstrap profiles and deterministic uninstall/reset for tests and shutdown.
- [ ] Ensure each application packages and registers only its intended adapters while required capabilities fail with an actionable subsystem and assembly name.
- [ ] Make AOT factory generation and reflection/component discovery consume all four adapter assemblies directly instead of relying on an `XRENGINE` source scan.
- [ ] Add the missing adapter assemblies, including AudioIntegration, to editor assembly discovery/reload/watch lists where those lists are still required.
- [ ] Preserve existing YAML, JSON, cooked-binary, MemoryPack, prefab, scene, and project type identities through assembly moves using the minimum necessary redirects or type forwards.
- [ ] Add compatibility tests that load representative pre-move animation, audio, input/VR, and model-import assets and prove each resolves to exactly one public runtime type.
- [ ] Remove duplicate public types, stale source paths, obsolete friend access, dead registrations, and compatibility shims whose consumers have migrated.
- [ ] Remove direct `XRENGINE` project references to Animation, Audio, Input, Modeling, FBX, glTF, InputIntegration, and ModelingBridge once no Phase 5-owned implementation requires them.
- [ ] Remove feature-specific packages, native binaries, content-copy rules, licenses, and build targets from `XRENGINE` after their final owners publish them successfully.
- [ ] Audit trimming/AOT/static registration so no adapter is preserved accidentally through a facade reference and no required adapter is trimmed from its intended application.
- [ ] Produce the exact remaining `XRENGINE` source/project/package/content inventory as the Phase 6 handoff; do not mix unrelated facade deletion into Phase 5.

## P5.7 - Validation And Closeout

- [ ] Build each lower feature/format library and each adapter independently with zero warnings.
- [ ] Build Runtime.Core and Runtime.Rendering and verify their approved project-reference sets are unchanged.
- [ ] Build Runtime.Bootstrap, `XRENGINE`, Editor, Server, VRClient, UnitTests, and `XRENGINE.slnx`.
- [ ] Run the adapter graph/source/API boundary suite and prove no project-reference cycle or forbidden upward edge remains.
- [ ] Run the targeted animation, humanoid/IK, motion-capture, audio, Steam Audio, lip-sync, input, controller, VR transform, model conversion/import, serialization, AOT, and publish tests.
- [ ] Validate representative legacy assets and scenes containing components from all four adapters.
- [ ] Start isolated named Editor sessions for the canonical OpenGL and Vulkan desktop paths, verify MCP readiness and representative adapter composition, inspect logs, and stop only the owned sessions.
- [ ] Run bounded headless Server startup without local input/VR requirements and verify its world initializes.
- [ ] Launch VRClient through its canonical profile and validate the available VR runtime path; record hardware/runtime-blocked checks explicitly.
- [ ] Validate at least one intended publish/AOT/static-registration configuration for adapter inclusion, native cargo, type discovery, and startup.
- [ ] Run `pwsh Tools/Generate-Dependencies.ps1`, review `docs/DEPENDENCIES.md` and `docs/licenses/`, and include only ownership-driven changes with compatible licenses.
- [ ] Update the reference design, affected architecture/developer docs, source maps, launch/setup docs, and the Phase 5 progress ledger with final ownership and validation evidence.
- [ ] Record all unresolved general facade/application migration work in the Phase 6 handoff and close this tracker only when every Phase 5 completion gate passes.

## Validation Matrix

| Lane | Required evidence |
|---|---|
| Dependency direction | Project and public-API graph tests for all feature, adapter, Core, Rendering, Bootstrap, facade, and application assemblies |
| Animation | Component binding, serialization, humanoid/IK, motion capture, diagnostics, and absent-host behavior |
| Audio | Source/listener/world lifecycle, Steam Audio, microphone, lip-sync/Audio2Face, optional provider/native diagnostics |
| Input/VR | Desktop/window/UI input, controller possession, remote input, VR actions/transforms, headless absence, available live VR runtime |
| Modeling/import | `XRMesh` conversion and booleans, FBX/glTF import, skinning/blendshapes/animation, async/cancellation, serialization |
| Compatibility | YAML/JSON/cooked/prefab/scene/project loading, type redirects/forwards, component discovery, AOT/static registration |
| Applications | Editor OpenGL/Vulkan smoke, headless Server smoke, VRClient smoke, intended publish layout |

## Phase 5 Completion Gates

- [ ] All four adapter assemblies contain their complete runtime-facing subsystem integration and no lower feature library references a runtime layer.
- [ ] Runtime.Core and Runtime.Rendering retain their Phase 3/4 dependency contracts.
- [ ] No adapter references another adapter, `XRENGINE`, Editor, an application, UnitTests, or a concrete rendering backend.
- [ ] The final ModelingBridge/import graph is explicitly represented in the reference design and enforced by tests.
- [ ] No Phase 5-owned source, host adapter, registration root, package, native binary, content rule, or license remains owned by `XRENGINE`.
- [ ] Adapter installation, absence, teardown, serialization compatibility, reflection/AOT discovery, trimming, and publishing are deterministic and tested.
- [ ] Editor, Server, VRClient, targeted tests, and the full solution build and start through the new adapter graph with zero new warnings.
- [ ] The Phase 6 handoff contains a bounded inventory of only the remaining compatibility facade and consumer-migration work.

## Recommended Execution Order

1. Accept and baseline the completed Phase 4 graph.
2. Lock the adapter dependency and composition contracts.
3. Close AnimationIntegration first.
4. Close AudioIntegration after removing its animation-adapter edge.
5. Close InputIntegration and VR composition.
6. Close ModelingBridge after the importer-graph decision.
7. Consolidate registration, compatibility, package, native, and publish ownership.
8. Run the full validation matrix and publish the Phase 6 handoff.

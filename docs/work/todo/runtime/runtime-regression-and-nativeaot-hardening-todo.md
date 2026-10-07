# Runtime Regression And NativeAOT Hardening TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Runtime Project Organization](../../../architecture/runtime/project-organization.md), [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md), [Cooked Asset Serialization](../../../architecture/assets/cooked-asset-aot-and-io.md), [Hot-Path Memory Control](../../../developer-guides/runtime/hot-path-memory.md)
Validation: [Runtime And AOT Validation](../../testing/runtime/runtime-and-aot-validation.md)

## Current State
The runtime has repaired many scene restoration, active-world inspection, material restoration, capture, snapshot, networking, root-motion, softbody, and NativeAOT groundwork defects. Several edits from the latest handoff are present but not accepted. The main open code areas are deterministic regression ownership, humanoid and animation correctness, shared rendering contracts, cooked asset diagnostics, public API boundaries, generated NativeAOT metadata, trim warnings, and representative AOT smoke content.

## Open Code Items

### Regression Ownership
- [ ] Create one owned engine test scope for each runtime lane. Files or types: `XREngine.UnitTests`, runtime service fixtures. Done when each lane installs and disposes only its required services.
- [ ] Reset static and shared state between fixtures. Files or types: application leases, asset services, cooked registries, renderer modules, texture-streaming providers, scheduler services, project preferences, editor automation, material counters, shadow-atlas counters, pipeline-resource counters, generation counters, world state, and play-mode state. Done when repeated tests do not depend on previous fixture order.
- [ ] Add early diagnostics for missing scheduler or provider composition. Files or types: unit-test fixture services and runtime service resolution. Done when failures name the fixture and required composition profile.
- [ ] Replace filename-fallback repository reads with one canonical path resolver. Files or types: source-contract and document-contract tests. Done when deleted or ambiguous paths fail with a clear diagnostic.
- [ ] Make source, shader, asset, and document contract fixtures fail when required inputs are absent. Files or types: unit-test contract helpers. Done when no actionable test becomes skipped or inconclusive because a path is missing.
- [ ] Remove dependence on previous fixture registration order and mutable global state. Files or types: material slots, shadow generations, settings mutations, registry indices, and environment overrides. Done when randomized runs keep stable results.
- [ ] Record reusable lane or fixture decisions in the Unit Test Project Reorganization TODO. Files or types: runtime test fixture docs. Done when shared fixture choices are documented without performing unrelated directory moves.

### Animation And Humanoid Correctness
- [ ] Write the imported-humanoid coordinate contract. Files or types: humanoid import and animation docs or runtime contract types. Done when body axes, side selection, mirrored limbs, handedness, bind transforms, and basis conversion have one canonical rule.
- [ ] Make humanoid bone discovery deterministic. Files or types: humanoid bone discovery code. Done when shoulder, arm, leg, duplicate-name, twist, helper, metacarpal, nested-finger, and alias cases resolve predictably.
- [ ] Define and apply one neutral-pose contract. Files or types: preview, reset, and runtime muscle application. Done when neutral pose uses one bind-relative or absolute-local rule.
- [ ] Correct configured and auto-profile axis mapping. Files or types: spine, shoulder, upper-leg, foot, and mirrored-chain mapping. Done when authored sign and swap choices persist through refresh and native solve plans.
- [ ] Preserve raw imported humanoid values separately from transformed runtime muscle pose. Files or types: imported humanoid pose storage. Done when raw values remain inspectable after retargeting.
- [ ] Scope `FlipMuscleZ` to the documented pitch and yaw families. Files or types: muscle conversion code. Done when it does not destroy raw values.
- [ ] Correct imported curve routing. Files or types: scalar curves, vector curves, tangent and interpolation mode handling, key-time offset handling, and infinity mode handling. Done when imported curves route to the correct runtime targets.
- [ ] Correct humanoid root motion. Files or types: root-motion scale, reset baseline, position, rotation, and projected channels. Done when root-motion tests cover those semantics.
- [ ] Preserve material and object-reference curve diagnostics and typed bindings. Files or types: animation binding diagnostics. Done when missing and mismatched bindings report actionable messages.

### Shared Rendering And OpenGL Contracts
- [ ] Triage each non-Vulkan rendering failure against current behavior. Files or types: shared rendering tests and OpenGL tests. Done when each retained failure records whether code or contract is authoritative.
- [ ] Replace obsolete private source-string checks with stable behavioral or state tests. Files or types: rendering tests. Done when tests no longer encode private method names or order unless they protect a compile-time or shader-layout invariant.
- [ ] Preserve only narrow source tripwires for stable invariants. Files or types: shader layout and compile-time contract tests. Done when source checks describe a real invariant.
- [ ] Repair the shadow and forward-lighting cluster. Files or types: cascade and point-light layer selection, atlas allocation, atlas clear, atlas generation, UV bias, receiver bindings, buffer layouts, fallback behavior, and time-budget behavior. Done when the cluster passes its rendering contracts.
- [ ] Reconcile render-pipeline resource keys and lifetimes. Files or types: resource keys, output formats, dependencies, stereo shapes, resize, and recreation. Done when resource recreation matches current ownership.
- [ ] Reconcile advanced and material-table records. Files or types: row sizes, offsets, dirty ranges, generation, and released-slot reuse. Done when table updates match the runtime layout contract.
- [ ] Restore or deliberately replace missing GPU-indirect shader and test assets. Files or types: GPU indirect shader assets and tests. Done when tests use the intended asset, not a similarly named substitute.
- [ ] Repair imported-texture streaming for OpenGL. Files or types: streaming registration, cache authority, mip cooking, budget fitting, and provider composition. Done when OpenGL uses the same supported streaming authority.
- [ ] Reconcile toon and uber-material generation. Files or types: canonical source restoration, forward global bindings, and moved document references. Done when generated material paths restore through current source ownership.
- [ ] Reconcile skybox and post-process behavior. Files or types: skybox ambient, tonemapping, stereo post-process, GTAO, and secondary passes. Done when behavior matches current pipeline ownership.

### Cooked Assets And Runtime Boundaries
- [ ] Repair blend-tree cooked schema inspection. Files or types: blend-tree published schema inspection. Done when the schema describes the serialized runtime model shape.
- [ ] Add missing-registration diagnostics for rejected cooked assets. Files or types: cooked asset loading and package validation. Done when missing registrations fail before compatibility behavior is added.
- [ ] Replace public gameplay API exposure of `MagicPhysX` types. Files or types: gameplay components that expose physics types. Done when APIs use engine-owned options, handles, or a named PhysX extension seam.
- [ ] Keep physics-chain control and target contracts independent of networking and device types. Files or types: physics-chain solver API. Done when future local and remote grab or pose inputs can use the contract without leaking device types.
- [ ] Reconcile retained physics-chain debug, dispatcher, and shader source contracts. Files or types: physics-chain debug path, dispatcher, and shader sources. Done when they match the intended runtime batching path.
- [ ] Reconcile editor exit-play-mode ordering and MCP persisted permission policy. Files or types: editor play transition and MCP permission code. Done when ordering and permissions match supported behavior.
- [ ] Repair moved checklist, document, and generated shader references that are part of supported acceptance. Files or types: runtime docs and generated shader references. Done when links and references resolve to current files.

### NativeAOT Player Graph
- [ ] Inventory the NativeAOT player graph. Files or types: MonkeyBall publish graph, assemblies, packages, native libraries, content roots, registrations, and reflection roots. Done when every player-rooted item has a runtime reason.
- [ ] Remove authoring-only surfaces from the final player graph. Files or types: YAML authoring, source import, editor cache, project tooling, and development diagnostics. Done when final players prefer cooked runtime assets.
- [ ] Split optional provider implementations that root unused dynamic code. Files or types: STT, TTS, speech vendor, media, and provider assemblies. Done when optional providers do not enter the selected player graph unless selected.
- [ ] Propagate selected renderer and application properties through generated project references and publish invocation. Files or types: generated projects and publish scripts. Done when selected settings reach each build edge.
- [ ] Make the AOT player reject unavailable authoring paths. Files or types: published content loading diagnostics. Done when the player emits one actionable diagnostic.
- [ ] Regenerate publish-layout and dependency/license reports after approved graph or cargo changes. Files or types: report scripts and dependency docs. Done when approved cargo changes have matching reports.

### Generated Runtime Metadata And Cooked Codecs
- [ ] Generate type identity to factory mappings. Files or types: generated AOT registration source. Done when player factories do not need scans.
- [ ] Generate cooked serializer and deserializer delegates. Files or types: `PublishedCookedAssetRegistry` registrations. Done when runtime cooked asset lookup uses generated delegates.
- [ ] Generate runtime member accessors. Files or types: property, field, method, and event accessors. Done when player member access does not use reflection.
- [ ] Generate concrete collection and nullable/value-tuple formatters. Files or types: runtime formatters. Done when player-used closed generics have static formatters.
- [ ] Generate animation-property binding delegates. Files or types: published animation bindings. Done when runtime animation binding does not reflect over members.
- [ ] Generate texture-streaming payload codecs. Files or types: `XRTexture2D.StreamingPayload.cs`. Done when the largest warning hotspot no longer uses reflective payload serialization.
- [ ] Generate transform, component, asset, and controller factories. Files or types: runtime contract generator outputs. Done when runtime construction uses generated factories.
- [ ] Make `PublishedCookedAssetRegistry` and `AotRuntimeMetadataStore` authoritative. Files or types: generated registrations and metadata store. Done when runtime lookup uses those surfaces.
- [ ] Remove player dependence on reflection scans and dynamic construction. Files or types: `Assembly.GetTypes`, case-insensitive `Type.GetType`, `Activator.CreateInstance`, reflective property enumeration, `MakeGenericType`, and runtime formatter discovery. Done when player-reachable paths do not use them.
- [ ] Replace reflective cooked collection and custom-object modules. Files or types: cooked binary modules. Done when generated concrete codecs or closed generic delegates handle the payloads.
- [ ] Decide whether runtime YAML is a player feature. Owner: Runtime owner. Done when player YAML is either excluded from runtime paths or implemented with static generation and explicit converters.
- [ ] Fail missing generated metadata at cooking or package validation. Files or types: cooker and package validator. Done when missing metadata fails before player load.

### Trimming And Third-Party Containment
- [ ] Propagate `DynamicallyAccessedMembers` requirements through matching API surfaces. Files or types: interfaces, overrides, parameters, generic arguments, properties, and return values. Done when linker requirements match across overrides.
- [ ] Fix IL2092 override mismatches. Files or types: trim annotations. Done when no suppression hides an override mismatch.
- [ ] Replace reachable `RequiresDynamicCode` operations with generated code. Files or types: player-reachable dynamic-code paths. Done when `RequiresUnreferencedCode` only marks intentional authoring boundaries.
- [ ] Guard unsupported single-file behavior. Files or types: IL3000 and IL3002 diagnostics. Done when unsupported behavior is isolated or platform guarded.
- [ ] Verify MemoryPack generated formatters on the player path. Files or types: MemoryPack formatter registration. Done when reflective provider fallback is not rooted.
- [ ] Triage third-party and runtime-library diagnostics. Files or types: package graph and linker report. Done when each diagnostic is eliminated, uses a static API, has an approved upstream fix, or has targeted runtime evidence.
- [ ] Request approval before dependency changes. Files or types: dependency manifests and reports. Done when approved changes include dependency and license report updates.
- [ ] Limit warning suppressions to named diagnostics. Files or types: suppression files and test links. Done when no project-wide `NoWarn`, broad `UnconditionalSuppressMessage`, or blanket linker descriptor root is used.

### AOT Smoke Content And Package Closure
- [ ] Verify generated renderer input hashes. Files or types: renderer generator and selected assemblies. Done when hashes match just-built assemblies.
- [ ] Add representative AOT smoke content. Files or types: inline and referenced animation clips, humanoid/imported animation bindings, cooked collection payloads, custom-object payloads, snapshots, texture-streaming payloads, and network/session metadata. Done when strict smoke exercises those assets.
- [ ] Add package inspections for unintended cargo. Files or types: package manifest, authoring assets, editor assets, provider assets, backend assets, native cargo, and facade cargo. Done when the package contains only justified runtime content.

## Decisions Needed

- [ ] Decide whether runtime YAML is supported in final players. Owner: Runtime owner.
- [ ] Decide how optional provider assemblies are selected for final players. Owner: Runtime owner.
- [ ] Decide each dependency upgrade or replacement before work starts. Owner: Repository owner.

## Out Of Scope

- Vulkan backend completion and physical-headset hardware qualification that belongs to the Vulkan and XR validation docs.
- Unit test project directory migration that belongs to the Unit Test Project Reorganization TODO.
- New CPU fallback paths for missing accelerated behavior.

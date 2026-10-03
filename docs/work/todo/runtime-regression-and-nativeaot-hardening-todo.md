# Runtime Regression And NativeAOT Hardening TODO

Created: 2026-08-28

Owner: Runtime / Testing / AOT

Status: Incomplete; work stopped at the user's request on 2026-10-03. Do not move
this document to `COMPLETED/`. Current implementation and validation are recorded in the
[runtime hardening progress ledger](../progress/runtime/runtime-regression-and-nativeaot-hardening-progress.md).
Runtime modularization remains complete.

## Handoff Status — 2026-10-03

This section is the current status; the older baseline and chronological ledger
are historical evidence. Changes remain in the working tree, without a commit or
merge from this task. Concurrent broker, OpenVR, Vulkan and earlier shadow changes
must be preserved and reviewed separately. A focused pass below is not full-suite
or NativeAOT acceptance.

### Completed And Validated Work

| Area | Implemented result | Evidence and limits |
|---|---|---|
| Scene restoration | Restore procedural geometry, render subscriptions, authored transforms, typed shader references and the existing possessed pawn. Defer PhysX kinematic updates until native scene attachment. | Multiple owned OpenGL runs, including 300 seconds with zero parity violations; viewed captures from two camera positions show the course, ball and bumpers. Three enter/exit-play cycles also pass. |
| Active-world inspection | Resolve node/transform/component commands against the active world before dormant snapshot caches. | Live `ResetRound` and `SetTilt` affect the visible scene; seven focused identity checks pass. Nested owned-object paths are listed separately below. |
| Material/shader restoration | Rebuild restored material shader-stage caches and restore explicit shader-source subscriptions. | Active-component inspection confirms the fragment shader; material restoration checks pass. Eleven shader subscription/dependency checks pass. Actual live restored-source invalidation still needs the nested-object probe. |
| Capture and bloom | Honor exact requested image dimensions; exercise temporal capture and public bloom schema/toggles. | Three-frame live sequence has zero failed/dropped frames; contact sheet viewed. Both revised bloom defaults and enable/visibility assertions pass. |
| Assets and snapshots | Preserve typed animation model bytes in inline snapshots; preserve persistent IDs during deferred object-cache publication without displacing the incumbent. | Combined snapshot/cooked/capture/material lane: 40/40. Prefab serialization: 40/40, including both former mesh-ID failures. Unchanged external import stable-closure/rollback case passes. New cache lifecycle checks remain unrun. |
| Focused runtime lanes | Repair component construction, networking registration, GPU fixture inputs, root-motion contracts and softbody math. | Component construction 5/5; networking 42/42; indirect/scatter/physics-chain/SurfelGI OpenGL 46/46; AO public schema 15/15; native root-motion/animation serialization 7/7; softbody 7/7. Softbody warmed dispatch median: 30,720 ns for 128 clusters × six members on this machine. |
| Humanoid mapping and neutral pose | Repair partial torso/arm discovery, mirrored semantic sides, sparse fingers/helper filtering, named-eye precedence, late initialization and explicit native neutral-pose authority. | Latest focused result: **37 passed / 5 failed**, improved from 18/42. This is not a completed humanoid lane; see remaining production defect below. |
| NativeAOT groundwork | Add static registrations/typed model paths, narrow schema-only authoring compilation and repair generated project/reference and trimming contracts. | Canonical Release editor build passed with zero warnings/errors. Latest strict publish still **fails with 688 IL2xxx/IL3xxx warnings**, down from 751 and 914. No allow-warning switch or warning-bearing packaged acceptance was used. Later animation codec edits are not covered by this publish. |

### Implemented Or Edited, But Not Yet Accepted

- **Published animation codecs:** new closed property/value/keyframe models,
  exact baked-sample restoration, strict clip/blend-tree/state-machine/motion
  paths, generated registry wiring and five version-2 schema declarations are
  work in progress. The narrow Animation build passed with zero warnings/errors;
  the integrated generated-player path has not been rebuilt or run. They need round-trip/rejection coverage, nested motion and
  raw-cache compatibility checks, a fresh strict publish and packaged runtime
  smoke. Do not infer
  correctness from the earlier typed snapshot model result.
- **Nested MCP `object_path`:** implemented for get/set/invoke and compiled in
  the isolated editor, but not exercised live or covered by the pending owned-
  object regression cases. Next probe must start from the live sphere component
  and address `Material`, `Material.Shaders[0].Source` and its parameters. A
  direct asset-ID probe can hit a dormant snapshot and is insufficient evidence.
- **Cache publication coverage:** six new cases in
  `XREngine.UnitTests/Core/ObjectCachePublicationTests.cs` are unbuilt/unrun.
  They cover persistent collisions, completion/abort, same-ID adoption,
  regeneration and a vetoed identity change. Existing prefab/import checks
  validate the production fix, not these new cases.
- **Humanoid automatic upper-leg expectation:** one correction to include the
  compiled canonical neutral baseline was made after the 37/42 run and has not
  been rerun. Keep the exact tolerance and native-acceptance checks.
- **Package manifest fixture:** `ControlPlaneTests` now explicitly declares
  `world.xrworld` as its entry point; that edit is unbuilt/unrun. The preceding
  test still failed verification. A separate direct probe was rejected because
  its source path traversed a reparse point; it is not passing evidence.

### Remaining Work, In Resume Order

1. Validate the unaccepted edits above at a stable source/build point. Re-run
   the focused asset, animation, MCP and humanoid lanes before expanding scope.
2. Fix the **four authored humanoid axis-mapping failures**. Refresh can retain
   a prior binding instead of current `Settings.BoneAxisMappings`, and continuous
   native solve plans bypass those mappings. Track explicit authored authority
   per bone, preserve generated geometry bases, and apply signed authored axes
   within the continuous solver. Do not route all plans through legacy Euler
   math or weaken assertions. Validate axis sign/swap, refresh persistence,
   zero-muscle baseline and unchanged automatic poses.
3. Validate actual live material parameter/resource revisions and shader reload
   through the new owned-object path. Audit `XRMaterialBase` parameter/texture
   subscriptions if that probe reproduces a missing revision; no base-class
   subscription fix has been made yet.
4. Reconcile the remaining non-Vulkan rendering source/path/behavior contracts,
   imported curves and humanoid full-clip playback, BlendTree schema inspection,
   package verification, editor exit ordering, MCP permissions and other small
   residuals. Preserve assertions for supported behavior and validate runtime
   paths before replacing stale private-source expectations.
5. Finish test composition and shared-state isolation; reconcile all focused
   results into the failure inventory, then run retained lanes, the exhaustive
   software suite and **three recorded deterministic randomized seeds**.
6. Finish the published animation/static binding migration and remaining AOT
   reflection roots. Major remaining files include
   `ImportedAnimationBindingRuntime.cs`, `AnimationPropertySerialization.cs`,
   cooked collection/custom-object modules, `JsonAsset.cs`, speech providers and
   optional backend integration. Inspect selected graph ownership before pruning:
   the last generated launcher still selects renderer backends `All`.
7. Rebuild and publish strictly to **zero warnings**, inspect package cargo and
   renderer hashes, run the representative packaged smoke, audit hot-path
   allocations, and finish aggregate builds/docs/dependency review. No final
   exhaustive, randomized, packaged-AOT or complete allocation acceptance has
   been performed.

### Blocks And Evidence

- **Missing original Unity corpus:** the user confirmed these are not on this
  machine: `XREngine.UnitTests/TestData/HumanoidConformance/unity-project/Assets/HumanoidPoseAuditExporter.schema7.cs`
  and `XREngine.UnitTests/TestData/HumanoidConformance/unity-project/Assets/Clips/editable-families-v7.anim`.
  Recover the exact hash-matching originals or produce a separately verified
  reference corpus. Do not fabricate files, replace hashes or waive provenance.
- **Disk and tool policy:** editor startup requires 10 GiB free. Generated
  outputs were compressed, but low space delayed the next live probe. Automatic
  tool policy rejected cleanup of obsolete build directories with “blocked by
  policy”; `AGENTS.md` did not prohibit that cleanup. No rejected removal was
  performed. Logs/reports and generated directories remain available.
- Evidence root: `Build/_AgentValidation/20261002-211500-runtime-hardening/`.
  Latest strict report: `reports/aot-warnings-688.md`, with publish logs beside
  it. Its categories are 374 general first-party, 174 cooked-binary, 66
  authoring/import/cache, 27 other first-party runtime and 47 third-party/runtime.
- Latest broad name-filtered run: **4,223 passes, 215 failures, six skips**.
  `reports/tests/current-reconciled-inventory.json` then matched 14 later reports:
  18 later passes, 16 later failures, 181 without a later observation. Subsequent
  prefab/import/bloom and humanoid results above postdate that reconciliation;
  update it rather than treating those numbers as a fresh remaining-failure
  count. Vulkan/hardware exclusions and unknown suite-order effects remain
  explicit; no completion gate below is implied by these partial results.

Predecessors and related work:

- [Runtime Modularization Phase 6 - Complete](COMPLETED/runtime-modularization-phase6-todo.md)
- [Runtime Modularization Phase 6 Progress](../progress/runtime/runtime-modularization-phase6-progress-2026-08-25.md)
- [Unit Test Project Reorganization TODO](tests/unit-test-project-reorganization-todo.md)
- [Humanoid Body Root Compensation TODO](avatar/humanoid-body-root-compensation-todo.md)
- [MonkeyBall VR Final-Build Runtime TODO](games/monkeyball-vr-final-build-runtime-todo.md)
- [Runtime Data Layout And Generated Contracts TODO](runtime/runtime-data-layout-and-generated-contracts-todo.md)
  provides the Roslyn generator, span-based cooked codecs, published-reader
  split, and `XRE_AOT_PARITY` diagnostics used by A0-A2. This tracker remains
  responsible for strict-publication acceptance.

## Goal

Close the non-Vulkan software debt exposed by Phase 6 closeout:

1. make the non-hardware regression suite deterministic, current, and green;
2. repair the actual animation, OpenGL/shared-rendering, cooked-asset,
   snapshot, physics-boundary, editor, and tooling regressions behind that
   suite; and
3. make the shipped player graph genuinely NativeAOT-safe so the MonkeyBall
   launcher publishes and passes its live smoke without
   `-AllowAotWarnings`.

This tracker is complete only when the current non-Vulkan software lanes pass
without stale-path skips, order-dependent global state, broad warning
suppressions, or reflection-only fallbacks in the AOT player path.

## Scope Boundaries

### In Scope

- deterministic UnitTests composition and repository-path resolution;
- non-Vulkan animation, rendering, OpenGL, asset, serialization, physics,
  editor, and tooling failures;
- stale or overly brittle source-contract tests exposed by current owners;
- player graph pruning and authoring/runtime boundary cleanup;
- generated AOT metadata, factories, codecs, and property bindings;
- correct trimming annotations and narrowly justified third-party handling;
- strict NativeAOT publication and live packaged-runtime validation.

### Explicitly Out Of Scope

- Vulkan implementation, Vulkan-specific contract failures, or concurrent
  Vulkan work;
- physical-headset, SteamVR/OpenVR device, Monado, or OpenXR hardware
  acceptance;
- supported-hardware NVIDIA Streamline/NIS acceptance;
- reopening or reversing the completed Phase 6 assembly ownership graph;
- the broad test-project directory/repository restructure already owned by the
  Unit Test Project Reorganization TODO;
- dependency upgrades or replacements without the approval and license review
  required by `AGENTS.md`.

Software-only package and boundary checks for optional hardware integrations
remain in scope. This tracker must not claim hardware behavior that was not
observed on the required device.

## Baseline Evidence

Phase 6 closeout evidence is under
`Build/_AgentValidation/20260827-215638-runtime-p68/`.

The baseline is intentionally descriptive, not an acceptance result:

- the last exhaustive console run reported 5,255 tests: 4,711 passed, 537
  failed, and seven skipped in 20 minutes 23 seconds;
- that run predates the final stale-path corrections, so 537 is not the current
  actionable failure count;
- post-fix Phase/profile/naming coverage passed 83/83;
- generated-launcher backend propagation passed 1/1;
- retargeted rendering source-contract classes execute instead of silently
  skipping, but still expose current renderer contract failures;
- a heuristic classification of the old TRX found roughly 136
  non-Vulkan-like failures, including some failures already repaired after the
  exhaustive run;
- the NativeAOT MonkeyBall live smoke passes with `-AllowAotWarnings`, reaches
  300 update ticks and 399 physics steps, and verifies its renderer input
  hashes;
- strict analysis currently reports 913 IL2xxx/IL3xxx diagnostics:
  - 424 cooked-binary runtime/fallback diagnostics;
  - 268 general first-party reflection/dynamic-code diagnostics;
  - 89 third-party/runtime-library diagnostics;
  - 88 editor/dev authoring, import, or cache diagnostics; and
  - 44 first-party runtime diagnostics.

Approximately 862 warning rows do not mention Vulkan. The 913 rows are not 913
independent code changes: compiler-site and final NativeAOT analysis can report
the same underlying reflection root more than once.

The warning inventory is recorded at
`Build/Reports/aot-final-game-publish-warnings.md` and copied into the Phase 6
evidence root. Refresh it before each burn-down phase rather than treating this
snapshot as permanent truth.

## Engineering Principles

- Fix behavior before rewriting its acceptance test. A source assertion may be
  updated only after deciding whether the old contract is obsolete or the
  implementation regressed.
- A source-contract test must resolve one explicit canonical path and fail with
  a clear missing-file diagnostic. It must not search by filename or become
  inconclusive when a production file moves.
- Tests must pass individually, in their subsystem lane, and in randomized
  full-suite order.
- Shared registries, settings, providers, worlds, schedulers, render services,
  and generation counters require owned setup and teardown.
- The AOT player path must use generated or statically registered behavior.
  Do not convert linker warnings into broad suppressions or silent runtime
  reflection fallbacks.
- Authoring import formats do not belong in a final player merely because the
  editor can load them. Prefer cooked runtime assets.
- Do not introduce heap allocations into animation evaluation, render
  submission, texture streaming, fixed update, input, or network hot paths.
- Public gameplay/runtime APIs must expose engine-owned contracts rather than
  MagicPhysX, OpenGL, Vulkan, or other backend types.
- Types derived from `XRBase` must use `SetField(...)` for property mutation.
- Missing accelerated behavior must remain visible through diagnostics; do not
  silently substitute a CPU path.

## R0 - Establish A Current, Reproducible Baseline

- [x] Reserve one bounded `Build/_AgentValidation/<run>/` root and create a
      progress ledger under `docs/work/progress/runtime/`.
- [x] Record the exact commit/working-tree state and explicitly list concurrent
      files that are outside this tracker.
- [x] Obtain a buildable source snapshot without modifying or reverting the
      concurrent Vulkan work.
- [ ] Build the relevant non-Vulkan owners and consumers independently with
      zero warnings and zero errors.
- [ ] Re-run the current focused Phase/profile/naming, publish, collectible,
      animation, cooked/snapshot, OpenGL/shared-rendering, physics-boundary,
      editor, and tooling lanes.
- [x] Run the exhaustive UnitTests project once and archive console output plus
      TRX without stopping at the first failure.
- [ ] Generate a machine-readable failure inventory with test identity,
      subsystem, failure signature, source owner, isolated result, suite-order
      result, and disposition.
- [ ] Classify every failure as one of:
  - [ ] product behavior regression;
  - [ ] test-fixture/service-composition defect;
  - [ ] shared-state/order leak;
  - [ ] stale source/API contract;
  - [ ] missing generated/test asset;
  - [ ] Vulkan-specific and excluded;
  - [ ] hardware-specific and excluded; or
  - [ ] duplicate symptom of another root cause.
- [ ] Record deterministic reproduction commands for every retained root cause.

Acceptance criteria:

- [ ] The actionable non-Vulkan failure count is current and reproducible.
- [ ] No failure is counted twice merely because compiler and runtime lanes
      expose the same cause.
- [ ] Excluded Vulkan and hardware work is listed but not mixed into software
      completion totals.

## R1 - Deterministic Test Composition And Path Contracts

- [ ] Introduce or consolidate an owned engine test scope that installs and
      disposes the minimum services required by each lane.
- [ ] Reset static/shared state between fixtures, including:
  - [ ] runtime application and asset-service leases;
  - [ ] cooked/published asset registries;
  - [ ] renderer module and texture-streaming providers;
  - [ ] engine scheduler and main-thread dispatch services;
  - [ ] project/editor preferences and environment overrides;
  - [ ] material, shadow-atlas, pipeline-resource, and generation counters;
  - [ ] world, play-mode, and editor automation state.
- [ ] Make missing scheduler/provider failures identify the fixture and required
      composition profile rather than failing later through reflection.
- [ ] Replace filename-fallback repository reads with one shared canonical-path
      resolver that rejects deleted and ambiguous paths.
- [ ] Make source-contract and documentation-contract fixtures fail, rather
      than skip, when their required source, shader, asset, or document is
      absent.
- [ ] Remove test dependence on previous fixture registration order, material
      slot allocation, shadow generation, settings mutations, or environment
      variables.
- [ ] Run the non-hardware suite with at least three deterministic randomized
      seeds and record each seed.
- [ ] Update the Unit Test Project Reorganization TODO with any reusable lane or
      fixture decisions; do not perform its unrelated directory migration here.

Acceptance criteria:

- [ ] Retained tests have the same result individually and in the full suite.
- [ ] No actionable test is skipped or made inconclusive by a missing path or
      service.
- [ ] Repeated runs do not change registry indices, generations, settings, or
      failure counts.

## R2 - Animation And Humanoid Correctness

- [ ] Write one canonical imported-humanoid coordinate contract covering body
      axes, positive/negative side selection, mirrored limbs, handedness, bind
      transforms, and source-to-engine basis conversion.
- [ ] Make humanoid bone discovery deterministic for:
  - [ ] left/right shoulder, arm, and leg chains;
  - [ ] duplicate names outside the mapped skeleton;
  - [ ] twist, helper, and metacarpal nodes;
  - [ ] nested fingers and common source aliases.
- [ ] Define neutral pose as a documented bind-relative or absolute-local
      contract and use it consistently in preview, reset, and runtime muscle
      application.
- [ ] Correct configured and auto-profile axis mapping for spine, shoulders,
      upper legs, feet, and mirrored chains.
- [ ] Preserve raw imported humanoid values separately from the transformed
      runtime muscle pose.
- [ ] Make `FlipMuscleZ` affect only the documented pitch/yaw families without
      destroying raw values.
- [ ] Correct imported scalar/vector curve routing, tangent/interpolation mode,
      key-time offset, and pre/post-infinity mapping.
- [ ] Correct humanoid root-motion scale, reset baseline, position, rotation,
      and projected-channel behavior.
- [ ] Preserve material/object-reference curve diagnostics and typed bindings.
- [ ] Validate representative imported clips across their full time ranges,
      not only at a single sample.
- [ ] Audit animation evaluation and imported-event dispatch for per-frame
      allocations after correctness is restored.

Acceptance criteria:

- [ ] All retained non-hardware animation and humanoid tests pass individually
      and in suite order.
- [ ] Raw imported curves, retargeted muscles, preview poses, and root motion
      have independently testable semantics.
- [ ] The resulting contracts can support a future singular avatar animation
      state-machine component without embedding VRChat-specific policy in the
      core animation types.

## R3 - Shared Rendering And OpenGL Contract Repair

- [ ] Triage every non-Vulkan rendering failure against current behavior and
      record whether the code or the contract is authoritative.
- [ ] Replace source-string checks for private method names/order with public or
      internal behavioral/state tests where a stable seam exists.
- [ ] Preserve narrowly scoped source tripwires only for genuine compile-time
      or shader-layout invariants.
- [ ] Close the directional/point/spot shadow and forward-lighting cluster:
  - [ ] cascade and point-light layer selection;
  - [ ] atlas allocation, clear, generation, and UV bias;
  - [ ] forward/deferred receiver bindings and buffer layouts;
  - [ ] fallback and time-budget behavior;
  - [ ] OpenGL live visual validation from more than one camera view.
- [ ] Reconcile render-pipeline resource keys, output formats, dependencies,
      stereo shapes, and resize/recreation lifetimes.
- [ ] Reconcile advanced/material-table row sizes, offsets, dirty ranges,
      generation, and released-slot reuse.
- [ ] Restore or deliberately replace missing GPU-indirect shader/test assets;
      do not make tests pass by searching for a similarly named file.
- [ ] Repair OpenGL imported-texture streaming registration, cache authority,
      mip cooking, budget fitting, and provider composition.
- [ ] Reconcile toon/uber-material generation, canonical-source restoration,
      forward global bindings, and moved completion-document references.
- [ ] Reconcile skybox ambient, tonemapping, stereo post-process, GTAO, and
      secondary-pass behavior against current pipeline ownership.
- [ ] Run an isolated OpenGL Editor session after each behavioral fix cluster,
      inspect screenshots and logs, and stop only the owned session.
- [ ] Re-run allocation checks for material publication, pipeline preparation,
      texture streaming, and shadow scheduling.

Acceptance criteria:

- [ ] The non-Vulkan/shared-rendering and OpenGL lanes are green.
- [ ] Live OpenGL evidence confirms fixes that affect pixels or GPU resources.
- [ ] No test encodes an obsolete private implementation merely to become
      green.

## R4 - Cooked Assets, Snapshots, Physics Boundaries, And Small Residuals

- [ ] Repair blend-tree cooked schema inspection so the published schema
      describes the serialized runtime model shape.
- [x] Preserve inline animation clip trees across scene snapshot round trips.
- [ ] Add or repair missing-registration diagnostics for rejected cooked assets
      before adding compatibility behavior.
- [ ] Replace `MagicPhysX` types exposed by public gameplay component APIs with
      engine-owned options, handles, or a clearly named PhysX extension seam.
- [ ] Keep physics-chain control/target contracts capable of future local and
      remote grabbing and posing without making networking or device types part
      of the solver API.
- [ ] Reconcile the retained physics-chain debug, dispatcher, and shader source
      contracts with the intended runtime batching path.
- [x] Make provider `response.failed` events surface as
      `AgentModelException` with the provider message and failure identity.
- [ ] Reconcile Editor exit-play-mode ordering and MCP persisted permission
      policy behavior.
- [ ] Repair moved checklist/document and generated shader references that are
      still part of supported acceptance.

Acceptance criteria:

- [ ] Cooked/snapshot round trips pass without facade identities or reflective
      compatibility fallback.
- [ ] Public gameplay APIs expose no backend-native types outside explicit
      backend extension namespaces.
- [ ] Small residual failures have focused behavioral validation and do not
      rely on unrelated full-suite success.

## A0 - Define And Narrow The NativeAOT Player Surface

- [ ] Inventory every assembly, package, native library, content root, feature
      registration, and reflection root in the MonkeyBall NativeAOT graph.
- [ ] Record why each rooted item is required by the selected application
      profile.
- [ ] Separate authoring-only YAML, source import, editor cache, project tooling,
      and development diagnostics from the final player graph.
- [ ] Split optional STT/TTS, speech vendor, media, or other provider
      implementations into feature assemblies when they otherwise root unused
      dynamic code.
- [ ] Ensure selected renderer/application properties propagate through every
      generated project reference and publish invocation.
- [ ] Make the AOT player prefer cooked assets and reject unavailable authoring
      paths with one actionable diagnostic.
- [ ] Regenerate the publish-layout and dependency/license reports after any
      approved graph or cargo change.

Acceptance criteria:

- [ ] Every player-rooted assembly and native item has a runtime reason.
- [ ] The 88 editor/dev warning rows are eliminated from the player graph or
      reclassified with evidence that the code is genuinely runtime-required.
- [ ] Removing authoring surfaces does not break the live cooked MonkeyBall
      smoke.

## A1 - Generated Runtime Metadata And Cooked Codecs

- [ ] Extend the generated AOT registration source to provide explicit:
  - [ ] type identity to factory mappings;
  - [ ] cooked serializer/deserializer delegates;
  - [ ] property, field, method, and event accessors required at runtime;
  - [ ] concrete collection and nullable/value-tuple formatters;
  - [ ] animation-property binding delegates;
  - [ ] texture-streaming payload codecs;
  - [ ] transform, component, asset, and controller factories.
- [ ] Make `PublishedCookedAssetRegistry` and `AotRuntimeMetadataStore` the
      authoritative runtime lookup surfaces for generated registrations.
- [ ] Remove reachable AOT-player dependence on `Assembly.GetTypes`,
      case-insensitive `Type.GetType`, `Activator.CreateInstance`, reflective
      property enumeration, `MakeGenericType`, and runtime formatter discovery.
- [ ] Replace reflective cooked collection/custom-object modules with generated
      concrete codecs or registered closed generic delegates.
- [ ] Replace reflection-driven animation property serialization with generated
      bindings for published animation types.
- [ ] Replace reflection-driven texture-streaming payload serialization; begin
      with `XRTexture2D.StreamingPayload.cs`, the largest current warning
      hotspot.
- [ ] Decide whether runtime YAML is a supported player feature:
  - [ ] if no, keep YAML entirely in editor/cooker paths;
  - [ ] if yes, use YamlDotNet static generation and explicit converters rather
        than reflective builders.
- [ ] Make missing generated metadata fail at cooking or package validation,
      before the player attempts to load the asset.

Acceptance criteria:

- [ ] The 424 cooked-binary warning rows are eliminated.
- [ ] AOT cooked asset, animation, snapshot, and texture-streaming validation
      exercises generated paths with reflection disabled or trapped.
- [ ] No serialized runtime type succeeds only because an assembly scan happened
      to find it.

## A2 - Trimming Contracts And Third-Party Containment

- [ ] Propagate `DynamicallyAccessedMembers` requirements through matching
      interfaces, overrides, parameters, generic arguments, properties, and
      return values.
- [ ] Fix IL2092 override mismatches rather than suppressing them.
- [ ] Replace reachable `RequiresDynamicCode` operations with generated code;
      use `RequiresUnreferencedCode` only to mark an intentionally non-AOT
      authoring boundary.
- [ ] Add platform guards or isolate unsupported single-file behavior for
      IL3000/IL3002 diagnostics.
- [ ] Verify MemoryPack uses generated formatters on the player path and does
      not root its reflective provider fallback.
- [ ] Determine whether each third-party/runtime-library diagnostic is:
  - [ ] eliminated by graph pruning;
  - [ ] eliminated by using a static/generated API;
  - [ ] an upstream defect with an available compatible fix;
  - [ ] unavoidable but proven safe by a targeted runtime test.
- [ ] Request approval before upgrading or replacing any dependency and rerun
      `Tools/Generate-Dependencies.ps1` after an approved change.
- [ ] Permit a narrow warning suppression only when it names one diagnostic,
      documents why static analysis is unable to prove the path, and links a
      runtime test that exercises it.
- [ ] Reject project-wide `NoWarn`, broad `UnconditionalSuppressMessage`, or
      blanket linker descriptor roots.

Acceptance criteria:

- [ ] The 268 general first-party and 44 first-party runtime warning rows are
      eliminated through static contracts or generated behavior.
- [ ] The 89 third-party/runtime rows are eliminated, isolated from the player,
      or individually justified with approved evidence.
- [ ] Warning reduction does not depend on hiding a reachable dynamic-code path.

## A3 - Strict NativeAOT Publication And Runtime Acceptance

- [ ] Publish MonkeyBall without `-AllowAotWarnings` and require zero
      IL2xxx/IL3xxx diagnostics.
- [ ] Verify generated renderer input hashes match the selected just-built
      assemblies.
- [ ] Inspect the final package for unintended authoring, editor, provider,
      backend, native, and facade cargo.
- [ ] Run the packaged launcher through the existing live smoke and require:
  - [ ] modular application/profile installation;
  - [ ] cooked project/settings/world load;
  - [ ] registered runtime asset type resolution;
  - [ ] scene activation and begin-play;
  - [ ] input/player setup available to the scripted smoke;
  - [ ] 300 update ticks and expected physics progression;
  - [ ] clean owned shutdown and zero missing-metadata diagnostics.
- [ ] Add representative AOT smoke content for:
  - [ ] inline and referenced animation clips;
  - [ ] humanoid/imported animation bindings;
  - [ ] cooked collection/custom-object payloads;
  - [ ] snapshot round trips;
  - [ ] texture-streaming payloads;
  - [ ] network/session metadata used by the selected profile.
- [ ] Audit update, animation, physics, render, streaming, and network paths for
      new allocations introduced by generated-code migration.
- [ ] Archive the strict publish log, warning report, package manifest, hashes,
      smoke log, and exit code under the task evidence root.

Acceptance criteria:

- [ ] NativeAOT publish and live smoke pass without an allow-warning switch.
- [ ] The strict warning report contains zero IL2xxx/IL3xxx rows.
- [ ] The packaged runtime exercises representative generated metadata/codecs,
      not startup alone.

## Final Validation Matrix

| Lane | Required evidence |
|---|---|
| Build | Independent relevant-project builds and one supported aggregate build, zero new compiler warnings/errors |
| Test integrity | Canonical paths, explicit composition, deterministic seeds, no actionable skip/inconclusive result |
| Animation | Humanoid mapping, neutral/bind pose, raw muscles, curves, root motion, full-clip sampling |
| Rendering | Shared/OpenGL behavior, live Editor evidence, shadow/pipeline/material/streaming contracts |
| Assets | Cooked schema, generated codecs, snapshots, missing-registration diagnostics |
| Boundaries | No backend-native gameplay API, no authoring surface accidentally rooted in player |
| NativeAOT | Zero strict warnings, exact package manifest, generated metadata exercised live |
| Quality | No hot-path allocation regression, clean diff, docs links, dependency/license review |

## Completion Gates

- [ ] The fresh non-Vulkan actionable regression inventory contains zero
      unresolved failure.
- [ ] Retained tests pass individually, by subsystem, in randomized order, and
      in the exhaustive software suite.
- [ ] No retained source/document/asset contract uses an ambiguous fallback or
      missing-path skip.
- [ ] Animation and humanoid import/runtime semantics are documented and green.
- [ ] Shared-rendering and OpenGL behavior is green with live visual evidence
      where pixels or GPU resources changed.
- [ ] Cooked assets and snapshots resolve through generated/current identities
      without reflection-only compatibility behavior.
- [ ] Public gameplay/runtime APIs expose no backend-native types outside named
      extension seams.
- [ ] The selected NativeAOT player graph contains only justified runtime
      assemblies, packages, native cargo, and content.
- [ ] MonkeyBall publishes without `-AllowAotWarnings` and reports zero strict
      IL2xxx/IL3xxx diagnostics.
- [ ] The packaged AOT smoke validates representative cooked assets, animation,
      texture streaming, snapshot, scene, physics, input, and session metadata.
- [ ] Targeted allocation audits report no new steady-state hot-path allocation.
- [ ] `git diff --check`, documentation-link validation, and dependency/license
      review pass.
- [ ] Vulkan, physical-headset, and supported-hardware NVIDIA work remains
      accurately external and is not claimed.
- [ ] The progress ledger records exact commands, results, failure dispositions,
      evidence paths, intentional exclusions, and commit/merge status.
- [ ] This tracker is moved to `docs/work/todo/COMPLETED/` only after every
      software completion gate above passes.

## Recommended Execution Order

1. Rebaseline and classify current failures.
2. Fix test composition, path resolution, and shared-state leaks.
3. Repair animation/humanoid behavior.
4. Repair shared/OpenGL rendering behavior and honest contracts.
5. Close cooked/snapshot, physics-boundary, editor, and tooling residuals.
6. Narrow the NativeAOT player graph.
7. Generate runtime metadata/codecs and remove reflection roots.
8. Fix trimming contracts and contain third-party diagnostics.
9. Run strict publish, packaged runtime, allocations, exhaustive regression,
   documentation, and final closeout.

Do not optimize for making the raw counts decrease. Optimize for removing one
root cause at a time with behavior-backed evidence; a warning suppression,
skipped test, filename fallback, or stale source assertion is not debt closure.

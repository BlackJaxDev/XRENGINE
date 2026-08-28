# Runtime Regression And NativeAOT Hardening TODO

Created: 2026-08-28

Owner: Runtime / Testing / AOT

Status: Proposed. This is post-Phase 6 quality work; runtime modularization
remains complete.

Predecessors and related work:

- [Runtime Modularization Phase 6 - Complete](COMPLETED/runtime-modularization-phase6-todo.md)
- [Runtime Modularization Phase 6 Progress](../progress/runtime/runtime-modularization-phase6-progress-2026-08-25.md)
- [Unit Test Project Reorganization TODO](tests/unit-test-project-reorganization-todo.md)
- [Humanoid Body Root Compensation TODO](avatar/humanoid-body-root-compensation-todo.md)
- [MonkeyBall VR Final-Build Runtime TODO](games/monkeyball-vr-final-build-runtime-todo.md)

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

- [ ] Reserve one bounded `Build/_AgentValidation/<run>/` root and create a
      progress ledger under `docs/work/progress/runtime/`.
- [ ] Record the exact commit/working-tree state and explicitly list concurrent
      files that are outside this tracker.
- [ ] Obtain a buildable source snapshot without modifying or reverting the
      concurrent Vulkan work.
- [ ] Build the relevant non-Vulkan owners and consumers independently with
      zero warnings and zero errors.
- [ ] Re-run the current focused Phase/profile/naming, publish, collectible,
      animation, cooked/snapshot, OpenGL/shared-rendering, physics-boundary,
      editor, and tooling lanes.
- [ ] Run the exhaustive UnitTests project once and archive console output plus
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
- [ ] Preserve inline animation clip trees across scene snapshot round trips.
- [ ] Add or repair missing-registration diagnostics for rejected cooked assets
      before adding compatibility behavior.
- [ ] Replace `MagicPhysX` types exposed by public gameplay component APIs with
      engine-owned options, handles, or a clearly named PhysX extension seam.
- [ ] Keep physics-chain control/target contracts capable of future local and
      remote grabbing and posing without making networking or device types part
      of the solver API.
- [ ] Reconcile the retained physics-chain debug, dispatcher, and shader source
      contracts with the intended runtime batching path.
- [ ] Make provider `response.failed` events surface as
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

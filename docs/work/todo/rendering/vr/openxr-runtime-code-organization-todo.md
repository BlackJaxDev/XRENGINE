# OpenXR Runtime Code Organization TODO

Last updated: 2026-07-16

Owner: Rendering / XR

Status: Proposed

Related docs:

- [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
- [OpenXR Future Work TODO](openxr-future-work-todo.md)
- [OpenXR Monado Testing Pipeline TODO](../../COMPLETED/openxr-monado-testing-pipeline-todo.md)
- [OpenXR SteamVR / OpenVR Parity TODO](openxr-steamvr-openvr-parity-todo.md)
- [Runtime Modularization Phase 3 TODO](../../runtime-modularization-phase3-todo.md)

## Goal

Reorganize the OpenXR runtime so file placement, namespaces, type ownership,
and dependency direction make the subsystem easier to understand and change.
Separate executable smoke-validation infrastructure from production OpenXR
runtime responsibilities without losing live runtime evidence, deterministic
validation, or the existing JSON/tooling contract.

The target is not merely a cleaner directory. The current partial-class layout
hides broad shared-state coupling, so the work should progress from safe
mechanical moves toward explicit subsystem ownership.

## Current Inventory

The directory
`XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/` currently has:

- 45 C# files.
- 15,590 total lines.
- 31 files contributing to the `OpenXRAPI` partial class.
- 14,316 lines contributing to that partial class.
- 14 smoke/strict-SPS files totaling about 2,063 lines, excluding validation
  behavior embedded in general OpenXR runtime files.

The largest files are:

| File | Lines | Current responsibilities |
|---|---:|---|
| `OpenXRAPI.Vulkan.cs` | 2,397 | Vulkan session and swapchains, render-mode resolution, sequential and single-pass stereo rendering, targets, mirror publication, prewarming, and strict-SPS behavior. |
| `OpenXRAPI.FrameLifecycle.cs` | 1,891 | Render, collect-visible, post-render, and swap callbacks; eye submission; GL state handling; RVC profiling; visibility collection; workers; and pacing handoff. |
| `OpenXRAPI.OpenGL.cs` | 1,431 | OpenGL session and swapchains plus backend-neutral camera, viewport, pose, preview, and desktop-mirror behavior, including Vulkan mirror dispatch. |
| `OpenXRAPI.Input.RuntimeNeutral.cs` | 1,095 | Public input queries, actions, bindings, haptics, hand tracking, VIVE trackers, caches, naming, and an input model. |
| `OpenXRAPI.VulkanRequirements.cs` | 1,055 | Runtime requirement querying, cached policy, Vulkan Enable2 bootstrap, instance/device creation, and several top-level support types. |

## Problems To Correct

### 1. The Partial Class Is A Hidden Monolith

Splitting `OpenXRAPI` across many files improves local navigation but does not
create ownership boundaries. Most files can read and mutate the same private
state. `OpenXRAPI.State.cs` has become a state dumping ground for:

- OpenXR instance, system, session, and space state.
- View configuration and visibility masks.
- Predicted and late pose caches.
- Controller, hand, and tracker state.
- Eye cameras, viewports, pipelines, and post-process state.
- Frame handoff and pacing-thread state.
- Parallel visibility-collection workers.
- OpenGL and Vulkan mirror and preview resources.
- Swapchains and runtime-recovery state.
- Smoke and milestone-specific validation overrides.

Foldering alone must not be treated as the final architecture.

### 2. Backend Files Do Not Have Cohesive Ownership

`OpenXRAPI.OpenGL.cs` contains backend-neutral camera, viewport, pose, preview,
and desktop-mirror code, including `TryRenderVulkanDesktopMirrorComposition`.
`OpenXRAPI.Vulkan.cs` combines setup, resource ownership, all rendering modes,
presentation evidence, and validation injection.

The existing `IXrGraphicsBinding` is a useful seam, but it is incomplete:

- Implementations delegate operations back into `OpenXRAPI`.
- `RenderViews` is currently an empty compatibility method.
- Runtime session setup still branches directly on concrete renderer types.
- Acquire/wait/release methods declared by the interface are not the authority
  for the active frame path.

### 3. Smoke Names Encode The Harness Instead Of The Data Owner

The OpenXR smoke run collects evidence from several rendering domains, so not
every emitted type belongs to OpenXR:

- `OpenXrSmokeDesktopRejectionEvidence` is produced by Vulkan desktop
  presentation policy.
- `OpenXrSmokeTemporalStateLedgerEntry` and
  `Phase524bTemporalStateDiagnostics` are written by the general temporal
  accumulation pass.
- Output and occlusion ledgers describe engine-wide frame behavior.
- Capture metrics describe render-pipeline outputs rather than OpenXR itself.
- `OpenXrSmokeFrameLedgerEntry` contains 127 mutable properties spanning
  OpenXR, Vulkan, resource lifetime, mesh data, output scheduling, desktop
  presentation, and occlusion.
- `OpenXrSmokeSummary` contains 107 mutable properties and is populated jointly
  by runtime code and the editor smoke controller.

### 4. The Smoke Lane Is Not A Unit-Test-Only Feature

Do not move the smoke subsystem wholesale into `XREngine.UnitTests`.

The smoke lane is an editor-hosted integration process:

- `Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1` and the SteamVR equivalent launch
  `XREngine.Editor` with `--smoke-*` arguments.
- `XREngine.Editor/Program.OpenXrSmokeRunController.cs` drives the live engine,
  retains frame evidence, writes the summary, validates it, and chooses the
  process exit code.
- Runtime hooks must observe private OpenXR/Vulkan state at the point where
  frames, swapchain images, and GPU resources are used.
- NUnit tests should test pure policies, data conversion, validation, and
  contracts; they should not own the executable harness.

Putting shared smoke types in `XREngine.UnitTests` would invert dependencies or
force test SDK and NUnit infrastructure into editor/runtime projects.

### 5. Public Contracts Are Nested Under A Concrete Runtime Type

Public configuration and timing contracts such as these are nested in
`OpenXRAPI`:

- `OpenXrActionSyncPolicy`
- `OpenXrCollectVisiblePosePolicy`
- `OpenXrPoseTiming`
- `OpenXrRenderPacingMode`
- `OpenXrTrackingLossPolicy`

This forces settings and host-service APIs to expose names such as
`OpenXRAPI.OpenXrRenderPacingMode`. These contracts should be namespace-level
types in their own files.

### 6. Runtime Discovery Is Duplicated

Active-runtime resolution is independently implemented by:

- `OpenXRAPI.NativeLoader.cs`
- `Instance.cs`
- `OpenXRAPI.VulkanRequirements.cs`

The implementations do not use identical environment, registry hive, registry
view, or fallback behavior. Runtime discovery and SteamVR launching need one
authority.

### 7. Durable Names Contain Temporary Milestone Numbers

`Phase524b` appears throughout runtime state, pose overrides, temporal
diagnostics, capture flow, validators, log keys, and method names. The durable
names should express behavior, for example:

- Deterministic validation pose override.
- Temporal-history diagnostics.
- Strict-stereo fault injection.
- Strict-stereo boundary capture.
- Temporal-scenario evidence validation.

Work-plan phase numbers should remain in historical work and evidence docs,
not permanent runtime APIs.

### 8. File And Namespace Naming Is Inconsistent

Current issues include:

- `Init.cs`, `Instance.cs`, `Extensions.cs`, and `Validation.cs` do not sort with
  other `OpenXRAPI.*` files.
- `OpenXRAPI` uses non-standard C# acronym casing while other types use
  `OpenXr`.
- `XREngine.Rendering.API.Rendering.OpenXR` repeats `Rendering` and differs
  from neighboring namespaces such as `XREngine.Rendering.Vulkan` and
  `XREngine.Rendering.OpenGL`.
- Several files contain multiple enums, records, classes, or interfaces despite
  the repository's one-type-per-file rule.

### 9. Tests And Documentation Resist Structural Changes

Multiple unit tests read exact OpenXR source-file paths and assert source text.
Moving a method between coherent files can therefore fail tests without
changing behavior.

The architecture inventory is also stale: `openxr-vr-rendering.md` still
describes the implementation as roughly 12 files, while the directory now has
45.

## Target Ownership

### Runtime OpenXR Assembly

`XREngine.Runtime.Rendering` should own:

- OpenXR loader and runtime discovery primitives.
- Instance, system, session, space, event, and recovery state.
- Frame preparation, location, rendering, submission, and pacing.
- OpenXR input, hand, tracker, haptic, and render-model integration.
- OpenGL and Vulkan graphics bindings.
- Allocation-safe runtime diagnostics and immutable diagnostic snapshots.
- Runtime-internal strict-stereo GPU capture hooks that require private backend
  state.

### General Runtime Diagnostics

Runtime-neutral evidence should live with its actual rendering domain:

- Temporal-history diagnostics under a general rendering/pipeline diagnostics
  location.
- Vulkan desktop-rejection diagnostics under Vulkan diagnostics.
- Generic frame-output and occlusion snapshots under rendering diagnostics.

These runtime types should not mention `Smoke` when they are useful or active
outside the smoke controller.

### Rendering Validation Assembly

Create a small `XREngine.Rendering.Validation` project when the snapshot seam
is ready. It should own:

- Smoke JSON contracts.
- Summary composition.
- Scenario definitions.
- Evidence validators.
- Runtime-snapshot-to-report mapping.
- Capture-artifact metadata and validation.

The dependency direction must be:

```text
XREngine.Editor --------------------+
                                    +--> XREngine.Rendering.Validation
XREngine.UnitTests -----------------+                  |
                                                       v
                                      XREngine.Runtime.Rendering
```

`XREngine.Runtime.Rendering` must never reference
`XREngine.Rendering.Validation`.

### Editor

`XREngine.Editor` should own the executable smoke host:

- Command-line and environment parsing.
- Unit Testing World configuration.
- Engine lifecycle subscription.
- Retained-cohort orchestration.
- Summary output path and process exit code.
- Shutdown and teardown coordination.

The current 1,741-line `Program.OpenXrSmokeRunController.cs` should become a
top-level editor feature rather than a private nested `Program` class.

### Unit Tests

`XREngine.UnitTests/Rendering/OpenXR/` should contain only test fixtures,
fixtures' local builders, and deterministic test data. Tests may reference the
validation project and runtime assembly but must not own shared production or
tooling types.

## Proposed Runtime Layout

The exact number of types may evolve during extraction, but ownership should
converge on this shape:

```text
OpenXR/
  OpenXrRuntime.cs

  Bootstrap/
    OpenXrNativeLoader.cs
    OpenXrRuntimeDiscovery.cs
    OpenXrInstanceFactory.cs
    OpenXrExtensionSet.cs
    OpenXrValidationLayer.cs
    Windows/
      SteamVrRuntimeLauncher.cs

  Runtime/
    OpenXrSessionStateMachine.cs
    OpenXrProbeRetryPolicy.cs
    OpenXrProbeRetryDecision.cs
    OpenXrRuntimeState.cs
    OpenXrRuntimeLossReason.cs
    OpenXrEventPump.cs
    OpenXrSessionCleanup.cs

  Frame/
    OpenXrFramePreparation.cs
    OpenXrFrameRenderer.cs
    OpenXrFrameSubmission.cs
    OpenXrVisibilityCollector.cs
    OpenXrFramePacer.cs
    OpenXrRenderPacingMode.cs

  Views/
    OpenXrViewConfiguration.cs
    OpenXrVisibilityMasks.cs
    OpenXrPoseCache.cs
    OpenXrResolutionPolicy.cs
    OpenXrFoveation.cs
    OpenXrPoseTiming.cs
    OpenXrCollectVisiblePosePolicy.cs
    OpenXrTrackingLossPolicy.cs

  Input/
    OpenXrInputSystem.cs
    OpenXrActions.cs
    OpenXrBindings.cs
    OpenXrHaptics.cs
    OpenXrHandTracking.cs
    OpenXrViveTrackers.cs
    OpenXrControllerRenderModels.cs
    OpenXrActionSyncPolicy.cs

  Graphics/
    IOpenXrGraphicsBinding.cs
    OpenGL/
      OpenGlXrGraphicsBinding.cs
      OpenXrOpenGlSession.cs
      OpenXrOpenGlSwapchains.cs
      OpenXrOpenGlRenderer.cs
      WglNative.cs
    Vulkan/
      VulkanXrGraphicsBinding.cs
      OpenXrVulkanRequirements.cs
      OpenXrVulkanBootstrapContext.cs
      OpenXrVulkanSession.cs
      OpenXrVulkanSwapchains.cs
      OpenXrVulkanRenderTargets.cs
      OpenXrVulkanSequentialRenderer.cs
      OpenXrVulkanStereoRenderer.cs
      OpenXrVulkanMirror.cs

  Integration/
    OpenXrWindowBinding.cs
    OpenXrSourceViewportResolver.cs

  Diagnostics/
    OpenXrRuntimeDiagnostics.cs
    OpenXrRuntimeDiagnosticsSnapshot.cs
    OpenXrSwapchainDiagnostics.cs
    StrictStereo/
      OpenXrStrictStereoCapture.cs
      OpenXrStrictStereoFaultInjection.cs
```

The first implementation stages may retain `OpenXrApi.*` partial files within
these folders. Do not require full class extraction in the same commit as the
mechanical moves.

## Phase 0 - Make Structural Refactoring Safe

- [ ] Add a test helper that discovers OpenXR source recursively instead of
  requiring exact file paths.
- [ ] Replace exact-path source-text assertions with behavioral or symbol-level
  tests where practical.
- [ ] For unavoidable structural contract tests, assert across the recursively
  discovered subsystem rather than a specific partial file.
- [ ] Inventory all scripts that consume `openxr-smoke-summary.json` and record
  their required schema fields.
- [ ] Inventory all CLI arguments and environment variables used by the smoke
  controller and runtime validation hooks.
- [ ] Update the source-file inventory in `openxr-vr-rendering.md` or replace it
  with a responsibility map that does not embed stale line counts.
- [ ] Capture a clean editor build and the narrowest deterministic OpenXR tests
  before moving files.

Acceptance criteria:

- [ ] Moving a method between OpenXR files does not fail a test solely because
  its path changed.
- [ ] The smoke JSON and CLI compatibility surface is explicitly listed.
- [ ] The architecture documentation reflects the current subsystem size and
  responsibilities.

## Phase 1 - Mechanical File And Folder Organization

Keep namespaces and behavior unchanged during this phase.

- [ ] Move bootstrap files under `OpenXR/Bootstrap/`.
- [ ] Move state-machine, recovery, event, and cleanup files under
  `OpenXR/Runtime/`.
- [ ] Move frame lifecycle, visibility collection, and pacing files under
  `OpenXR/Frame/`.
- [ ] Move view, pose, resolution, IPD, visibility-mask, and foveation files
  under `OpenXR/Views/`.
- [ ] Move input, hand, tracker, haptic, binding, and render-model files under
  `OpenXR/Input/`.
- [ ] Move graphics binding types under `OpenXR/Graphics/` with `OpenGL/` and
  `Vulkan/` subfolders.
- [ ] Move editor/window integration files under `OpenXR/Integration/`.
- [ ] Move the smoke and strict-SPS files under temporary
  `OpenXR/Diagnostics/Smoke/{Contracts,Instrumentation,Validation}/` folders.
- [ ] Rename `Init.cs` to the primary type name.
- [ ] Rename `Instance.cs`, `Extensions.cs`, and `Validation.cs` so they sort
  with their owning type during the partial-class transition.
- [ ] Verify that no namespace or serialized field changes occurred.

Acceptance criteria:

- [ ] The OpenXR root contains only the primary facade/coordinator and
  responsibility folders.
- [ ] The mechanical move produces no behavior or JSON schema changes.
- [ ] Targeted tests and editor build pass after updating test discovery.

## Phase 2 - Type And Naming Hygiene

- [ ] Choose the coordinator name:
  - `OpenXrApi` if it remains a thin Silk/OpenXR wrapper.
  - `OpenXrRuntime` if it remains responsible for runtime orchestration.
- [ ] Standardize C# type and file casing on `OpenXr` while retaining `OpenXR`
  in product-facing prose and folder names where appropriate.
- [ ] Simplify `XREngine.Rendering.API.Rendering.OpenXR` to an intentional
  namespace such as `XREngine.Rendering.OpenXR`.
- [ ] Lift public configuration/timing enums out of the coordinator type.
- [ ] Make internal-only runtime and renderer enums internal rather than nested
  public surface.
- [ ] Split `XrGraphicsBindings.cs` into the interface and one implementation
  per file.
- [ ] Split `OpenXrProbeRetryPolicy.cs` into policy, decision, and disposition
  files.
- [ ] Split `OpenXRAPI.VulkanRequirements.cs` support types into their own
  files.
- [ ] Split `OpenXrStrictSpsFailurePolicy.cs` support types into their own
  files.
- [ ] Split `OpenXrSmokeTemporalStateLedgerEntry.cs` into enum, record, and
  recorder files before moving those responsibilities.
- [ ] Rename `Phase524b` runtime types, fields, methods, log keys, and settings by
  behavior.

Acceptance criteria:

- [ ] Every enum, interface, class, record, and struct is in its own file.
- [ ] Public settings no longer expose nested `OpenXRAPI.*` contract types.
- [ ] Runtime code no longer uses milestone numbers as durable behavioral names.

## Phase 3 - Split Oversized Partial Files By Responsibility

This phase may still use partial files, but each file must become cohesive.

### State

- [ ] Split core instance/session/space state from view and pose state.
- [ ] Move input state beside the input behavior that owns it.
- [ ] Move pacing and worker state beside their thread loops.
- [ ] Move backend-specific mirror, preview, and swapchain state beside the
  corresponding graphics backend.
- [ ] Move pipeline-cloning and eye-camera-settings state beside view integration.
- [ ] Remove validation-only fields from the general state file.

### Frame Lifecycle

- [ ] Separate frame preparation and pacing-owner handoff.
- [ ] Separate visibility collection and parallel collection workers.
- [ ] Separate projection-layer assembly and frame submission.
- [ ] Move OpenGL state capture/sanitization into OpenGL integration.
- [ ] Move eye-camera settings propagation into the views/integration area.
- [ ] Separate RVC diagnostics from core frame execution.

### OpenGL

- [ ] Separate OpenGL session creation.
- [ ] Separate OpenGL swapchain creation and destruction.
- [ ] Move backend-neutral camera, viewport, and pose behavior out of the OpenGL
  file.
- [ ] Move backend-neutral desktop mirror dispatch out of the OpenGL file.
- [ ] Keep only OpenGL-owned FBO, texture, blit, and context behavior in the
  OpenGL backend.

### Vulkan

- [ ] Separate Vulkan OpenXR session creation and runtime requirement validation.
- [ ] Separate swapchain format selection and swapchain lifecycle.
- [ ] Separate eye and stereo render-target lifecycle.
- [ ] Separate sequential-eye, parallel-recording, and true-single-pass paths.
- [ ] Separate preview and mirror publication.
- [ ] Move strict-stereo fault injection and evidence capture into diagnostics.

### Input

- [ ] Separate action-set and action creation.
- [ ] Separate interaction-profile binding suggestions.
- [ ] Separate cached runtime-neutral query APIs.
- [ ] Separate haptics.
- [ ] Separate hand tracking.
- [ ] Separate VIVE tracker discovery and pose paths.
- [ ] Move the runtime input action model into its own file.

Acceptance criteria:

- [ ] No OpenXR partial file remains a multi-subsystem dumping ground.
- [ ] Backend-neutral methods do not live in an OpenGL- or Vulkan-named file.
- [ ] Large files have clear, single-responsibility names and navigable sizes.

## Phase 4 - Extract Owned Runtime Components

- [ ] Introduce an `OpenXrRuntime` coordinator with a deliberately small public
  API.
- [ ] Extract one authoritative `OpenXrRuntimeDiscovery` service.
- [ ] Extract Windows-only SteamVR launch/service coordination.
- [ ] Extract instance creation and extension negotiation.
- [ ] Extract session lifecycle and recovery state machine.
- [ ] Extract frame-loop state and thread-affinity enforcement.
- [ ] Extract pose/view caches with explicit synchronization ownership.
- [ ] Extract the OpenXR input system.
- [ ] Turn the graphics binding into a real backend abstraction that owns
  session binding, swapchains, rendering, and cleanup.
- [ ] Remove unused graphics-binding methods or route the actual frame path
  through them.
- [ ] Remove renderer-type branches from the generic runtime state machine where
  backend policy can own the difference.

Acceptance criteria:

- [ ] Components cannot mutate unrelated subsystem state through partial-class
  access.
- [ ] Runtime discovery has exactly one policy authority.
- [ ] Graphics backends own their resources and do not delegate every operation
  back into the coordinator.
- [ ] The coordinator exposes engine-level operations without exposing Silk
  implementation details unnecessarily.

## Phase 5 - Establish The Diagnostics And Smoke Boundary

- [ ] Decide whether current always-recorded `RecordSmoke*` counters are general
  operational telemetry or smoke-only instrumentation.
- [ ] Rename general telemetry to `OpenXrRuntimeDiagnostics`.
- [ ] Gate validation-only collection through an optional allocation-safe sink
  or collector.
- [ ] Add an immutable `OpenXrRuntimeDiagnosticsSnapshot` containing only
  runtime-owned OpenXR data.
- [ ] Replace `OpenXRAPI.CreateSmokeSummary()` with snapshot capture.
- [ ] Move temporal-history diagnostics out of OpenXR and rename them without
  `OpenXrSmoke` or `Phase524b` terminology.
- [ ] Move Vulkan desktop-rejection evidence under Vulkan diagnostics.
- [ ] Move generic frame-output and occlusion evidence under rendering
  diagnostics.
- [ ] Keep GPU capture hooks in the runtime assembly when they require private
  renderer state, but return runtime-neutral capture records.
- [ ] Create `XREngine.Rendering.Validation`.
- [ ] Move smoke summary contracts, builders, scenario definitions, and evidence
  validators into the validation project.
- [ ] Ensure Runtime.Rendering does not reference the validation project.
- [ ] Move `OpenXrSmokeRunController` into
  `XREngine.Editor/Diagnostics/OpenXR/Smoke/` as a top-level type.
- [ ] Split the controller into option parsing, run orchestration, ledger
  retention, capture collection, summary building, validation, and exit policy.

Acceptance criteria:

- [ ] Runtime OpenXR code exposes diagnostics, not an editor/tool-specific JSON
  report model.
- [ ] Editor and unit tests share validation behavior without test dependencies
  leaking into runtime or editor production dependencies.
- [ ] Disabled validation adds no per-frame heap allocations.
- [ ] Required diagnostics remain available to the live integration smoke lane.

## Phase 6 - Reshape The Smoke Report Contract

Preserve schema version 8 until the new ownership seam is functional. Then make
one intentional schema change rather than accumulating compatibility shims.

- [ ] Replace the flat `OpenXrSmokeFrameLedgerEntry` with composed sections:
  - Frame identity and retained-cohort position.
  - OpenXR acquire/wait/release and projection submission.
  - CPU/GPU frame timing.
  - Vulkan validation and resource lifetime.
  - Mesh-frame-data and descriptor metrics.
  - Output scheduling and command generations.
  - Desktop presentation.
  - Occlusion evidence.
- [ ] Replace the flat `OpenXrSmokeSummary` with composed sections:
  - Run metadata and runtime identity.
  - Requested/effective configuration.
  - Runtime/session lifecycle.
  - Input and pose availability.
  - Swapchains and frame cohort.
  - Captures and temporal scenarios.
  - Validation results, warnings, and failures.
- [ ] Prefer immutable records or `init`-only contracts where JSON tooling
  permits.
- [ ] Use typed enums in the validation model where stringly typed values do not
  need forward compatibility.
- [ ] Bump the schema version once.
- [ ] Update Monado, SteamVR, strict-SPS, and Vulkan Phase 5.2.4b scripts in the
  same change.
- [ ] Update behavioral tests to deserialize and validate the new schema.
- [ ] Document the new schema and retain an example report under the appropriate
  testing documentation if useful.

Acceptance criteria:

- [ ] Report sections reflect data ownership and are understandable without
  reading the editor controller.
- [ ] All report-consuming scripts use the new schema.
- [ ] There is no mixed period where runtime emits one schema while validators
  expect another.

## Phase 7 - Validation And Closeout

- [ ] Build `XREngine.Runtime.Rendering` and `XREngine.Editor`.
- [ ] Run targeted OpenXR policy, timing, RVC, view-mode, and strict-stereo tests.
- [ ] Run the deterministic no-HMD OpenXR smoke lane.
- [ ] Validate JSON generation, normalization, evidence validation, exit codes,
  and teardown handling.
- [ ] Validate both OpenGL and Vulkan OpenXR compilation paths.
- [ ] Run SteamVR hardware validation when available.
- [ ] Confirm no new allocations were added to render, collect-visible, pacing,
  swapchain, or submission hot paths.
- [ ] Update `openxr-vr-rendering.md` to match the final ownership model.
- [ ] Update this TODO with completed phases and remaining follow-ups.
- [ ] Move stable architecture guidance out of this TODO when the reorganization
  is complete.

Acceptance criteria:

- [ ] OpenXR startup, recovery, frame pacing, input, rendering, mirror, and smoke
  behavior remain functional.
- [ ] The root OpenXR directory is navigable by responsibility.
- [ ] Production runtime, editor smoke harness, validation contracts, and NUnit
  fixtures have explicit dependency boundaries.
- [ ] Architecture docs and tests no longer depend on the retired layout.

## Migration Risks

- Source-contract tests currently hard-code exact OpenXR file paths.
- Partial files cannot move to another assembly while they depend on private
  `OpenXRAPI` state.
- Changing the smoke summary shape affects PowerShell validators and historical
  evidence tooling.
- Extracting backend ownership can change thread, graphics-context, device, and
  swapchain lifetime behavior if combined with semantic cleanup.
- OpenGL session creation requires a current WGL context and must retain its
  render-thread handoff.
- Vulkan session creation and recreation must preserve device-loss and
  renderer-transition synchronization.
- An interface or diagnostics sink called from hot paths must not introduce
  allocations, boxing, captured closures, or unbounded contention.
- Namespace and public enum changes touch settings, host-service interfaces,
  editor parsing, tests, and documentation.

Mitigation:

- Keep mechanical moves separate from behavior changes.
- Preserve namespaces during Phase 1.
- Introduce tests and snapshots before extracting ownership.
- Change the JSON schema only after the diagnostics seam works.
- Validate one backend/lifecycle boundary at a time.

## Deferred Decisions

- [ ] Whether the primary type should ultimately be `OpenXrApi`,
  `OpenXrRuntime`, or a small facade plus both internal concepts.
- [ ] Whether `XREngine.Rendering.Validation` should be a reusable class library
  or an editor-owned feature assembly.
- [ ] Whether general diagnostics use a sink interface, an owned recorder, or
  immutable snapshot sources composed at capture time.
- [ ] Whether JSON contract types should remain mutable DTOs or become immutable
  records with explicit serializers.
- [ ] Whether existing environment-variable names containing milestone numbers
  should be renamed immediately or changed with the report-schema migration.

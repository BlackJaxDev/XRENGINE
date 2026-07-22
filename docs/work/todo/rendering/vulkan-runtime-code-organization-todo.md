# Vulkan Runtime Code Organization TODO

Last updated: 2026-07-16

Owner: Rendering / Vulkan

Status: Proposed

Related documentation and work:

- [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md)
- [Rendering Code Map](../../../architecture/rendering/code-map.md)
- [Frame Lifecycle And Dispatch Paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Vulkan Render Loop Design](../../design/rendering/vulkan-render-loop-design.md)
- [Vulkan Desktop Frame Loop Decomposition TODO](vulkan-desktop-frame-loop-decomposition-todo.md)
- [Vulkan Primary Command Recording Fast Path TODO](optimization/vulkan-primary-command-recording-fast-path-todo.md)
- [Vulkan Dynamic Rendering Migration TODO](vulkan-dynamic-rendering-migration-todo.md)
- [Render Pipeline Resource Lifecycle TODO](render-pipeline-resource-lifecycle-todo.md)
- [OpenGL And Vulkan Rendering Hot Reload TODO](rendering-backend-hot-reload-todo.md)
- [OpenXR Runtime Code Organization TODO](vr/openxr-runtime-code-organization-todo.md)
- [Backend Renderer Folder Organization TODO](../COMPLETED/backend-renderer-folder-organization-todo.md)

## Goal

Replace the Vulkan backend's hidden partial-class monolith with explicit,
cohesive subsystem owners. File placement, type ownership, dependency
direction, and hot-path state flow should make it possible to understand or
change one Vulkan responsibility without first loading most of
`VulkanRenderer` into working memory.

The target is not another move-only folder pass. The existing folder taxonomy
is directionally useful, but most implementation files still contribute to one
`VulkanRenderer` partial class and can freely access all renderer state. This
work should preserve the public renderer facade while progressively extracting
device, frame, command, render-graph, resource, descriptor, pipeline, OpenXR,
and UI authorities with narrow contracts.

The steady-state render paths must remain allocation-free. New ownership
boundaries must use explicit stack-only contexts, immutable plans, and reused
workspaces rather than per-frame dependency-injection objects, delegate
pipelines, LINQ, or interface dispatch inside per-operation loops.

This work must also preserve the module boundary required by the
[OpenGL And Vulkan Rendering Hot Reload TODO](rendering-backend-hot-reload-todo.md):
the complete Vulkan implementation will ultimately compile from
`XREngine.Runtime.Rendering.Vulkan.dll`, below the stable backend-neutral
`XREngine.Runtime.Rendering.dll`. Extracted Vulkan subsystem owners must not
leak their concrete types, callbacks, workers, or native handles into the stable
kernel. Folder/type reorganization and DLL extraction should be coordinated so
that neither workstream repeats the same move or treats the current partial
class as the final module API.

## Relationship To The Desktop Frame-Loop Decomposition

The
[Vulkan Desktop Frame Loop Decomposition TODO](vulkan-desktop-frame-loop-decomposition-todo.md)
is the focused execution plan for the desktop-frame portion of this broader
reorganization. Its frame-attempt context, typed phase outcomes, post-acquire
recovery policy, and OpenXR activity contract are prerequisites for removing
desktop frame ownership from the `VulkanRenderer` partial-class monolith.

The two plans should be implemented as coordinated work:

- The frame-loop decomposition TODO remains the detailed authority for
  `WindowRenderCallback`, acquire/submit/present ownership, frame-slot state,
  and desktop/OpenXR frame coordination.
- This TODO defines the surrounding subsystem boundaries and the desired final
  owner, `VulkanDesktopFrameCoordinator`.
- Responsibility-specific renderer partials proposed by the frame-loop TODO
  are acceptable as an intermediate migration step, but are not the final
  ownership boundary described here.
- Device, synchronization, resource-lifetime, command-recording, and
  diagnostics services extracted by this plan should be reused by the desktop
  coordinator rather than duplicated inside it.
- Progress and validation evidence for the desktop-frame work should update
  both plans when a change satisfies acceptance criteria in each document.

## Audit Snapshot

The 2026-07-16 working-tree audit found approximately:

- 319 C# files and 121,000 physical lines under
  `Rendering/API/Rendering/Vulkan/`.
- 254 files contributing to `partial class VulkanRenderer`.
- 104,000 lines, or about 86% of the subsystem, inside that partial class.
- 317 files using the same flat `XREngine.Rendering.Vulkan` namespace.
- 33 files at or above 1,000 lines, including 15 at or above 2,000 lines.
- 131 files below 30 lines, showing that file count alone does not correspond
  to useful ownership boundaries.
- 26 thread-static fields and broad ambient reads of renderer/runtime state in
  command recording, render-graph planning, OpenXR, and mesh rendering.

The largest concentration points included:

| File | Approximate lines | Responsibilities currently combined |
|---|---:|---|
| `Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs` | 9,359 | Command-buffer caching, frame-op dispatch, render scopes, state transitions, barriers, uploads, diagnostics, and recording policy. |
| `OpenXR/VulkanRenderer.OpenXR.cs` | 6,209 | XR frame scopes, eye recording, image resources, mirror/preview, prewarm, runtime pressure, submission policy, and diagnostics. |
| `BackendObjects/Textures/VkImageBackedTexture.cs` | 4,328 | Allocation, image/view/sampler lifetime, layout tracking, uploads, transfers, staging, mipmaps, and event handling. |
| `RenderGraph/VulkanRenderer.ResourcePlannerState.cs` | 4,192 | Planner context, generations, registry merging, physical allocation, graph rebuilds, validation, readback scopes, and temporal history. |
| `Bootstrap/VulkanRenderer.LogicalDevice.cs` | 3,401 | Capability discovery, policy, device construction, feature chains, extension loading, and reporting. |
| `BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs` | 3,363 | Descriptor allocation, reuse, fingerprints, writes, validation, and buffer/image/uniform resolution. |
| `BackendObjects/Programs/VkRenderProgram.cs` | 3,258 | Program lifecycle, linking, layouts, pipelines, compute descriptors, uniforms, and renderer-global link-queue behavior. |
| `Frame/VulkanRenderer.ResourceLifetimeTracking.cs` | 3,144 | Resource-use tracking, retirement policy, completion observation, and deferred destruction. |

Line counts are audit anchors, not acceptance criteria. Active Vulkan work may
move them before this TODO begins.

## Problems To Correct

### 1. Partial Files Are Acting As A Module System

The folders improve navigation, but they do not enforce ownership. A private
field added for one concern is visible to every `VulkanRenderer` partial. This
makes dependencies implicit, encourages ambient state reads, and allows a
local change to affect command recording, OpenXR, resource lifetime, and frame
submission simultaneously.

New stateful `VulkanRenderer` partials should be treated as migration debt, not
as the desired endpoint.

### 2. Several Folder Contracts Are Violated

Representative examples include:

- `BackendObjects/MeshRendering/VkMeshRenderer.cs` contains renderer-global
  frame-operation queue and signature behavior before the actual mesh wrapper.
- `BackendObjects/Buffers/VkDataBuffer.cs` contains renderer allocation and
  image-tracking behavior in addition to the buffer wrapper.
- `Commands/VulkanRenderer.StateTracking.cs` owns render-graph planner,
  allocator, barrier, scope, and key state.
- `Frame/VulkanRenderer.FrameLoop.cs` contains the general backend-object
  factory in addition to desktop frame orchestration.
- `Commands/VulkanRenderer.Blit.cs` contains readback and pixel-decoding
  behavior despite a separate readback area.
- `Types/VkTransformFeedback.cs` contains a stateful runtime wrapper rather
  than a small backend value or interop type.

Folder README contracts should describe real ownership and be enforced by the
code, not merely by file names.

### 3. Very Large Methods Hide Lifecycle Obligations

`TryRecordCommandBuffer` spans more than 4,000 lines,
`EnsureCommandBufferRecorded` spans about 800 lines, and
`CreateLogicalDevice` spans more than 1,300 lines. The desktop
`WindowRenderCallback` also coordinates most frame phases in one method.

These methods mix policy, state capture, Vulkan structure construction,
resource ownership, error recovery, diagnostics, and cache mutation. Their
legal early exits and exactly-once obligations are not visible in the type
system.

### 4. Ambient State Obscures Hot-Path Inputs

Thread-static state and direct reads of `RuntimeEngine.Rendering.State`,
statistics, and environment settings make call requirements unclear. They also
complicate parallel recording and make nested or re-entrant behavior harder to
reason about.

Per-frame and per-recording inputs should be captured once into explicit
allocation-free contexts. Settings that are not intended to change during a
frame should be snapshotted before recording begins.

### 5. Backend Wrapper Ownership Leaks Through The Renderer

Many backend objects are nested under `VulkanRenderer`, forcing production and
test code to name concrete types as `VulkanRenderer.SomeType`. Wrapper
creation, lookup, and lifetime also depend on broad renderer access. Generic
static caches risk sharing state beyond the owning Vulkan renderer even though
`AbstractRenderer` already has a per-renderer object-cache concept.

Backend objects should become namespace-level internal types and use an
explicit per-renderer registry and binding allocator.

### 6. Render-Graph Planning Is Mutable And Distributed

Compiler, barrier, resource-binding, planner-cache, allocator, and execution
state are spread across `RenderGraph/` and `Commands/`. Resource names use a
duplicated string grammar such as `tex::`, `fbo::`, and `buf::`. Compiler cache
identity does not clearly include every mutable metadata revision, and some
APIs return reusable scratch collections without expressing their borrowed
lifetime.

Planning should produce an immutable `VulkanRenderGraphPlan` consumed by
recording and execution. Binding names should be parsed once into a typed key.

### 7. Source-Text Tests Resist Safe Refactoring

Several tests open exact Vulkan file paths and search method text. Moving a
method to its correct owner can therefore fail a test without changing
behavior, or silently weaken the assertion when the old file remains.

Behavioral, policy, and internal API tests should replace path-sensitive
checks. Temporary structural tests should discover a subsystem recursively.

### 8. Type Grouping Does Not Follow Domain Ownership

Files such as `VulkanCommandChains.cs`, `VulkanFeatureProfile.cs`,
`VulkanTextureUploadService.cs`, `VulkanResourceAllocator.cs`,
`VulkanShaderTypes.cs`, and `VulkanResourcePlanner.cs` contain many top-level
types. `Records/Classes`, `Records/Structs`, and `Enums` group declarations by
C# syntax instead of rendering responsibility.

Each enum, interface, class, record, and struct should live in its own file,
organized by domain such as `FrameOps`, `Descriptors`, `PipelineKeys`, or
`FrameData`.

## Target Ownership Model

`VulkanRenderer` should remain the backend facade required by the engine, but
delegate implementation and state ownership to focused components:

```text
VulkanRenderer
  |-- VulkanDeviceContext
  |-- VulkanDesktopFrameCoordinator
  |-- VulkanCommandScheduler
  |-- VulkanCommandRecorder
  |-- VulkanRenderGraphRuntime
  |-- VulkanResourceLifetimeTracker
  |-- VulkanDescriptorManager
  |-- VulkanPipelineManager
  |-- VulkanOpenXrBackend
  `-- VulkanImGuiBackend
```

Ownership rules:

- Each component owns its mutable state and exposes the narrowest practical
  internal API.
- Components must not retain pointers or spans into stack-only frame contexts.
- Frame and recording contexts carry inputs and temporary ownership; long-lived
  services own caches, pools, Vulkan handles, and reusable workspaces.
- Queue submit and present operations go through one tracked gateway with
  stable diagnostic operation names.
- Resource destruction goes through one lifetime tracker and retirement queue.
- Render-graph compilation and planning produce immutable versioned output.
- Backend wrappers depend on a narrow device/resource context, not the whole
  renderer facade.
- Dependencies flow from orchestration toward services; lower-level services
  must not call back into the facade to discover ambient state.

## Allocation-Free Contexts

Introduce small explicit contexts as the relevant owners are extracted:

- `VulkanFrameAttempt`: a `ref struct` capturing frame identity, frame slot,
  swapchain generation, image ownership, synchronization state, and named
  phase outcomes for one desktop callback attempt.
- `VulkanCommandRecordingContext`: a `ref struct` containing the command
  buffer, immutable render settings, frame/view identity, active render scope,
  binding state, diagnostics sink, and references to reused scratch storage.
- `VulkanOpenXrFrameContext`: a stack-only view of predicted timing, eye/view
  targets, acquired image ownership, mirror policy, and submission state.
- `VulkanRenderGraphBuildContext`: a short-lived input view used to create an
  immutable plan; it must not escape into cached state.

Prefer concrete internal types and direct calls on hot paths. Interfaces are
appropriate at stable backend or test seams, but not for every frame operation.

## Proposed Folder Layout

The layout may evolve as ownership becomes concrete. Keep the existing flat
namespace during initial mechanical work so file moves and ownership changes
remain reviewable.

```text
Vulkan/
  VulkanRenderer.cs

  Device/
    Capabilities/
    Creation/
    Queues/

  Frame/
    Desktop/
    Swapchain/
    Synchronization/
    Timing/

  Commands/
    FrameOps/
    Scheduling/
    Recording/
    Submission/
    Transfers/

  RenderGraph/
    Bindings/
    Compilation/
    Planning/
    Synchronization/
    Execution/

  Resources/
    Memory/
    Lifetime/
    Images/
    Buffers/
    Uploads/

  BackendObjects/
    Buffers/
    Textures/
    Framebuffers/
    Materials/
    MeshRendering/
    Programs/
    Queries/
    Samplers/

  Descriptors/
  Pipelines/
  Shaders/
  Features/
  Diagnostics/
  UI/ImGui/
  OpenXR/
    Eye/
    Mirror/
    Preview/
    Resources/
  Interop/
```

Do not create generic `Helpers`, `Managers`, `Common`, `Records`, or `Types`
dumping grounds. A small value type belongs beside the operation or owner whose
contract it expresses.

## Phase 0 - Make Structural Refactoring Safe

- [ ] Record the base commit and dirty-worktree state before implementation.
- [ ] Inventory tests, scripts, and docs that reference exact Vulkan source
  paths or nested `VulkanRenderer` type names.
- [ ] Add a shared test helper that discovers Vulkan source recursively for
  structural assertions that cannot yet be replaced.
- [ ] Replace exact-path and source-text assertions with behavioral, policy,
  or internal API tests where practical.
- [ ] Capture a baseline runtime-rendering build and the narrowest relevant
  Vulkan tests.
- [ ] Update stale source counts and frame-loop descriptions in
  `vulkan-renderer.md`, or remove counts that cannot remain accurate.
- [ ] Record public and internal Vulkan types consumed outside
  `XREngine.Runtime.Rendering` before de-nesting or renaming them.
- [ ] Identify active work that overlaps command recording, resource planning,
  frame lifetime, OpenXR, or mesh rendering and establish merge order.

Acceptance criteria:

- [ ] Moving a Vulkan method between files does not fail a test solely because
  the path changed.
- [ ] External consumers of nested Vulkan types are known.
- [ ] Baseline build/test results and pre-existing failures are recorded.

## Phase 1 - Correct Mechanical Organization Debt

Keep behavior and namespaces unchanged in this phase.

- [ ] Remove the empty `Pipelines/VulkanRenderer.GraphicsPipeline.cs` partial.
- [ ] Remove or revive the comment-only
  `Resources/Buffers/VulkanRenderer.UniformBuffers.cs` file.
- [ ] Move the excluded/commented ray-tracing prototype out of compiled-source
  organization or replace it with a durable design note.
- [ ] Rename `VKSampler.cs` to `VkSampler.cs` for normal C# acronym casing.
- [ ] Replace vague phase/scratch file names such as
  `DeviceLossDiagnostics.Phase1.cs` with responsibility names.
- [ ] Move renderer-global frame-operation queue/signature behavior out of
  `VkMeshRenderer.cs` and into `Commands/FrameOps/`.
- [ ] Move renderer-global allocation and image tracking out of
  `VkDataBuffer.cs` into `Resources/`.
- [ ] Move planner/allocator/barrier state out of
  `Commands/VulkanRenderer.StateTracking.cs` into `RenderGraph/` and
  `Resources/` owners.
- [ ] Move the backend-object factory out of `FrameLoop.cs`.
- [ ] Move readback and pixel decoding out of `VulkanRenderer.Blit.cs`.
- [ ] Move `VkTransformFeedback` to its runtime owner.
- [ ] Split multi-type files so every top-level type has a matching file name.
- [ ] Replace `Records/Classes`, `Records/Structs`, and `Enums` groupings with
  domain folders.
- [ ] Update local folder README contracts after the corrected moves.

Acceptance criteria:

- [ ] Files comply with their folder contracts.
- [ ] No production file contains unrelated renderer-global behavior before or
  after its named backend wrapper.
- [ ] Every touched top-level type follows the one-type-per-file rule.
- [ ] The runtime-rendering project builds without new warnings.

## Phase 2 - De-Nest Backend Contracts And Establish A Renderer Registry

- [ ] Inventory nested Vulkan wrapper, key, context, and diagnostic types.
- [ ] Move independently meaningful types to namespace-level `internal` types
  in domain folders.
- [ ] Update external consumers to use intentional contracts rather than
  `VulkanRenderer.SomeType` names.
- [ ] Introduce a per-renderer backend-object registry owned by the Vulkan
  device/resource context.
- [ ] Remove or constrain generic static caches that can cross renderer or
  device ownership boundaries.
- [ ] Introduce a per-renderer binding allocator where descriptor or binding
  identity currently depends on renderer-wide state.
- [ ] Preserve explicit ownership for imported/external handles.
- [ ] Add tests covering two renderer/device contexts so caches cannot leak
  wrappers, bindings, or lifetime state between them.

Acceptance criteria:

- [ ] Backend wrappers no longer require nesting for access to renderer state.
- [ ] Object identity and cache lifetime are scoped to one renderer/device.
- [ ] No backend object retains the full renderer solely as a service locator.

## Phase 3 - Extract Device And Capability Ownership

- [ ] Split logical-device capability query from enablement policy.
- [ ] Introduce an immutable `VulkanDeviceCapabilities` snapshot.
- [ ] Extract feature/extension-chain construction into a device builder.
- [ ] Extract device extension-function loading from device creation.
- [ ] Extract queue-family and queue-handle ownership into the device context.
- [ ] Extract capability reporting and diagnostics from creation policy.
- [ ] Make enabled capabilities, required capabilities, and optional fallbacks
  distinguishable in logs and APIs.
- [ ] Preserve existing device-loss state-machine and first-error behavior.

Acceptance criteria:

- [ ] `CreateLogicalDevice` is a short coordinator over focused operations.
- [ ] Runtime feature checks read one immutable capability authority.
- [ ] Device creation does not depend on unrelated frame, OpenXR, or wrapper
  state.

## Phase 4 - Extract Resource, Upload, And Lifetime Services

- [ ] Introduce `VulkanResourceLifetimeTracker` as the sole authority for
  resource-use publication and deferred destruction.
- [ ] Introduce a focused retirement queue driven by completed timeline/fence
  observations.
- [ ] Separate image allocation from image-view and sampler caches.
- [ ] Separate imported/external image ownership from engine-owned allocation.
- [ ] Separate upload scheduling, staging allocation, transfer recording, and
  upload publication.
- [ ] Split `VkImageBackedTexture` into lifecycle, imported upload, view cache,
  sampler, layout, transfer, event, and mipmap responsibilities.
- [ ] Split `VkDataBuffer` wrapper state from renderer-level buffer operations.
- [ ] Ensure command recording never performs unplanned persistent-resource
  allocation.
- [ ] Document exactly-once retirement and destruction invariants.

Acceptance criteria:

- [ ] Resource destruction has one traceable path.
- [ ] Wrapper disposal does not require broad renderer-state mutation.
- [ ] Steady-state recording and frame submission introduce no new allocations.
- [ ] Existing resource-lifecycle tests and targeted Vulkan tests pass.

## Phase 5 - Consolidate The Render Graph

- [ ] Introduce a typed `VulkanResourceBindingKey` that parses and owns the
  current texture/framebuffer/buffer binding grammar.
- [ ] Remove duplicated binding-name parsing and prefix checks.
- [ ] Split compiler/cache, frame-operation sorting, swapchain-context
  resolution, secondary-recording buckets, and attachment compatibility into
  focused compilation/scheduling types.
- [ ] Make metadata revision or immutable metadata identity part of compiler
  cache validity.
- [ ] Replace scratch-list-returning APIs with an explicit borrowed-workspace
  contract or caller-provided destination.
- [ ] Separate barrier usage collection and Vulkan mapping from plan building.
- [ ] Make the barrier builder return an immutable `VulkanBarrierPlan`.
- [ ] Extract planner context capture, generation transaction, registry merge,
  physical allocation, temporal history, graph rebuild, and validation from
  `VulkanRenderer.ResourcePlannerState.cs`.
- [ ] Introduce `VulkanRenderGraphRuntime` or
  `VulkanResourcePlanCoordinator` as the state owner.
- [ ] Make graph build produce one immutable, versioned
  `VulkanRenderGraphPlan` consumed by recording and execution.
- [ ] Confirm whether currently isolated helpers such as swapchain-context
  coalescing are required, then connect or remove them.

Acceptance criteria:

- [ ] Compilation, planning, synchronization, and execution have distinct
  owners and one-way dependencies.
- [ ] Cached plans cannot survive an unrepresented metadata revision.
- [ ] Resource binding syntax is defined and parsed in one place.
- [ ] Recording consumes immutable plan data without renderer-global planner
  reads.

## Phase 6 - Extract Command Scheduling And Recording

- [ ] Separate primary command-buffer cache/allocation from recording policy.
- [ ] Introduce `VulkanCommandScheduler` for command-chain ordering,
  parallel-recording buckets, and cache reuse decisions.
- [ ] Introduce `VulkanCommandRecorder` for Vulkan command emission.
- [ ] Add `VulkanCommandRecordingContext` and capture all frame/view/settings
  inputs before dispatch.
- [ ] Replace thread-static recording state with context fields or explicitly
  scoped reusable workspaces.
- [ ] Extract render-scope begin/end and attachment compatibility into a render
  scope controller.
- [ ] Split frame-operation dispatch into cohesive per-domain recorder methods
  or types.
- [ ] Extract barrier emission and transfer/upload recording.
- [ ] Separate recording diagnostics from recording policy.
- [ ] Decompose `TryRecordCommandBuffer` by named lifecycle phase while keeping
  tightly coupled Vulkan stack structures local.
- [ ] Decompose `EnsureCommandBufferRecorded` into cache validation, scheduling,
  recording, and publication operations.
- [ ] Preserve stable queue-operation labels when moving tracked calls.
- [ ] Benchmark or instrument allocations before and after extraction.

Acceptance criteria:

- [ ] The top-level recording method reads as a short ordered lifecycle.
- [ ] Every command operation receives required state explicitly.
- [ ] No thread-static field is required for normal command recording.
- [ ] Parallel recording does not share mutable per-recording state.
- [ ] The primary recording fast path remains allocation-free and does not
  regress measured CPU cost.

## Phase 7 - Decompose Large Backend Wrappers And Shared Services

- [ ] Split `VkMeshRenderer.Descriptors.cs` into allocation/reuse,
  fingerprints, writes/validation, buffer resolution, image/sampler
  resolution, and uniform resolution.
- [ ] Extract renderer-global program link queue behavior from
  `VkRenderProgram`.
- [ ] Split `VkRenderProgram` into lifecycle, bindings, linking, layouts,
  graphics pipelines, compute descriptors, and compute uniforms.
- [ ] Consolidate descriptor ownership behind `VulkanDescriptorManager`.
- [ ] Consolidate graphics/compute pipeline creation and caches behind
  `VulkanPipelineManager` without forcing incompatible pipelines into one
  cache model.
- [ ] Split ImGui input/clipboard integration, GPU resources and rendering,
  texture registry, and immutable draw snapshots.
- [ ] Split shader auto-uniform logic into declaration parsing, constant
  evaluation, std140 layout, and binding rewrite responsibilities.
- [ ] Keep wrapper APIs small and device/resource-context based.

Acceptance criteria:

- [ ] Each wrapper file has one obvious lifecycle and data owner.
- [ ] Renderer-global queues and caches do not live inside individual wrappers.
- [ ] Descriptor and pipeline caches have explicit device lifetime.
- [ ] ImGui and shader utilities do not use the renderer as a service locator.

## Phase 8 - Extract The Desktop Frame Coordinator

Implement the detailed
[Vulkan Desktop Frame Loop Decomposition TODO](vulkan-desktop-frame-loop-decomposition-todo.md),
which is the authoritative checklist for this phase, with this document's
ownership model as the endpoint. Do not mark Phase 8 complete here until the
focused decomposition TODO's completion criteria are also satisfied.

- [ ] Introduce `VulkanDesktopFrameCoordinator` rather than stopping at more
  stateful `VulkanRenderer` partial files.
- [ ] Use `VulkanFrameAttempt` and typed phase outcomes.
- [ ] Centralize post-acquire abort and recovery policy.
- [ ] Keep acquire, submit, present, completion, and slot-advance ownership
  explicit.
- [ ] Expose a narrow thread-safe desktop-frame activity snapshot to OpenXR and
  diagnostics.
- [ ] Move unrelated generic renderer APIs out of the frame subsystem.

Acceptance criteria:

- [ ] `WindowRenderCallback` is a short facade delegation or coordinator entry.
- [ ] Every acquired image and synchronization primitive has an exactly-once
  terminal path.
- [ ] OpenXR no longer reads mutable desktop frame-loop fields directly.
- [ ] Desktop rendering remains allocation-free in steady state.

## Phase 9 - Extract Vulkan OpenXR And ImGui Backends

Coordinate the OpenXR boundary with the
[OpenXR Runtime Code Organization TODO](vr/openxr-runtime-code-organization-todo.md).

- [ ] Introduce `VulkanOpenXrBackend` as the Vulkan graphics-binding authority,
  not as a second owner of general OpenXR runtime policy.
- [ ] Split external frame scopes, eye recording/cache, mirror, preview/copy,
  resource ownership, validation/prewarm, runtime-pressure policy, and
  submission gating.
- [ ] Pass `VulkanOpenXrFrameContext` explicitly through eye recording and
  submission.
- [ ] Keep generic OpenXR session, input, pose, and pacing state out of the
  Vulkan folder.
- [ ] Publish immutable diagnostics snapshots rather than exposing private
  Vulkan state to smoke validators.
- [ ] Introduce `VulkanImGuiBackend` after descriptor, resource, and command
  seams are stable.
- [ ] Ensure OpenXR and ImGui use the same submission, resource lifetime, and
  device-loss authorities as desktop rendering.

Acceptance criteria:

- [ ] Vulkan OpenXR code owns only graphics-binding and Vulkan presentation
  responsibilities.
- [ ] Eye, mirror, and preview paths do not directly mutate desktop coordinator
  internals.
- [ ] ImGui does not own parallel descriptor, upload, or retirement systems.

## Phase 10 - Normalize Namespaces And Assembly Boundaries

Do this only after ownership has stabilized; do not combine namespace churn
with initial file extraction.

- [ ] Introduce subsystem namespaces that match durable ownership where they
  materially improve API clarity.
- [ ] Keep facade and intentionally public contracts in stable rendering
  namespaces.
- [ ] Mark implementation types `internal` by default.
- [ ] Update architecture docs, folder README files, and active TODO links.
- [ ] Consider a separate Vulkan implementation project only after dependency
  direction is clean and a project split would enforce an already-proven
  boundary.
- [ ] If a project split is selected, document dependency and native-library
  consequences before changing project files.

Acceptance criteria:

- [ ] Namespace boundaries describe ownership instead of historical file
  placement.
- [ ] No circular subsystem dependency is hidden by a shared namespace.
- [ ] A separate project is optional, not required to compensate for poor type
  boundaries.

## Phase 11 - Add Architecture Guardrails

- [ ] Add a focused architecture test or analyzer rule preventing new stateful
  `VulkanRenderer` partial files without an explicit exception.
- [ ] Add tests preventing top-level multi-type dumping-ground files in the
  Vulkan subsystem.
- [ ] Add tests for per-renderer cache isolation and device lifetime.
- [ ] Add allocation regression coverage for primary command recording and the
  desktop frame loop where existing harnesses support it.
- [ ] Add invariant tests for resource retirement and post-acquire recovery.
- [ ] Document the approved dependency direction in the Vulkan architecture
  guide and relevant folder README files.

Acceptance criteria:

- [ ] The old monolith cannot regrow silently.
- [ ] Ownership violations produce a focused test or analyzer failure.
- [ ] Contributors can find the state owner for a command, resource, or frame
  transition from the architecture documentation.

## Validation Strategy

Run validation proportionally after each phase rather than waiting for the full
reorganization to land.

- [ ] Build runtime rendering after every mechanical move or owner extraction:

  ```powershell
  dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
  ```

- [ ] Run the narrowest tests for the changed responsibility.
- [ ] Run `Test-VulkanPhase3-Regression` after command, graph, lifetime, or
  frame-loop changes.
- [ ] Build the editor after facade, namespace, or project-boundary changes:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- [ ] Run a Vulkan Unit Testing World startup after device, swapchain, command,
  or desktop-frame changes.
- [ ] Run the applicable OpenXR Monado/SteamVR smoke lane after Vulkan OpenXR,
  submission, synchronization, descriptor, or resource-lifetime changes.
- [ ] Compare validation-layer output before and after lifecycle changes.
- [ ] Measure command-recording and frame-loop allocations before and after
  hot-path extractions.
- [ ] Record active investigation evidence under `Build/_AgentValidation/` and
  durable phase status under `docs/work/progress/rendering/`.

## Migration And Review Strategy

- Prefer small vertical extractions that establish one state owner and migrate
  all of its callers before starting the next owner.
- Keep mechanical moves, namespace changes, and behavioral fixes in separate
  reviewable changes whenever practical.
- Do not leave two long-lived authorities for resource lifetime, submission,
  descriptor allocation, or render-graph planning.
- When extraction exposes a correctness bug, record the bug and its validation
  explicitly instead of hiding a semantic fix inside a move.
- Avoid temporary abstractions whose only purpose is forwarding every member of
  `VulkanRenderer`; that preserves the service-locator problem under a new type.
- Prefer internal concrete APIs until a stable multi-implementation seam is
  demonstrated.
- Land device/resource/render-graph foundations before command and frame
  coordinators, then extract OpenXR and ImGui after the shared services are
  stable.

## Completion Criteria

- [ ] `VulkanRenderer` is a small facade and composition root, not the owner of
  most backend state.
- [ ] Device, resources, render graph, command recording, desktop frames,
  descriptors, pipelines, OpenXR, and ImGui each have an explicit owner.
- [ ] No production hot path depends on thread-static context for ordinary
  operation.
- [ ] The largest orchestration methods read as named, testable lifecycles.
- [ ] Backend wrappers are namespace-level types with per-renderer/device
  identity and lifetime.
- [ ] Render-graph plans are immutable, versioned, and free of hidden borrowed
  scratch lifetimes.
- [ ] Resource destruction, queue submission/presentation, and post-acquire
  recovery each have one authority.
- [ ] Folder contracts, namespaces, tests, and architecture docs agree with the
  implementation.
- [ ] Targeted tests, runtime-rendering build, editor build, Vulkan startup, and
  relevant OpenXR smoke validation pass without new warnings or validation
  errors.
- [ ] Command recording and frame orchestration remain allocation-free in
  steady state and show no material CPU regression.

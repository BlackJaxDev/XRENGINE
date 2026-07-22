# OpenGL And Vulkan Rendering Hot Reload TODO

Created: 2026-07-21

Owner: Rendering / Editor / Runtime modularization

Status: Proposed

Related design, execution, and architecture documents:

- [Runtime Modularization And Bootstrap Extraction Plan](../../design/runtime-modularization-plan.md)
- [Runtime Modularization Phase 4 - Remaining Rendering Move](../runtime-modularization-phase4-todo.md)
- [Vulkan Runtime Code Organization TODO](vulkan-runtime-code-organization-todo.md)
- [OpenGL Renderer](../../../architecture/rendering/opengl-renderer.md)
- [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md)
- [Render Pipeline Resource Lifecycle](../../../architecture/rendering/render-pipeline-resource-lifecycle.md)
- [Dedicated Render Thread And Window Ownership Plan](../../design/rendering/dedicated-render-thread-window-ownership-plan.md)
- [Vulkan Shader Object Pipeline Replacement](../../design/rendering/vulkan-shader-object-pipeline-replacement-design.md)
- [OpenXR Runtime Code Organization TODO](vr/openxr-runtime-code-organization-todo.md)

## Goal

Make rendering iteration possible without restarting the editor process.

The completed system must support three progressively broader reload levels:

1. Shader and render-asset hot reload replaces affected GPU programs and state
   while the current renderer remains alive.
2. Compatible managed method updates use .NET Hot Reload as a fast path.
3. Structural OpenGL or Vulkan C# changes rebuild and reload only the active
   backend assembly, then recreate that renderer inside the existing editor
   process while preserving the editor session and logical scene state.

"Fully hot-reloadable" in this document means that ordinary rendering work can
continue without closing the editor. It does not promise that arbitrary runtime,
contract, dependency, native-driver, window-creation, or XR-runtime changes can
be patched into live native objects. When an edit cannot be applied in place,
the renderer may pause, tear down, reload its module, recreate GPU state, and
resume. The editor process, project, scene, selection, camera placement, undo
history, and unsaved authoring state must survive that operation.

## User Outcome

A rendering developer should be able to:

1. launch the editor once;
2. edit GLSL or OpenGL/Vulkan C# code;
3. save or explicitly request a backend build;
4. see build and reload status in the editor;
5. keep rendering with the new code after a successful swap;
6. keep the last known-good renderer or receive an actionable diagnostic after
   a compile, load, initialization, or resource-rehydration failure; and
7. continue editing without reconstructing the scene or restarting the editor.

No requested accelerated path may silently switch to a CPU renderer or another
graphics API because reload failed.

## Scope And Reload Contract

### Level 1 - Live shader and render-asset replacement

Expected to remain within the current renderer:

- top-level shader-source edits;
- edits to included GLSL files and registered snippets;
- shader stage additions/removals when the material/program contract permits it;
- uniform, sampler, image, storage-buffer, push-constant, and descriptor-layout
  changes with explicit binding-state reconstruction;
- generated uber-shader variants;
- compute, vertex, fragment, geometry, tessellation, task, and mesh shaders;
- transform-feedback declarations;
- material parameter changes;
- render-pipeline asset or settings changes whose resource-generation key can
  represent the new state; and
- backend pipeline/program rebuilds caused by those changes.

Target experience: compile a candidate asynchronously, keep rendering with the
last good candidate, and atomically publish the replacement at a frame boundary.

### Level 2 - Managed delta update

.NET Hot Reload is an optional fast path for compatible changes such as many
method-body edits. It is not the correctness boundary because unsupported
"rude edits," constructor/state changes, active frames, static state, and type or
contract changes may not acquire the desired live state merely because metadata
and method bodies were patched.

Every applied renderer-code delta must do one of the following:

- prove that no state invalidation is required;
- execute a registered metadata-update handler that invalidates the affected
  renderer caches or render-pipeline state; or
- request a Level 3 backend-module reload.

The workflow must never automatically restart the entire editor process when a
backend edit cannot use Level 2. It should offer or initiate the backend-module
reload path instead.

### Level 3 - Backend module rebuild and soft renderer restart

Structural backend edits rebuild a separate OpenGL or Vulkan DLL, unload the
old collectible module after renderer teardown, load the new generation, create
a replacement renderer for the existing window/session, rehydrate GPU state,
and resume.

This is the mechanism that makes "do not restart the editor" reliable. It is not
an in-place mutation of every old object.

### Explicitly outside the first completion milestone

- hot reloading `XREngine.Runtime.Rendering` itself;
- changing the backend-module ABI without rebuilding/restarting its stable host;
- dependency upgrades or replacements;
- replacing a loaded native DLL in place when the OS or native library does not
  support unload;
- changing process architecture, target framework, or graphics-driver state;
- preserving GPU-only temporal/history data that has no valid CPU or asset
  reconstruction source;
- preserving an active OpenXR session across Vulkan device replacement before
  explicit XR session-recreation support exists; and
- production NativeAOT dynamic assembly loading. Production/AOT builds use
  static backend registration; collectible loading is an editor/CoreCLR feature.

These cases must be reported as explicit reload-boundary limitations. Wherever
possible, the editor may preserve its process and request a window, context, or
XR-session recreation instead of requiring an editor restart.

## Current Baseline

The repository already contains useful pieces, but they do not yet form an
end-to-end hot-reload contract.

### Existing shader and asset behavior

- `AssetManager` watches game and engine asset roots and reloads already-loaded
  assets when their primary files change.
- `XRShader` emits `SourceChanged` when its type, source object, or source text
  changes.
- `ShaderSourceResolver` records include dependencies and can detect stale file
  timestamps/lengths when source resolution is requested.
- Vulkan `VkShader` subscribes to `TextFile.TextChanged`, invalidates compiled
  artifacts, and notifies `VkRenderProgram`.
- Vulkan program/layout and mesh-pipeline code already has pipeline dirtiness,
  asynchronous compilation, shared caches, command-buffer invalidation, and
  deferred resource-retirement mechanisms.
- OpenGL `GLRenderProgram` can build a replacement program and relink after its
  `GLShader.SourceChanged` event.
- OpenGL and Vulkan have asynchronous shader/pipeline compilation machinery.

### Known shader-path gaps to close

- `GLShader` currently observes `XRShader.PropertyChanged` for `Source` and
  `Type`, but does not directly observe `XRShader.SourceChanged` or the current
  `TextFile.TextChanged`. In-place editor changes can therefore miss the backend
  invalidation path even though replacing the `Source` object works.
- Include/snippet dependencies are freshness-checked when source is resolved,
  but no authoritative reverse dependency watcher proactively identifies and
  invalidates every loaded shader affected by a changed include.
- Reload generation ordering is not a documented contract. A slow compile from
  an older edit must never publish after a newer edit.
- Failure behavior is inconsistent. Every path must retain the last known-good
  program/pipeline when candidate compilation or linking fails.
- Interface-changing reloads need a common contract for material parameter
  reconciliation, descriptors, push constants, transform feedback, command
  buffers, and pipeline/resource generations.
- Generated shader/program/pipeline caches need bounded stale-generation
  eviction so repeated edits do not accumulate live managed or GPU resources.

### Existing renderer recreation behavior

`XRWindow` already contains a device-loss recovery path that:

- deactivates the current renderer;
- waits for GPU work when possible;
- destroys cached API render objects and removes their wrappers;
- cleans up the renderer;
- creates and initializes a replacement renderer for the same window;
- invalidates scene-panel and viewport pipeline resources; and
- resizes/rebinds framebuffer-dependent state.

That lifecycle is the starting point for Level 3. It must be generalized into a
first-class renderer replacement transaction rather than copying it into a
second reload-only path.

The existing device-loss path explicitly rejects recreation of an
OpenXR-owned Vulkan device. The first hot-reload milestone must preserve that
safety rule and expose a visible "stop/restart XR session before backend reload"
result until XR-owned device recreation is implemented and validated.

### Current assembly-boundary blocker

`OpenGLRenderer`, `VulkanRenderer`, their nested/concrete API wrappers, shared
rendering assets, render pipelines, windows, and the renderer factory all
currently compile into `XREngine.Runtime.Rendering.dll`. The composition host
also directly constructs concrete renderer types.

A DLL loaded in the default `AssemblyLoadContext` cannot become independently
collectible later. Therefore a reliable Level 3 implementation requires the
concrete backends to become leaf modules before the editor process starts:

```text
XREngine.Runtime.Rendering.dll                  (stable, non-collectible)
  |-- backend-neutral rendering model and assets
  |-- render pipelines and logical resource generations
  |-- XRWindow/XRViewport and renderer replacement coordinator
  |-- AbstractRenderer and backend-module contracts
  `-- module catalog/loader contracts

XREngine.Runtime.Rendering.OpenGL.dll           (collectible in editor)
  |-- OpenGLRenderer and every GL API wrapper
  |-- GL shader/program/pipeline and resource implementations
  |-- GL ImGui/platform-window backend
  |-- GL shared-context/compiler workers
  `-- OpenGL-specific OpenXR/UI/video/native interop

XREngine.Runtime.Rendering.Vulkan.dll           (collectible in editor)
  |-- VulkanRenderer and every Vulkan API wrapper
  |-- device/swapchain/frame/command/resource/descriptor/pipeline owners
  |-- Vulkan ImGui backend
  |-- Vulkan compilation/recording workers
  `-- Vulkan-specific OpenXR/DLSS/Streamline/video/native interop
```

The stable rendering assembly must not reference either backend module. Backend
modules may reference the stable rendering assembly, Runtime.Core, Data, and
other approved lower dependencies. They must not reference each other, Editor,
or an application executable.

Applications may have build/package edges that cause backend DLLs to be copied
to output, but runtime discovery must use the stable backend-module contract,
not direct C# references to concrete renderer types.

## Target Backend Module Contract

Define a deliberately small stable contract in `XREngine.Runtime.Rendering`.
Names below are illustrative; the implementation may refine them while keeping
the ownership rules.

```csharp
public interface IRenderBackendModule : IDisposable
{
    RenderBackendModuleMetadata Metadata { get; }
    IRenderBackendCapabilities Capabilities { get; }

    IRuntimeRendererHost CreateRenderer(
        IRuntimeRenderWindowHost window,
        in RendererCreationOptions options);

    ValueTask PrepareForUnloadAsync(
        RendererModuleUnloadContext context,
        CancellationToken cancellationToken);
}
```

The stable contract needs:

- backend ID (`OpenGL`, `Vulkan`), display name, module generation, build hash,
  target framework, process architecture, and ABI version;
- factory entry point and supported renderer/window/API kinds;
- capability queries that replace stable-host type tests such as
  `renderer is VulkanRenderer`;
- explicit reload limitations such as `RequiresWindowRecreation`,
  `RequiresXrSessionRestart`, or `NotReloadableBecauseNativeDependency`;
- lifecycle hooks for creation, quiescing, teardown, and diagnostics;
- no concrete GL/Vulkan types in arguments, returns, events, exceptions, or
  stable static fields; and
- no delegates whose target is a collectible module retained after unload.

`AbstractRenderer`, `IRuntimeRendererHost`, generic render assets, and logical
render-resource contracts remain stable. Concrete API wrappers must be removed
from `GenericRenderObject.APIWrappers` before module unload. No backend wrapper
or backend-owned `Type`, exception, task, thread, callback, or closure may remain
reachable from a non-collectible static or long-lived engine object.

## Backend Assembly Loading Rules

Editor dynamic loading must use a collectible `AssemblyLoadContext` per loaded
backend generation.

- Build to normal project output, then copy the complete load set to a unique,
  versioned shadow directory before loading it.
- Use `AssemblyDependencyResolver` rooted at the shadow-copied module.
- Always unify stable engine assemblies with the default load context. Reject a
  module that attempts to load private copies of `XREngine.Runtime.Rendering`,
  Runtime.Core, Data, or other declared shared contracts.
- Resolve only explicitly approved private managed/native dependencies from the
  module generation directory.
- Validate module ID, ABI version, target framework, architecture, expected
  entry point, and file hashes before activation.
- Keep the current and at least one last known-good module generation until the
  replacement initializes and produces a valid frame.
- Delete expired shadow generations only after their load contexts are proven
  unreachable and their native files are no longer loaded.
- Bound retained generations and disk usage.
- Never load arbitrary DLLs discovered elsewhere in the project or user asset
  tree.

For production and NativeAOT builds, generate or explicitly register static
backend factories at build time. Dynamic module discovery and collectible
loading must be editor-only capabilities, not hidden production requirements.

## Renderer Replacement Transaction

Create one renderer-replacement coordinator used by manual reload, automatic
reload, backend switching where supported, and device-loss recovery.

Suggested states:

```text
Idle
  -> BuildPending
  -> ReplacementRequested
  -> Quiescing
  -> DrainingGpu
  -> DestroyingWrappers
  -> CleaningBackend
  -> UnloadingModule
  -> LoadingCandidate
  -> InitializingCandidate
  -> RehydratingResources
  -> AwaitingFirstValidFrame
  -> Resuming
  -> Idle

Any candidate state may enter Failed -> RollingBack -> Resuming/FailedStopped.
```

Required transaction behavior:

1. Coalesce rapid reload requests and associate all work with a monotonic module
   generation.
2. Finish or abandon the current frame at a legal boundary.
3. Block new backend wrapper creation, GPU submission, upload publication,
   command recording, and pipeline publication for the retiring generation.
4. Stop and join backend-owned compiler, upload, recording, readback, and
   presentation workers. Late results must carry a generation token and be
   rejected.
5. Prepare OpenXR/OpenVR, ImGui, detached platform windows, video upload, and
   external native integrations for renderer teardown.
6. Wait for relevant GPU fences or device/context idle with a bounded timeout
   and actionable diagnostics. Never force unload while native work can call
   into collectible managed code.
7. Destroy cached API wrappers and remove them from stable render assets.
8. Drain deferred retirement queues and validate that no live backend resource
   remains unless the contract explicitly transfers ownership to a stable host.
9. Clean up the backend, unregister native/debug callbacks, and release handles.
10. Release all stable references to the backend module and request ALC unload.
11. Load and validate the candidate generation.
12. Create/initialize replacement renderers for every affected window as one
    coordinated backend generation.
13. Invalidate physical pipeline resources and recreate imported/window targets.
14. Rehydrate resource wrappers lazily or through an explicit warm-up budget.
15. Require a valid rendered/presented frame before discarding the last-good
    module generation.
16. On failure, clean up the candidate and reload/reinitialize the last-good
    generation when safe. If rollback also fails, leave rendering stopped with
    a visible diagnostic while the editor remains usable where possible.

Do not hold the global engine lock while waiting for build, GPU, worker, GC, or
module-load completion. The transaction must declare its render-thread, window
owner-thread, and application-thread handoffs explicitly.

## State Preservation And Rehydration Rules

### State that must survive Level 3

- project and world ownership;
- loaded logical assets and asset IDs;
- scene graph and component state;
- editor selection, hierarchy expansion, inspector targets, undo/redo, and
  unsaved authoring data;
- editor and player camera transforms;
- viewport layout, logical resolution policy, and pipeline selection;
- logical materials, shaders, meshes, textures, buffers, samplers, and render
  resource descriptors;
- effective rendering settings and debug toggles, except settings explicitly
  requiring context/window recreation; and
- persistent disk caches whose compatibility fingerprints still match.

### State that must be recreated or reset

- GL/Vulkan object IDs and native handles;
- descriptor pools/sets/heaps, program/pipeline layouts, framebuffers, command
  pools/buffers, fences, semaphores, queries, and swapchains;
- backend wrapper caches;
- backend compile/upload/recording queues;
- ImGui renderer resources and font textures;
- GPU timing/query history;
- temporal rendering history whose old images are destroyed; and
- transient pipeline generations and imported external targets.

Temporal resets must be intentional and visible in diagnostics. They must not be
mistaken for stale resource reuse. Resources that exist only on the GPU must be
classified before implementation: reconstruct from logical/CPU/cooked source,
explicitly reset, or declare backend reload unsupported while they are active.

## Execution Plan

### HR0 - Baseline, inventory, and branch contract

- [ ] Reconcile this work with the active Runtime Modularization Phase 4 branch
  and do not independently move the same source in competing branches.
- [ ] Inventory every OpenGL/Vulkan source file, package, native library,
  generated binding, embedded resource, content-copy rule, AOT registration,
  reflection lookup, serializer type identity, and test that must move with each
  backend.
- [ ] Inventory every stable-layer reference to `OpenGLRenderer`,
  `VulkanRenderer`, nested backend wrapper types, Silk OpenGL/Vulkan types,
  backend enums, and backend-specific exception/diagnostic types.
- [ ] Inventory all static fields, event subscriptions, callbacks, delegates,
  GC handles, thread-static state, worker threads, tasks, timers, and native
  registrations owned by either backend.
- [ ] Inventory multi-window, detached ImGui viewport, shared GL context,
  OpenXR/OpenVR, DLSS/Streamline/XeSS, video streaming, texture upload, profiler,
  RenderDoc/debug-marker, and device-loss integration points.
- [ ] Record current renderer creation/destruction and device-loss recovery call
  graphs, including thread affinity and exact cleanup order.
- [ ] Capture baseline shader reload and full renderer recreation timings for
  representative OpenGL and Vulkan Unit Testing World scenes.
- [ ] Record managed memory, GPU resource counts, native handle counts, worker
  counts, and pipeline/program cache size before and after repeated manual
  renderer recreation.
- [ ] Define initial performance budgets only after the baseline is captured.

Acceptance criteria:

- [ ] Every concrete backend dependency has a target owner or an explicitly
  documented blocker.
- [ ] The inventory distinguishes stable contract state from disposable backend
  state.
- [ ] Existing unrelated build, validation, and runtime failures are recorded
  separately from hot-reload work.

### HR1 - Complete shader dependency hot reload

- [ ] Add an authoritative loaded-shader dependency index from normalized source
  paths to every top-level shader and generated variant that consumes them.
- [ ] Update the index whenever source resolution, includes, snippets, source
  paths, or generated variant composition changes.
- [ ] Connect game/engine file changes, rename, create, and delete events to the
  dependency index.
- [ ] Debounce duplicate/partial file-system events and wait for a readable,
  stable file before compiling; never block the watcher callback on compilation.
- [ ] Make `GLShader` observe in-place `XRShader.SourceChanged`/`TextChanged`
  behavior symmetrically with Vulkan.
- [ ] Give shader-source and program-build requests monotonic revisions and
  reject stale asynchronous results.
- [ ] Compile/link candidates without destroying the active last-good shader,
  program, pipeline, or descriptor state.
- [ ] Publish successful candidates only at a legal frame boundary.
- [ ] Keep the last-good candidate active after compile/link/reflection failure
  and surface file, include stack, stage, backend, variant, and complete compiler
  diagnostics in the editor.
- [ ] Rebuild uniform/material metadata, descriptor state, push-constant ranges,
  transform-feedback state, vertex input, affected pipelines, and cached command
  buffers when the shader interface changes.
- [ ] Invalidate only affected Vulkan pipeline/command variants where dependency
  tracking is complete; use a visible broader invalidation when it is not.
- [ ] Retire replaced GL/Vulkan objects only after in-flight use ends.
- [ ] Bound stale shader/program/pipeline/generated-variant cache generations.
- [ ] Cover all supported shader stages and compute-only programs.

Acceptance criteria:

- [ ] Editing a top-level shader or any transitive include updates OpenGL and
  Vulkan without restarting or recreating the renderer.
- [ ] A deliberately broken edit leaves the previous image rendering and shows
  an actionable error.
- [ ] Saving edits rapidly cannot publish an older result after a newer result.
- [ ] Repeated successful and failed reloads do not leak programs, pipelines,
  layouts, descriptors, command buffers, managed subscriptions, or cache entries.

### HR2 - Define and enforce the stable backend-module boundary

- [ ] Add the backend-module metadata, factory, lifecycle, capability, creation,
  reload-limitation, and diagnostic contracts to `Runtime.Rendering`.
- [ ] Replace direct concrete construction in rendering-host services with an
  installed module catalog/factory.
- [ ] Replace stable-layer `is OpenGLRenderer`/`is VulkanRenderer` checks and
  concrete casts with capabilities or backend-neutral interfaces.
- [ ] Ensure `XRWindow` and the generalized renderer replacement coordinator do
  not reference concrete backend types.
- [ ] Ensure editor UI, profiler, MCP, tests, and application composition do not
  retain concrete backend objects across reload.
- [ ] Make `GenericRenderObject` wrapper cleanup complete, deterministic, and
  verifiable before module unload.
- [ ] Define ABI compatibility/versioning and fail visibly on mismatches.
- [ ] Define static factory registration for production/AOT and collectible
  registration for the editor without duplicating behavior.
- [ ] Add project/source-contract tests that prevent `Runtime.Rendering` from
  referencing the concrete backend projects or their API packages.
- [ ] Add project/source-contract tests that prevent either backend from
  referencing the other, Editor, Server, VRClient, or executable policy.

Acceptance criteria:

- [ ] A test backend module can be loaded, create a renderer, tear down, unload,
  and be collected without OpenGL/Vulkan code present.
- [ ] The stable rendering kernel compiles without concrete renderer type names.
- [ ] Required factory failures are actionable; no renderer is silently chosen.

### HR3 - Extract `XREngine.Runtime.Rendering.OpenGL.dll`

- [ ] Create `XREngine.Runtime.Rendering.OpenGL` as a leaf backend project.
- [ ] Move `OpenGLRenderer`, every GL API wrapper, GL-specific values, shader and
  program linking, program pipelines, resource upload, readback, query, mesh,
  material, framebuffer, ImGui, and diagnostics implementation into it.
- [ ] Move OpenGL-specific OpenXR, Ultralight/UI driver, video upload, platform
  window, and native callback behavior with the backend.
- [ ] Move Silk.NET.OpenGL/WGL and other OpenGL-only package/native/content
  ownership out of the stable rendering project wherever no stable consumer
  remains.
- [ ] Make backend wrapper types namespace-level/internal where practical; do
  not expand the current partial/nested-type coupling during the move.
- [ ] Define ownership of the primary window context versus module-owned shared
  contexts. The first implementation may retain the primary window/context in
  the stable window host, but every backend-created shared context and worker
  must stop and release before unload.
- [ ] Unregister GL debug callbacks and ensure callback delegates are no longer
  native-reachable before unload.
- [ ] Stop/join GL compile, binary upload, mesh generation, texture upload,
  readback, and detached ImGui viewport work.
- [ ] Preserve context-creation settings that require window recreation as an
  explicit reload limitation rather than pretending they were applied.
- [ ] Move backend tests and replace source-text tests that assume old paths.
- [ ] Update AOT factory generation and static production registration.
- [ ] Update application output/package rules so the backend and its private
  dependencies are copied without a compile-time concrete-type dependency.

Acceptance criteria:

- [ ] `Runtime.Rendering` builds with no OpenGL implementation source and no
  accidental OpenGL-only dependency.
- [ ] The editor can statically load and render through the extracted backend
  before collectible reload is enabled.
- [ ] OpenGL cleanup leaves no live shared context, worker, callback, wrapper, or
  GL object owned by the retiring module.
- [ ] Detached ImGui platform windows either survive and rebind or are recreated
  with documented editor-state preservation.

### HR4 - Extract `XREngine.Runtime.Rendering.Vulkan.dll`

- [ ] Create `XREngine.Runtime.Rendering.Vulkan` as a leaf backend project.
- [ ] Move `VulkanRenderer`, every Vulkan API wrapper, device/swapchain/frame
  loop, command recording, resource lifetime, descriptors, pipelines, render
  graph, ImGui, diagnostics, and feature implementation into it.
- [ ] Move Vulkan-specific OpenXR, DLSS/Streamline, video upload, VMA bridge,
  debug callbacks, generated bindings, native binaries, and content ownership
  with the backend or behind an explicitly lower optional integration contract.
- [ ] Move Silk.NET.Vulkan and Vulkan-only extension/package ownership out of the
  stable rendering project wherever no stable consumer remains.
- [ ] Coordinate with the Vulkan Runtime Code Organization TODO so source moves
  do not freeze the partial-class monolith as the permanent module design.
- [ ] Make async shader/pipeline compilation, command-chain workers, secondary
  recorders, uploads, readbacks, and presentation tasks generation-aware and
  joinable.
- [ ] Generalize device-loss teardown/recreation primitives for the normal
  reload transaction without weakening device-loss diagnostics.
- [ ] For the first Level 3 implementation, recreate the Vulkan instance/device,
  queues, allocator, swapchain, caches, and resources rather than trying to
  transfer live handles across assembly generations.
- [ ] Persist only disk pipeline/prewarm caches whose device/build/feature
  fingerprints remain compatible.
- [ ] Unregister Vulkan debug and external SDK callbacks before unload.
- [ ] Ensure deferred retirement and tracked lifetime registries are empty or
  safely completed before the module becomes collectible.
- [ ] Move backend tests and replace path-sensitive source-text tests.
- [ ] Update AOT factory generation and static production registration.
- [ ] Update application output/package rules for the backend and private native
  dependencies.

Acceptance criteria:

- [ ] `Runtime.Rendering` builds with no Vulkan implementation source and no
  accidental Vulkan-only dependency.
- [ ] The editor can statically load and render through the extracted backend
  before collectible reload is enabled.
- [ ] Vulkan cleanup leaves no live backend worker, callback, wrapper, tracked
  resource, pending retirement, or module-owned native handle.
- [ ] Validation remains clean through one explicit teardown/recreation cycle.

### HR5 - Implement the collectible module catalog and loader

- [ ] Add installed/static and editor-collectible module catalog modes behind
  the same stable factory contract.
- [ ] Implement versioned shadow-copy staging and `AssemblyDependencyResolver`.
- [ ] Enforce shared assembly unification and reject duplicate stable contracts.
- [ ] Implement managed and native dependency resolution with bounded search
  roots and actionable failure diagnostics.
- [ ] Validate module metadata, architecture, target framework, ABI, entry point,
  backend ID, and hashes before activation.
- [ ] Track the active, candidate, and last-good generations explicitly.
- [ ] Retain weak references to unloaded contexts for diagnostics without keeping
  their assemblies or types alive.
- [ ] Add an unload verification command/test that reports known roots when an
  ALC remains alive after cooperative unload and diagnostic GC cycles.
- [ ] Bound generation retention and safely clean old shadow directories.
- [ ] Ensure loader/build/cache directory changes cannot trigger recursive asset
  or module rebuild watchers.
- [ ] Reject activation when any required native binary is already loaded from
  an incompatible generation and cannot be safely reused/unloaded.

Acceptance criteria:

- [ ] A minimal backend generation can be loaded/unloaded repeatedly and its ALC
  becomes unreachable.
- [ ] A module with a duplicate contract DLL, bad ABI, wrong architecture, or
  missing native dependency is rejected without affecting the active renderer.
- [ ] Last-good module files remain available until candidate acceptance.

### HR6 - Implement editor build and reload orchestration

- [ ] Add a build service that targets only the selected backend project and
  captures structured MSBuild/Roslyn diagnostics.
- [ ] Support explicit "Build and Reload Renderer" first; add opt-in automatic
  reload after manual behavior is reliable.
- [ ] Debounce saves, cancel superseded builds, and never load incomplete output.
- [ ] Publish a generation manifest only after a successful build and complete
  shadow copy.
- [ ] Keep build work off render/application hot paths.
- [ ] Distinguish compile failure, staging failure, module validation failure,
  teardown failure, unload leak, candidate initialization failure, first-frame
  failure, and rollback failure.
- [ ] Add cancellation rules: cancellation is safe before quiescing; after
  teardown begins the transaction must finish candidate activation or rollback.
- [ ] Add a CLI/VS Code task for starting the editor in renderer-development mode.
- [ ] Do not set `.NET watch` to restart the whole process automatically on rude
  edits in this workflow.

Acceptance criteria:

- [ ] A backend compile error leaves the current renderer untouched.
- [ ] A successful backend build produces exactly one candidate reload request.
- [ ] Rapid saves/builds cannot activate a stale module generation.

### HR7 - Generalize and harden renderer replacement

- [ ] Extract the existing `XRWindow` device-loss recreation sequence into the
  shared replacement coordinator.
- [ ] Preserve device-loss retry/circuit-breaker behavior while allowing a
  user/code-reload reason with its own policy and diagnostics.
- [ ] Coordinate every window using the retiring backend; never mix backend
  module generations in shared static/native state.
- [ ] Define legal frame-abandonment, present, and swapchain ownership behavior
  for reload requested during acquire, recording, submission, or present.
- [ ] Define render/application/window-owner thread handoffs and assert them.
- [ ] Block new wrapper creation and backend work publication after quiescing.
- [ ] Add timeouts and diagnostics for workers, GPU drain, native callbacks,
  resource cleanup, ALC unload, candidate initialization, and first valid frame.
- [ ] Never forcibly unload an assembly while module code or callbacks can still
  execute.
- [ ] Invalidate render-pipeline physical generations and imported targets after
  replacement while preserving logical layouts and profiles.
- [ ] Reset temporal histories and backend telemetry explicitly.
- [ ] Warm essential editor/UI/present resources before resuming; schedule
  optional resource rehydration within existing work budgets.
- [ ] Accept the candidate only after a valid render and present/capture result.
- [ ] Implement candidate cleanup and last-good rollback through the same
  transaction primitives.

Acceptance criteria:

- [ ] OpenGL and Vulkan each complete a manual same-generation renderer restart
  without restarting the editor or losing logical editor/scene state.
- [ ] Extracted backend generations can then be replaced by a newly loaded DLL.
- [ ] A failed candidate returns to the last-good generation or leaves rendering
  safely stopped with a visible error.
- [ ] No frame uses objects from two backend generations.

### HR8 - Preserve editor, viewport, ImGui, and logical render state

- [ ] Audit every editor/rendering state owner and move session-critical logical
  state out of collectible modules.
- [ ] Preserve editor selection, inspectors, undo/redo, unsaved assets, cameras,
  viewport layout, render-pipeline selection, and debug settings.
- [ ] Preserve the ImGui context/docking configuration where backend ownership
  permits it; recreate renderer/platform resources explicitly.
- [ ] Preserve logical render resources and rebuild API wrappers from asset/CPU/
  cooked sources.
- [ ] Classify GPU-only simulation, temporal, query, and capture resources and
  define reset or unsupported behavior.
- [ ] Rebind scene panels, viewport pipeline instances, external textures,
  editor thumbnails, gizmos, debug rendering, and profiler feeds.
- [ ] Make stale backend objects fail generation validation instead of invoking a
  disposed module.
- [ ] Add a visible one-frame/reload placeholder rather than presenting stale or
  uninitialized data when rehydration is incomplete.

Acceptance criteria:

- [ ] A representative edited scene remains open, selected, and unsaved across
  backend reload.
- [ ] Camera and viewport state are unchanged apart from intentional temporal
  history reset.
- [ ] No inspector, thumbnail, profiler, or UI callback retains the old module.

### HR9 - Integrate .NET Hot Reload as the fast lane

- [ ] Add a documented Debug launch/task using `dotnet watch` or the supported
  debugger Hot Reload workflow for the editor and backend projects.
- [ ] Add metadata-update handlers for caches/state that must be invalidated
  after compatible renderer method updates.
- [ ] Classify common backend edits as safe delta, delta plus invalidation, or
  module reload required.
- [ ] Surface which mechanism applied each saved change.
- [ ] Provide an explicit "escalate to backend reload" action when Hot Reload
  reports an unsupported edit or the developer wants constructors/static state
  rebuilt.
- [ ] Ensure Level 2 and Level 3 cannot run concurrently.
- [ ] Test that an applied method update affects subsequent frames and that
  active/stale methods do not create mixed-generation assumptions.

Acceptance criteria:

- [ ] Common method-body edits appear without renderer recreation where safe.
- [ ] Structural edits route to Level 3 without an editor-process restart.
- [ ] Developers can see whether state invalidation or renderer recreation
  occurred.

### HR10 - Editor controls and diagnostics

- [ ] Add renderer-development preferences for manual/automatic shader reload,
  automatic backend build/reload, debounce, timeouts, and last-good retention.
- [ ] Add an ImGui rendering-development panel or focused diagnostics section.
- [ ] Show active backend, module generation/build hash, ALC identity, reload
  state, build status, shader status, current limitation, and last error.
- [ ] Add "Reload Shaders," "Build and Reload Renderer," "Retry Candidate,"
  "Roll Back," and "Copy Diagnostics" actions with correct enablement.
- [ ] Show progress without blocking the editor message pump.
- [ ] Log a structured reload transaction with durations for build, quiesce, GPU
  drain, wrapper destruction, cleanup, unload, load, initialization,
  rehydration, first frame, and rollback.
- [ ] Add counters for successful/failed reloads, stale results rejected, live
  module generations, unload leaks, and last-good rollbacks.
- [ ] If MCP actions are added, provide status/query and explicit reload tools,
  preserve authorization/read-only policy, and regenerate MCP documentation.

Acceptance criteria:

- [ ] A developer can understand every reload failure without searching generic
  logs first.
- [ ] High-frequency diagnostics remain gated/throttled and do not allocate in
  steady-state render paths.

### HR11 - Multi-window, OpenXR/OpenVR, and external integration hardening

- [ ] Validate same-backend multi-window replacement as one atomic generation.
- [ ] Validate OpenGL shared contexts and detached ImGui platform windows.
- [ ] Define whether mixed OpenGL/Vulkan windows may reload independently; if
  global stable state prevents it, make the group constraint explicit.
- [ ] Preserve the current safety rejection for active OpenXR-owned Vulkan device
  replacement until a full session teardown/recreate path is implemented.
- [ ] Add an editor-preserving workflow that stops XR presentation/session,
  reloads the backend, and offers to restart XR.
- [ ] Audit OpenVR/OpenXR action, swapchain, mirror, eye-preview, and native
  callback ownership.
- [ ] Audit Streamline/DLSS/XeSS, RenderDoc markers/capture state, video upload,
  external texture sharing, and profiler native integrations.
- [ ] Report integrations that make the current generation non-reloadable and
  name the exact resource/callback preventing teardown.
- [ ] Never silently disable XR or an explicitly requested accelerated feature
  merely to complete reload.

Acceptance criteria:

- [ ] Desktop reload behavior is not weakened by unavailable XR hardware.
- [ ] Active XR receives a safe, visible result instead of unsafe device teardown.
- [ ] Supported XR session restart preserves the editor process and world state.

### HR12 - Failure injection, leak detection, and stress hardening

- [ ] Inject shader compile, program link, backend build, shadow-copy, ABI,
  dependency resolution, initialization, first-frame, and rollback failures.
- [ ] Inject delayed GL compile/upload completions and Vulkan shader/pipeline/
  command-recording results from an obsolete generation.
- [ ] Inject GPU-drain timeout, stuck worker, outstanding callback, resource leak,
  and ALC-unload leak conditions.
- [ ] Inject device loss during build, quiesce, initialization, and first frame.
- [ ] Test rapid alternating successful and broken edits.
- [ ] Test reload during resize, minimize/restore, scene change, play-mode change,
  asset import, shader prewarm, and viewport creation/destruction.
- [ ] Run at least 100 same-backend reload cycles and track managed memory,
  collectible ALCs, threads/tasks, native handles, GL/Vulkan objects, descriptor
  pools, pipeline/program caches, and disk generations.
- [ ] Make leak diagnostics identify retaining roots or owner categories where
  practical.
- [ ] Verify reload orchestration adds no steady-state per-frame allocations or
  synchronization after it returns to `Idle`.

Acceptance criteria:

- [ ] No injected candidate failure corrupts the active last-good renderer.
- [ ] Old generations cannot publish work after retirement.
- [ ] Repeated reload reaches a bounded memory/resource steady state.
- [ ] An unload leak blocks unsafe activation and produces an actionable report.

### HR13 - Validation matrix, performance, documentation, and closeout

- [ ] Add focused unit tests for dependency indexing, revisions, candidate
  publication, ABI validation, module resolution, reload state transitions,
  rollback, capability routing, and stale-generation rejection.
- [ ] Add project/source-contract tests for the final assembly graph.
- [ ] Add integration tests using a minimal fake backend before depending on GPU
  hardware.
- [ ] Run shader hot reload on OpenGL and Vulkan for every supported shader stage
  and representative interface changes.
- [ ] Run full backend reload for Editor default and Unit Testing World scenes.
- [ ] Validate default/deferred/forward/post-process/shadow/UI/compute/meshlet/
  GPU-driven paths available in the test world.
- [ ] Validate resize, HDR/AA changes, multi-window, detached ImGui viewports,
  device-loss recovery, and supported XR teardown/restart.
- [ ] Compare save-to-visible-frame, build, teardown, reload, first-frame, and
  warm-up timings against HR0 baselines.
- [ ] Define and enforce budgets for editor unresponsiveness, skipped/placeholder
  frames, reload duration, and repeated-cycle memory/resource growth.
- [ ] Build Runtime.Core, stable Runtime.Rendering, both backend modules, all
  integrations, Bootstrap, Editor, Server, VRClient, UnitTests, and the solution.
- [ ] Update architecture docs to describe final DLL ownership, stable contracts,
  reload limits, and developer workflow.
- [ ] Update VS Code tasks/launch profiles and contributor docs.
- [ ] Run `Tools/Generate-Dependencies.ps1` after package/native ownership moves,
  then review `docs/DEPENDENCIES.md` and licenses.
- [ ] Regenerate MCP docs if reload tools are added.
- [ ] Record final hardware/software validation and known unsupported cases in a
  durable testing or progress note.

Acceptance criteria:

- [ ] Shader and backend C# iteration works without restarting the editor for the
  documented OpenGL and Vulkan desktop workflows.
- [ ] The final assembly graph matches the Runtime Modularization plan.
- [ ] All unsupported reload cases fail visibly and preserve the editor process
  where technically safe.
- [ ] No new compiler warnings, validation errors, forbidden dependencies,
  unload leaks, hot-path allocations, or silent fallbacks remain.

## Required Validation Scenarios

| Axis | Required cases |
|---|---|
| Backend | OpenGL 4.6, Vulkan dynamic rendering, retained Vulkan legacy mode while supported |
| Reload level | Shader/asset, .NET delta, same-DLL renderer recreation, new backend DLL generation, rollback |
| Shader | Top-level source, transitive include, broken compile, interface change, generated variant, compute, optional mesh/task |
| Window | Main editor window, resize, minimize/restore, multiple windows, detached ImGui platform window |
| Pipeline | Default scene, Unit Testing World, forward, deferred, shadow, post-process, UI, capture |
| Work scheduling | Async compile, upload, Vulkan pipeline compile, command-chain workers, stale completion |
| Failure | Build, dependency, ABI, initialization, first frame, GPU timeout, unload leak, rollback |
| XR | No XR, inactive XR configured, active OpenVR/OpenXR safety rejection, supported session restart when implemented |
| Longevity | 1, 10, and 100 reload cycles; rapid edits; alternating success/failure |

## Global Acceptance Criteria

This TODO is complete only when all of the following are true:

- `XREngine.Runtime.Rendering.OpenGL.dll` and
  `XREngine.Runtime.Rendering.Vulkan.dll` are separate leaf backend assemblies.
- `XREngine.Runtime.Rendering.dll` does not reference or directly construct
  either concrete backend.
- shader includes and in-place editor source edits reliably hot reload on both
  backends with last-good failure behavior.
- the editor can rebuild, shadow-copy, load, activate, tear down, unload, and
  replace each backend without restarting the process;
- old collectible backend contexts are proven unreachable after teardown;
- renderer replacement preserves logical editor/scene state and explicitly
  resets only documented GPU state;
- failed builds/candidates do not disturb the active renderer, and failures
  after teardown use a validated last-good rollback;
- OpenXR/OpenVR and non-unloadable native limitations are explicit and safe;
- repeated reload has bounded managed, native, GPU, cache, thread, and disk use;
- the workflow has actionable editor UI and structured diagnostics; and
- canonical builds, targeted tests, live rendering validation, dependency docs,
  and architecture docs are complete.

## Recommended Implementation Order

1. Complete HR0 and HR1 immediately; shader dependency hot reload is useful
   before assembly modularization finishes.
2. Land the Phase 4 stable rendering capability boundary and HR2 together.
3. Extract and statically validate OpenGL and Vulkan modules through HR3/HR4.
4. Prove the loader with a fake backend in HR5 before connecting GPU teardown.
5. Generalize renderer replacement and prove same-assembly recreation in HR7.
6. Add backend build/reload orchestration and state preservation through HR6/HR8.
7. Add .NET Hot Reload as an optimization, not as the lifecycle foundation.
8. Harden multi-window/XR/external integrations, failure injection, longevity,
   performance, and closeout last.

The backend DLL split is not optional cleanup: it is the prerequisite that makes
the broad no-editor-restart promise technically enforceable.

# Physics debug visualization performance TODO

Status: production implementation is complete. PhysX and Jolt publish packed, triple-buffered
frames once per fixed step; worlds retain geometrically sized GPU batches and reuse one upload
across views. Deterministic tests, explicit benchmark harnesses, live-matrix presets, and a
RenderDoc capture helper are authored but have not been run, per the July 23 request to defer
all validation and benchmarks.

## Objective

Render backend-generated physics diagnostics at approximately the same cost as an equivalent point/line/triangle batch submitted through the normal instanced debug-shape renderer.

This work covers PhysX first, then adapts Jolt to the same engine-owned contract. It does not change simulation behavior or silently reduce the requested visualization detail.

## Pre-implementation behavior and bottlenecks

- With visualization disabled, `PhysxScene.DebugRender` now exits before touching the native
  render buffer. When visualization is enabled, it still walks the native PhysX render buffer
  every time the viewport command executes.
- Every point and line crosses `IRuntimePhysicsServices` separately.
- Every PhysX triangle is expanded into three independent line submissions.
- Those calls enter the general-purpose debug-shape queues and `ConcurrentBag` storage intended for unrelated, individually authored shapes.
- The normal debug-shape swap currently creates worker tasks and waits for them before upload.
- `VPRC_RenderDebugPhysics` runs per rendered view, so multiple editor or XR views can repeat native-buffer traversal and queueing for the same simulation result.
- `PhysxScene` already contains direct-memory point, line, and triangle writers, including packed-color variants, but no renderer owns or consumes those writers.
- `InstancedDebugVisualizer` already supports persistent point, line, and triangle buffers plus direct-memory population. The missing work is lifecycle and pipeline integration, not a new primitive renderer.
- Solid triangles in the general debug renderer are not a substitute for lit world geometry.
  `AppendLateDebugOverlay` targets the post-process output after scene depth is gone and
  explicitly disables depth testing/writes, while its fragment shader is intentionally unlit.
  A per-shape `depthTested` flag cannot override that pass-level contract.

The normal debug-shape baseline also has primitive-specific debt. In particular,
`Engine.Rendering.Debug.RenderSphere` allocates a `Vector3[400]` and expands one solid sphere
into 722 queued triangles per invocation. Physics fixture spheres deliberately remain small
cached meshes until sphere/capsule debug topology becomes retained or instanced.

## Measured baseline and immediate result

Same Vulkan editor view, VSync off, camera at `(0,20,45)` looking at `(0,2.5,12)`:

| Metric | Ordinary lit box fixtures | Intermediate wire-batched box fixtures |
| --- | ---: | ---: |
| Whole-frame median | 19.48 ms | 11.82 ms |
| Whole-frame p90 | 22.48 ms | 13.98 ms |
| Whole-frame p95 | 23.30 ms | 14.81 ms |
| Reported achieved rate | 40.10 Hz | 86.42 Hz |
| Desktop render CPU | 10.79 ms | 5.82 ms |
| Vulkan command-buffer record | 8.55 ms | 6.26 ms |

The disabled native physics-debug pass measured approximately `0.008 ms`. The wide-view
regression came from ordinary fixture meshes entering both deferred and motion-vector passes,
not disabled backend debug extraction.

The measured batch was an intermediate wireframe mitigation. The current playground instead
uses one retained `LitBoxBatchComponent` for every box fixture. It streams transformed
vertices into one opaque deferred draw with vertex color, flat normals, scene-depth writes,
and normal lighting. Compound spheres and the capsule use cached lit meshes. Repeat the same
capture for this final lit configuration before treating the table as the current performance
baseline.

## Immediate mitigations completed

- [x] Skip native PhysX render-buffer traversal when visualization is disabled.
- [x] Submit native PhysX points as point primitives rather than tessellated spheres.
- [x] Gate per-joint gizmos with the Physics Testing World's `RenderPhysicsDebug` setting.
- [x] Move box-heavy playground visuals from one lit mesh per fixture to one retained,
  vertex-colored opaque deferred box batch. Keep scene depth and lighting intact without
  restoring per-fixture commands.
- [x] Replace the remaining compound-sphere and capsule debug-overlay visuals with cached lit
  meshes, including a reusable true solid capsule topology.
- [x] Persist retained box-batch registrations through cooked Play snapshots and rebuild
  runtime mesh resources against the restored scene-node identities.
- [x] Honor explicit profiler-frame-logging settings so a disabled profiler cannot distort
  the rendering baseline.
- [x] Rate-limit enabled FPS-drop diagnostics to one worst-thread report per second.

## Correctness prerequisites completed

- [x] The Physics Testing world no longer enables every PhysX visualization flag unconditionally.
- [x] Explicit `RenderPhysicsDebug: false` settings now disable all visualization flags in both editor and runtime bootstrap paths.
- [x] Native rigid-body actor links are marked runtime-only and excluded from YAML and MemoryPack persistence.
- [x] Cooked snapshot serialization proactively routes known reflection-owned engine object models away from unsupported MemoryPack probes.
- [x] Unsupported MemoryPack runtime types are negatively cached after their first failed probe.
- [x] Physics authoring value types have explicit MemoryPack contracts where supported, while nested geometry structs use field-preserving reflection encoding.
- [x] Active-PhysX snapshot regression coverage verifies native actor graphs do not enter Play-mode snapshots.
- [x] Scene-snapshot coverage verifies the retained lit fixture batch survives the clean
  Edit-to-Play clone with all entries resolved to the restored hierarchy.

## Target architecture

### 1. Define one backend-neutral physics debug frame

- [x] Add an engine-owned `PhysicsDebugFrame` contract containing read-only point, line, and triangle batches.
- [x] Store packed colors and tightly packed GPU-ready vertex data; do not allocate a managed object per primitive.
- [x] Include a monotonically increasing simulation/debug generation so render views can reuse the same published frame.
- [x] Define ownership explicitly: the physics backend writes the build frame, publishes it after simulation, and cannot mutate it until all render consumers have moved to a newer generation.
- [x] Use double or triple buffering so physics production and rendering do not contend on a single buffer.
- [x] Represent empty and disabled frames without allocations.

### 2. Connect the existing PhysX bulk path

- [x] Move PhysX render-buffer extraction out of `DebugRender` and publish it once after the physics step.
- [x] Replace the orphaned raw-pointer bulk delegates with `PhysxDebugFrameAdapter`, which writes the same compressed layouts directly into the owned typed frame.
- [x] Preserve PhysX triangles as triangles. Expand them to wireframe only when a specific visualization mode requires wireframe output.
- [x] Replace per-primitive `IRuntimePhysicsServices.Render*` calls with one batch publication per primitive kind.
- [x] Keep PhysX native render-buffer pointers inside the documented `PxRenderBuffer` lifetime; copy before the next simulation step invalidates them.
- [x] Record source primitive counts, copied bytes, copy time, and dropped/capped primitives in allocation-free telemetry.

### 3. Give the renderer persistent batch ownership

- [x] Make a world-owned physics debug visualizer reuse `InstancedDebugVisualizer` or extract its reusable buffer-management core.
- [x] Allocate point, line, and triangle capacity geometrically and retain it across frames.
- [x] Add shrink hysteresis or an explicit trim operation; do not resize buffers because one frame has fewer primitives.
- [x] Prefer persistent mapped buffers or the backend's lowest-overhead streaming path.
- [x] Upload only the published counts and dirty ranges.
- [x] Keep the steady-state path free of LINQ, boxing, captured closures, string construction, and managed collection growth.
- [x] Make allocation or upload failures explicit in diagnostics. Do not silently switch to a slower CPU fallback.

### 4. Collect once and render every view

- [x] Remove backend extraction from `VPRC_RenderDebugPhysics`.
- [x] Have the command draw the latest published generation without repopulating it.
- [x] Instrument one mono view, two editor viewports, and stereo XR to prove they share one extraction/upload generation; execution is deferred.
- [x] Keep view-dependent transforms, depth state, and overlay behavior in the render command rather than duplicating world geometry.
- [x] Keep physics in its world-owned shared debug-batch renderer to avoid the individual-shape queues.
- [x] Split depth-tested world diagnostics from on-top overlays at the render-pass level.
  The world-debug pass must bind a target with the active scene depth before post-processing;
  the late overlay may continue using no depth.
- [x] Cap the steady-state physics visualization to at most one point, one line, and one triangle draw per world/view.

### 5. Remove task and queue overhead from backend batches

- [x] Bypass the ad-hoc `ConcurrentBag`/queue path for published physics batches.
- [x] Do not use `Task.Run`, `Parallel.Invoke`, or `Task.WaitAll` for physics debug population.
- [x] Keep the existing individual-shape API for gameplay and editor diagnostics; backend batches must not make that API more expensive.
- [x] Adopt compact shape-instance staging and allocation-free sequential population for the normal debug path where it removes duplicate topology work.

### 6. Make the normal primitive baseline allocation-free

- [x] Replace per-frame sphere, capsule, cone, cylinder, circle, and quad tessellation with
  cached unit topology or compact analytic/instance records.
- [x] Remove the `Vector3[400]` sphere allocation and the 722 per-call solid-sphere triangle
  insertions from the steady-state path.
- [x] Carry transform, dimensions, color, solid/wire mode, and depth layer as instance data
  instead of baking world-space vertices on the CPU.
- [x] Provide separate depth-tested world-debug and on-top overlay batches so solid
  diagnostics do not obscure the scene merely to remain batched.
- [x] Instrument the one-render-command-per-`DebugDrawComponent`
  callback overhead and replace per-callback tessellation with one compact instance
  submission. The run needed to decide whether a retained registry is material is deferred.
- [ ] Re-run the physics-path comparison against this improved baseline; do not claim parity
  by comparing against an avoidably allocating reference path.

### 7. Add conservative visibility and load control

- [x] Feed a conservative union of active camera frusta into the PhysX visualization cull box when supported.
- [x] Do not perform an interface call or CPU frustum test per primitive.
- [x] Use the backend cull box plus one frame-level bounded chunk; no per-primitive fallback culling is used.
- [x] Add configurable point, line, triangle, and byte budgets with visible overflow counters.
- [x] Preserve deterministic prefix/order behavior when a diagnostic budget is reached.
- [x] Never drop detail without surfacing the cap and the number of omitted primitives.

### 8. Adapt Jolt to the same contract

- [x] Change `JoltEngineDebugRenderer` to build packed batches instead of invoking shape rendering per primitive.
- [x] Publish Jolt frames through the same ownership, generation, telemetry, and render-command path as PhysX.
- [x] Keep backend-specific conversion code behind a narrow adapter; buffer growth and drawing belong to the shared renderer.
- [x] Preserve line, contact, constraint, and body-color conversion semantics; execution-based visual verification is deferred.

## Validation and benchmarks

### Deterministic tests

Coverage below is authored but deliberately not executed yet.

- [x] Verify exact point, line, and triangle counts and representative vertex/color values from a synthetic native buffer.
- [x] Verify a triangle remains one triangle batch entry rather than becoming three queued lines.
- [x] Verify disabled visualization publishes an empty frame and performs no extraction or upload.
- [x] Verify multiple render views consume one generation without additional extraction or upload.
- [x] Verify buffer growth retains old allocations until replacement is safe and does not resize on normal count variation.
- [x] Verify frame publication cannot expose partially written data.
- [x] Verify backend shutdown, world replacement, and renderer disposal release every retained native/GPU resource.
- [x] Add equivalent PhysX and Jolt contract tests.

### Allocation and throughput benchmarks

- [ ] Repeat the original wide-view benchmark with the current lit, depth-writing box batch
  and record whole-frame, render-CPU, command-recording, draw-count, and allocation deltas.
- [x] Add an explicit benchmark harness that feeds identical primitive mixes to:
  - a preallocated packed normal-debug baseline;
  - the new physics debug frame path.
  The removed per-primitive physics path remains available through pre-change source history
  rather than being retained in production code.
- [x] Cover at least 10K, 100K, and 1M total primitives with line-heavy and triangle-heavy mixes.
- [x] Instrument physics extraction, conversion, synchronization, upload, render submission, and GPU debug-pass time separately.
- [x] Configure warm steady-state capture tooling that excludes initialization and deliberate capacity growth.
- [x] Record managed allocations, native temporary bytes, GPU upload bytes, draw calls, and batch generation/view counts.
- [x] Add an OpenGL/Vulkan and desktop/stereo matrix runner; execution remains deferred.

### Live Physics Testing world matrix

- [ ] Shapes-only visualization.
- [ ] Contacts and normals enabled.
- [ ] Joint frames and limits enabled.
- [ ] Simulation meshes and collision edges enabled.
- [ ] All visualization flags enabled as a deliberate stress case.
- [x] Visualization disabled, confirming effectively zero native debug extraction/render
  cost (`VPRC_RenderDebugPhysics` approximately `0.008 ms` in the measured run).
- [x] Re-enter Play mode repeatedly and confirm no serialization exception storm or native
  access violation (four live Edit -> Play -> Edit cycles).
- [x] Confirm the lit fixture batch, including the floor, remains visible after the clean
  Play snapshot restore and locomotion-pawn possession.

## Acceptance gates

- [ ] With an identical primitive mix and count, steady-state physics debug extraction plus upload has median CPU cost no greater than `1.10x` the normal instanced debug batch and p95 no greater than `1.20x`.
- [ ] The physics visualization path allocates `0 B` of managed memory per steady-state frame after warmup.
- [ ] Physics extraction and upload happen at most once per published simulation generation, independent of render-view count.
- [ ] No per-primitive interface/delegate dispatch, `ConcurrentBag` insertion, `Task.Run`, or blocking task wait remains in the PhysX batch path.
- [ ] At most three physics debug draw calls are issued per world/view: points, lines, and triangles.
- [ ] No triangle-to-line expansion occurs unless explicitly requested by the selected visualization mode.
- [ ] OpenGL and Vulkan images preserve the current colors, depth behavior, and overlay semantics.
- [ ] Depth-tested world diagnostics pass ordinary front/behind occlusion checks against
  opaque scene meshes; on-top diagnostics remain explicitly opt-in.
- [ ] No scene fixture representation is routed through the late unlit overlay merely to
  obtain batching.
- [ ] Retained diagnostic and fixture batches preserve their logical entries across scene
  serialization, Play snapshot restoration, and world replacement while rebuilding only
  runtime-owned CPU/GPU resources.
- [ ] Diagnostic caps and failures are visible through logs/profiler telemetry and never silently hide requested output.

## Recommended execution order

1. Add benchmark/telemetry counters around the current path to establish the baseline.
2. Define and test `PhysicsDebugFrame` ownership and generation semantics.
3. Connect PhysX packed bulk writers and publish once per simulation step.
4. Attach persistent renderer buffers and make every view consume the same generation.
5. Remove the legacy per-primitive PhysX path after visual and count parity passes.
6. Add culling/budgets, then validate high-count and multi-view cases.
7. Port Jolt to the shared contract.
8. Capture representative OpenGL and Vulkan frames with RenderDoc after the CPU path is fixed, checking draw count, uploaded resources, target contents, and GPU timing.

## Useful implementation touchpoints

- `XREngine.Runtime.Core/Scene/Physics/Physx/PhysxScene.cs`
- `XREngine.Runtime.Core/Scene/Physics/Jolt/JoltScene.cs`
- `XRENGINE/Scene/Physics/Physx/InstancedDebugVisualizer.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Debug.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugPhysics.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugShapes.cs`
- `XREngine.Runtime.Rendering/Rendering/XRWorldInstance.Runtime.cs`
- `XREngine.Runtime.Core/Scene/Physics/RuntimePhysicsServices.cs`

## RenderDoc checkpoint

`rdc doctor` was attempted during the diagnosis, but `rdc` is not currently installed or
available on `PATH`. The installed
`C:\Program Files\RenderDoc\renderdoccmd.exe` is available as the capture fallback. A capture
was not required to identify this regression because source inspection established the
late-overlay target/depth state and rebuilt Vulkan screenshots confirmed the corrected
deferred output. Use the fallback for the final draw-count/resource inspection and keep all
captures and exports under the active `Build/_AgentValidation/<run>/renderdoc/` root.

## Deferred execution commands

Do not run these as part of the implementation-only change. They are the prepared follow-up:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~PhysicsDebug"
pwsh .\Tools\Benchmarks\Measure-PhysicsDebugFrames.ps1 -Configuration Release
pwsh .\Tools\Benchmarks\Measure-PhysicsDebugVisualizationMatrix.ps1 -Configuration Release
pwsh .\Tools\RenderDoc\Capture-PhysicsDebugVisualization.ps1 -Configuration Release -Preset All
```

`XRE_PHYSICS_DEBUG_PRESET` is a unit-testing-world-only launch override with `Disabled`,
`Shapes`, `Contacts`, `Joints`, `Simulation`, and `All` values. The matrix and RenderDoc
helpers set and clear it automatically.

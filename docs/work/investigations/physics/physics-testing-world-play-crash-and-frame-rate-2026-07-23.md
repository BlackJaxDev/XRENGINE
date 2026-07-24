# Physics Testing World play crash and frame rate

Status: implementation and automated/live validation complete on 2026-07-23;
the later lighting/depth regression is also fixed, awaiting final user confirmation.

## Problem

In the Physics Testing World:

- framing all physics fixtures caused a substantial frame-rate drop;
- entering Play mode could terminate the process with `0xC0000005` in
  `PxRigidDynamic_wakeUp_mut`; and
- earlier Play transitions raised repeated first-chance
  `MemoryPackSerializationException` failures.

Follow-up Play testing exposed additional correctness regressions:

- the first performance mitigation made the playground boxes wireframe;
- dynamic fixtures fell through the floor;
- Play retained the flying inspection camera instead of possessing a locomotion pawn;
- below-plane resets moved objects only incrementally instead of restoring the pose captured
  at Play start; and
- the locomotion controller could initially stand on the floor, then lose support and fall
  through after several seconds.

The solid-debug-shape revision then exposed a rendering regression: physics fixtures were
unlit and ignored ordinary scene occlusion even when their material requested depth testing.
After the lit batch replaced that overlay, entering Play made the scene almost entirely black:
only the independently serialized sphere and capsule meshes remained visible.

## Root causes

### Native Play-mode crash

The failing transition had two native-lifecycle hazards.

First, physics scene initialization, Play entry, and destruction were allowed to run on the
update thread while queued actor mutations ran on the fixed physics thread. A managed
`IsReleased` check cannot protect a native call when another thread may free the actor between
the check and the call.

Second, the fixed-thread trace identified the immediate `wakeUp` fault:

1. Play snapshot restore constructed replacement rigid-body components and their native actors.
2. Those actors were still detached because the replacement `PxScene` was not attached yet.
3. `ResetToInitialPose` restored body state and queued `WakeUp`.
4. The physics queue executed that request before Play entry attached the actor to the scene.
5. PhysX dereferenced invalid scene state inside `PxRigidDynamic::wakeUp`.

`PxRigidDynamic::wakeUp` and `putToSleep` are not valid operations for a detached actor. The
actor wrapper was alive, so checking only `IsReleased` did not cover this state.

### MemoryPack exceptions

Play snapshots were probing MemoryPack for reflection-owned engine types that do not have a
valid generated MemoryPack contract. The last live failure was the nested
`DirectionalLightComponent.CascadeShadowBiasOverride` value. Repeated speculative probes
created the debugger exception storm even when a later reflection fallback could serialize
the value.

### Wide-view frame rate

The measured all-fixtures slowdown was not the disabled PhysX visualization path:

- `RenderPhysicsDebug`, transform-point debug, and transform-line debug were disabled.
- `VPRC_RenderDebugPhysics` measured approximately `0.008 ms`.
- `PhysicsBallCount` was zero.

The remaining view-dependent work was 30 ordinary `ModelComponent` mesh commands. Most were
simple box fixtures, but each entered the deferred mesh pass and was replayed in the
motion-vector pass. In the same run, Vulkan command-buffer recording was approximately
`8.55 ms`.

Two additional multipliers were found:

- joint components emitted roughly 10,920 line submissions per frame when their individual
  gizmos remained enabled; and
- explicit `EnableProfilerLogging: false` was ignored, allowing synchronous FPS-drop
  diagnostics to grow to roughly 40 MB and amplify stalls.

### Unlit fixtures and incorrect depth

The solid fixture mitigation reused the general debug-triangle renderer. That renderer is an
unlit visualization path, and `DefaultRenderPipeline.AppendLateDebugOverlay` draws it after
post-processing into `PostProcessOutputFBO` with depth testing and depth writes disabled.
That target does not carry the scene depth needed to occlude world geometry. A
`depthTested: true` shape flag therefore could not repair the pass-level attachment and state:
the geometry was still unlit and effectively on top.

The capsule also used a debug wire lattice rather than a real solid capsule mesh, and the
compound body's two spheres remained in the same unlit overlay path.

### Play snapshot dropped the lit box batch

Edit-to-Play intentionally serializes and restores a clean scene copy. The first
`LitBoxBatchComponent` revision kept its fixture registrations only in a private runtime list.
Cooked snapshot serialization therefore restored a constructor-created batch with zero box
entries and no mesh. Because the floor, walls, ramps, platforms, and ordinary box fixtures
all belonged to that one batch, they disappeared together. The sphere and capsule visuals
were ordinary serialized `ModelComponent` meshes, explaining why they were the only visible
objects.

### Play-world collision and locomotion

The dynamic fixtures inherited a managed `MaxContactImpulse` default of zero. PhysX treats
that as a zero contact-impulse ceiling, so a correctly overlapping contact could not actually
hold the body above the floor.

The locomotion path had four separate problems:

1. `PxControllerFilters` enabled `Prefilter` and `Postfilter` and supplied managed callback
   vtables even though the intended stable default was native static/dynamic filtering with
   null callbacks. Every controller sweep crossed an unnecessary managed/native callback
   boundary.
2. The controller sweep canceled downward displacement at the floor, but the gameplay
   `Velocity` retained and accumulated gravity. Grounded frames could therefore submit an
   increasingly large downward sweep until contact transitions became unstable.
3. The authored 25-degree and 55-degree ramp boxes penetrated the floor by approximately
   `0.25 m` and `0.77 m`, respectively, creating overlapping static support surfaces at the
   controller course.
4. The min-Y reset was implemented only for `IAbstractDynamicRigidBody`. PhysX and Jolt
   character controllers are not dynamic rigid bodies, had no absolute teleport-and-clear
   contract, and retained queued fall/support motion after an ordinary position assignment.

The Physics Testing World also used the generic preplaced-pawn game mode, so Play had no
reason to spawn or possess a collision-aware `CharacterPawnComponent`. When the locomotion
pawn was first introduced, its controller could activate before the later spawn transform was
applied, causing its reset pose to be captured at the wrong location.

## Implemented changes

### Physics lifetime safety

- Added a synchronous FIFO physics-thread barrier.
- Moved physics scene initialization, Play entry, and destruction behind the barrier.
- Made wake, sleep, and kinematic-target mutations execute immediately when already on the
  physics thread.
- Made wake and sleep safe no-ops while a dynamic actor is detached from a scene. Scene entry
  performs the required wake after attachment.

The lifecycle ordering invariant is now:

`previous queued actor work -> scene lifecycle operation -> subsequent actor work`

### Snapshot serialization

- Proactively route known reflection-owned scene/component types away from unsupported
  MemoryPack probes.
- Negatively cache unsupported MemoryPack runtime types.
- Preserve public fields for reflection-encoded value types.
- Mark native actor links runtime-only.
- Give supported physics authoring values explicit contracts.
- Route `CascadeShadowBiasOverride` through cooked-binary reflection.

### Rendering and diagnostics

- Return from `PhysxScene.DebugRender` before native render-buffer traversal when
  visualization is disabled.
- Preserve native PhysX points as point primitives instead of tessellated spheres.
- Tie all joint-component gizmos to the Physics Testing World's `RenderPhysicsDebug` setting.
- Render all box-heavy test fixtures through one retained `LitBoxBatchComponent`. It streams
  their world-space positions and flat normals into one opaque deferred mesh, writes the
  G-buffer and scene depth, receives lighting, and retains the low command/draw count of the
  performance mitigation.
- Persist each batch entry as a scene-node ID, half extent, and color. After cooked scene
  deserialization, resolve those IDs against the restored hierarchy and rebuild the
  constructor-owned mesh/material resources before rendering.
- Render compound spheres and the dynamic capsule as cached lit opaque meshes instead of
  late-overlay debug primitives.
- Add a reusable solid capsule generator and correct `XRMesh.Shapes.FromVolume(Capsule)`,
  which previously returned a cone for solid capsule requests.
- Honor the launch setting for profiler frame logging, with the Physics Testing World default
  disabled.
- When FPS-drop logging is enabled, emit only the worst thread drop per snapshot and apply a
  one-second global cooldown while reporting the number of suppressed drops.

### Play-world physics behavior

- Default dynamic rigid bodies to an unlimited `MaxContactImpulse`, matching the native PhysX
  default.
- Use PhysX native static/dynamic CCT filtering without managed pre/post-filter callbacks.
- Reconcile gameplay velocity whenever the controller is grounded: remove stale velocity into
  the support surface and submit only a bounded one-step gravity probe to retain contact.
- Author ramp centers from their rotated vertical extents so their collider bottoms meet,
  rather than penetrate, the floor.
- Add a backend-neutral character-controller `Teleport` operation. Both PhysX and Jolt clear
  pending motion, requested/effective velocity, support/platform inheritance, collision flags,
  and backend velocity before applying the new absolute position.
- Seed the locomotion controller's spawn position at activation, then recapture the exact pose
  in `OnBeginPlay` so components that were already active in Edit mode still use the real
  Play-start boundary. Apply the same gravity-aligned min-Y policy as dynamic bodies; reset
  clears managed velocity, acceleration, and jump/coyote state.
- Give the Physics Testing World a `LocomotionGameMode`, spawn its character at
  `(0, 2, 1.5)` before component activation, and possess it on Play. The flying pawn remains
  available only as the explicit no-clip mode.

## Validation

Same editor camera for both measurements:

`position=(0,20,45), lookAt=(0,2.5,12), Vulkan, VSync off`

| Metric | Before fixture batching | Intermediate wire-batched fixtures |
| --- | ---: | ---: |
| Whole-frame median | 19.48 ms | 11.82 ms |
| Whole-frame p90 | 22.48 ms | 13.98 ms |
| Whole-frame p95 | 23.30 ms | 14.81 ms |
| Reported achieved rate | 40.10 Hz | 86.42 Hz |
| Desktop render CPU | 10.79 ms | 5.82 ms |
| Vulkan command-buffer record | 8.55 ms | 6.26 ms |

The measured intermediate p95 is below the 16.67 ms desktop 60 Hz budget. The wireframe
revision was subsequently rejected. The current implementation combines every box fixture
into one retained, lit, depth-writing deferred draw; only the capsule and sphere topology use
ordinary cached lit meshes. This preserves the avoided per-fixture render commands while
restoring normal scene rendering. It still needs an identical final performance capture.

Additional validation:

- Four complete Edit -> Play -> Edit cycles succeeded; the process remained alive and the
  final engine state was `Edit`.
- The latest run contained no `Fatal error`, `0xC0000005`,
  `PxRigidDynamic_wakeUp`, `AccessViolationException`,
  `MemoryPackSerializationException`, or `[MEMORYPACK SERIALIZE FAIL]` signature.
- The active-PhysX snapshot regression asserts zero first-chance
  `MemoryPackSerializationException` events.
- No `profiler-fps-drops.log` was created with the explicit setting disabled.
- A clean locomotion pawn settled at `(0, 0.8074, 1.5)` and remained supported without drift
  for more than ten seconds.
- Forcing that pawn below the min-Y plane returned it to the authored
  `(0, 2, 1.5)` Play-start position, after which it settled normally.
- Native CCT traversal remained supported for 600 moving fixed steps.
- The full locomotion component remained supported for 600 fixed steps with reset disabled;
  its grounded downward velocity stayed bounded instead of accumulating.
- Focused physics/serialization/world-builder tests: 15 passed, 0 failed.
- Jolt controller parity and teleport-state tests: 19 passed, 0 failed.
- Editor build: succeeded with 0 warnings and 0 errors.
- Rebuilt isolated Vulkan editor validation confirms:
  - box faces receive directional lighting and occlude one another and the floor correctly;
  - the dynamic capsule is a closed, smooth, lit solid; and
  - the compound body's spheres are lit and are correctly occluded by its center box.
- Focused Physics Testing World builder tests after the rendering correction: 6 passed,
  0 failed.
- Solid-capsule topology and `FromVolume` regression tests: 2 passed, 0 failed.
- The snapshot regression serializes and restores the complete Physics Testing scene, then
  requires the restored batch to retain every entry, resolve only restored node IDs, and
  report itself built.
- A rebuilt isolated Vulkan Edit -> Play -> Edit pass shows the floor and batched box
  fixtures in the possessed locomotion camera. Its logs contain no fatal, access-violation,
  MemoryPack, shader, or Vulkan validation signature.

Visual evidence:

`Build/_AgentValidation/mcp-sessions/physics-play-perf-20260723/mcp-captures/Screenshot_20260723_122229_393_b3595593fad54657a802c5e93eb82e01.png`

`Build/_AgentValidation/20260723-physics-lit-depth/mcp-captures/Screenshot_20260723_151525_838_dcf12b4460fc429fb9ba92ce0c293d9f.png`

`Build/_AgentValidation/20260723-physics-lit-depth/mcp-captures/Screenshot_20260723_151554_856_882cbf3c2add4a19816a32001c97eaca.png`

`Build/_AgentValidation/20260723-physics-lit-depth/mcp-captures/Screenshot_20260723_154134_255_a79a0322d46f49c2bda5ddcc4eb85899.png`

## Follow-up: Edit-mode visualization and Vulkan allocation failure

The July 23 follow-up reproduced `VulkanOutOfMemoryException` while changing nested physics
visualization settings. `PhysicsVisualizeSettings` changes were incorrectly forwarded through
the global rendering-settings event. Both default render pipelines treated that event as a
render-architecture change, invalidated all 123 physical resource specifications, and raced
multiple swapchain/HDR generations during a resize. The eventual depth-image allocation
failed while recreating the 2560x1494 swapchain.

Physics visualization settings now notify their subscribed physics scenes directly without
requesting a global render-pipeline rebuild. This keeps diagnostic toggles out of render-target
and swapchain lifetime management.

Edit mode previously had no native PhysX frame to publish because physics simulation is
disabled there. PhysX now publishes a non-simulating authoring preview by traversing actor
shapes and emitting their current sphere, capsule, box, bounds, velocity, and axis geometry.
Jolt republishes its current body geometry on the same path. Collection is throttled to 30 Hz
per world and continues to use the retained debug-geometry renderer.

The play snapshot also exposed `LitBoxBatchEntry` as an unsupported MemoryPack runtime type.
It is now explicitly routed through cooked-binary reflection serialization.

Validation used a rebuilt, isolated Vulkan editor session because `rdc`/RenderDoc CLI was not
installed:

- Edit mode displayed physics geometry before any simulation step.
- Play mode entered and remained active with the locomotion pawn and native PhysX debug lines.
- A second rebuilt run contained no Vulkan allocation, MemoryPack, fatal-error, or access
  violation signature.
- Core and engine builds completed with 0 warnings and 0 errors.

Evidence:

`Build/_AgentValidation/20260723-physics-lit-depth/mcp-captures/Screenshot_20260723_170333_775_14d645cba32f4fdd99c967dfc0991292.png`

`Build/_AgentValidation/20260723-physics-lit-depth/mcp-captures/Screenshot_20260723_170424_231_b7d10ff8ffc34fefa760cf46a91de4af.png`

## Follow-up: Game-mode UI ownership

The Physics Testing World switched possession from the editor flying pawn to the
`LocomotionGameMode` character, but UI ownership did not switch with it. The play-mode UI
notification had no subscribers, ImGui deliberately continued rendering during Play, and a
camera without an explicit UI could fall back to the first active screen-space canvas in the
world's combined root list. Because that list includes hidden editor roots, the gameplay
camera could inherit the editor canvas.

The game-mode contract now includes an optional runtime player UI component type.
`LocomotionGameMode` declares an empty screen-space `UICanvasComponent`; the runtime host
creates and binds it before possession and destroys it at end play. Viewport fallback is
limited to canvases in the same editor/gameplay scene scope as the active camera. Editor
ImGui and its docked scene-panel presentation render only in Edit mode.

The first live exit-play check also showed that hidden editor roots were deactivated during
world teardown and never reactivated. Hidden editor roots now remain outside gameplay
begin/end callbacks and receive a balanced deactivate/reactivate around visual-scene
destruction, restoring the editor UI and input registrations on exit.

Validation:

- Editor and isolated-session builds completed with 0 warnings and 0 errors.
- Focused game-mode UI, editor-scene lifecycle, and Physics Testing serialization tests:
  4 passed, 0 failed.
- In Play, MCP reported `PlayerCamera` at
  `Player1_CharacterPawn/CameraOffset/Camera` with an active screen-space
  `UICanvasComponent`.
- Full-window captures at two different gameplay-camera transforms showed the Physics
  Testing scene with no ImGui editor chrome.
- After exit, MCP reported `Edit`, `Editor View`, and its active screen-space canvas; the
  full-window capture showed the restored ImGui editor shell.
- The final Vulkan session logs contained no error, exception, fatal, VUID, or validation
  error signature.

Evidence:

`Build/_AgentValidation/mcp-sessions/locomotion-ui-switch-final-20260723/mcp-captures/edit-before-play.png`

`Build/_AgentValidation/mcp-sessions/locomotion-ui-switch-final-20260723/mcp-captures/play-empty-ui.png`

`Build/_AgentValidation/mcp-sessions/locomotion-ui-switch-final-20260723/mcp-captures/play-empty-ui-alt-camera.png`

`Build/_AgentValidation/mcp-sessions/locomotion-ui-switch-final-20260723/mcp-captures/edit-after-play-restored.png`

## Remaining work

The immediate Physics Testing World behavior is implemented and validated, pending user
confirmation in the normal editor session. Backend-generated PhysX and Jolt visualization
still uses per-primitive submission when explicitly enabled. The retained, publish-once bulk
path, final lit-box benchmark, and acceptance gates are tracked in
`docs/work/todo/rendering/physics-debug-visualization-performance-todo.md`.

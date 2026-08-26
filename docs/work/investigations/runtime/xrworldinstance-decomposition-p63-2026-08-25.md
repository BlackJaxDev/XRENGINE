# XRWorldInstance Decomposition (P6.3)

## Problem

`XRWorldInstance` is a facade-owned aggregate that currently combines Core world
identity and lifecycle, physics ownership, Rendering publication and picking,
Input pawn refresh, Bootstrap game-mode composition, and Editor-only hidden-scene
policy. P6.3 removes that aggregate and establishes ownership-correct focused
runtime objects without changing live world behavior.

## Target Design

- `RuntimeWorld` in Runtime.Core is the canonical identity stored on scene nodes.
- Runtime.Core owns target-world, scene/root membership, play/tick lifecycle, and
  physics state.
- Runtime.Rendering attaches a focused render-world capability to `RuntimeWorld`.
- Runtime.InputIntegration owns controlled-pawn/input refresh behavior.
- Runtime.Bootstrap owns host composition and game-mode ordering.
- Editor owns hidden editor scenes, gizmo/tool roots, and editor-only policy.
- `RuntimeWorldRegistry` replaces the static facade dictionary with explicit,
  resettable lifetime.

## Validation Plan

1. Build the focused runtime projects and editor.
2. Start one named isolated OpenGL editor session, capture and inspect at least
   two distinct camera views, then stop only that session and inspect its logs.
3. Repeat with a distinct named Vulkan session.
4. After the live paths pass, update and run the targeted lifecycle, rendering,
   picking, physics coordination, editor-scene, VR-world, and boundary tests.

## Progress

- 2026-08-25: Completed member/call-site inventory (68 source consumers).
- 2026-08-25: Chose a Core identity plus attached-capability architecture; a
  Bootstrap aggregate implementing all subsystem interfaces is explicitly
  rejected because it would only rename the existing facade.
- 2026-08-25: Implemented `RuntimeWorld`, `RuntimeWorldRenderer`, focused input
  and editor integrations, and Bootstrap host composition with explicit Core,
  render, and editor registries.
- 2026-08-25: Removed `XRWorldInstance.cs`,
  `XRWorldInstance.PhysicsDebug.cs`, and
  `XRWorldInstance.PhysicsRaycastRequest.cs`; migrated all production, sample,
  editor, MCP, and test consumers.
- 2026-08-25: Completed named OpenGL and Vulkan editor runs, inspected multiple
  camera views and logs, and stopped only the owned sessions.

## Results

P6.3 is complete. Runtime.Core owns the canonical non-visual `RuntimeWorld`;
scene nodes never receive a rendering or Bootstrap aggregate. Rendering is an
attached `RuntimeWorldRenderer` capability. InputIntegration owns pawn refresh,
Bootstrap owns world/game-mode/backend composition, and Editor owns the hidden
scene policy.

The host publishes its provisional identity before initial scene activation,
then registers Core and binds settings before loading target scenes. This was
required to preserve initial renderable/light registration and to prevent
activation callbacks from recursively constructing a second host. Retargeting
rekeys Core and Bootstrap registries after target assignment but before new
scene loading. Disposal keeps Rendering alive while Core unloads components,
then detaches rendering capabilities and registries.

Lifecycle parity includes:

- backend initialization before gameplay root activation;
- backend teardown after root deactivation but before persistent editor-root
  reactivation;
- policy-excluded editor roots remaining outside gameplay begin/end callbacks;
- physics-reset snapshots retaining native and scene-transform state;
- deterministic settings subscriptions, light-cache rebuilds, input refresh,
  GPU-picking preference propagation, and queued-pick cancellation; and
- explicit multi-world registry retarget, removal, reset, and disposal.

### Build And Test Evidence

- Runtime.Core, Runtime.Rendering, Bootstrap, Editor, UnitTests, Server, and
  VRClient: Debug build passed with zero warnings and zero errors.
- Focused P6.3 matrix: 51/51 passed. It includes canonical identity, initial
  render composition, registry retarget/reset, disposal, lifecycle, physics,
  editor scenes, render registration, GPU picking, snapshot, and source-contract
  coverage.
- Complete OpenXR timing/pipeline contract fixture: 57/57 passed.
- Phase 6 stateful boundary fixture: 6/6 passed after regenerating the 365-row
  source ownership manifest. The three deleted facade rows are `Migrated` and
  retain concrete destination paths.
- Phase 4/5 dependency and physics-backend boundaries: 35/35 passed. Validation
  exposed and removed a verified-empty legacy OpenGL directory tree; no source
  or generated file was deleted.
- Production/sample C# search: zero `XRWorldInstance` references.

### OpenGL Live Evidence

Session `p63-world-opengl` selected `OpenGLRenderer`. Two inspected captures are:

- `mcp-captures/opengl-final/Screenshot_20260825_215740_912_1731570229d3432f9aa0c234831a21d0.png`
  from `(0, 2, 4)` looking toward `(0, 1, -4)`; and
- `mcp-captures/opengl-final/Screenshot_20260825_215756_355_850f699b46374fe89bb7121162642381.png`
  from `(6, 4, 0)` looking toward `(0, 1, -5)`.

Both paths are relative to
`Build/_AgentValidation/20260825-205443-runtime-modularization-p63/`. The
captures have different hashes and visibly different composition; the editor
UI, skybox, hierarchy, and Mitsuki model were rendered. The final session logs
contain no fatal, exception, unhandled-error, or OpenGL error outside explicit
`glGetError` instrumentation. The named session was stopped through the session
manager.

### Vulkan Live Evidence And External Limitation

Session `p63-world-vulkan` selected `VulkanRenderer`. Camera cuts, render-on-
demand invalidation, and an explicit focus on the `Mitsuki` node all completed.
The runtime reported 57 active commands, 54 opaque deferred meshes, 57 resident
draws/instances/geometries/materials, and 55 CPU-visible draws against the
correct Core world, camera, and pipeline. This proves that P6.3 initial scene
composition and render publication are present on Vulkan.

The inspected final/pipeline captures remained the same red/blue presentation,
and `AlbedoOpacity` was zero. The Vulkan profiler stopped at frame lifecycle
`Failed` before acquire/record/submit. After the owned session was stopped and
logs flushed, the sole runtime error was:

`[Vulkan][PresentNow][RendererPaused] frame=10 stage=PipelineCompilation ... dynamic UI secondary command recording was deferred`

There was no validation VUID or device loss. A warm-cache `-NoBuild` restart of
the same named session reproduced the failure and was also stopped through the
session manager. This limitation belongs to the separately active
[Vulkan PresentNow frame-readiness investigation](../../todo/rendering/vulkan-present-now-frame-readiness-todo.md),
whose live acceptance was already paused. P6.3 does not add a fallback or widen
scope into that renderer rewrite.

The separate, non-gating `VulkanDeferredProbeGiFixesTests` fixture currently
passes 15/36 and fails 21 source-contract expectations for unfinished or moved
Vulkan frame-op, descriptor, and device-fault implementation. P6.3 only updated
the picking-source path within that fixture; focused P6.3 picking coverage is
green. The remaining failures stay owned by the Vulkan investigation.

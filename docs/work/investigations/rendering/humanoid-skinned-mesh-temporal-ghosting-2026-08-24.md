# Humanoid Skinned-Mesh Temporal Ghosting Investigation

Last Updated: 2026-08-24
Status: Humanoid visual gate passed; skinned motion-vector producer isolated for a renderer follow-up

Related animation work:

- `docs/work/todo/avatar/humanoid-body-root-compensation-todo.md`
- `docs/work/investigations/avatar/humanoid-body-root-compensation-2026-08-24.md`

## Problem Statement

Earlier captures appeared to show camera-dependent temporal halos and multiple
residual silhouettes around Mitsuki. The continuation therefore reproduced
free-running `D:\Desktop\misc\Mitsuki.fbx` with
`Assets/Walks/Sexy Walk.anim` in the ImGui Unit Testing World on OpenGL and
inspected final color, anti-aliasing state, temporal logs, and the Velocity
resource independently.

The animation audit and live hierarchy queries show one coherent skeleton, no
duplicated avatar hierarchy, stable fixed-time evaluation, and stable loop/root
motion state. Fresh consecutive-frame captures did not reproduce an
accumulating silhouette in XRENGINE. They did expose a real, separate rendering
defect: skinned deformation contributes no motion to the Velocity target.

## Reproduction

1. Configure the Unit Testing World for OpenGL, Mitsuki, and Sexy Walk.
2. Load the matching Unity humanoid profile through
   `XRE_UNITY_HUMANOID_AVATAR_PROFILE`.
3. Focus the editor camera on Mitsuki.
4. Capture paused fixed samples and a consecutive-frame running sequence.
5. Compare the final color, Velocity, and temporal input/output resources.

Disposable evidence belongs under:

`Build/_AgentValidation/20260824-continue-humanoid-root/`

## Confirmed Observations

- Fixed samples at `0`, `0.8`, `1.6`, and `2.4` seconds show one upright,
  coherent avatar and correspond to the matching Unity pose silhouettes.
- Direct playback, state-machine playback, seeks, restarts, forward loops,
  reverse loops, and clip replacement do not accumulate skeletal pose state.
- A four-frame FXAA sequence and a complete eight-frame FXAA sequence from the
  opposite camera show one non-accumulating silhouette through the loop:
  `mcp-captures/rendering/baseline-fxaa/` and
  `mcp-captures/rendering/baseline-fxaa-opposite/` under the continuation run.
- An eight-frame TSR sequence also shows one coherent silhouette. During MCP
  capture, TSR repeatedly reported `history generation awaiting layer reseed`
  and rendered current-frame data until reseeding, so this proves the visual
  humanoid result but not healthy TSR history reuse.
- Velocity exports are uniformly black under both FXAA and TSR while the avatar
  is moving:
  `mcp-captures/rendering/fxaa-resources/RenderPipeline_Velocity_20260824_223951.png`
  and
  `mcp-captures/rendering/tsr-resources/RenderPipeline_Velocity_20260824_223812.png`.
- The content-basis wrapper is a stable `-90` degree X conversion below the
  semantic model root; root motion is applied only to the semantic root.
- The fixed Unity screenshot series itself contains uncleared older silhouettes
  after the first frame. Those raster artifacts are not used as the pose-parity
  oracle; the refreshed Unity JSON and current-frame silhouettes are.

## Root Cause

`Build/CommonAssets/Shaders/Scene3D/MotionVectors.fs` and its stereo variant
compute both current and previous clip positions from the same current
`FragPosLocal`. For ordinary skinned meshes the draw model matrix is identity,
and `RenderCommandMesh3D` deliberately returns that same identity as the
previous model matrix. Bone deformation therefore cancels out and produces
zero velocity.

`XRMeshRenderer` already exposes `PreviousSkinPaletteBuffer` and an external
GPU-physics path swaps current/previous palettes, but the normal CPU/vertex
skinning path neither allocates nor snapshots a previous palette. The generated
motion-vector vertex program also exports only the current skinned local
position. The black resource is therefore explained without attributing the
problem to humanoid Body/Hips evaluation.

## Iterations

### Baseline

- Completed: captured and visually inspected FXAA sequences from two camera
  positions and a TSR sequence.
- Completed: exported and visually inspected Velocity under FXAA and TSR.
- Completed: correlated the black target with the motion shader, render-command
  previous-matrix policy, generated skinning shader, and palette-buffer lifetime.
- RenderDoc was not needed because MCP resource export and source inspection
  identified the failing producer unambiguously. `rdc doctor` nevertheless
  passed for Python 3.10.6, RenderDoc 1.44, replay, command-line capture, and the
  Vulkan layer.

The named `humanoid-temporal-v1` editor session was stopped through
`Manage-McpEditorSession.ps1`. Its logs are under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260824-222901-humanoid-temporal-v1/logs/`.

## Renderer Follow-up Contract

A renderer fix should snapshot the previous ordinary skin palette once per
render frame and provide a motion-vector-specific generated vertex output for
the previous skinned local position. The fragment shader must compare current
and previous deformed positions. Compute-skinning and externally supplied
physics palettes must obey the same lifetime contract. This should be validated
with non-black motion on moving vertices, zero motion on static vertices,
history reseeding after discontinuities, and both OpenGL and Vulkan captures.

That change is intentionally not folded into the humanoid Body/root patch: it
crosses renderer, generated-shader, compute-skinning, and buffer-lifetime
contracts and is not required to make the current animation pose match Unity.

## Acceptance Criteria

- Passed for the humanoid gate: running Mitsuki playback has one stable,
  current-frame silhouette from two camera positions.
- Passed for animation state: seeks, loops, reverse playback, restart, clip
  replacement, and direct/state-machine handoff do not retain an older pose.
- Isolated, not fixed here: ordinary skinned motion vectors remain zero and TSR
  capture-time history reseeding remains renderer follow-up work.

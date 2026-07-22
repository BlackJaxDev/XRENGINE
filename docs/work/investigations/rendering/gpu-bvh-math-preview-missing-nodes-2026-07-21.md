# GPU BVH Math Preview Missing Nodes

## Problem

In the Math Intersections Unit Testing World, GPU Scene BVH showed only the 75
source AABBs and GPU Mesh BVH showed only the wavy grid mesh. Neither preview
rendered the GPU-resident hierarchy comparable to its CPU counterpart.

## Issues Found

- Both components reported ready, passing workloads with the expected topology
  counts: 75 scene nodes and 399 mesh nodes.
- The original queued GPU workloads ran without a valid render-graph pass, so
  Vulkan rejected their BVH build/refit dispatches.
- After correcting workload admission and renderer preparation, Vulkan frame-op
  tracing proved the hierarchy dispatch and draw were valid (the mesh case drew
  4,788 line instances), but they were appended after
  `RenderToWindow_TsrOutputTexture`.
- Those lines targeted `ForwardPassFBO` after the final viewport copy. The next
  frame cleared that target, so they could never appear onscreen.
- The same direct renderer API was used by the Math test, the ImGui model BVH
  preview, and the legacy model preview component.

## Solution

- Keep GPU BVH workload execution in the component render callback under a valid
  pass identity, independently of whether debug visualization is enabled.
- Queue GPU BVH overlay requests per pipeline and deduplicate them by renderer.
- Double-buffer reusable request lists so steady-state rendering does not
  allocate in the per-frame path.
- Drain requests from `VPRC_RenderDebugShapes` inside the real
  `LateDebugOverlay`, before post-processing and the final viewport copy.
- Route the Math test and both model-preview entry points through this queue.
- Prepare the generated line renderer before dispatch/draw and give the compute
  program the diagnostic name `GpuBvhDebugLines`.

## Validation Evidence

Baseline captures:

- `Build/_AgentValidation/20260721-gpu-bvh-preview/mcp-captures/Screenshot_20260721_201141_696_7266a17eb61f42069118c31d8d085641.png`
- `Build/_AgentValidation/20260721-gpu-bvh-preview/mcp-captures/Screenshot_20260721_201244_558_019fcb87ba0449f88c8b469fe5aabb26.png`

Validation completed:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`:
  succeeded with 0 warnings and 0 errors.
- Targeted `GpuMeshBvhPreviewContractTests`: 6 passed, 0 failed.
- A fresh Vulkan GPU Scene capture was produced at
  `Build/_AgentValidation/20260721-gpu-bvh-preview/mcp-captures/Screenshot_20260721_220444_642_60dc8b3e935c456fb365616f6a217adc.png`.

Visual inspection of that final capture and a corresponding GPU Mesh capture
were interrupted when the user asked to wrap up. They remain the next validation
step; the code is build- and test-clean.

RenderDoc tooling passed `rdc doctor`, but the apphost capture loaded a
different desktop-mode scene and the OpenXR launch lacked an available form
factor. Vulkan frame-op tracing supplied the decisive pass-order evidence
instead.

## User Confirmation

Pending.

## 2026-07-22 Query-parity follow-up

### Problem

The GPU rigs displayed topology but did not run their CPU counterparts' actual
queries. GPU Scene only checked buffer/topology readiness; GPU Mesh did the same
and its packed triangles were malformed for non-interleaved meshes.

### Root causes

- The CPU and GPU rigs had separate workload definitions. The GPU scene moved
  one AABB instead of three and neither GPU rig dispatched the CPU rig's query.
- Vulkan can defer an entire primary command buffer while a newly activated
  rig's graphics pipelines compile. One-shot BVH build/triangle-pack work in
  that discarded buffer was still marked submitted by the host wrappers.
- `mesh_bvh_pack_triangles.comp` and `mesh_triangle_aabbs.comp` declared a
  non-interleaved position buffer as `vec4[]`. Static `XRMesh` positions are
  tightly packed `Vector3` values, so the shader advanced 16 bytes per vertex
  through a 12-byte stream. Skinned position buffers, by contrast, use four
  scalars per element.

### Fix

- CPU and GPU tests now share the scene animation, query bounds, mesh segment,
  and brute-force helpers. GPU compute shaders return hit count, nearest mesh
  distance, traversal status, and node classifications for matching visuals.
- Invalid GPU BVH headers request a cadence-limited rebuild until a real build
  result is observed. Mesh rebuilds retain immutable source storage views so a
  retry does not churn in-flight Vulkan resources.
- Both mesh shaders load non-interleaved positions from a scalar array using a
  host-provided `PositionStrideScalars`, supporting packed static `vec3` and
  skinned `vec4` inputs exactly.

### Evidence

- Vulkan frame-op logs showed primary recording deferrals immediately after the
  original one-shot BVH submissions.
- A temporary in-shader control query proved the packed triangle data differed
  from the CPU source before the stride fix.
- After the fix, all four live components passed concurrently and the two
  inspected captures listed in the testing note showed matching CPU/GPU debug
  semantics from different camera positions.
- The final post-cleanup Vulkan sample reported scene hits `8` CPU / `8` GPU
  and mesh hits `1` CPU / `1` GPU, with every rig ready and passing. Nine
  targeted shader/contract tests and the GPU BVH build/refit test also passed.

## 2026-07-22 Debug-overlay stability follow-up

### Findings

- CPU Scene calls `CpuBvhRenderTree.DebugRender` with the animated query AABB.
  That API prunes every disjoint subtree, so internal boxes appear and disappear
  whenever the moving query crosses their bounds. Three animated item refits per
  update also move the displayed bounds. This is query visualization, not a
  stable full-tree view or evidence of tree corruption.
- GPU Mesh classifies a node before descending to its children. A completed
  triangle hit therefore proves that the root and every ancestor on the hit path
  were classified as visited.
- The normal GPU Mesh overlay draws classified yellow nodes, unclassified cyan
  nodes, green leaves, and the source wireframe together as transparent,
  depth-disabled line geometry. Many boxes share the root/perimeter edges, so
  repeated cyan/base lines can visually cover the yellow root edge.
- The CPU legacy mesh tree and GPU Morton LBVH also have different topology and
  leaf grouping. Matching queries and hit results do not imply matching sets of
  yellow node boxes.

### Suggested visualization cleanup

- Draw the complete CPU Scene tree in stable base colors, then draw query-visited
  nodes in a separate highlight pass instead of pruning the base tree by query.
- Draw GPU Mesh base nodes first and classified nodes again in a final opaque or
  wider highlight pass (or offer a classified-only mode), so coincident base
  geometry cannot obscure the visited root/path.

### Evidence and attempted probe

- Close-up Vulkan capture:
  `Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/mcp-captures/Screenshot_20260722_120507_047_822f51923e6d43ac8e1a79e48bca3fbc.png`.
- A temporary classified-only source probe was reverted. Its rebuilt editor
  stalled before the workload became ready while the current unrelated Vulkan
  resource-allocation loop repeatedly recreated viewport resources, so it was
  not treated as evidence.
- `rdc` was unavailable in the current shell. Source control-flow plus the
  successful GPU hit result are sufficient to prove root visitation.

### Implemented resolution

- CPU Scene now renders the complete BVH once in a low-alpha base pass and
  renders the query-filtered nodes again as a separate highlight pass. The
  animated query changes only the highlight, so disjoint internal nodes no
  longer disappear from the underlying tree.
- CPU Mesh uses the same base-then-highlight ordering, keeping its presentation
  contract aligned with the GPU test.
- GPU Scene and GPU Mesh queue complete-tree base geometry separately from
  query-classified geometry. The late-debug pass drains base GPU overlays
  before ordinary CPU debug geometry and drains opaque, wider GPU highlights
  afterward. Coincident cyan/source lines therefore cannot cover the yellow
  GPU Mesh root and visited ancestor path.
- Query semantics are unchanged: both CPU/GPU pairs still share their animated
  inputs and brute-force oracle, while the GPU rigs build and traverse their
  GPU-resident BVHs.

### Resolution evidence

- Two CPU Scene captures taken several seconds apart retain the full cyan tree
  while the query highlight moves:
  `Screenshot_20260722_121821_142_6162bc6848974899b0d94c04dce74603.png`
  and
  `Screenshot_20260722_121841_119_2596e6df115145028b0ddf10df334d6f.png`.
- Two GPU Mesh captures from different camera positions show the opaque yellow
  outer root and visited ancestor path over the cyan/green base hierarchy:
  `Screenshot_20260722_121906_623_25230760004d4f0b850770c8ce504ff5.png`
  and
  `Screenshot_20260722_121922_863_90fdcbd2dd034817b00198ceaee4f33c.png`.
- All captures are under
  `Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/mcp-captures/`.
- The live GPU Mesh component reported ready and passing with one hit while the
  screenshots were captured.
- `GpuMeshBvhPreviewContractTests`: 8 passed, 0 failed.
- Editor build: succeeded with 0 errors. Two warnings came from concurrently
  modified `VPRC_SurfelGIPass` fields outside this change.
- Final Vulkan log:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-22_12-17-50_pid47976/log_vulkan.log`.
  It contains expected startup/toggle pipeline deferrals and no validation
  errors, device loss, or exceptions.

## 2026-07-22 Configurable debug displays

All four Math BVH rigs now expose per-rig Inspector controls for topology
visibility, display layers, and colors. `All`, `LeavesOnly`, and `InternalOnly`
use the same semantic filter on CPU and GPU. CPU Scene gained a detailed,
allocation-free BVH debug traversal callback that reports real leaf state; GPU
rigs continue filtering in `bvh_debug_lines.comp`.

Base/visited topology, source geometry, query, hit marker, and validation marker
can be toggled independently. GPU rigs also expose their line widths and maximum
debug-node count. These settings affect visualization only and do not change the
shared CPU/GPU inputs, GPU traversal, or validation oracle.

Live Vulkan inspection covered GPU Mesh all/leaves/internal modes and CPU Scene
leaves-only mode. Eleven targeted tests passed, the redirected editor build was
warning/error clean, and the PID 48516 Vulkan log contained no validation
errors, device loss, or exceptions. Exact evidence paths are recorded in the
Math Intersections BVH testing note.

## 2026-07-22 Scene query shapes and partial containment

CPU Scene and GPU Scene now share a selectable animated box, sphere, or frustum
query. The GPU shader classifies AABBs against the actual sphere or frustum;
frustum classification includes the face, AABB-axis, and edge-cross-axis SAT
tests instead of substituting the frustum's enclosing bounds. Class one is full
containment and class two is partial containment.

The scene rigs expose a separate partial-node color and visibility toggle. GPU
debug lines apply a class visibility mask, while the CPU detailed traversal now
reports `EContainment` directly to its callback. Benchmark clones copy the
selected query and display settings without allocating their own Inspector UI.

Live Vulkan GPU Scene sphere and frustum queries both reported ready and
passing; CPU Scene frustum also passed. The PID 42632 Vulkan logs contained no
shader/validation errors, device loss, or exceptions. The testing note records
the hit counts and inspected capture.

## 2026-07-22 Raycast and mesh shape-query extension

All four rigs now share one allocation-free animated query definition supporting
Box, Sphere, Frustum, and a finite Raycast. Scene tests classify their AABB
items with that query. Mesh tests use the same node pruning, then independently
test enabled triangle vertices, edges, and faces; raycast mode keeps the
previous nearest-triangle behavior.

The GPU mesh compute path writes a seven-bit result per source face (three
points, three lines, one triangle), aggregate channel counts, and the raycast
distance. The debug view consumes those actual GPU result bits rather than
reconstructing hits from the CPU oracle.

Live diagnosis found two CPU-oracle false-positive sources. Triangle-vs-box
used the closest-point `AABB.Intersects(Segment)` heuristic instead of the
exact finite segment SAT predicate, and `Sphere.IntersectsSegment` tested the
infinite supporting line without clamping its roots to the segment. The query
volume now uses exact finite predicates matching the GPU shader. After those
fixes, every CPU/GPU rig passed every query shape, and both mesh rigs matched
all enabled point/line/triangle counts. Nineteen focused tests passed; the final
Vulkan log and inspected captures are recorded in the testing note.

## 2026-07-22 Geometry containment classification correction

The earlier partial-containment display attached class two to BVH traversal
nodes. That made **partial** nearly indistinguishable from **visited** and did
not answer which source geometry actually overlapped the query. That display
contract is superseded by semantic classification of the queried geometry:

- gray = disjoint;
- magenta = intersected but not fully contained;
- green = fully contained (or the green validation marker when parity passes).

Visited BVH nodes now use one traversal color regardless of whether their
bounds are contained or intersected. **Show Intersected Geometry** controls the
magenta source geometry only; it no longer changes traversal-node visibility.

CPU Scene stores the actual `EContainment` returned for every collected AABB.
GPU Scene now writes one classification per source AABB to a readback SSBO, so
its colors come from the GPU query rather than a CPU-side reconstruction. Mesh
queries store the same three-state result for each enabled point, edge, and
triangle. The GPU mesh result word uses two bits for each of its seven source
elements (three points, three edges, one face); ray hits are intersections, not
containment.

Live validation exposed an intermittent one-item CPU/GPU Scene frustum mismatch
at a separating-axis boundary. The Math BVH CPU oracle now mirrors the GPU
frustum/AABB SAT classifier operation-for-operation. Ten consecutive live
frustum samples then passed for both GPU Scene and GPU Mesh.

The redirected editor build completed with zero warnings/errors and 21 focused
tests passed. Final inspected captures are:

- `Screenshot_20260722_142529_471_2f70ef463607477a9d9a340784d7eb66.png`
  for paired CPU/GPU Scene gray/magenta/green AABBs;
- `Screenshot_20260722_142550_338_beb35e16e17146e4814c462775ee1128.png`
  for GPU Mesh gray/magenta/green triangle results.

They are under
`Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/mcp-captures/`.
The final Vulkan session log is under
`Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/live-session/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-22_14-23-41_pid27952/`
and contains no shader compilation errors, validation errors, device loss, or
unhandled exceptions.

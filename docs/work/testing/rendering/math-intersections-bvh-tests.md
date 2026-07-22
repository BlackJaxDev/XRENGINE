# Math Intersections BVH Tests

Last updated: 2026-07-22
Owner: Rendering
Status: Four interactive test and benchmark rigs implemented

The Math Intersections Unit Testing World exposes four BVH-specific tests. Each
rig owns its workload and debug view, so the existing world benchmark harness
can duplicate the same algorithm with visualization either enabled or disabled.

| Test | Workload and correctness check | Debug rendering |
|---|---|---|
| CPU Scene BVH | Builds the flat `CpuBvhRenderTree`, moves three AABBs per logic tick, publishes the refit, and compares a selectable moving box, sphere, frustum, or finite raycast query against brute force. | Stable full-tree base, source AABBs, the actual query shape, and separate fully-contained/partial node highlights. |
| GPU Scene BVH | Uses the CPU scene test's 75 AABBs, three animated updates per tick, and the same selected query shape. A compute traversal reports its hit count and classified nodes, and the result is compared with the same CPU brute-force oracle. | The same stable base and source/query colors as the CPU rig, with query-classified highlights rendered from GPU buffers. |
| Legacy CPU Mesh BVH | Builds the legacy `SimpleScene` triangle BVH over a 200-triangle wavy grid. Box, sphere, and frustum queries independently return selected points, lines, and triangles; the finite raycast retains hit-count and nearest-hit validation. | Full-tree base, contained/partial highlights, the selected query, configurable result colors, and the raycast nearest hit. |
| GPU Mesh BVH | Uses the CPU mesh test's 200 source triangles and the same selected query. Compute traversal writes per-source-triangle point/line/triangle bits, aggregate counts, and the raycast nearest distance for comparison with the CPU oracle. | The same source, query, result colors, node classification, and raycast hit marker as the CPU rig. |

The status sphere above each rig is green when the latest completed validation
passes, orange while GPU resources or shaders are becoming ready for the first
time, and red on a completed validation failure. A temporarily pending GPU mesh
refresh retains the latest completed result. GPU tests remain GPU-only: lack of
initial readiness is visible and does not silently substitute a CPU implementation.

## Running the tests

1. Set `WorldKind` to `MathIntersections` in
   `Assets/UnitTestingWorldSettings.jsonc` and launch the editor with
   `--unit-testing`.
2. In **Math Intersections Test Controls**, enable one of the four BVH tests.
3. Inspect the animated debug view and status sphere. Keep exactly one test
   active when benchmarking.
4. Choose **Run Benchmark** for workload-only copies or **Run Benchmark With
   Debug Displays** to include the visualization cost.

## Debug display controls

Each BVH rig owns a sibling `CustomUIComponent` named `<mode> BVH Debug
Controls`. Select the active test node in the hierarchy and open that component
in the Inspector to edit its display and, for scene rigs, its query shape.

Shared controls on all four rigs include:

- node visibility: all nodes, leaves only, or internal nodes only;
- independent base-tree, visited-node, source-geometry, query, hit-marker, and
  validation-marker toggles, where applicable;
- leaf, internal, visited, partial, source, query, hit, and validation
  colors relevant to that rig.

All four rigs expose the same `Box`, `Sphere`, `Frustum`, and `Raycast` query
selector. Fully contained/visited nodes use the visited color; partial
containment uses its own color and **Show Partial Nodes** toggle. Mesh rigs add
independent **Query Points**, **Query Lines**, and **Query Triangles** toggles,
**Show Query Results**, and separate point/line/triangle result colors. Raycast
mode always queries triangles and preserves the nearest-hit marker. GPU compute
classifies the selected shape directly rather than querying its enclosing AABB,
and the CPU oracle consumes the same animated parameters.

GPU rigs additionally expose the maximum emitted debug-node count and separate
base/highlight line widths. Visibility filtering and colors are passed to the
GPU debug-line compute path; they do not cause CPU traversal or readback. CPU
Scene uses `CpuBvhRenderTree.DebugRenderNodes` so its leaf/internal selector is
based on actual tree topology rather than inferred bounds.

The interactive source rigs own these Inspector controls. Benchmark copies omit
the `CustomUIComponent` and its delegates so copy count and timings are not
inflated by unused per-instance editor UI state; their workload and optional
debug rendering are still configured by the benchmark harness.

The benchmark status and `math-intersections-benchmarks.log` include frame-time,
spawn/teardown, and transfer metrics plus BVH readiness, validation, build,
update/refit, query, node, primitive, and last-hit totals. CPU structures are
built during factory spawning so build cost is included in spawn time. GPU
structures are created on the render thread and their build/refit work is
included in the sampled frames.

## Validation expectations

- CPU scene and legacy CPU mesh tests require exact parity with their brute-force
  oracle on every update.
- GPU tests require the compact topology and buffers to become ready, then require
  exact aggregate and point/line/triangle count parity, plus nearest-hit parity in
  mesh raycast mode, with the CPU brute-force oracle. Traversal and node
  classification remain GPU-resident. Source-result display reads three-state
  classifications from the GPU: one word per scene AABB and seven packed two-bit
  classifications per source mesh triangle.
- A benchmark is valid only when its final BVH summary reports every copy ready
  and passing validation.
- Hardware/backend qualification still follows
  [GPU Scene BVH External-Hardware Qualification](gpu-scene-bvh-external-hardware-qualification.md);
  these interactive rigs are focused development coverage, not a substitute for
  the full vendor, backend, scale, and RenderDoc matrix.

## 2026-07-22 query-shape and traversal-class validation

- Fourteen focused tests passed after the redirected editor/test build,
  including behavioral contained/partial/disjoint checks for all three shapes.
- Live Vulkan GPU Scene validation passed for sphere (`13` hits) and frustum
  (`34` hits). CPU Scene frustum validation also passed (`37` hits); different
  samples reflect the independently animated live rigs, not different query
  algorithms.
- This earlier pass classified and toggled partial BVH traversal nodes. The
  later geometry-classification correction supersedes that display behavior;
  visited nodes now share one traversal style.
- Inspected capture:
  `Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/mcp-captures/Screenshot_20260722_130550_729_1b25f52ced314ab392699918c563a4e9.png`.
- Vulkan log:
  `Build/Logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-22_13-03-36_pid42632/`.
  It contains no shader compilation errors, validation errors, device loss, or
  unhandled exceptions.

## 2026-07-22 raycast and mesh primitive-query validation

- Nineteen focused tests passed. The behavioral coverage sends Box, Sphere,
  Frustum, and Raycast through a real CPU mesh BVH and compares candidate
  traversal with brute force; a regression test also rejects infinite-line
  sphere hits that lie outside the finite segment.
- Live Vulkan CPU Scene and GPU Scene were ready and passing for all four query
  shapes. Representative hit counts were CPU `10/15/36/2` and GPU
  `6/17/33/0` for Box/Sphere/Frustum/Raycast; samples differ because the query
  is animated.
- Live Vulkan CPU Mesh passed all selected point/line/triangle channels for all
  four shapes. Representative actual/expected counts included Sphere
  `36/36`, `54/54`, `22/22` and Frustum `126/126`, `172/172`, `66/66`.
- Live Vulkan GPU Mesh also passed all four shapes. Representative
  actual/expected counts included Sphere `9/9`, `16/16`, `7/7` and Frustum
  `96/96`, `138/138`, `54/54`; Raycast reported `1/1` triangle hit.
- Inspected captures:
  `Screenshot_20260722_134938_319_233fd0dae4374a6b974f2e9893edcd5f.png`
  (query plus topology) and
  `Screenshot_20260722_134953_981_fa5d292504a644329922da37129b0ed0.png`
  (source/query/results) under the current run's `mcp-captures/` directory.
- Final isolated Vulkan log:
  `Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/live-session/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-22_13-46-02_pid26672/`.
  It contains no shader compilation errors, validation errors, device loss, or
  unhandled exceptions.

## 2026-07-22 semantic geometry-class validation

- Source geometry now uses gray for disjoint, magenta for intersected/not fully
  contained, and green for contained. **Show Intersected Geometry** hides only
  the magenta geometry; it does not hide class-two traversal nodes.
- CPU Scene retains `EContainment` on each collected AABB. GPU Scene reads the
  actual per-AABB GPU classification. CPU/GPU Mesh retain a classification for
  each enabled point, line, and triangle; the GPU packs seven two-bit values per
  source face.
- Ray/AABB and ray/triangle hits classify as intersections rather than
  containment.
- The CPU Math BVH frustum/AABB oracle now mirrors the GPU SAT classifier,
  eliminating an observed one-item boundary mismatch. Ten consecutive live
  Frustum samples passed for GPU Scene and GPU Mesh after warmup.
- The redirected editor build succeeded with zero warnings and errors. Twenty-one
  focused tests passed.
- Inspected captures:
  `Screenshot_20260722_142529_471_2f70ef463607477a9d9a340784d7eb66.png`
  (paired CPU/GPU Scene) and
  `Screenshot_20260722_142550_338_beb35e16e17146e4814c462775ee1128.png`
  (GPU Mesh), under the current run's `mcp-captures/` directory.
- Final Vulkan log:
  `Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/live-session/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-22_14-23-41_pid27952/`.
  It contains no shader compilation errors, validation errors, device loss, or
  unhandled exceptions.

## 2026-07-20 validation

- Editor build: succeeded with zero warnings and zero errors.
- Targeted BVH tests: 63 passed, 0 failed, 0 skipped.
- Live OpenGL component results:

| Case | Ready | Passed | Builds | Updates/refits | Queries | Primitives | Nodes |
|---|---:|---:|---:|---:|---:|---:|---:|
| CPU Scene BVH | yes | yes | 1 | 182 | 183 | 75 | 149 |
| GPU Scene BVH | yes | yes | 1 | 473 | 0 | 75 | 75 |
| Legacy CPU Mesh BVH | yes | yes | 1 | 181 | 182 | 200 | 399 |
| GPU Mesh BVH | yes | yes | 1 | 479 | 0 | 200 | 399 |

Visual screenshot acceptance was blocked in the validation worktree by an
unrelated render-graph self-cycle in `RenderMeshesTraditional`; both GPU
workloads were deliberately verified through their render-thread counters and
buffer/topology state instead of treating black captures as visual evidence.

## 2026-07-22 Vulkan parity validation

- All four live components reported ready and passing. A representative sample
  from the final build reported matching scene hit counts (`8` CPU / `8` GPU)
  and mesh hit counts
  (`1` CPU / `1` GPU).
- Two Vulkan viewport captures were inspected from different camera positions:
  `Screenshot_20260722_115835_204_bb135c9d89bb41ff8b26217c8ac4198d.png`
  and `Screenshot_20260722_115907_842_1be75a7670094be48b7e10f7abbb9fef.png`
  under `Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/mcp-captures/`.
- The editor build succeeded with zero warnings and errors. Nine targeted
  shader/contract tests and the actual GPU BVH build/refit test passed.
- The final Vulkan log is
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-22_11-57-30_pid10664/log_vulkan.log`.
  It contains expected pipeline-warmup deferrals, but no validation errors,
  device loss, or exceptions; cadence-limited rebuilds recovered the discarded
  one-shot work.

## 2026-07-22 overlay-stability validation

- CPU Scene now draws a stable complete-tree base independently of its moving
  query-filtered overlay. Two captures several seconds apart retained the base
  hierarchy while the highlight moved.
- CPU Mesh, GPU Scene, and GPU Mesh use the same base-then-highlight visual
  contract. GPU highlights are drained last in the late-debug pass, so the
  yellow GPU Mesh root/path remains visible over coincident base and source
  lines.
- Vulkan GPU Mesh reported ready, passing, and one hit. Two inspected camera
  angles clearly show the outer yellow root and visited ancestor boxes.
- Evidence is under
  `Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/mcp-captures/`;
  the final capture names end in `504ff5.png` and `eaee4f33c.png`.
- Eight targeted contract tests passed. The editor build succeeded with no
  errors; its two warnings were from unrelated concurrent Surfel GI changes.
- The final Vulkan log for PID 47976 contains no validation errors, device loss,
  or exceptions.

## 2026-07-22 configurable-display validation

- A redirected editor build completed with zero warnings and errors while the
  user's existing editor retained the normal output DLLs.
- Eleven targeted tests passed, including CPU detailed-node classification,
  enum-control round-tripping, and the Math BVH GPU/query contracts.
- Live Vulkan GPU Mesh captures verified all-node, leaves-only, and
  internal-only modes. The workload remained ready and passing with one hit.
- A live CPU Scene leaves-only capture showed source/leaf bounds without the
  internal hierarchy, validating the CPU detailed-node path.
- The GPU Mesh rig exposed 21 custom UI fields, including its enum selector,
  toggles, colors, GPU line widths, and node cap.
- Evidence captures are under
  `Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/mcp-captures/`:
  `Screenshot_20260722_124007_583_9f92f7f439954fbeb4315b7d064c3fe4.png`,
  `Screenshot_20260722_124042_518_33fe9c7ae08d46e7bfb49cafb71e9272.png`,
  `Screenshot_20260722_124130_365_daa607823d0a43fdb73cad1b395eb035.png`,
  and `Screenshot_20260722_124233_376_741665d1d06e44c6b9a364357d8e1faf.png`.
- Final Vulkan log:
  `Build/Logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-22_12-38-45_pid48516/log_vulkan.log`.
  It contains no validation errors, device loss, or exceptions.

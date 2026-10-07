# Physics Chain Scale Implementation Status

Date: 2026-10-07
State: Work stopped at the owner's request. Implementation and acceptance remain open.
Code items: [Thousands-scale optimization](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md)
Validation: [Physics validation](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale)
Investigation: [Skinned GPU chain benchmark](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md)

## Completed Code

| Area | Implemented behavior | Limit |
| --- | --- | --- |
| GPU inputs | Retained mapped input banks, 48-byte affine records, owned pending packets, and exact local-rest caches replace transient input staging. | Smaller index/flag packing and the full residency backlog remain open. |
| Bounds and materials | Palette-box bounds contain current and compatible previous poses. Draw-material snapshots include command overrides. Translation-class effects add padding; unsupported and non-finite effects reject the GPU route. | The full effect and backend matrix remains open. CpuDirect can still draw a command whose GPU bounds consumers reject it. |
| Output history | Producer tokens and failure guards prevent unrelated or failed output from becoming valid previous deformation. | History and failure unit tests remain open. |
| Covered rendering | Fully covered renderers use committed GPU output and retained static draw plans. Root motion does not require ordinary per-bone CPU bounds work. Indexed groups preserve each member's palette, deformation, and draw identity. | Partial coverage, source edits, multiple consumers, and OpenGL need their remaining checks. |
| Directional shadows | GPU culling and grouped native submission exist. Covered output publication dirties matching cached cascades; strict admission does not substitute generic CPU collection. | Native cascade slices, caster policy, and failure recovery are not fully validated. |
| World runtime | The world owns clocks, rest-input ranges, and the bound particle graph. World/hierarchy transitions retire the old owner before propagation and block destination admission until propagation ends. | Component packing, quality, and other runtime state still need the ownership cleanup. Two-world concurrent transfer is not checked. |
| CPU workers | Aligned counters avoid shared cache lines. Worker faults preserve completion signals and the join boundary. | Full CPU pipeline parallelism, specialization, and allocation cleanup remain open. |
| Selective readback | Independent world banks, source-generation witnesses, exact resident ranges, held-input gathering, and shared solved-bone composition replace unsafe shared or authored-pose reads. Affine rows include translation and have a typed matrix decoder. | First-use admission needs the correction below. Cross-world and failure checks remain open. |
| Debug and deferred lifetime | One bounded compact world batch feeds two indexed indirect debug draws. Batches own their buffers, renderers, and compute program. Raw requests, operations, and physical payload rows retain leases. CAS retirement prevents double disposal and zero-use resurrection. Vulkan checks native Ready before reuse, skips occupied pooled operations, and seals leased compute bindings. | First-use admission and recurring snapshot allocation remain open. No final live native debug capture was run. |
| Compute backend compatibility | The five-argument compute method remains available. The leased overload is explicit. An unknown backend returns `Unsupported` for a non-null lease. | No backend silently drops the resource owner. |
| Editor inspection | Existing compatible interface references can be assigned through MCP. This permits live World detach and reattach without creating another object. | Reference assignment still follows normal property setters and ownership rules. |

The deferred debug batch does not own the shared particle/static arenas. If a
source retires before Vulkan materialization, exact descriptor validation
rejects the request. This is an explicit failure path, not a promise that
queued debug work survives arena replacement.

## Validation At This Boundary

| Check | Result |
| --- | --- |
| Latest combined editor Release build | Passed after the debug lifetime corrections: zero warnings and errors. The final clean incremental build took 8.43 seconds. |
| Existing targeted tests | An earlier run passed 22 scheduler, world, lifecycle, route, and coverage tests. The full test suite was not rerun after the final lease changes. No unit tests were added or changed in this continuation. |
| World transitions | Live world detach/reattach, parent move/restore, and deactivation/reactivation passed on the earlier combined binary. Active states recovered one chain and valid strict publication. |
| Selective solved-pose readback | Five live requests each delivered 216 bytes in three frames. Bone translations exactly matched the selected particle positions in the same result. Rotations were nonidentity. Total delivered data was 1,080 bytes, with no stale discard or failure. |
| Strict material route | Translation, rotation rejection, neutral command override, rejected override, and recovery passed. Native main and shadow admission followed the route. Presented exports were viewed. |
| Indexed instance groups | Native wide and near captures had 64 and 25 unique members. All 13,312 current and valid previous positions fit their own bounds. Of these, 6,656 previous positions differed from current positions. |
| Final debug/lifetime runtime | Not run on the latest binary. The final source review found the two code issues below. |
| Editor cleanup | The owned `chain-scale-finish` session is stopped. No replay session remains open. No commit, staging, or push was made. |

The passing live checks above precede the final atomic disposal and debug
snapshot changes. They do not validate those last changes.

## Performance Findings

The last observer-free moving-shadow window completed 397 frames in 30.971
seconds: 12.818 Hz, with completed-frame interval p95 83.689 ms. It added 471
accepted directional groups without a new rejection, generic fallback, input
growth, or physics readback. The required 100 Hz and 10 ms p95 targets failed.

A separate instrumented 2,000-chain window reached 10.371 Hz and p95 115.631
ms. RenderDoc and the material-effect fixture were enabled. This is diagnostic
evidence, not a matched timing comparison. A 15-second CPU sample trace placed
substantial work in bridge submission, hierarchy activity, rest capture,
dependency checks, and committed spatial queries. Sampled CPU time is not
per-frame wall time. GPU pipeline timing was disabled, so no GPU pass timing
isolates the frame delay to physics. CPU wall-time and synchronization
attribution are also missing. Do not claim a measured speed gain from these
different observer configurations.

The shader inventory found existing precomputed rest lengths and capsule
inverse terms. Solved-position distances and directions are dynamic. They
must not be cached as invariant data. Remaining coefficient work needs exact
parameter/version witnesses and a matched measurement.

## Resume Point

1. Fix active first-use admission in debug and readback. The current first
   retain happens during enqueue, after some resource access. Acquire a scoped
   active lease before touching storage, reject retirement before mutation,
   and release the guard in `finally`.
2. Move sealed snapshot storage into each reusable `ComputeDispatchOp`.
   `TryDispatchCompute` currently allocates a snapshot for every leased
   dispatch. A retained operation must keep its own unchanged snapshot.
3. Build the updated editor. Run the existing narrow tests. Start a fresh named
   isolated session and complete the debug, readback, and shadow checks in the
   validation plan. View the exported native targets.
4. After live feature validation, obtain the requested unit-test clearance.
   Update the stale layout/source expectations and add the listed history,
   bounds, readback, ownership, and lease tests.
5. Collect separate diagnostic GPU timing and CPU wall-time evidence. Then run
   a matched observer-free 2,000-chain window on the corrected binary. Optimize
   the measured critical path without reducing strict simulation quality.
6. Continue the remaining world ownership, CPU, residency, kernel, collision,
   skinning, activity/sleep, and cleanup code items. They remain listed in the
   active todo. Do not close that document while these items or its acceptance
   gates remain open.

No fresh 2,000-chain timing result exists for the final debug/lifetime binary.
No pending approval was treated as clearance.

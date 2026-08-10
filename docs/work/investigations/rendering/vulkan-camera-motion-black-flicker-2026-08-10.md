# Vulkan camera-motion black flicker and CPU regression

Status: root causes isolated; final directional-cascade motion acceptance pending
Opened: 2026-08-10

## Problem statement

On Vulkan, holding an editor camera arrow key can make the scene viewport remain
black until the key is released. Frame rate drops sharply during the move and can
appear not to recover afterward. The directional light, especially its cascaded
shadows, makes the failure substantially worse.

The performance objective is twofold:

1. no black or stale-output frame while the camera moves, with prompt recovery
   after movement stops; and
2. warmed desktop Vulkan render-loop CPU below 1 ms at p95, excluding explicit
   frame pacing/GPU waits, with zero per-frame heap allocation in the path.

## Reproduction and measurement notes

- Use the Vulkan Unit Testing World in a named isolated MCP editor session.
- Maximize the editor and confirm the active viewport is not the minimized 1x1
  surface before comparing timings.
- Disable render-on-demand for measurements. Otherwise the FPS overlay can keep
  displaying the last expensive scene frame while only UI frames continue,
  which looks like a permanent failure to recover.
- The repeatable camera endpoints used by this investigation were:
  - A: position `(15.001201, 21.636095, 21.018045)`, look target
    `(6.947728, 18.215202, 16.176654)`;
  - B: position `(-21.999998, 19, 5.000001)`, look target
    `(-12.479667, 15.970803, 4.567258)`.
- Move A to B and B to A over eight seconds, sampling Vulkan profiler output every
  0.5 seconds. Test light/cascades on, whole light off, and light on/cascades off.

## Evidence and root causes

### 1. Swapchain recreation could permanently strand mapped frame slots

The desktop swapchain image completion ledger was reset during recreation without
carrying forward the strongest completion value from the retired images. A later
frame could therefore fail to prove that its mapped frame-data slot was reusable,
leaving the viewport black after resize/minimize/recreation.

The replacement swapchain now inherits the strongest retired image timeline
completion value. Minimize/restore live validation passed after this change.

### 2. A mixed scheduled/inline command chain was treated as one reusable run

`CountContiguousMeshCommandChainRun` did not split a contiguous mesh chain when
scheduled membership changed. The reusable primary path would execute only part
of a dense pass and fall back after approximately half the meshes.

The run is now partitioned whenever scheduled membership changes. A warmed dense
main pass subsequently executed roughly 729 reusable secondary command buffers;
primary recording fell to about 0.4 ms.

### 3. Directional cascade publication amplifies camera motion into a CPU rebuild

Isolation aligned with the user report:

- with the whole directional light disabled, a 13-frame motion sequence contained
  no full-black frame;
- with the light enabled but cascades disabled, the motion spike was materially
  smaller than the original cascaded path;
- before the mixed-chain fix, cold cascaded moves reached approximately
  2.1--2.5 seconds, versus about 366 ms with the whole light disabled and about
  485 ms with only cascades disabled;
- after the mixed-chain fix, warmed cascaded moves still produced roughly
  147--181 ms spikes.

Publishing even one changed directional cascade changes shader-visible lighting
state and invalidates persistent program-binding artifact generations for nearly
every visible mesh. One detailed phased-refresh frame rebuilt 789 artifacts and
materialized 1,245 frame operations/1,229 draws. Phasing one cascade per frame was
therefore worse: it spread the global invalidation across multiple motion frames.

The cascade policy now holds the last internally coherent atlas generation while
the camera moves and preserves its shader-visible atlas slot byte-for-byte. It
also tracks logical consecutive content mismatch rather than treating the
physical atlas render age as staleness. The default maximum consecutive stale
camera/content frames is four.

### 4. The first motion-hold settle signal was false

Using cascade content-hash stability initially kept most moving frames in the
reusable path at roughly 28--38 ms, with about 834 artifact reuses and only two
builds. It still admitted a 283.6 ms frame followed by a 227 ms frame in the
middle of a move. Cascade matrices are texel-snapped, so their hashes can remain
unchanged for four frames even while the actual editor camera is moving.

The current implementation instead tracks the actual source camera render matrix
once per render frame, separately for desktop and HMD sources. Only consecutive
stable source-camera poses can release the held cascade refresh. This latest
change builds cleanly but its A-to-B/B-to-A live acceptance pass was intentionally
stopped when the user requested this handoff; it must not yet be described as
runtime-validated.

### 5. The warmed steady-state CPU bottleneck is not primary recording

With primary reuse working, the full Debug viewport measured approximately
30--34 FPS and 28--33 ms whole-frame time. Vulkan CPU remained near 25 ms while
primary command recording was about 0.4 ms. Approximately 18--21 ms was spent in
frame-operation preparation and mesh request materialization.

`VulkanFrameLoop.DrainQueuedMeshRenderRequests` currently processes roughly 800
mesh requests every frame. Inside the per-request loop it repeatedly captures
frame-stable planner/view/allocation state, constructs a
`VulkanMeshMaterializationSnapshot`, pushes pipeline/camera scopes, materializes
the mesh operation, rents a `MeshDrawOp`, and then the frame loop drains, sorts,
coalesces, splits, and normalizes arrays of those operations. Reaching a total
render-loop CPU target below 1 ms requires eliminating that repeated steady-state
work, not merely micro-optimizing Vulkan submission.

## Implemented changes

- Preserve retired swapchain-image completion authority across recreation.
- Partition contiguous mesh command-chain runs when scheduled membership changes.
- Track cascade staleness as consecutive content mismatches.
- Hold the previously published cascade atlas slot byte-for-byte during motion.
- Debounce a combined cascade refresh until the actual source camera pose is
  stable, rather than relying on texel-snapped cascade hashes.
- Raise the default directional-cascade motion hold from two to four frames and
  update the runtime setting descriptions.

## Validation completed

- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -warnaserror`
  passed with 0 warnings and 0 errors after the source-camera pose change.
- `rdc doctor` passed the Windows, Vulkan runtime, and RenderDoc-layer checks.
- Swapchain minimize/restore and mixed scheduled/inline reuse fixes passed their
  isolated live checks.
- The directional-light-off sequence and contact sheet are under
  `Build/_AgentValidation/vulkan-phase42-43-final-20260810/camera-motion-evidence/light-off-smooth/`.
- The last fully exercised Vulkan session contained no Vulkan validation VUID,
  mapped-slot reopen error, missing swapchain writer, dropped frame operation, or
  device loss. The sole `DeviceFault` match was a capability diagnostic saying
  that device-fault reporting was unavailable, not a fault report.
- The named session `vulkan-camera-motion-20260810` is stopped.
- User acceptance is pending.

No tests were added or changed while this runtime regression remains under live
validation, per repository testing policy.

## Immediate next step: finish black-flicker acceptance

1. Restart the named isolated Vulkan session with command-chain trace/detail
   diagnostics disabled, maximize it, and disable render-on-demand.
2. Confirm the directional light and four cascades are enabled.
3. Warm at A, perform A-to-B and B-to-A eight-second moves, and sample every
   0.5 seconds.
4. During movement, require artifact builds to remain near the reusable baseline
   (about 2 builds/834 reuses), no mid-motion 200+ ms cascade refresh, no black
   viewport image, and no Vulkan validation/ownership error.
5. After release, verify one coherent refresh after four stable source-camera
   frames and return to the warmed baseline within the following few frames.
6. Capture and inspect a motion image sequence and recheck `log_vulkan.log`.
7. If the post-release combined refresh is still visually disruptive or exceeds
   the frame budget, stop changing cadence. Decouple directional-light dynamic
   data from persistent mesh binding-artifact generations so a shadow refresh
   updates frame-slot data without rebuilding every mesh artifact.

## Next debugging plan: render-loop CPU below 1 ms

The target must use a warmed p95 distribution and report explicit GPU/present
waits separately. Primary recording already meets the target in the reuse case;
the following work addresses total active render-loop CPU.

1. **Instrument the remaining 18--21 ms precisely.** Add allocation-free nested
   counters around mesh dequeue, context resolution, scope publication,
   materialization/artifact lookup, `MeshDrawOp` rent/enqueue, drain, sort,
   coalesce, split, and normalization. Record request counts, dirty counts, cache
   hit rates, bytes/allocations, p50/p95/p99, and no-work frames.
2. **Hoist frame-stable state out of the mesh loop.** Capture
   `PublishedResourcePlannerRuntimeState`, descriptor view-family identity,
   external-swapchain/prewarm flags, the synchronous-allocation policy, and the
   `VulkanMeshMaterializationSnapshot` once per drain. This is a safe first
   optimization and establishes how much time is repeated policy lookup.
3. **Batch scope changes rather than pushing two scopes per mesh.** Group the
   drained requests by pipeline/camera (or carry an immutable render-context
   token in each request) so pipeline/camera publication occurs once per batch.
   Verify those scope implementations and the queue introduce zero hot-path heap
   allocations.
4. **Stop rematerializing unchanged meshes every frame.** Introduce a persistent
   prepared-mesh operation/artifact keyed by renderer, pass, target, material,
   geometry, view family, render-graph revision, and binding-layout generation.
   Producers should enqueue only dirty/change records; camera transforms and
   other dynamic values should be frame-slot arena offsets consumed by the
   existing reusable secondary command buffers.
5. **Cache the static frame-operation plan.** When the static operation/schedule
   signature and render-graph revision are unchanged, reuse the drained/sorted/
   coalesced/split/normalized plan. Patch only uploads, dynamic UI, and explicitly
   dirty operations. Avoid allocating new `FrameOp[]` arrays on the reuse path.
6. **Remove global invalidation from dynamic lighting and camera data.** Persistent
   descriptor/binding artifacts should depend on layouts and resource identities,
   not per-frame cascade matrices, stale ages, or camera values. Publish those
   values into stable frame-indexed buffers so shadow refresh and camera motion
   do not trigger hundreds of artifact builds.
7. **Validate in descending checkpoints.** First drive frame-op preparation below
   5 ms, then below 2 ms, then below 0.5 ms; retain primary recording below
   0.5 ms and submit/present active CPU below 0.2 ms. The final gate is warmed
   active render-loop CPU below 1 ms p95, zero steady-state allocations, no
   black/stale output, and no validation or ownership failures during repeated
   camera moves and resize/minimize recovery.

RenderDoc remains useful for proving that cached plans still bind and write the
correct targets, but the next performance pass should be driven by CPU stage
timing and allocation traces rather than GPU capture alone.

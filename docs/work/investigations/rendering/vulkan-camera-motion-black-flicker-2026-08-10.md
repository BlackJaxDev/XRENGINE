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

## 2026-08-10 maximize/skybox follow-up

The skybox component, fullscreen triangle, material, and Background command all
remain active after maximizing. The failure is below skybox shading: the resize
commits a new render-resource generation, recompiles extent-dependent pipeline
variants, and the first new-generation frame can submit consumers whose source
color/depth images have not reached their declared attachment or sampled layout.
`HDRSceneTex` and `LightingAccumTexture` then remain identically zero, so the
final viewport is black even though the skybox draw is still present.

Validation exposed three independent correctness holes:

- ImGui and dynamic-text overlay recorders called `vkBeginCommandBuffer` twice
  for the same command buffer. They now begin once through the command runtime.
- The tracked barrier encoder published image lifetime dependencies but did not
  publish each image barrier's resulting access/layout state. It now journals
  every image barrier after emission.
- Async graphics warmup skipped draw operations before their pass transition and
  render-graph barriers, then submitted downstream consumers as a partial frame.
  Primary recording now defers before `vkBeginCommandBuffer` until every pipeline
  required by the sealed graph is executable.

Those changes eliminate the repeated already-recording and stale shader-read /
attachment-layout errors. A remaining first-use resize hole is still under
investigation: a newly allocated depth/stencil image is sometimes first observed
by a sampled descriptor as `DepthStencilReadOnlyOptimal` while Vulkan still has
the physical subresources in `Undefined`. Unconditionally transitioning sampled
depth while it is also a destination attachment was tested and rejected because
it made `HDRSceneTex` black at startup even without validation errors. The final
fix must distinguish a legitimate read-only depth attachment from a write-capable
attachment (or repair the frozen descriptor/resource-plan ownership) rather than
removing the attachment guard globally.

Evidence is under
`Build/_AgentValidation/20260805-2158-phase40-vulkan-perf/20260810-cpu-below-1ms/`.
The relevant validation session log is
`Build/_AgentValidation/mcp-sessions/vulkan-cpu-20260810/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-10_19-36-34_pid28848/log_vulkan.log`.
The Vulkan leaf build passes with zero warnings and errors. No tests were added or
run while live resize correctness remains unresolved.

### Resolution: startup and maximize skybox failure

The remaining black viewport was not a skybox shader, viewport, depth-test, or
resource-resize failure. Frame-operation scheduling combined pass ranks from two
different graphs:

- the currently published resource graph had only seven passes from another
  render context and ranked `Background` near the front;
- the main scene's complete pipeline metadata contained synthetic passes such as
  `QuadBlit_LightCombineFBO_to_ForwardPassFBO`, but those missing passes received
  ranks from a different topological order.

That mixed rank space recorded the skybox first, followed by the ForwardPass
color clear and deferred-lighting blit. The skybox draw was valid but was erased
later in the same frame. Maximizing made the symptom deterministic because the
new `HDRSceneTex` generation no longer retained any prior color.

The fix has two parts:

- Frame-operation sorting now uses each operation context's complete pipeline
  pass metadata as its ordering authority and uses the published compiled graph
  only as a fallback. The metadata-order lookup is cached once per contiguous
  context while constructing sort keys, avoiding a per-operation cache lookup.
- `DefaultRenderPipeline` explicitly declares that `Background` depends on the
  light-combine-to-ForwardPass blit. This is necessary because inactive capture
  branches can describe the reusable `Background` bucket before its active
  ForwardPass occurrence, so first-declaration order is not a semantic ordering
  guarantee.

The earlier experiment that disabled skybox depth testing was reverted; the
original `LessOrEqual`, depth-read-only behavior is correct once the draw is in
the intended pass order. Temporary shader and viewport diagnostics were also
removed.

Live validation used the exact VS Code Unit Testing World environment
(`XRE_WORLD_MODE=UnitTesting`, `--unit-testing`) with Vulkan validation and sync
validation enabled:

- startup at 1920x1080 visibly rendered the procedural sky:
  `Build/_AgentValidation/20260805-2158-phase40-vulkan-perf/20260810-cpu-below-1ms/mcp-captures/Screenshot_20260810_214429_123_4badd9001e344600988e8276ebf7c79c.png`;
- after maximizing to 2560x1369, the procedural sky remained visible:
  `Build/_AgentValidation/20260805-2158-phase40-vulkan-perf/20260810-cpu-below-1ms/mcp-captures/Screenshot_20260810_214449_358_72b2c5d541394848a16d3aaaa4450a90.png`;
- trace validation before removing diagnostics showed the ForwardPass clear and
  light-combine draw before `Background` on every warmed frame;
- no resize image-layout VUID or push-constant VUID remained. Sync validation
  still reports one separate startup `WRITE_AFTER_READ` hazard in the ImGui
  platform-window submission's acquire/layout-transition dependency; it is not
  in the main scene or skybox submission and did not recur on maximize.

`rdc doctor` passed, but the attempted RenderDoc child-process injection did not
produce a capture before timeout. MCP intermediate-texture captures and Vulkan
operation/barrier logs were sufficient to identify and verify the ordering bug.
The startup/maximize skybox regression is now resolved locally; user acceptance
is still pending.

## 2026-08-11 input-refresh and reported-FPS follow-up

The report that the debug FPS remained high while the scene appeared to update
only after mouse or keyboard input was not caused by a desktop Vulkan
render-on-demand gate. Live inspection showed both
`EditorFlyingCameraPawnComponent.RenderOnDemand == false` and
`XRViewport.Suppress3DSceneRendering == false`. The desktop callback continued
through acquire, record/reuse, submit, and present on every frame.

Two regressions made the behavior look demand-driven:

1. Commit `16047d7e4` applied the whole-frame resource-version signature to every
   command chain. That signature includes the visible operation stream, so
   ordinary camera visibility changes marked every otherwise stable secondary as
   `ResourcePlan` dirty. Command-chain schedule identity still needs the complete
   signature, but per-chain shared-resource invalidation now uses only the
   resource-allocation generation. Exact packet, descriptor, and prepared-key
   checks continue to reject a chain whose own native dependencies changed.
2. Commit `ec0efb261` moved clean-primary reuse ahead of current-frame mesh
   frame-data registration. `TryReuseCleanCommandChainPrimaryVariant` therefore
   consumed refresh requests left in thread-local recording scratch by the last
   fresh primary, potentially for a different acquired image slot. Those stale
   `PendingMeshDraw` snapshots contain the old camera matrices and auto-uniform
   view generation. Heavy fresh recordings published a current camera once, then
   many cheap UI/present frames reused the old scene data, producing the observed
   sample-and-hold behavior.

The primary fast path now rebuilds/registers the current static and dynamic
refresh cohort before attempting reuse. The cohort is stamped with frame-plan
generation, render-frame ID, and frame-data image index; the reuse function
rejects it unless all three match. Refresh writes use that frame-data image
index even when it differs from the acquired command-buffer image, while
artifact ownership remains keyed by the acquired image. The previously disconnected
`XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE=0` override once again gates the fast
path.

The debug overlay also averaged instantaneous `1 / delta` samples. That
arithmetic mean is biased high when a workload alternates long recording stalls
with short UI/present frames. It now reports `sample count / summed elapsed time`
over a fixed 60-frame duration ring, with no per-frame allocation.

Isolation followed the requested order:

- Sponza off and the directional light off: a post-fix three-second camera sweep
  advanced 237 render frames in 1.063 seconds (223.1 FPS), with a 4.324 ms worst
  sampled frame, no sample over 33.3 ms, and clean primary reuse.
- For visual acceptance without restoring the dense scene, the Sponza hierarchy
  remained disabled except for one leaf submesh used as a camera-motion marker;
  the directional light remained disabled. A four-second focus interpolation
  advanced 1,223 frames in 7.417 seconds of externally sampled wall time
  (164.9 FPS), with an 8.651 ms worst unique sampled frame, no frame over
  16.7 ms, clean primary reuse in all 100 unique samples, and visibly different
  intermediate images without mouse or keyboard input. The inspected images are
  the `121817`, `121818`, `121819`, and `121820` captures in the evidence folder.
- Directional light alone was previously about 116.8 FPS and amplified dirty
  work, but was not the base cause.
- Sponza alone reproduced the expensive path. Before the signature split,
  visibility changes produced up to roughly 850 operations and 500--650 ms
  frames while re-recording hundreds of stable secondaries. After the split,
  warmed sweeps reused up to 725 secondaries with zero secondary recordings.
- Sponza-only still has a separate CPU scaling problem. Current diagnostics show
  primary reuse rejecting a `leaf` draw because a mutable frame-source descriptor
  allocation reports a pool miss. That forces primary encoding/operation
  preparation back into roughly 50--370 ms frames even though the secondary
  command buffers themselves remain reusable. This does not reproduce in the
  requested stripped scene and should be addressed as the next dense-scene
  optimization rather than folded into the input-refresh correctness fix.
- A cleared final-presentation ledger can still report a missing descriptor
  observation on a valid reuse frame. The plan-owned frame-source fast path
  intentionally skips the redundant descriptor refresh/fingerprint walk, but
  that also skips the ledger observation. This is a diagnostic false positive,
  not evidence that the refreshed draw cohort is stale.

Four Sponza-only screenshots captured during one interpolated camera move show
the rendered viewpoint progressing without additional mouse input. They are in
`Build/_AgentValidation/vulkan-phase42-43-final-20260810/20260811-input-refresh/mcp-captures/`
with timestamps `120919`, `120920`, `120921`, and `120923`. Full-resolution
readback itself costs roughly 0.5 seconds and is not a cadence measurement.

The Vulkan leaf build and editor build pass with warnings treated as errors.
The live post-fix session reported zero Vulkan validation messages. No tests were
added or run while this runtime regression remains in live validation, per the
repository testing policy.

### Full-Sponza, no-directional-light scaling pass

The dense-scene follow-up used the requested isolation: the deferred Sponza
import was enabled, both directional lights were disabled, and editor-camera
render-on-demand remained disabled. It exposed one completion-authority bug and
one avoidable steady-state planning cost.

Desktop mesh frame-data slots are swapchain image slots, but
`CanUpdateCompletedDescriptorFrameSlot` had retained only the frame-in-flight
timeline test after `ec0efb261`. A descriptor slot could therefore be treated as
available according to the wrong completion domain. The command synchronization
state now carries a non-owning view of the desktop image timeline values. Frame
slots continue to use the frame-slot timeline, while desktop descriptor slots
use their acquired-image timeline; OpenXR keeps its existing external authority.

Stable primary reuse also still paid for sealing the complete frame plan before
discovering that the exact prior schedule and primary artifact were reusable.
The fast path now:

- captures presentation, target, resource, layout, clear, and policy authority
  before plan construction;
- computes semantic and resource/descriptor version signatures from the current
  raw operation streams;
- resolves an exact cached command-chain schedule identity;
- projects current live operations through the producer-to-sealed permutations
  captured by the last fresh primary;
- builds a current-frame, current-image frame-data refresh cohort; and
- reuses the primary before `FramePlan.BuildAndSeal` only when every exact check
  succeeds. Any mismatch falls back to the unchanged full planning/record path.

Dynamic UI initially prevented this path from succeeding because its secondary
recorder rejected an unsealed operation before checking whether the immutable
secondary already matched exactly. Exact secondary reuse is now checked first;
only actual encoding requires a sealed operation. A shared immutable per-view,
per-pass snapshot and persistent program-binding artifact reuse also remove
hundreds of repeated camera-matrix copies and binding-schema walks from the
Sponza draw loop.

Final live evidence from `vk-sponza-no-dir-final-20260811`:

- the focused exterior view contained 398 tracked renderables; all 725 scheduled
  chains were reused, zero chains were recorded, the primary was reused, and all
  835 mesh draws reused their program-binding artifact with zero builds;
- ten warmed samples had medians of 22.136 ms whole-frame, 13.224 ms frame-op
  preparation, 0.529 ms packet construction, 3.200 ms primary handling,
  2.307 ms frame-data manifest work, and 0.896 ms submission;
- after moving inside, a no-input interval advanced 176 actual render frames in
  3.134 seconds (56.1 Hz), while the output profiler reported 56.5 Hz. The scene
  output was `FreshRender`, `scene_rendered=true`, and `skipped=false`;
- Vulkan validation errors, descriptor binding failures, primary-reuse
  rejections, and dynamic-UI unsealed-operation rejections were all zero in the
  final profiler snapshot and session log.

The inspected exterior and interior images are:

- `Build/_AgentValidation/vulkan-phase42-43-final-20260810/20260811-descriptor-reuse-scaling/mcp-captures/Screenshot_20260811_151055_077_201e73d5c9a94a188b61963500b2492d.png`;
- `Build/_AgentValidation/vulkan-phase42-43-final-20260810/20260811-descriptor-reuse-scaling/mcp-captures/Screenshot_20260811_151138_810_fa8d573ea3a94fb6a4b1cc07f9e3fb17.png`.

They show radically different rendered viewpoints while the cached primary and
secondaries remain active, ruling out the prior sample-and-hold behavior. The
full-resolution screenshot readback is synchronous and briefly disturbs frame
cadence, so it was excluded from steady-state rate measurements. The final log
is under
`Build/_AgentValidation/mcp-sessions/vk-sponza-no-dir-final-20260811/logs/`.

Several narrower experiments were measured and reverted because they were
neutral or slower: batch pipeline/camera scopes, an all-direct-owner prepass,
reusing unpinned `MeshDrawOp` plan objects, and bulk draining the producer queue.
A global buffer-readiness epoch was also rejected before implementation because
dynamic buffers change every frame and would continuously invalidate it; missing
one asynchronous upload or delete transition would additionally make the cache
incorrect.

Packet sealing is no longer the dense-scene bottleneck. The remaining 10--13 ms
is genuine per-draw producer/materialization work needed to recreate the current
raw draw stream and its refresh cohort. Removing that cost requires the broader
persistent cached-producer-plan design already described in the Vulkan core
hardening work: producer-owned immutable draw artifacts with explicit dirty
records and frame-slot dynamic data, rather than another local reuse shortcut.
It is now isolated as follow-on scaling work, not an input-demand or stale-frame
correctness blocker. The Vulkan leaf build passes with warnings treated as
errors. No tests were added or run before user acceptance of this live fix, per
the repository testing policy.

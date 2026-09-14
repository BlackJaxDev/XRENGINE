# Mitsuki animated mesh index allocation investigation

## Problem and measured cause

The user reported roughly 3–4 GB of editor RAM while running animated Mitsuki
with Vulkan and AdvancedRenderPipeline, then requested a fix and avoidance of
array allocation / `.ToArray()` on hot paths.

The original screenshot's process exited. Read-only EventPipe samples were
collected from its replacement named session `vulkan-ownership-0914`, PID 42692,
on 2026-09-14. Configuration was one animated Mitsuki model, one looped native
animation, Vulkan, AdvancedRenderPipeline, CPU Direct, and desktop rendering.
A preceding renderer snapshot reported a 1200×800 display and 804×536 internal
extent. No settings or process lifecycle changes were made to that session.

- A 12-second System.Runtime counter sample showed managed heap growth from
  2,310 MB to 3,322 MB, with allocation rates of 296–627 MB/second.
- At the end of that sample the large-object heap was 2,695 MB.
- A later allocation capture attributed over 90% of sampled allocation traffic
  to `System.Int32[]`. These are sampled allocation estimates, not retained-object
  sizes or GPU memory totals.
- Sampled stacks identify both pre-collection and swap paths:
  `RenderableMesh.ApplyPendingRenderMatrixUpdates → RenderInfo.SwapBuffers →
  VisualScene3D.OnRenderableSwapBuffers → GPUScene.TryUpdateMeshCommand →
  ValidateMeshForGpu → XRMesh.GetIndices → SelectMany.ToArray`.
- `GetIndices(Triangles)` creates an `int[3]` per triangle and a flattened array.
  Validation invoked it even when the triangle list was already known nonempty.
- The heap later dropped from 4,611 MB to 2,736 MB after collection. This proves
  severe allocation churn; it does not establish that all baseline memory is a
  permanent leak. The other session stopped before a retained-object inventory.

Evidence: `Build/_AgentValidation/20260914-140540-mitsuki-memory/reports/before-*.json`.
Counter values use decimal MB as reported by System.Runtime.

## Fix

- Use existing `XRMesh.HasIndexData` for GPU mesh-validation presence checks.
- Use existing `XRMeshRenderer.TryGetMesh` in the swap/collect update path instead
  of allocating the `GetMeshes` compatibility tuple array.
- Replace five count-only index-flattening calls in meshlet payload validation,
  freshness checks, and import metadata with `Triangles?.Count ?? 0`.
- Document `GetIndices` as an allocating copy API and reinforce the repository
  rule against array materialization for hot-path inspection and iteration.

The constant-time validation guards remain cheap to repeat. No topology cache,
new invalidation state, dependency change, or test changes were introduced.
Actual index-consuming import/cook operations retain their existing behavior.

## Validation

The allocation fix is validated through the live editor. The complete Vulkan
renderer is not being declared visually correct by this investigation.

- Focused diff whitespace check passed.
- Independent read-only source review found no semantic regression in null
  renderer, primitive index, material override, or triangle-count behavior.
- Isolated session `mitsuki-memory-0914`, PID 31108, built successfully with
  **0 warnings and 0 errors** in 1:54.76. Only this investigation's session was
  started and stopped; the session manager recorded `Stopped` at 14:22:44 PDT.
- Mitsuki loaded with the humanoid animation state machine and IK solver.
  `log_animation.log` recorded first native playback at 14:11:41 PDT.
- The renderer snapshot reported Vulkan with requested/effective `CpuDirect`,
  AdvancedRenderPipeline, 68 tracked renderables, 64 GPU scene commands, and
  48 opaque commands flagged as skinned. The snapshot's Vulkan validation
  message and error counts were zero.
- An initial 12-second post-change counter sample allocated 31.6–72.2 MB/s
  (mean 36.5 MB/s). The focused 10-second allocation capture measured
  **20.4–32.6 MB/s, mean 29.3 MB/s**, compared with the baseline mean 550.1 MB/s.
- The two 10-second type samples attributed 4,063,688,144 bytes before and
  16,006,056 bytes after to `System.Int32[]`: **99.6% less sampled integer-array
  traffic**. These are weighted allocation samples, not exact object counts.
- The runs used different display/internal extents: baseline 1200×800/804×536,
  fixed build 1920×1080/1286×723. They also differed by two scene commands.
  This is evidence that the identified churn was removed, not a controlled
  frame-rate benchmark or an exact before/after Task Manager RAM comparison.
- Captures from multiple camera positions changed with the view. With a
  temporary directional light added only to this isolated runtime, the full
  character was visible in an animated walk pose. The saved world has skybox
  and directional lights disabled. Colors remained abnormal; material and
  shading correctness remain outside the allocation result.
- A later interactive-resize recording at 14:21:20 PDT rejected one desktop
  frame with `Advanced visibility raster has no accepted-plan picking
  authority`. The stack was in primary command recording via the interactive
  resize path; subsequent frames resumed. This preceded the stop request and
  is a runtime issue, not shutdown-only teardown noise. No claim is made that
  this allocation patch fixes it.
- No tests were added, changed, or run. The targeted full editor build, live
  animation path, counters, allocation samples, and source review provide the
  validation required for this fix without altering tests during feature
  validation.

The visible-character capture is
`mcp-captures/Screenshot_20260914_141938_829_5ad69e2e54d5427da9c811abe3812ba6.png`
under the evidence root. Reports include `allocation-comparison.json`,
`after-counters-initial.json`, `after-allocation-types.json`, and
`after-large-heap.json`; build and runtime logs are copied into `logs/`.

## Initial remaining memory footprint

Removing temporary index arrays does not eliminate the renderer's large baseline
capacity. A later process snapshot still had a 3,178,573,824-byte working set and
7,141,527,552 private bytes. These counters measure different things; neither
should be equated to live managed objects or GPU memory.

An unsuspended CLRMD walk of the large and pinned object heaps covered 59
segments and about 2.06 GB. It included approximately 128 MB of free space and
may include uncollected garbage. It was not a GC-root analysis, so it does not
prove that all objects below are retained or that the renderer needs this much
live storage.

| Large object type | Sampled bytes (decimal MB) |
| --- | ---: |
| VulkanAdvancedVisibilityOperationPayload arrays | 281.2 |
| VulkanPreparedStableBinRecord arrays | 199.2 |
| VulkanPreparedStableBinHeader arrays | 168.7 |
| Vulkan stable-bin manifest dictionary entries | 160.2 |
| FrameOpContext arrays | 140.1 |
| VulkanAdvancedScenePublicationUse arrays | 115.3 |
| FrameOpResourceUse arrays | 77.1 |
| MeshDrawPayload arrays | 71.2 |
| Int32 arrays | 11.1 |

Source inspection maps the dominant types to eager capacities that are reused:

- `FrameOperationPayloads` initializes payload columns at general capacity.
- `FrameOperationStream` owns fixed-capacity frame-operation contexts.
- `VulkanPreparedStableBinStream` allocates record, header, scratch, and
  submission-plan arrays up front.
- `VulkanStableBinManifestCache.Clear` retains dictionary backing capacity.
- `VulkanPreparedFrameRecording` and
  `VulkanResidentTemplateFrameSlotLifetimes` reserve publication-use arrays at
  the mesh request queue capacity.

Those capacities are a separate optimization target. A follow-up should measure
actual high-water usage and object roots across frame slots, then size or grow
storage outside hot paths while preserving Vulkan publication and frame lifetime
contracts. This patch does not change those ownership rules or hide the Vulkan
path behind a fallback. Smaller remaining allocations include transform child
recalculation tasks and task arrays; the measured fix is not a claim of zero
per-frame allocation throughout the engine.

## Retained-memory follow-up

The user requested further investigation to minimize RAM. The existing isolated
build was restarted with `-NoBuild` as PID 43680, preserving the binary that
validated the earlier index-allocation fix. Passive CLRMD samples at 14:34:47
and 14:35:39 PDT measured actual cache/table occupancy. The sampler did not
suspend the process, force collection, create a dump, or change engine state.
The named session was stopped at 14:36:45 PDT.

### Confirmed cache growth and fix

The same `VulkanStableBinManifestCache` retained 131,908 entries, then 179,781
entries 52.57 seconds later, while topology generation remained 49. Its backing
entry array grew from 160.2 MB to 332.2 MB. This is approximately 911 additional
entries per second for a 64-command scene. This corrects the earlier tentative
interpretation of that dictionary as simply a large retained capacity.

Canonical visibility keys include the current scene/vertex/index generation and
range. Those execution identities can change without topology changing, while
the global cache invalidates only on topology changes. Every miss allocated an
aggregate manifest and stored another full execution key.

Canonical visibility bins now aggregate into preallocated frame-stream-owned
dependency/native-use slabs. Each header has a reusable manifest view. Stream
copies copy the aggregate into the destination's storage and rebind both the
header and sealed submission plan. Reuse follows the existing stream thaw and
completion boundary. Immutable resident-template manifests keep their existing
cold cache path. Exact generation, range, queue, layout, stride, and access
checks remain; no per-frame cache clearing or array materialization was added.

After this fix, the canonical scene left the global cache at **zero entries and
zero backing capacity**. The final build remained at zero in samples taken at
14:55:16 and 14:58:58 PDT, 222 seconds apart, while animation and camera changes
continued. The intermediate build also remained at zero across 117 seconds.

### Fixed storage reductions

Two additional changes reduce storage without lowering active scene limits:

- Native compatibility keys retain only the exact fields used by equality and
  hashing, instead of carrying the full native state and `PendingMeshDraw`.
  Existing immutable backing storage supplies uncommon larger vertex-binding
  sequences; constructing or comparing keys does not copy arrays.
- UI/upload streams with both Advanced visibility budgets zero now use shared
  empty Advanced payload/closure arrays. Main-scene streams retain their full
  existing capacity, including when the scene is temporarily empty.

The baseline and final CLRMD samples measured these array totals (decimal MB):

| Storage | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| Stable-bin record arrays | 199.230 | 150.668 | 48.562 |
| Stable-bin header arrays | 168.723 | 120.161 | 48.562 |
| Advanced operation payload arrays | 281.150 | 204.473 | 76.678 |
| **Measured array reduction** | | | **173.802** |

The compact key is 624 bytes smaller. Its copy in each of the 77,824 preallocated
sealed submission plans saves another **48.562 MB**, calculated from the layout
change and observed object count; this is not a separate before/after retained
heap measurement. Final sealed-plan objects total 47.940 MB.

The bounded replacement manifest storage costs **19.613 MB** across the 19
streams: 2.802 MB of dependency slabs, 11.207 MB of native-use slabs, 0.623 MB of
view-reference arrays, and 4.981 MB of view objects. Thus the fixed-storage
reduction is approximately **202.75 MB net**, excluding smaller closure savings
and the eliminated growing dictionary and its aggregate manifests. Avoided cache
storage is additional and depends on elapsed runtime; its entry array alone was
332.236 MB at the second baseline sample.

### Live validation and limits

- Both isolated editor builds succeeded with **0 warnings and 0 errors**: the
  manifest/key changes in 45.09 seconds, and the final inactive-lane reduction
  in 22.43 seconds. No tests were added, modified, or run under the repository's
  feature-validation policy.
- Independent read-only review of manifest lifetime/copying and key semantics
  found no blocking defect. Broker runs used requested and actual
  `gpt-5.6-sol` with matching model identity. The coordinator checked the
  remaining constructor, reuse, and inactive-lane call sites locally.
- Final PID 57704 ran from 14:53:06 until the owned session manager recorded
  `Stopped` at 15:04:55 PDT. Final profiler read-back reported
  AdvancedRenderPipeline, 64 GPU scene commands, 68 tracked renderables,
  scene/output rendered, and zero Vulkan validation messages/errors. A
  temporary directional light increased frame operations from 37 to 50.
- A final 12-second counter sample measured **32.5-35.6 MB/s allocations, mean
  34.0 MB/s**; managed heap was 2,093-2,157 MB and GC committed storage was
  2,294-2,297 MB. The remaining per-frame allocation rate is a separate target;
  these storage fixes do not claim zero engine-wide allocation.
- Working-set snapshots ranged from 4.118 GB at 14:55:16 to 1.641 GB at 14:58:58;
  the final counter window was 1.884-1.886 GB. Private bytes remained roughly
  5.1-5.4 GB. Collection, paging, workload changes, and system memory pressure
  make this unsuitable as a controlled Task Manager before/after comparison.
  The cache occupancy and table/layout reductions are the stronger evidence.
- Front and side captures were actually inspected and showed coherent Mitsuki
  geometry in an animated walk pose. The previously observed red/blue/dark
  shading remained. The temporary light and camera changes were runtime-only;
  saved settings were not changed.
- Logs contain the existing accepted-plan picking-authority rejection at
  14:57:15, plus transient publication rejections at startup and light creation.
  The newly created light briefly attempted shadows before its shadow setting
  was disabled and logged a failed shadow-atlas render. Later scene rendering
  resumed. No Vulkan VUID or manifest overflow/generation-conflict messages
  were found. These observations do not validate the complete renderer or
  establish that the pre-existing visual/publication issues are fixed.
- An additional duplicate-character validation attempt failed during cloning:
  `IKSolverLimb+AxisDirection` has no default constructor/type converter for
  deserialization. The duplicate was not inserted, and the original scene
  still rendered with 64 commands afterward. Multi-instance validation through
  this tool therefore remains incomplete; the unrelated serializer was not
  changed.

Evidence under `Build/_AgentValidation/20260914-140540-mitsuki-memory/`:

- `reports/capacity-tables-{1,2}.json`: baseline occupancy/layout samples.
- `reports/capacity-after-tables-{1,2}.json`: manifest/key changes.
- `reports/capacity-final-tables-{1,2}.json` and
  `reports/capacity-final-counters.json`: all three changes.
- `mcp-output/capacity-final-profiler.json`: final scene/output read-back.
- `logs/capacity-build-1.log`, `logs/capacity-build-final.log`, and
  `logs/capacity-{baseline,fixed-1,final}/`: build and runtime evidence.
- `mcp-captures/Screenshot_20260914_145723_981_c4947acdc65e41b289b7e2a1439bd6c4.png`
  and `mcp-captures/Screenshot_20260914_145851_033_01e3735b177a42e1a481db422a173d68.png`:
  inspected front and side views.

### Targets identified before the second pass

1. **Provision only required desktop/XR views and frame slots.** The builder
   owns four primary slots and four spare/retired slots. Only primary slots 0
   and 1 had published a plan in the sample; the other six had zero plan
   generations. All eight plans also own two logical OpenXR header streams,
   even for this desktop run. Those sixteen unused logical views reserve
   about 123.5 MB across contexts, headers, resource uses, and targets. This
   overlaps the other totals and must not be added to them. Provisioning must
   follow the device/view and retirement contracts at a cold boundary.
2. **Separate publication count from mesh request capacity.** Ten native
   publication-use arrays reserve 4,096 entries of 2,816 bytes, totaling
   115.3 MB. The sampled lifetime slots used zero or one publication. A
   dedicated publication budget can avoid reserving one large publication
   record for every possible mesh without weakening lifetime validation.
3. **Use explicit per-opcode payload budgets and compact contexts.** Main-scene
   Advanced payload arrays still occupy 204.5 MB. Frame-operation context
   arrays occupy 140.4 MB; static streams reserve 12,800 contexts for only
   37 baseline operations. Most stable-bin capacity also remains unused:
   19 streams reserve 4,096 records each, while only two held 49 records at
   the baseline sample. Size from measured high-water usage at provisioning
   boundaries, with explicit growth/failure behavior for larger scenes.
4. **Continue allocation and asset-root profiling.** The final scene still
   allocates about 34 MB/s. Earlier evidence identified transform child
   recalculation tasks/task arrays. A full-heap inventory also found many
   per-vertex skin-weight dictionaries, but it did not trace GC roots or
   establish that every observed object remains live. Measure ownership and
   necessity before discarding CPU asset data needed by editor workflows.

Arbitrarily shrinking global constants could reject valid larger scenes and is
not a solution. Further changes should preserve allocation-free frame execution,
exact native generation checks, and explicit capacity/retirement contracts.

## Second storage reduction pass

The user asked to continue the remaining targets. Three additional changes were
implemented without reducing the admitted desktop scene/request limits:

- Logical OpenXR views now own header storage only and reference their physical
  stream's contexts, resource uses, targets, and payloads. Headers retain the
  original indices. The source is frozen before publication and existing plan
  leases prevent reset while a view is in use. Logical views cannot append,
  reorder, lower, or provision independent physical columns.
- The frame-plan builder provisions the actual primary frame-slot count. This
  desktop configuration now owns two primary slots plus the existing four
  retirement/spare slots, instead of four primary slots plus four spares. The
  outer reference table still supports all eight frame-data indices. OpenXR
  eye/mirror activation provisions its extra indices before plan preparation;
  already activated Advanced family banks are provisioned before publishing the
  new slot count. No live leased plan is replaced by this cold activation.
- Canonical raw publication leases keep their 4,096-entry limit. Their much
  larger native receipt records now have a separate 256-entry pool, matching
  the native publication runtime's existing per-slot limit, plus an empty
  sentinel. Preallocated integer maps associate raw leases with native receipts.
  Reset, deduplication, preflight, transfer, and release preserve both ownership
  domains. Raw leases still survive when the optional native receipt cannot be
  obtained; shrinking both arrays to 256 would incorrectly reject that path.

The measured arrays below compare `capacity-final-tables-2.json` with
`remaining-targets-tables-1.json` from the same Vulkan/Advanced/CpuDirect desktop
scene. Values are decimal MB; these are array inventories, not GC-root proofs.

| Array storage | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| Advanced operation payloads | 204.473 | 153.354 | 51.118 |
| Frame contexts | 140.366 | 43.854 | 96.513 |
| Frame headers | 25.121 | 13.314 | 11.807 |
| Frame resource uses | 77.322 | 41.365 | 35.957 |
| Native publication receipts | 115.344 | 7.237 | 108.106 |
| Stable-bin records | 150.668 | 118.948 | 31.720 |
| Stable-bin headers | 120.161 | 94.864 | 25.297 |
| **Measured selected arrays** | | | **360.517** |

The ten new raw-to-native maps add about 0.164 MB, so these selected storage
changes save approximately 360.35 MB net before other smaller savings. This is
additional to the earlier approximately 202.75 MB fixed-storage reduction and
the eliminated growing manifest dictionary. The same types must not be counted
again as a separate overall process-memory reduction.

PID 52612 reported two active primary plans and four unused spare plans; each
logical view held only its own headers. Native receipt arrays had 257 entries,
while raw publication arrays retained 4,096. The manifest dictionary remained
at zero count/capacity in samples 109 seconds apart. Working set changed from
3.647 GB to 2.625 GB while private bytes remained about 4.50 GB, illustrating
why process counters are weaker evidence than the measured table changes.

The storage build and the subsequent native-transfer preflight correction built
with zero warnings/errors in 42.05 and 22.30 seconds. PID 11552 subsequently
reported a completed frame, 64 scene commands, 68 tracked renderables, and zero
dropped frame operations. Independent source review checked shared-view
lifetime, late XR slot provisioning, and native publication receipt transfer.
The preflight now deduplicates incoming identities against both the destination
and earlier entries in the same source transfer. Broker review used matching
requested/actual `gpt-5.6-sol` models. A headset and near-capacity transfer stress
were not exercised; no tests were added, edited, or run.

### Transform allocation follow-up

A fresh 10-second sample from PID 11552 measured 32.45-35.50 MB/s allocations,
mean 34.04 MB/s, including 26.24 MB of weighted `Task[]` allocation samples.
`ChildrenRecalcAsync` and `AsyncChildrenRenderMatrixRecalc` allocated a task
array for every child traversal. They now rent task arrays only for branching
nodes, await the used span, and return the cleared rental after completion.
Zero-child traversals return immediately and one-child traversals await directly.
Existing child-snapshot ownership and update/render matrix semantics remain.

The transform build succeeded with zero warnings/errors in 36.22 seconds.
Its first allocation sample (`transform-after-allocations.json`, PID 8008) is
**not a valid performance comparison**: the user triggered a selection-related
terminal render failure before sampling, so the scene was no longer submitting
GPU frames. It is retained only as evidence of the failed measurement.

A fresh PID 17296 sample verified successful GPU frame progression immediately
before and after the 10-second trace (frames 218 to 1319). Weighted `Task[]`
allocation samples fell from 26,237,880 bytes (246 ticks) to 426,400 bytes (4 ticks),
a 98.4% reduction. `ChildrenRecalcAsync` state-machine samples remained similar
at 54.81 MB before and 53.98 MB after. Total allocation counters averaged
31.58 MB/s (30.72-32.27 MB/s), compared with 34.04 MB/s before. Scene command
counts differed during the sample, so this is not a controlled FPS benchmark
or a precise attribution of the overall allocation-rate change.

Final passive table inventories on PID 17296, 270 seconds apart, retained six
physical builders, 7.24 MB of native receipt arrays, 153.35 MB of Advanced payload
arrays, 43.85 MB of context arrays, and a manifest dictionary count/capacity of
zero. Working-set variation remained much larger than the table changes; the
selected-array inventory is the basis for the reported fixed-storage saving.

### Selection-triggered terminal failure

During PID 8008, the user reported that selecting the Body scene node froze the
renderer. Update/collection continued and MCP remained responsive, but the last
successful GPU scene frame was 565. At 15:40:16 PDT frame 566 latched a terminal
`PackageExceptionUnmatchedHandle` during PresentNow frame-plan sealing. The
missing draw was handle `(1, 1)`, pass 1, with flags `0x100106` and compatibility
reason `None`. Capacities were available and the device was not lost.

Selection enables `RenderableMesh`'s stencil override and `ForceCpuRendering`.
That produces a `CpuFallbackOnly` canonical submission and an ordered exception.
The Advanced opaque command chain emits native stages but has no corresponding
ordered mesh emission for this case. Passive CLRMD evidence captured the retained
13-entry ingress: all entries were fullscreen/composition work, with no
canonical handles, no transform IDs, and no stencil test. The Body draw was
absent; the mismatch was not an observed handle-generation substitution.

The pipeline's terminal check is correctly exposing missing required work. The
fix must supply the selected geometry/highlight through a coherent native and
ordered contract; suppressing the exception or disabling selection would conceal
the defect. Screenshot capture timed out after 15 seconds while terminal. Only
the owned named session was stopped after evidence capture.

A second live run independently reproduced the same failure with MCP selection
of the exact Body node. PID 17296 rendered successfully through frame 9675,
then latched the same unmatched handle at frame 9676. The retained ingress had
26 entries after adding a runtime-only diagnostic light. Rendering remained
rejected at frame 11016 while update/collection continued. This confirms that
the selection failure is reproducible separately from the memory measurement.

The implemented fix separates editor highlight bits from genuine CPU ownership.
`RenderableMesh` publishes hover/selection bits without changing
`ForceCpuRendering`. Traditional GPU routing still uses the ordered stencil
path for these bits. Advanced visibility reads the canonical editor identity
and writes the bits into the existing metadata target (bits 25-26). Advanced
post-processing merges those bits with legacy stencil highlights, treating the
invalid/background metadata sentinel as zero. Selection retains native skinning
and shading without another render target or another raster of the selected
mesh. Genuine CPU requests and the unmatched-required-work check remain active.

Highlight flags are content updates, masked out of structural draw/geometry/
render-state signatures so pointer movement does not tombstone geometry or
replace draw handles. Publication captures both bits once and seals them into
immutable frame data; an unnecessary second render-side property snapshot was
removed during review. The shader variant cache uses a static factory with
explicit state, avoiding a captured delegate allocation on every cache hit.

The first editor build passed with zero warnings/errors in 91.93 seconds. Its
live PID 37068 rendered with Body selected and later Shirt selected through
frame 4811 without a terminal failure. Source review then found a Vulkan sampler
order mismatch: metadata must be declared after the existing samplers to match
its binding at mono slot 7 / stereo slot 5. That correction was applied before
accepting visual validation. The second build passed with zero warnings/errors
in 80.89 seconds.

PID 40168 verified completed frame progression after the sampler correction.
Body selection produced an outline; `clear_selection` read back an empty
selection, and a later captured frame showed both outline and transform gizmo
removed. GPU frame 2724 completed with no terminal failure or dropped frame
operations. Switching to Shirt and back to Body completed frames 4638 and 5569.
The captures visibly moved the outline to the selected mesh while skinning and
animation continued. Both camera angles were inspected; the dark/red/blue scene
shading was already present in the baseline and is not resolved by this fix.
Two passive inventories 175 seconds apart retained six physical builders,
153.35 MB of Advanced payload arrays, 43.85 MB of context arrays, 7.24 MB of native
receipt arrays, and a zero-count, zero-capacity manifest dictionary.

Final source review also required the new GLSL sampler alias to participate in
both Vulkan frame-source classifiers, so resource-generation changes rebase it
through its captured logical texture name. Traditional diagnostic passthrough
and CPU recovery now apply the same ownership mask as frustum/BVH/SoA culling;
excluded commands are nonfatal and cannot be restored into GPU work after a
legitimate empty result. The existing zero-readback mask remains zero. The
modified copy shader passed the installed `glslangValidator` syntax check.
The final source review approved these changes. The final isolated editor build
passed with zero warnings/errors in 85.34 seconds. No tests were added, modified,
or run; the changed compute shader received a syntax check and the Vulkan
Advanced scene received the live validation described here.

On final PID 50704, selecting Body and changing the camera's TSR scale to 0.75
recreated metadata from 1286x723 to 1440x810 and advanced resource generation 2
to 3. An initial deferred frame was followed by completed frame 1140 with no
terminal failure or dropped operations. A captured frame retained the animated
Body outline at the new resolution. Restoring the original scale and clearing
selection advanced generation to 4 and completed frame 2621; the final capture
showed the outline and gizmo removed. This exercises desktop texture rebinding
across actual resource-generation changes. Stereo/headset rendering and live
traditional diagnostic fallback were not exercised.

All validation editor processes were started and stopped through the one owned
`mitsuki-memory-0914` session. Other editor processes were not restarted; an
already-running editor must be rebuilt/restarted to load the corrected binaries.

Evidence under the same task root includes:

- `reports/remaining-targets-array-comparison.json` and
  `reports/remaining-targets-tables-{1,2}.json` for the storage comparison.
- `reports/transform-before-allocations.json`,
  `reports/transform-after-active-allocations.json`, and
  `reports/transform-task-array-comparison.json` for the valid task-array trace.
- `reports/body-freeze-ingress.json`, `logs/body-selection-freeze/`, and
  `logs/transform-active-and-selection-repro/` for the failing selections.
- `mcp-output/body-highlight-final-*.json`,
  `reports/body-highlight-final-tables-{1,2}.json`, and
  `logs/body-highlight-build-{1,2}/` for repeated selection/clearing and memory.
- `mcp-output/body-highlight-{before-scale,scaled,restored}-resources.json`,
  `mcp-output/body-highlight-build-final-profiler.json`,
  `logs/body-highlight-build-final.log`, and `logs/body-highlight-build-final/`
  for final build and render-scale validation.

Captures saved under `mcp-captures/` were actually inspected. Selected Body,
selected Shirt, cleared selection, and the render-scale transition were checked;
no claim of corrected baseline scene lighting is made.

### Validation-layer qualification

All isolated runs in this investigation used the `RenderDocFriendly` diagnostic
preset, whose logs explicitly report `ValidationLayers=False`. Earlier zero
Vulkan error/message counts in this note are only reported telemetry counts;
they are **not** a successful Vulkan validation-layer run. Builds, live scene
submission, allocation sampling, and source ownership review are the validation
performed here.

### Further memory opportunities

Per-opcode capacity budgets and more compact physical contexts remain candidates
once high-water needs are measured across scene sizes. Main-scene payload and
stable-bin capacities still dominate the reduced footprint. Transform async
state machines and other rendering allocations also remain. CPU asset/skin
weight retention requires a GC-root investigation before releasing editor data.

## User feedback

The user requested this fix after the measured diagnosis. User confirmation of
the implemented result is pending.

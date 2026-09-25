# Avatar scene publication stalls

## Report and objective

The user reports multi-second or indefinite editor freezes when an imported FBX
avatar enters the scene. Import completion, hierarchy publication, and first GPU
use must keep editor interaction responsive. A background importer alone does
not establish that the scene publication and first render are bounded.

## Evidence

The isolated `xr-runtime-choice` editor session with process ID 66812 exposed
both import-time work and an overlapping OpenXR renderer replacement failure:

- At 22:18:56, the update profiler attributed 204.304 ms to
  `RuntimeWorldHost.ProcessDirtyTransforms`; total sampled update work was
  234.783 ms. Parent and descendant dirty entries may repeatedly traverse the
  same imported hierarchy. Subsequent render-matrix diagnostics reported 390,165
  applications. These counters are separate from the timed 204 ms sample.
- The importer spent substantial time in mesh conversion on a background job.
  That duration alone does not explain a blocked editor; scene publication and
  render-thread work require separate timing.
- Import completion reapplies all editor render preferences. That path scheduled
  three identical mesh-bound traversals and two pipeline preference applications.
  The grouped conditions now schedule each operation once.
- At 22:19:01, visibility collection created a shadow atlas texture while its
  window's renderer was retired. Eager wrapper publication threw from
  `GetOrCreateAPIRenderObject`, which terminates the visibility loop. This is an
  indefinite-freeze cause during overlapping renderer replacement, separate from
  the transform spike. Optional logical-object wrapper publication now needs to
  skip retirement atomically; strict direct renderer use must still reject it.

## Validation in progress

The next isolated run (process 35636) completed the same avatar import and showed
the avatar from two camera positions. Dirty-transform batching now claims each
entry once and removes descendant roots whose ancestors are in the same batch.
The largest sampled transform pass fell from 204.304 ms to 15.392 ms; ordinary
samples were approximately 3–4 ms. This is an improvement, not evidence of a
frame-drop-free handoff.

Remaining measurements and changes:

- The first geometry frame reported 274.2 ms of Vulkan resource preparation.
  Foreground mesh preparation bypassed the existing cold-work slice, and an
  individual pending request could retry synchronously until a 30-second watchdog.
  Work is being bounded across frames while retaining completed preparations.
- A 3,482.6 ms no-completed-render interval must **not** be attributed to the
  pipeline rebuild named in the watchdog snapshot. The detailed job log measured
  that rebuild at 5.88 ms after a 2,230.5 ms queue wait. The snapshot happened just
  after rendering resumed and does not identify the preceding wait's owner.
- Steady visibility work remained approximately 85–100 ms in sampled frames,
  including 25–31 ms of canonical scene publication. Added nested timings separate
  scene planning, capacity checks, resource preflight, mutation, and publication.
- Native events shared the render thread and were blocked by an unbounded wait
  for visibility. The collapsed editor now returns to its host event pump after
  an 8 ms publication wait. Timeout does not consume visibility, release the
  collector, or render stale scene commands. This keeps native event processing
  alive; it does not itself redraw ImGui while scene work is pending.
- Quadtree removal stopped searching after the first child that did not contain
  an item. The traversal now checks subsequent children before reporting failure.
- Canonical scene planning revalidated every triangle index on every frame.
  An active registration now reuses validation only when its mesh identity,
  geometry revision, topology, and source counts still match. New or changed
  geometry and actual geometry appends keep full validation. This follows the
  existing `MarkGeometryChanged` requirement for in-place mesh edits.

The same process exercised Monado and SteamVR single-pass sessions, submitting
287 and 337 sampled frames with zero end-frame failures. Both stops restored the
same desktop pawn, and the desktop scene remained visible. A separate shadow
publication capacity rejection persisted after repeated runtime changes; stable
shadow payload updates and retained publication lifetimes are being investigated.

## Sampled stack diagnosis

The next build passed with zero warnings/errors, but process 53716 still showed
a 9,508.7 ms no-completed-render interval during `Advanced.VisibilityPreparation`.
The cold mesh admission budget had already yielded before this stall. Texture
upload coroutines waited approximately 9,453 ms behind the blocked render thread;
the queue delay was a consequence, not evidence that texture uploading caused it.

A 45-second sampled-thread-time trace of process 41476 attributed approximately
7,275 ms on the render thread to `AdvancedGpuDeformationResources.TryGetOrAddMesh`,
including 5,701 ms in `TryGetBlendshapeData`. Count and Pack both visited each
canonical blendshape for every vertex, with a linear per-vertex name search when
the list order differed. This multiplied the shape-search cost twice during the
first deformation publication.

The replacement builds reusable per-vertex source indices once for each cold mesh,
preserving direct-position precedence and first matching-name fallback, then uses
constant-time lookup during Count and Pack. It reduces repeated name search from
O(vertices × canonical shapes × source shapes) to O(vertices × total shapes),
where total shapes includes both source and canonical names. The scratch table
retains four bytes per cell, rounded to its
growth capacity; this is a memory tradeoff, not a claim of small fixed storage.
The next isolated build passed with zero warnings/errors. Process 50428 reduced
the no-completed-render interval to 5,841.6 ms, which remains unacceptable for an
interactive handoff. Its sampled render-thread stacks attributed approximately
5,597 ms to cold deformation preparation: 1,822 ms in Count, 2,350 ms in Pack,
778 ms in canonical vertex packing, and 327 ms building source indices. These
are inclusive sampled totals, not independently additive frame measurements.

The next change makes cold preparation resumable under one frame budget. Pending
CPU payloads must remain private until complete, source revisions must match
across yields, and a pending mesh must defer preparation before any deformation
job or static generation is published. Returning the existing boolean failure
alone would incorrectly classify pending work as unavailable deformation.

The same trace separately attributed approximately 923 ms to first canonical
geometry registration and 1,019 ms to legacy atlas insertion on the collect
thread. These totals cover the tracing interval and are not individual-frame
latency measurements. Frame-drop-free scene attachment is not yet established.

Use an isolated editor with the same avatar and rendering settings. First measure
startup import and scene insertion without a runtime toggle, then validate the
OpenXR transition overlap. Compare update/render timings and view screenshots
after publication. Do not infer responsiveness from successful import callbacks
or runtime frame submission alone.

No tests have been added or modified during this live regression investigation.

## Incremental preparation validation

Cold CPU preparation now retains private scratch state across a shared 4 ms
render-frame budget. Indexing, counting, packing, vertex conversion, and influence
reads advance without publishing partial deformation jobs. Static append and
native allocation remain indivisible; this is not a hard bound on total frame
time. Unsupported source witnesses are cached per mesh to avoid restarting
unavailable preparation on every frame.

Process 48540 exposed a count/pack predicate mismatch on non-finite blendshape
deltas: Count used `lengthSquared > epsilon`, while Pack skipped only
`lengthSquared <= epsilon`. NaN satisfies neither predicate, so Pack could append
uncounted values and throw `IndexOutOfRangeException`. Repeated attempts then
reported a secondary frame-upload slot deferral. Both append paths now use the
same positive predicate as Count. Arena ownership was not relaxed.

The next build passed with zero warnings/errors. Process 62856 packed all 291,285
records for `Milltina_body` and advanced to subsequent meshes without that
exception. Full scene publication and remaining load latency still require
validation. At a low render cadence, a 4 ms frame budget can stretch several
seconds of CPU preparation into minutes of wall time. The next scoped approach
is to prepare immutable CPU payloads while FBX workers still exclusively own
their meshes, before scene attachment. Reading mutable live vertices on an
arbitrary background task is not safe.

Native FBX workers now prepare a write-once CPU payload after final mesh splitting
and before scene attachment. A weak mesh-keyed cache retains packed vertex,
blendshape range, sparse record, and delta arrays under source revision/reference
witnesses. Renderer recreation reuses those arrays; unsafe skinning reads and
static GPU append still run on the render owner. Other source/cache paths retain
the sliced preparation behavior. This adds retained CPU memory: 3.2 million
16-byte sparse records alone use approximately 51 MB, before delta/vertex arrays.
The dense name lookup scratch is released after packing.

The build passed with zero warnings/errors. Process 18180's native import took
28.131 seconds versus 24.702 seconds previously. The complete desktop publication
became Ready within 38 seconds of import completion, with 109 draws, 103
deformation jobs, one dispatch, and 1,762,654 deformed vertices. The previous run
took approximately 4 minutes 42 seconds after import. After switching to Monado,
the new run became Ready again without repeating Index/Count/Pack work. Logged
pending work was limited to owner-thread influence copying, with no recurring
packing exception or frame-upload slot failure. These measurements establish a
reduction in preparation latency, not instant loading or a frame-drop-free path.

## Continuation

The user requested a wrap-up on 2026-09-25. Remaining profiling, cache-memory,
owner-thread work, and desktop/XR acceptance checks are listed in
[Editor OpenXR toggle, rendering, and import responsiveness](../../todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md).
Instant appearance and zero frame drops remain unverified requirements.

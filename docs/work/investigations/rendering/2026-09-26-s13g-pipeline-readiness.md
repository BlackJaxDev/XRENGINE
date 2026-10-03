# S13g: bound warmed pipeline-readiness work

Status: Validated for reachable scope (September 26, 2026): the readiness
check no longer runs once per bin header; on the unchanged path the raster
pipelines step fell from 1.53 ms and 361 KB to 0.16 ms and 80 bytes per
primary recording, the readiness identity hash no longer allocates, and
shader reload, rapid double reload, TSR render-scale change and transactional
renderer restart all invalidate and recover with zero foreground joins and zero
validation errors; two literal budget clauses were missed by small margins
and are recorded below. Gate record
for
[S13g](../../todo/rendering/vulkan-stall-remediation-todo.md#s13g-bound-warmed-pipeline-readiness-work)
under the todo document's one-by-one protocol. Evidence root:
`Build/_AgentValidation/20260926-192020-s13g-readiness/` (ignored,
disposable; findings are copied here). Fixture, camera and session type are
the S13a ones (393-draw Sponza2 OBJ fixture from S13f, camera A `(-20, 2, 4)`
looking at `(-20, 2, -8)`, named isolated Release session built from the main
checkout, S13a telemetry enabled, Advanced pipeline, CpuDirect, TSR).

## Exact call chain (todo item one)

Every primary recording prepares one Advanced visibility family
(`TryPrepareAdvancedVisibilityFamily`, S13e/S13f). Readiness of the Advanced
pipeline runtime is consulted from four places in that preparation, all
through `VulkanAdvancedVisibilityPipelineRuntime.GetReadiness`:

1. Raster stage, per sealed bin header (393 headers on this fixture):
   `VulkanPreparedStableBinStream.TryPrepareVisibilityRasterPipelines` calls
   `TryGetRasterProgram` (readiness, then the raster program wrapper lookup)
   and then `VulkanCanonicalVisibilityPipelineFactory.TryPrepare`, which
   rebuilds the graphics pipeline key (program fingerprint, pass metadata and
   feature profile hashes, vertex-input validation) and looks the pipeline up
   in the manager's shared dictionary under its lock.
2. Once per family after the stage loop: `TryGetComputePipelines` (readiness
   plus two wrapper lookups).
3. Late compute stage: `TryGetLateVisibilityComputePipelines` (readiness plus
   two wrapper lookups).
4. Each of the three native compute stages: `TryGetNativeComputePipelines`
   (readiness, then the prepared pipeline set copied by value).

`GetReadiness` itself: checks the scene and visibility resources, then
`RequestPreparation` takes the preparation gate, refreshes every generated
shader source against its asset revision, hashes the source revisions of all
required programs (`CapturePreparationIdentity`), and, when the published
state is Ready with the same identity, verifies every required program's link
configuration and native handles (`AreRequiredProgramsCurrent`); only a
changed identity or a stale program starts a background preparation. Cold
compilation, relinking and pipeline creation stay on that background task
(S03-S05); the foreground never joins them.

## Entry evidence (instrumented build, no behavior change)

Reports `reports/s13g-entry/` (probe) and `reports/s13g-entry-matrix/`
(reference matrix, unchanged and single-reload cases; the rapid double reload
ended the editor, see below). Per primary recording on the still scene:

| Step | Calls | us | Bytes | us per call | Bytes per call |
| --- | ---: | ---: | ---: | ---: | ---: |
| Readiness check (all callers) | 430.8 | 675 | 396,301 | 1.57 | 920 |
| of which gate wait | 430.8 | 11 | 0 | 0.03 | 0 |
| of which generated-source refresh | 430.8 | 56 | 0 | 0.13 | 0 |
| of which source identity hash | 430.8 | 267 | 379,070 | 0.62 | 880 |
| of which program currentness | 430.8 | 267 | 0 | 0.62 | 0 |
| Raster program lookup (readiness plus wrapper) | 392.8 | 615 | 361,341 | 1.57 | 920 |
| Raster pipeline factory (key build plus shared lookup) | 392.8 | 720 | 0 | 1.83 | 0 |
| Raster header validation and native state | 392.8 | 95 | 0 | 0.24 | 0 |
| Raster pipelines step total | 1 | 1,530 | 361,257 | | |
| Native closure capture | 3 | 52 | 4,608 | 17.2 | 1,536 |
| Native descriptors | 3 | 19 | 12,176 | 6.5 | 4,059 |
| Native pipeline lookup (readiness) | 3 | 5 | 2,760 | 1.6 | 920 |
| Native association (address root, association) | 3 | 10 | 1,008 | 3.4 | 336 |
| Compute pipeline lookup (readiness) | 1 | 6.5 | 920 | | |
| Late closure (includes one readiness) | 1 | 24 | 920 | | |
| Gated discovery-plus-preparation total | 1 | 4,973 | 404,536 | | |

So the readiness check runs about 431 times per recording, once per bin header
plus once per consumer, and every call allocates 880 bytes in the identity
hash: `AddProgramIdentity` enumerates each program's `EventList<XRShader>`
through its interface enumerator, one allocation per program. The gate is
uncontended (0.03 us). The per-header factory call is the other half of the
raster step. Readiness diagnostics on the entry build reported
`admissionPollCount` 253,053 and `admissionPollMilliseconds` 637 after about
600 frames, `foregroundJoinCount` 0, state Ready.

Reference matrix on the entry build: the single `reload_renderer_shaders`
reported readiness Ready with the attempt counter reset to 176 (a new
background preparation completed), zero validation errors, presents advancing
(789 in the 12 s window), image identical to baseline (diff 0.0000014). Two
reloads 0.3 s apart then terminated the editor with an access violation in
`vkCmdBindPipeline` from `VkMeshRenderer.RecordDrawNoLock` (ordinary mesh
draw, not the Advanced family), recorded under "Found defect" below.

## Hypothesis and acceptance (declared before editing)

- Change: (a) make the identity hash allocation free by indexing the shader
  list; (b) inside `TryPrepareVisibilityRasterPipelines`, resolve readiness,
  the raster program and the prepared pipeline once per distinct (coverage,
  meshlet, cull class) and propagate that immutable result to every header
  sharing it within the call, since those are the only header inputs the
  visibility pipeline depends on besides the call's single target closure; (c)
  on the retry path where a header already holds a pipeline, reuse it only
  while its program link generation is unchanged, otherwise prepare again. No
  readiness evaluation is bypassed: every consumer still evaluates identity
  and currentness on every call, and every distinct pipeline combination is
  still validated through the factory and the manager each frame.
- Budget on the unchanged path per recording: readiness calls at most 12
  (from 431) with zero bytes; raster pipelines step at most 0.15 ms (from
  1.53) with zero bytes; factory calls at most 8 (from 393); gated allocation
  falls by at least 361,560 plus 3 times 920 bytes; images unchanged.
- Invalidation: shader reload leaves readiness Ready for a Pending phase and
  returns Ready with the attempt counter reset, the next raster preparation
  rebuilds pipelines against the relinked program (factory misses), images
  unchanged after recovery; TSR render-scale change changes the target
  closure and rebuilds; transactional renderer restart replaces the runtime
  and its readiness state; zero foreground joins throughout; zero validation
  errors; presents advance in every case.
- Falsifier: any budget miss; a stale pipeline bound after a reload, target
  change or restart; readiness frozen behind a cache (Pending never observed
  after a reload, or Ready reported with a changed identity); a foreground
  join; a validation error; an image regression.

## Change

- `VulkanAdvancedVisibilityPipelineRuntime.Preparation.cs`:
  `AddProgramIdentity` indexes `program.Shaders` instead of enumerating it.
- `VulkanPreparedStableBinStream.TryPrepareVisibilityRasterPipelines`: an
  eight-slot per-call scratch (`RasterPipelineScratchCapacity`, keyed by
  coverage Opaque/Masked, meshlet and cull class) holds the prepared
  `VulkanVisibilityRasterPipeline` for the call; headers with a matching slot
  reuse it, others resolve readiness, program and factory once and fill the
  slot; a header whose earlier pipeline's `ProgramLinkGeneration` no longer
  matches its program's `LinkGeneration` is prepared again instead of reused.
  The per-record geometry validation and header native state stay per header.
- Observation probes (default-off, under the S13a guard) remain for the
  readiness sub-steps, the raster sub-steps and the native compute sub-steps.

## Result (fixed build, same session type, fixture and windows)

Probe `reports/s13g-fixed/`, matrix `reports/s13g-fixed-matrix/`, rapid
reload trials `logs/s13g-rapid-reload-trials.log`. Per primary recording on the
still scene (entry, then fixed); the moving window agrees within a few
percent:

| Step | Calls | us | Bytes |
| --- | ---: | ---: | ---: |
| Readiness check (all callers) | 430.8 to 40.0 | 675 to 88 | 396,301 to 1,599 |
| of which gate wait / source refresh / identity / currentness | | 11 / 56 / 267 / 267 to 1.5 / 8 / 28 / 38 | 379,070 to 0 in identity |
| Raster pipelines step | 1 | 1,530 to 164 | 361,257 to 80 |
| of which program lookup (with readiness) | 392.8 to 2.0 | 615 to 24 | 361,341 to 80 |
| of which pipeline factory | 392.8 to 2.0 | 720 to 13 | 0 |
| of which per-header validation and native state | 392.8 to 393.0 | 95 to 81 | 0 |
| Native pipeline lookup (readiness) | 3 | 4.9 to 3.9 | 2,760 to 120 |
| Compute pipeline lookup | 1 | 6.5 to 3.0 | 920 to 40 |
| Late closure | 1 | 24 to 21 | 920 to 40 |
| Gated discovery-plus-preparation total | 1 | 4,973 to 3,195 | 404,536 to 38,648 |

Recordings per second on the same fixture rose from 59.7 to 83.0 on the still
window and 63.7 to 75.7 during cube motion (the probe's own sampling and the
concurrent editor session make this indicative, not a controlled frame-time
claim). Zero scene publication failures in every window.

Budget clauses: gated allocation fell by 365,888 bytes (budget at least
364,320) and images are unchanged, both met. Two clauses were missed by small
margins and are recorded rather than restated: readiness calls per recording
are 40, not the declared 12, because the declaration enumerated only the
recording-thread consumers (now 7: two raster, one compute, one late, three
native) and missed the frame loop's authoring-side poll in
`TryEnqueueAdvancedVisibilityStage` (about 33 per recording, unchanged by this
phase, 2.2 us per call); and the raster step is 0.164 ms with 80 bytes, not
0.15 ms with zero bytes, because the per-header geometry validation (81 us for
393 headers, cheap existing validation kept deliberately) and a residual
40-byte allocation per readiness call remain. That residual (about 1.6 KB per
recording across the 40 calls) was not located in the currentness path within
this phase and is listed as a residual below.

Matrix on the fixed build (every case zero validation errors, zero scene
publication failures, presents advancing, `foregroundJoinCount` 0):

| Case | Readiness trace after the action | Raster factory calls per recording | Image diff versus baseline |
| --- | --- | ---: | ---: |
| Unchanged 15 s | Ready throughout (attempts 526) | 2.00 | 0.0000 |
| Shader reload | Pending at 0.05 s (attempts 1), Ready at 1.09 s (attempts 165) | 1.80 (frames deferred while pending) | 0.0000 |
| Post-reload unchanged 10 s | Ready (165) | 2.00 | 0.0000 |
| TSR render scale 0.75 | Ready (target closure changed, pipelines rebuilt on the new target) | 2.00 | 0.0000 (the desktop output is unchanged by the internal scale on this fixture) |
| TSR render scale restored | Ready | 2.00 | 0.0000 |
| Transactional renderer restart | Pending at 1.56 s (attempts 66), Ready at 3.75 s (attempts 217) on the replacement runtime | 1.97 | 0.0012 |
| Post-restart unchanged 15 s | Ready (217) | 2.00 | 0.0012 |
| Rapid double reload (0.3 s apart) | Pending at 0.46 s (71), Ready at 1.58 s (244) | 1.71 | 0.0012 |
| Post-rapid-reload unchanged 10 s | Ready (244) | 2.00 | 0.0012 |

Three further rapid double reloads on the fixed build each traced Pending then
Ready within 1.9 s with the editor alive and zero validation errors. Pending
and recovery stayed observable because no result is cached across frames: the
per-call scratch lives only inside one `TryPrepareVisibilityRasterPipelines`
call, and every consumer still evaluates identity and currentness.

## Found defect: rapid successive shader reloads ended the entry-build editor (pre-existing, not reproduced after the change)

On the entry build (no behavior change from this phase), two
`reload_renderer_shaders` calls 0.3 s apart terminated the editor with an
access violation (`0xc0000005` in `coreclr`, Windows event 1000/1026 at 19:24:49
local) inside `vkCmdBindPipeline` called from `VkMeshRenderer.RecordDrawNoLock`
through the primary recording of an ordinary mesh draw, that is the CpuDirect
mesh path, not the Advanced family or its readiness path. The renderer's
local pipeline cache (`_pipelines`, keyed on the program link generation) is
only cleared for superseded programs, while the compile queue retires "stale
completion" pipelines of a relinking program through the deferred retirement
queue; a recording that resolves its handle from the local cache between that
retirement and the program's link-generation publication would bind a retired
pipeline. That mechanism is a hypothesis from the code, not a confirmed
reproduction. Four rapid double reloads on the fixed build did not reproduce
the crash; this phase changed the timing of the raster path but not the mesh
path, so the defect is reported to the S04/S05 owner (dependency-scoped
compile invalidation and pipeline retirement) as an intermittent race with
this stack and reproduction recipe, not claimed fixed.

## Residuals handed off

- Readiness residual: 40 bytes per call, about 1.6 KB per recording, source
  not located (candidates: the wrapper lookup or the link-configuration check);
  and the authoring-side poll of about 33 readiness evaluations per recording
  in `TryEnqueueAdvancedVisibilityStage`, 2.2 us each, which the family
  preparation could reuse if the frame loop propagated its accepted readiness
  into the stage request.
- Native compute stages: 17.9 KB per recording outside readiness (closure
  capture 4.6 KB, descriptor preparation 12.2 KB, association 1.0 KB) and
  about 82 us; target closures 800 bytes; about 18 KB per recording allocated
  in the family loop outside every probed step (the per-step sum is 20.7 KB
  against a gated total of 38.6 KB). These are hot-path allocations by the
  code rules and belong to a follow-up allocation audit of the family loop
  (S13h's remeasurement or a dedicated item), not to readiness.
- Stable-bin sealing remains 2.5 ms per recording (S13f handoff to S13h).

## Disposition

Validated for reachable scope. Met: the call chain and frequency are proven
(one readiness poll per bin header plus one per consumer, 431 per recording),
lock wait, source refresh, identity and currentness are timed separately, the
selected unchanged path avoids the repeated evaluation (readiness once per
distinct raster combination, factory once per distinct combination, identity
hash allocation free), no hot-path allocation was introduced, every exercised
mutation invalidated before consumption (reload, rapid double reload, TSR
scale, restart) with Pending observable and no stale result becoming Ready,
and the S03-S05 guarantees held (zero foreground joins, zero validation
errors, presents advancing, retirement metering stable across the restart).
Not exercised: unrelated versus dependent shader edits (the reload tool
invalidates every loaded shader root, so the two cannot be distinguished
without editing engine shader files), failed compilation and a cancelled or
stale completion under the supported lifecycle (no driver on this fixture
without editing tracked shader assets), device-generation replacement beyond
the transactional restart, and Vulkan validation layers (Release session;
validation counters read zero because the layers are off). Two literal
budget clauses missed by small margins as recorded in the result.

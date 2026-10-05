# Production Component-Profile Admission And Output

Updated: 2026-10-01
Status: Production diagnostic image passed; clean promotion remains open.

The production profile routes a fixed moderate-static world through the ordinary
viewport and `DefaultRenderPipeline`, using GPU indirect zero-readback submission.
The initial component fixtures passed, but integrating a real world exposed
construction, admission, and output-evidence defects.

## Findings And Corrections

- `XRDataBuffer` defers publication during construction. Explicit `Generate()`
  previously attempted backend cache insertion before owner-first publication.
  It now completes that publication before generating, preserving ambient
  construction-batch ownership. The generic render-object constructor remains
  unchanged because other types generate while their constructors are active.
- GPU-pass initialization swallowed construction failures and retried them as
  shader readiness. Strict cold preparation now preserves and rethrows the
  original initialization failure; ordinary asynchronous preparation retains its
  readiness behavior.
- The renderer consumed first-production preparation eligibility before an
  admission attempt succeeded. Eligibility now remains until a valid submission
  receipt returns. Measured frames never retry pending admission.
- Failure diagnostics queried Advanced admission for a Default pipeline. That
  query initiated unrelated shader preparation. Diagnostics now inspect an
  existing Advanced preparation snapshot only for an Advanced pipeline.
- Production coarse timings came from the target's synthetic recording counter,
  producing zero-valued evidence. The profile now retains completed production
  timing samples only at their matching engine frame IDs. Missing samples remain
  invalid. Statistics tracking is pinned before warmup and restored afterward.
- Successful submission counts and an output hash initially accepted a black
  image. The profile now reads the exact last measured receipt once after capture
  and derives the hash, PNG, and red-anchor oracle from those same bytes. Rejected
  runs retain the image, admission snapshot, oracle, raw stream, and gates.
- The early rejected image had seven GPU-scene commands, the expected camera
  position, raw-albedo debug mode, no pending compute or compact-program flags,
  and no completed opaque visibility descriptor or visited material-table stage.
  Readback routing and camera initialization were ruled out: exact receipt
  readback uses the same target image and completed fence as the generic reader.
- The shared mesh-command gate used CPU-published membership for Default
  zero-readback passes, which could prevent dispatch with empty CPU membership.
  Authored GPU commands now use configured GPU-pass topology. Authored CPU-only
  commands retain their CPU membership gate, so empty pre/post-render and overlay
  commands are not newly admitted by a viewport GPU override. The runtime image
  remained black after this correction, so it did not explain the full failure.
- A trial full-scene workload gate using GPU scene publication did not change
  the rejected output and was removed. Cold-frame tracing confirmed the opaque
  command executes with authored/effective zero-readback, configured GPU and CPU
  pass membership, and seven commands in the active frame snapshot. The outer
  command gates do not explain this fixture's failure. Material stages are
  stamped only inside compact raster submission, so `NotVisited` alone does not
  prove that the GPU pass was skipped.
- Additional cold tracing showed the same prepared pass and pipeline instance
  entering GPU rendering with ready programs and no observed false return. The
  command container catches individual exceptions, while this invocation has no
  output FBO or exact completion reservation to reject partial authoring. The
  production scene now enables `PropagateCommandExceptions` so the original
  failure aborts before a submission receipt; ordinary viewport recovery retains
  its default policy. Strict containers retain the first exception, complete
  their authored pop/unbind traversal, and then rethrow it. This preserves the
  state scopes used by cold retry and teardown. Temporary traces were removed.
- Strict execution exposed the original error: the material binding layout has
  four texture indices plus flags and a 36-word/144-byte std430 row, but the CPU
  upload struct retained three texture indices and a 32-word/128-byte row. The
  upload now includes the emissive index and three header-padding words before
  the first vec4. Packing still uses authoritative layout offsets, and the
  constructor's size/stride guard remains enabled. The subsequent runtime still
  produced a black image, so this fixed an ABI error without resolving output.
- The post-ABI diagnostic reached the opaque visibility and material paths:
  the last measured frame had seven visible draws, a completed visibility
  descriptor and material-bucket readback of `[0, 7, 0]`, 35 required and 35
  ready material rows, and zero invalid material IDs. Native indirect API calls
  were still zero, and the
  exact receipt image contained zero pure-red pixels. The `output_visual_oracle`
  gate correctly rejected this frame. The next investigation boundary is the
  conversion from prepared material buckets to native indirect drawing; the
  admission counters alone do not establish where that conversion stopped.
- Production coarse timing now reads matching engine-frame IDs from the
  statistics boundary reached by `BeginFrame`. The retained diagnostic has 30
  completed coarse samples, selected `OpaqueDeferred` queries, and required
  calibration. Those observations could not establish correctness while the
  output was black.
- The remaining black-output cause was cold native mesh/index ownership. Deferred
  mesh-version publication and `BaseVersion.Generate()` did not create Vulkan
  wrappers, while index commits touched only existing owners. At the cold
  renderer-facade boundary, `VulkanRenderer.TrySyncMeshRendererIndexBuffer`
  now resolves or creates the mesh and index-buffer wrappers through
  `GenericToAPI`; command-runtime lookup remains lookup-only. This allowed the
  prepared material buckets to reach native indirect recording without
  creating wrappers during command recording. The earlier black runs remain
  rejected evidence.
- The next selected production diagnostic passed all 21 validity gates over 30
  captured frames. Its retained admission snapshot records seven visible draws,
  material buckets `[0, 7, 0]`, 35 of 35 material rows ready, zero invalid
  material IDs, and three planned, native, and count-path indirect calls. The
  exact-receipt image has 360 red-anchor pixels and hash
  `0A260808D8EA09AA928D870675927215F10C2229243462397B02DC9777DF689E`.
  The PNG was viewed and showed the expected gray wall and red anchor. Selected
  and coarse GPU samples completed; standard and synchronization validation
  reported zero errors and one loader warning. Capture-thread allocation was
  21,700,480 bytes; worker allocation remains unmeasured. This establishes one
  passing diagnostic capture, not a clean baseline or promoted optimization.
- Cold admission attempts advance the explicit world clock even when their
  submissions are rejected. Production input metadata now snapshots the timer's
  final render timestamp before scene disposal, including those attempts and
  drain frames. Synthetic fixtures retain their successful-submission formula.
- A readiness probe measured 3,654 ms of resource-generation preparation before
  the first material-backing request. The old shared five-second retry budget
  could therefore reject normal staged progress. Production profiles now retain
  the existing session-wide recipe deadline and cancellation token through every
  retry, including capture-thread warmup. Each unchanged admission stage has a
  five-second stall limit, and the 4,096-attempt cap remains. Other scene callers
  retain their five-second total default. Measured frames still make one attempt;
  material authority and allocation-worker behavior were not relaxed.
- Selected timestamp commands belong to a dedicated uncached primary. Recording
  rejects a selected sink with a cached artifact owner or reuse policy, preventing
  later clean frames from replaying session-owned query pools. Secondary keys and
  clean primary identities remain unchanged.

The dedicated compile worker exists and deduplicates requests by pipeline key.
The terminal pending log did not establish a missing worker, key churn, or a
material-map lifetime defect. Subsequent successful runs preserved the material
map. The measured readiness progression, rather than the terminal exception
alone, justified separating the stage-stall limit from the existing recipe
deadline.

The production profile environment now pins six diagnostic flags to `0` and
restores each prior process/runtime override afterward: `P3Logging`,
`BucketLoopDryRun`, `SkipCommandSwapIfClean`, `BucketLoopSkipEmpty`,
`ForceSingleBucket`, and `MdicGlFinish`. This keeps external diagnostic toggles
from changing the fixed fixture's recording path.

## Related Worker Evidence

The synthetic secondary fixture lazily allocated its completion wait handle
during capture. Caching and warming the handle fixed that allocation. An interim
count-polling optimization introduced a race between `CountdownEvent.Signal()`
and the coordinator's next `Reset()`: a zero count could precede event signaling.
The coordinator now always waits for the actual native completion event. Worker
allocation totals are snapshotted at capture end and exclude drain submissions.
The named MCP lifecycle and worker-count matrix passed after these corrections.

## Disposable Evidence

Evidence belongs to
`Build/_AgentValidation/20261001-111944-component-profiling/`:

- `logs/production-default-final.log`: pending compute admission before the
  preparation-eligibility correction.
- `logs/production-cold-admission.log`: completed submission followed by an
  explicitly rejected unmeasured worker allocation budget.
- `logs/production-oracle-runtime.log` and `reports/production-oracle-runtime/`:
  exact receipt readback, complete coarse samples, and rejected black output.
- `logs/production-guard-runtime.log` and `reports/production-guard-runtime/`:
  complete selected pass samples with calibration; the same rejected image and
  unvisited material-table stages.
- `logs/production-topology-runtime.log` and `reports/production-topology-runtime/`:
  the corrected mesh-command gate still rejected black output, narrowing the
  failure to the surrounding branch or an earlier GPU-pass admission return.
- `logs/production-gate-probe.log`, `logs/production-return-probe.log`, and
  `logs/production-identity-probe.log`: cold traces ruled out outer gates, missing
  render context, pass replacement, and pending programs; all temporary traces
  were subsequently removed.
- `logs/production-strict-runtime.log`: original GPU material row-layout
  exception propagated before the first native production submission.
- `logs/production-bucket-runtime.log` and `reports/production-bucket-runtime/`:
  after the 36-word material-row fix, visibility and material buckets completed,
  but no native indirect draw was recorded and the red-anchor oracle rejected
  the black output. The retained admission, query, oracle, gate, and PNG files
  are diagnostic evidence rather than an accepted result.
- `logs/production-material-probe.log` and `reports/production-material-probe/`:
  exact-receipt PNG, oracle, admission, query, validation, raw-stream, gate, and
  result artifacts for the first passing production diagnostic capture after
  cold mesh/index wrapper resolution.
- `logs/build-production-final.log`, `logs/production-final.log`, and
  `reports/production-final/`: zero-warning/error Release build and trace-free
  repeat with all 21 gates passing, 30 captured frames, zero validation errors,
  matching output hash, and a viewed PNG with 360 red-anchor pixels. Final
  capture-thread allocation was 21,736,920 bytes; worker allocation is unmeasured.
- `logs/production-readiness-probe.log`: cold resource generation consumed
  3,654 ms before material preparation began, identifying the cumulative
  startup-budget defect; temporary progress traces were subsequently removed.
- `logs/build-production-lifecycle-final.log`,
  `logs/production-lifecycle-final.log`, and `reports/production-lifecycle-final/`:
  final build passed without warnings/errors; all 21 gates passed on 30 frames,
  with the same output hash and zero validation errors. Actual explicit clock
  metadata was 2.7333388 seconds and capture-thread allocation 21,332,560 bytes.
- `logs/production-timeout-check.log`: a one-second recipe deadline exited 1
  with an explicit timeout and no accepted result. Direct CLI timeouts now fail
  visibly instead of taking the successful user-cancellation exit path.
- `logs/tests-final.log`: 29 of 32 targeted tests passed. Three presentationless
  host tests failed at setup because the engine execution scheduler was not
  installed; this run does not establish a clean full-suite pass.
- `logs/tests-fixtures-approved.log` and
  `reports/tests/component-fixtures-approved.trx`: after explicit user clearance,
  the existing scheduler scope was installed in the three host fixtures and
  comparison fixtures declared their measured allocation fields. All 38 focused
  tests passed, including lifecycle/recreation and synchronization validation;
  no tests were skipped. Earlier missing-field comparator failures remain in
  `logs/tests-comparison-final.log`.
- `logs/mcp-final.log`: all eleven named MCP lifecycle checks passed.
- `logs/external-artifact-hook.log` and `reports/external-artifact-hook/`: one
  required 68-byte dummy file was attached and hashed. This verifies bounded
  artifact attachment, not external GPU capture or replay.
- `logs/mcp-completion-fix.log` and `reports/mcp/`: passed owner-thread lifecycle,
  capture cancellation/timeouts, process ownership, and worker-count matrix.

Required findings are recorded above; disposable files are not repository
dependencies. Final validation and remaining hardware acceptance are maintained
in the [implementation evidence](../../progress/rendering/vulkan-component-profiling.md).

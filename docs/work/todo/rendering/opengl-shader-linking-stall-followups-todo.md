# OpenGL Shader Linking Stall Follow-ups TODO

Last Updated: 2026-05-06
Current Status: log analysis captured, implementation not started
Scope: reduce OpenGL shader/program linking churn, binary upload backpressure,
and adjacent render-thread stalls observed in the 2026-05-06 editor runs.

## Goal

Fix the current startup/linking throughput problems without reintroducing the
editor-window freeze regression from the previous experimental changes.

The target result is a renderer startup path where shader binary cache hits do
minimal CPU work, failed shader variants do not spam full retry work every
frame, repeated program requests are coalesced or backed off, and unrelated
mesh/index/asset preparation work does not monopolize the render thread.

## Source Logs

Primary run:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-06_12-08-30_pid33072/log_rendering.txt`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-06_12-08-30_pid33072/log_opengl.txt`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-06_12-08-30_pid33072/profiler-render-stalls.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-06_12-08-30_pid33072/profiler-fps-drops.log`

Regression comparison run:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-06_11-43-57_pid28296/log_rendering.txt`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-06_11-43-57_pid28296/log_opengl.txt`

## Current Findings

The 12:08 run does not show the previous unrecovered shader-link freeze
signature. It does show recovered startup stalls and several high-cost paths
that can still freeze or visibly hitch the editor.

Observed shader/linking issues:

- `BINARY_CACHE_HIT: 1627`
- `BINARY_CACHE_MISS: 1214`
- `BINARY_UPLOAD_ASYNC_QUEUED: 855`
- `BINARY_UPLOAD_BACKPRESSURE: 772`
- `SOURCE_BACKEND_SELECTED: 2`
- `SOURCE_BUILD_BEGIN: 2`
- repeated cache misses for two failed separable `Geometry|Fragment` hashes,
  each using about 382 KB and 10,329 source lines,
- `Combined:UIBatchTextMaterial` binary cache hits appear hundreds of times,
  implying material/program lifecycle churn,
- several cached link-prep paths take about 39 to 44 ms, suggesting source
  compile input preparation still runs on binary cache hits.

Observed shader failure:

- Fragment shader: `Build/CommonAssets/Shaders/Uber/UberShader.frag`
- Geometry shader: `Build/CommonAssets/Shaders/DirectionalCascadeAtlasShadowDepth.gs`
- Link error: `"FragBinorm" not declared as input from previous stage`
- Program shape: separable `Geometry|Fragment`

Observed non-linking render stalls:

- `GLMeshRenderer.RefreshIndexBuffersFromCache`: about 1152 ms on the render
  thread,
- `XRMesh Constructor > InitMeshBuffers`: about 557 ms,
- `UITextBatched.fs` asset deserialization inside render: about 482 ms,
- `XRWindow.ProcessPendingUploads`: spikes around 34 to 90 ms,
- startup mesh generation queue slices reaching about 91 to 156 ms.

## Regression Guard Rails

Do not reintroduce the previous freeze-prone changes while implementing this
todo.

- Do not enable shared-context source linking for the previously hazardous
  single-stage graphics cases.
- Do not switch to binary-load-only program handles that skip required program
  lifecycle state.
- Do not globally share generated separable vertex programs as a first fix.
- Do not mask the shadow-caster failure with a broad `UberShader` varying
  removal or source-pruning workaround.
- Do not skip render-program handle creation, binding semantics, or material
  readiness state just because a hash is known failed unless the owning call
  path has been audited.
- Do not add per-frame heap allocations to render submission, swap/present,
  visible collection, per-frame update, or shader binding hot paths.

---

## Phase 0 - Branch, Baseline, And Repro

Outcome: the next implementation series has a clear baseline and can prove it
does not revive the freeze regression.

- [ ] Create a dedicated branch for this todo list and implementation series.
- [ ] Preserve the 12:08 log set as the current post-revert baseline.
- [ ] Preserve the 11:43 log set as the freeze-regression comparison baseline.
- [ ] Capture counts for binary cache hits, misses, queued uploads,
  backpressure events, source builds, and failed source hashes.
- [ ] Capture top render-thread stalls from `profiler-render-stalls.log` and
  `profiler-fps-drops.log`.
- [ ] Add or identify a repeatable editor startup scenario that exercises UI
  text rendering, shadow caster variants, and the startup mesh queue.
- [ ] Define a regression pass/fail rule for editor startup:
  - no unrecovered render-thread stall,
  - no repeated failed hash full-source retry loop,
  - no UI text program upload flood,
  - no render-thread job over 250 ms after warmup unless explicitly expected.

Acceptance criteria:

- Baseline numbers are documented in the implementation PR notes.
- The repeatable startup scenario can be run before and after each phase.

## Phase 1 - Fix Shadow Shader Interface Failure

Outcome: the failed `Geometry|Fragment` shadow variants link or are replaced by
a deliberate, narrower shader contract.

- [ ] Inspect `DirectionalCascadeAtlasShadowDepth.gs` and `UberShader.frag`.
- [ ] Identify all fragment inputs declared by the active Uber shadow variant.
- [ ] Make the geometry shader emit the required interface, including
  `FragBinorm` and likely `FragTan`, using pass-through or deterministic
  synthesized values as appropriate.
- [ ] Prefer a narrow geometry/fragment interface fix over broad Uber source
  pruning.
- [ ] If the shadow fragment path does not need the full Uber input interface,
  consider a dedicated shadow fragment shader as a follow-up, not as the first
  fix.
- [ ] Verify the two previously failed hashes no longer produce the
  `"FragBinorm" not declared as input from previous stage` link error.
- [ ] Verify the repeated 382 KB / 10,329-line failed-source miss loop is gone.

Acceptance criteria:

- The shadow caster variants link successfully or fail once with a clear,
  rate-limited diagnostic.
- No editor freeze regression appears during the startup scenario.

## Phase 2 - Skip Source Prep On Binary Cache Hits

Outcome: warm binary-cache hits do not materialize full compile inputs.

- [ ] Locate the OpenGL render-program link preparation path.
- [ ] Split binary-cache probing from source compile input preparation.
- [ ] When a valid binary cache hit is found, avoid `PrepareCompileInputs()` or
  equivalent full source materialization.
- [ ] Keep enough lightweight metadata for logging: hash, program label,
  separable flag, shader stage list, source byte/line summaries if already
  cached in metadata.
- [ ] Ensure source compile inputs are still prepared for binary misses,
  intentional source builds, diagnostics that explicitly require source text,
  and cache write paths.
- [ ] Verify cached link-prep warnings around 39 to 44 ms disappear or fall
  below the warning threshold.

Acceptance criteria:

- Binary-cache-hit prep performs no full shader source concatenation.
- Warm startup does not log slow cached link-prep warnings for the same hashes.
- Shader linking diagnostics still include useful metadata.

## Phase 3 - Rate-limit Failed Hash Retries

Outcome: a known failed hash does not repeat expensive cache-miss and diagnostic
work every frame.

- [ ] Audit the failed-program cache and all callers that observe failed link
  state.
- [ ] Move known-failed checks before expensive cache-miss logging and source
  summary generation where safe.
- [ ] Keep material/program readiness semantics intact for callers that need a
  concrete failure state.
- [ ] Add a rate-limited `SOURCE_FAILED_SKIPPED` or equivalent rendering log
  event keyed by program hash.
- [ ] Include compact metadata in the rate-limited event: program label,
  separable flag, shader stages, last failure reason, and elapsed time since
  first failure.
- [ ] Verify repeated failed hashes do not generate full binary miss records
  every frame.

Acceptance criteria:

- A failed hash produces one full diagnostic and then compact rate-limited
  follow-up logs.
- The failed-hash path does not change successful binary cache behavior.

## Phase 4 - Coalesce Binary Upload Work

Outcome: duplicate program requests do not flood the async binary upload queue.

- [ ] Identify the key used for binary cache entries and async binary upload
  jobs.
- [ ] Add in-flight upload tracking keyed by binary cache key or stable program
  hash.
- [ ] If the same program instance already has an upload pending, avoid
  re-enqueueing it.
- [ ] If multiple program instances need the same binary, choose a conservative
  first behavior:
  - wait for the first upload to complete before retrying,
  - or apply a short frame-based backoff before creating another GL program
    handle,
  - or coalesce only within the same owner/material until cross-owner sharing
    is audited.
- [ ] Do not globally share generated separable program handles in this phase.
- [ ] Log compact coalescing/backoff events so queue behavior remains visible.
- [ ] Verify `BINARY_UPLOAD_BACKPRESSURE` drops substantially in the startup
  scenario.

Acceptance criteria:

- Duplicate upload attempts for the same program instance are eliminated.
- Backpressure count is materially lower than the 12:08 baseline.
- No render readiness deadlock or editor freeze appears.

## Phase 5 - Fix UI Text Program Lifecycle Churn

Outcome: `Combined:UIBatchTextMaterial` is prepared once per relevant render
version instead of repeatedly recreated or requeued.

- [ ] Trace where `Combined:UIBatchTextMaterial` render programs are created,
  invalidated, generated, and requested during editor UI text rendering.
- [ ] Confirm whether `GetImmediateRenderVersion(...)`, material versioning, or
  render-pass selection causes repeated `Generate()` calls.
- [ ] Add a pending/linked latch so the same render version is not generated
  again while already pending or ready.
- [ ] Reuse the same UI text material/render program for stable render-pass and
  debug-mode combinations.
- [ ] Update per-frame text data through uniforms, SSBOs, buffers, or draw data
  rather than recreating material/program state.
- [ ] Verify `Combined:UIBatchTextMaterial` binary cache hits/uploads no longer
  dominate the rendering log.

Acceptance criteria:

- UI text rendering does not enqueue hundreds of identical program uploads.
- Text still renders correctly through editor startup and world hierarchy UI.
- No new per-frame allocations are introduced in text render submission.

## Phase 6 - Slice Render-thread Mesh And Index Work

Outcome: mesh/index preparation does not block a render frame for hundreds of
milliseconds.

- [ ] Inspect `GLMeshRenderer.RefreshIndexBuffersFromCache` scheduling.
- [ ] Move refresh work out of in-frame render-thread jobs where possible.
- [ ] If the work must remain render-thread-bound, apply a per-frame budget and
  resume over multiple frames.
- [ ] Inspect `XRMesh` construction and `InitMeshBuffers` call sites that occur
  under render scopes.
- [ ] Move heavy mesh buffer initialization to preload, background preparation,
  or the existing mesh generation queue where safe.
- [ ] Cap `GLMeshGenerationQueue` processing time per frame during startup.
- [ ] Verify the 1152 ms index refresh and 557 ms mesh initialization stalls no
  longer appear in profiler logs.

Acceptance criteria:

- No single mesh/index preparation scope exceeds the agreed startup stall
  threshold.
- Meshes still render correctly after budgeted preparation.
- Startup boost does not starve input/window responsiveness.

## Phase 7 - Preload Or Defer Shader Asset Deserialization

Outcome: shader asset deserialization does not happen inside first visible
render commands.

- [ ] Identify shader assets deserialized during `XRViewport.Render` and
  `RenderCommand.Render`, including `UITextBatched.fs`.
- [ ] Preload common editor/UI/render-pipeline shader assets before first
  visible render when practical.
- [ ] For optional assets, move deserialization to an async preparation path and
  render with a known fallback until ready.
- [ ] Ensure asset deserialization failures still surface clearly in logs.
- [ ] Verify `UITextBatched.fs` no longer causes a first-render stall around
  482 ms.

Acceptance criteria:

- Common editor shader assets are ready before first UI draw.
- First-render shader asset deserialization is absent or below the stall
  threshold.

## Phase 8 - Logging And Validation Cleanup

Outcome: verbose diagnostics stay useful without becoming a performance problem
or obscuring the next investigation.

- [ ] Convert repeated per-frame program diagnostics into summary/rate-limited
  events where appropriate.
- [ ] Keep high-value fields: elapsed time, render-thread stall time, program
  hash, program label, separable flag, shader count, shader stage list, source
  bytes, source lines, binary size, backend, and queue/backpressure state.
- [ ] Add summary counters at shutdown or interval boundaries for:
  - binary cache hits,
  - binary cache misses,
  - async queued uploads,
  - backpressure events,
  - source builds,
  - source failures,
  - failed-hash skips,
  - duplicate upload coalesces.
- [ ] Verify verbose logging does not overflow profiler queues during the
  startup scenario.
- [ ] Document the final OpenGL linking behavior in the feature doc if behavior
  changes from the current implementation.

Acceptance criteria:

- Logs still explain link and upload timing.
- Repeated known-state events are compact enough for long editor sessions.
- The final validation run includes rendering, OpenGL, render-stall, and
  FPS-drop logs.

## Final Validation

- [ ] Build `XREngine.Runtime.Rendering`.
- [ ] Build `XREngine.Editor`.
- [ ] Run the repeatable editor startup scenario once cold and once warm.
- [ ] Compare post-change counts against the 12:08 baseline.
- [ ] Confirm the previous freeze-regression signatures are absent.
- [ ] Confirm the shadow shader link failure is fixed or rate-limited.
- [ ] Confirm cached link prep no longer performs full source preparation.
- [ ] Confirm UI text program churn is materially reduced.
- [ ] Confirm mesh/index/asset stalls are below the agreed threshold or have
  explicit follow-up issues.
- [ ] Update related rendering feature docs with the final behavior.

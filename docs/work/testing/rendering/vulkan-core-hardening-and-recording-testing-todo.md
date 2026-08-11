# Vulkan Core Hardening And Recording Testing TODO

Last Updated: 2026-08-11
Owner: Rendering
Status: Active

This tracker owns all post-code-change validation for the consolidated
[Vulkan Core Hardening And Recording Code Changes TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md).
It contains no implementation work.

The required source architecture, frame lifecycle, CPU efficiency contract, and
observability semantics are defined by the
[Vulkan Render Loop Target Architecture](../../design/rendering/vulkan-render-loop-target-architecture.md).
This tracker may not close on visual correctness alone: structural simplicity,
fault containment, zero-allocation hot paths, CPU budgets, complete slow-frame
attribution, measured data layouts, audited unsafe boundaries, and profiler
overhead are equal promotion gates.

It also owns the evidence formerly tracked by advanced-render-pipeline phases
06 through 10. Implementation and cutover changes now live in sections 10
through 14 of the consolidated code-changes tracker.

## Phase 1-3 Closeout Evidence (2026-08-06)

- [x] Migrate stale Vulkan unit-test call sites for the removed schedule-cache
  authority and required `CommandChainKey.ChainOrdinal`.
- [x] Run `Test-VulkanPhase3-Regression`: 110 passed, 0 failed.
- [x] Build `XRENGINE.slnx` with `--no-restore -warnaserror`: zero warnings and
  zero errors.
- [x] Run the final named isolated Vulkan editor session with standard
  validation. The MCP profiler reported zero validation errors and zero pending
  retired resources; Vulkan/rendering logs contained no VUID, device-loss,
  submission-rejection, lifetime-rejection, fatal, or unhandled-exception match.
- [x] Exercise two camera-dependent viewport captures. Both returned the typed
  Vulkan no-transfer-readable-image result and confirmed no CPU or OS-window
  fallback.

## Phase 4 Vertical-Cut Evidence (2026-08-06)

- [x] Run the final Phase 3 regression task after the Phase 4 cut: 110 passed,
  0 failed.
- [x] Run the focused Phase 4, Phase 2.1, stable-packet, GPU hot-layout,
  presentation-independent, and CPU-span suites: 177 passed, 0 failed.
- [x] Build the complete solution with `--no-restore -warnaserror`: zero
  warnings and zero errors.
- [x] Exercise the deterministic presentationless clear/readback/hash path and
  the accepted-submission receipt/lifetime path.
- [x] Run a final isolated standard-validation Vulkan session. The profiler
  reported zero validation errors/messages, zero pending retired resources, and
  a live generation-1 mapped-frame arena with three chunks / 100,663,296 mapped
  bytes. Clean shutdown logs contained no VUID, device-loss, rejection, fatal,
  unhandled, or access-violation match.
- [x] Record the post-cut architecture inventory: 918 hand-written files /
  181,146 physical lines, 321 renderer partial declarations, 379 type-wide
  unsafe files, 101 ambient facade-callback files, and two thread-static files.
  The final Phase 4 structural budget is still open.
- [x] Validate the follow-up device-capability ownership cut: focused
  device-context, Phase 4, Phase 2.1, backend-registry, and
  presentation-independent suites passed 50/50; the independent hardware
  presentationless clear/readback/hash smoke passed 1/1 without skipping.
- [x] Run the post-device-capability validation-enabled Vulkan session and full
  warnings-as-errors build. The session reported zero validation
  errors/messages and zero pending retired resources; clean shutdown logs had
  no VUID, validation error, device-loss, rejection, fatal, unhandled, or access
  violation match; the solution build completed with zero warnings and errors.
- [x] Record the post-device-capability architecture inventory: 918 hand-written
  files / 181,253 physical lines, 320 renderer partial declarations, 378
  type-wide unsafe files, 101 ambient facade-callback files, and two
  thread-static files. This is a one-partial/one-unsafe-file reduction from the
  preceding cut, not the final Phase 4 structural gate.
- [x] Audit every Phase 4 implementation checkbox against production code and
  validation evidence, then rewrite the epic-sized requirements as hierarchical
  parent/child gates. Checked children now expose independently validated
  vertical slices while final integration parents remain open.
- [x] Validate the native-lifetime context cut: focused device-context,
  backend-registry, presentation-independent, diagnostics, and fault-boundary
  coverage passed 25/25; `Test-VulkanPhase3-Regression` passed 110/110; the
  full warning-as-error solution build completed with zero warnings and errors.
- [x] Run three validation-enabled Vulkan sessions after the native-lifetime cut
  and the final architecture-review fixes.
  The final profiler snapshot reported zero validation messages/errors, zero
  pending retired resources, three mapped-frame chunks / 100,663,296 mapped
  bytes, and active rendering. Clean shutdown logs contained no VUID,
  validation error, device-loss, fatal, unhandled, or access-violation match.
- [x] Record the post-native-lifetime architecture inventory: 926 hand-written
  files / 181,668 physical lines, 308 renderer partial declarations, 372
  type-wide unsafe files, 101 ambient facade-callback files, and two
  thread-static files. The final Phase 4 structural budget remains open.
- [ ] Reconcile the legacy broad source-contract aggregates with the current
  split-file layout and one-way architecture baselines. The exploratory broad
  run still contains moved-file/obsolete-marker failures unrelated to the
  focused runtime gate; do not report the full unit-test suite as green.
- [ ] Run the OpenXR paths against a live supported runtime; no live runtime was
  available for this vertical cut.

## Automated Tests

- [ ] Add focused behavioral and source-contract coverage for device-loss
  quiescence, first-failure preservation, resource retirement, descriptor
  fingerprinting, graph cycles/missing producers/uninitialized reads, frame-slot
  ownership, and invalidation keys.
- [ ] Add deterministic fault injection at submit, timeline/fence wait,
  descriptor update, allocation, and OpenXR frame boundaries.
- [ ] Add deterministic primary-reuse coverage that cycles desktop swapchain
  images while camera/view data changes. Require the refresh cohort's plan
  generation, render-frame ID, frame-data image index, and recorded-order
  projection to match; prove that prior thread-local recording scratch cannot be
  accepted as current authority.
- [ ] Cover completion-domain selection explicitly: frame-in-flight resources
  use the frame-slot timeline, desktop descriptor/frame-data image slots use the
  desktop image timeline, OpenXR uses external completion, and swapchain
  recreation inherits the strongest retired-image graphics-timeline
  requirement.
- [ ] Cover speculative pre-seal primary reuse with stable and structurally
  changed dynamic UI. Exact secondary reuse must accept an unsealed current
  operation, while a changed operation must fall back to full sealing without
  stale data, duplicate metrics, or a spurious validation warning.
- [ ] Add CI-safe Vulkan/OpenXR smoke coverage that does not require a physical
  headset, including capture work queued beside eye rendering.
- [ ] Cover all mesh strategies and ensure zero-readback lanes make no forbidden
  CPU visibility/count reads or silent CPU fallback.
- [ ] Cover material-classification record layout, exact-once pixel assignment,
  subgroup and bounded fallback equivalence, indirect-dispatch construction,
  independent capacity overflows, and required-mode failure behavior.
- [ ] Cover reconstructed-surface, native-material, clustered-light, shadow,
  AO, decal, GI, sky/background, and texture-indirection contracts on OpenGL and
  Vulkan.
- [ ] Cover late-pass eligibility, scene-color feedback rules, sorted alpha,
  weighted OIT, PPLL, depth peeling, transparent motion, fog/atmosphere,
  temporal masks, HDR output, and every supported upscale path.
- [ ] Cover stable view-set layout, per-eye/layer addressing, eye-independent
  visibility/history, imported XR resources, capture profiles, asynchronous
  editor selection, and nested/repeated output generations.
- [ ] Add source-contract coverage forbidding classic GBuffer, deferred light
  accumulation, light-combine, ordinary opaque-forward recovery, V2 type names,
  and same-frame production readback from the advanced path.
- [ ] Add shader-layout, cache-key, command-tree, resource-layout, and
  deterministic overflow coverage for every promoted OpenGL/Vulkan feature
  profile.
- [ ] Build the editor, server, and VR client whenever their shared rendering
  contracts change.

## Architecture, Simplicity, And Profiler Contract Validation

- [x] Add a reproducible Vulkan source-inventory report that separates
  hand-written and generated files and records file/line counts, partials,
  mutable facade fields, directory depth, largest files/methods, dependencies,
  and duplicate lifecycle authorities.
- [x] Record the reproducible 2026-08-06 baseline of 890 hand-written files /
  178,506 physical lines in the Vulkan core path, 323 `VulkanRenderer` partial
  declarations, 381 type-wide unsafe files, 102 ambient facade-callback files,
  and two thread-static files. The older 858-file / 170,048-line draft baseline
  predated the final Phase 1-3 source and is superseded.
- [ ] Verify the final hand-written Vulkan core has at most 550 files / 125,000
  lines, the lifecycle spine has at most 40 files / 20,000 lines, and
  `VulkanRenderer` is one non-partial facade file of at most 500 lines.
- [ ] Verify the main frame orchestration method is at most 100 logical lines,
  lifecycle paths use at most two owner directories below `Vulkan/`, and every
  file above 1,500 physical lines or method above 150 logical lines has the
  design-required approved ownership exception.
- [ ] Verify reductions were not achieved by combining unrelated top-level
  types, hiding behavior in generated files, relocating Vulkan-specific code to
  a backend-neutral assembly, or replacing partials with a service locator or
  forwarding-only abstraction layer.
- [ ] Add architecture coverage enforcing facade-to-owner dependency direction,
  one mutable authority for device, output, plan, resource lifetime, command,
  queue, and telemetry state, and no ordinary hot-path thread-static or ambient
  facade lookup.
- [ ] Add source/API coverage preventing a new production planner, scheduler,
  lifetime tracker, descriptor publication model, queue gateway, profiler stage
  taxonomy, or stateful `VulkanRenderer` partial without an explicit design
  revision.
- [ ] Review the frame spine manually from wake/acquire through settlement and
  record that every early return has a typed outcome and settles acquire,
  frame-slot, upload, worker, output-image, and timeline ownership exactly once.
- [ ] Cover the stable lifecycle stage schema and require engine/render frame,
  output/view-set, frame-slot, generation, span/parent/cross-thread link,
  thread/worker, timestamp, allocation, classification, operation, result, and
  reason fields in retained traces.
- [ ] Cover nested inclusive/exclusive calculation, cross-thread causal links,
  worker wall span/overlap/imbalance, critical-path selection, wait-versus-work
  classification, ring wrap/overflow counters, dropped-span reporting, and
  deterministic export ordering.
- [ ] Inject a bounded CPU delay into each lifecycle stage, worker path, queue
  gateway, and external wait seam; prove the expected stage becomes the dominant
  exclusive/critical-path contributor without double counting another stage.
- [ ] Prove detailed captures attribute at least 99% of the frame-root wall
  interval and emit an explicit `Unattributed` failure for every synthetic or
  real gap of 50 microseconds or more.
- [ ] Prove aggregate and targeted capture perform zero managed allocation after
  warmup, never format strings on measured threads, and continue to report
  overflow/failure when a ring or stage budget is exhausted.
- [ ] Verify editor, runtime-stat, MCP/component-profile, JSON, CSV, and trace
  outputs resolve the same stable IDs, outcomes, counts, and intervals from one
  schema.

## Data Layout, Unsafe Boundary, And Cache Validation

SIMD-specific parity, hardware-width, telemetry, and full-frame promotion
evidence follows the
[Vulkan CPU SIMD Refactor Pass Design](../../design/rendering/vulkan-cpu-simd-refactor-pass-design.md).
The scalar oracle and live rendering path must be correct before a vector width
is accepted.

- [ ] Generate a machine-readable hot-data layout report for each promoted
  profile containing type/stream name, element size and field offsets,
  alignment, stride, capacity/high-water mark, managed-reference presence,
  producer/consumers, fields touched, bytes read/written/copied, and owning
  resource/frame generation.
- [ ] Add exact CPU/shader/native layout tests using `Unsafe.SizeOf<T>()`,
  `Marshal.OffsetOf<T>()` where appropriate, shader reflection/contracts, and
  Vulkan header expectations. Fail on an unversioned field, size, alignment, or
  shader-layout change.
- [ ] Add source/API coverage requiring hot/native bit-copied records to be
  fixed-layout and blittable, forbidding GC references and ambiguous native
  `bool` fields, and preventing padded structs from being serialized or compared
  as arbitrary bytes.
- [ ] Add source-contract coverage that confines unsafe code to the approved
  native/mapped-memory owners, rejects type-wide unsafe on the renderer facade,
  and rejects raw-pointer buffer APIs where a span or typed arena slice can carry
  length and ownership.
- [ ] Verify every unsafe arena reservation checks capacity, element/byte
  arithmetic overflow, base and slice alignment, generation, and one-writer
  ownership before exposing memory. Exercise zero, one, maximum accepted,
  overflow, stale-generation, double-release, and use-after-settlement cases.
- [ ] Run diagnostic canary/poison and randomized sequence coverage for native
  scratch, mapped-frame, prepared-draw, descriptor, and readback arenas; require
  deterministic rejection rather than adjacent-slice corruption.
- [ ] Validate mapped host-visible memory with coherent and non-coherent memory
  types where available. Check host/device ownership, flush/invalidate range
  expansion to `nonCoherentAtomSize`, frame-slot reuse, and no host write while a
  submitted GPU read is outstanding.
- [ ] Verify `stackalloc` is span-backed, statically or explicitly bounded, and
  absent from loops; verify a pooled buffer is not used after return or retained
  by a frame plan, cached artifact, worker, or diagnostic callback.
- [ ] Compare existing AoS, candidate SoA, compact AoS/hot-cold, and candidate
  AoSoA only for the actual hot consumers at low, medium, and high realistic
  counts. Include construction/publication, transpose, scalar tail, copy,
  synchronization, and settlement cost rather than timing only the inner loop.
- [ ] Keep an idiomatic safe `Span<T>` implementation as the reference for every
  proposed pointer or explicit-SIMD path. Promote the lower-level implementation
  only when repeated Release measurements exceed the run-to-run noise band and
  improve the owning lifecycle stage and full-frame p95 without correctness or
  low-count regressions.
- [ ] Record at least instructions/cycles, bytes processed, operations per item,
  L1/L2/last-level cache misses, branch misses, allocation, and wall time for the
  relevant CPU layout comparisons when supported by the reference profiler.
  Separate CPU cache locality from GPU memory-bandwidth results.
- [ ] Run the layout matrix on at least one supported Intel x64 and one supported
  AMD x64 reference machine; add Windows Arm64 before promoting that CPU target.
  Record CPU model, cache-line size, vector widths, .NET runtime/JIT version,
  tiered-PGO state, and profiler/tool versions.
- [ ] Prove GPUScene culling/classification reads only its declared stage streams
  and that no unconditional broad-command build or SoA extraction remains.
  Record GPU bytes, dispatches, barriers, cull/classification duration, visible
  count, and whole-frame result before and after.
- [ ] Verify the final scene streams remain under one schema/storage owner and
  compare source-file count, runtime-owner count, Vulkan allocation/binding
  count, publication calls, and lifetime transitions before and after. Reject a
  per-column wrapper or resource explosion even when an isolated kernel improves.
- [x] Remove `GPURenderExtractSoA.comp`, its scratch buffers, and uncalled
  compatibility methods from production. A future replacement requires a named
  active consumer and retained benchmark evidence.
- [ ] Prove final indirect buffers remain valid contiguous
  `VkDrawIndirectCommand`/`VkDrawIndexedIndirectCommand` arrays with correct
  offset, count, stride, bounds, and shader-to-native field semantics.
- [ ] Prove frame-operation lowering preserves deterministic order and graph
  semantics while planning/scheduling iteration touches numeric opcode and
  per-kind streams rather than polymorphic `FrameOp` references.
- [ ] Prove render packets and prepared mesh draws use frame-owned range storage,
  perform no per-packet/per-draw array allocation or `ArrayPool` rent after
  warmup, and do not touch cold managed owners or diagnostic names during normal
  worker encoding.
- [ ] Measure prepared-draw hot-header size, side-stream bytes, cache misses,
  commands per microsecond, and full `CommandRecord` p50/p95/p99 for compact AoS
  versus any AoSoA candidate. Reject AoSoA if packing or tail cost erases the
  encoder win.
- [ ] Prove graph/barrier planning uses typed numeric IDs and flat offset/count
  ranges while the native boundary receives complete contiguous Vulkan AoS
  arrays. Compare edge/barrier counts, bytes touched, planning p95, and native
  scratch high-water marks.
- [ ] Prove descriptor publication scans only relevant dirty/generation streams
  and materializes only dirty native ranges or descriptor bytes. Record scanned,
  emitted, copied, and skipped descriptor counts/bytes and validate all resource,
  view, sampler, layout, and publication generations.
- [ ] Run worker scaling with 1..N supported workers under identical immutable
  work. Capture cache-to-cache/contested-access evidence where available, verify
  worker allocation base and stride alignment, and reject a layout with false
  sharing, global-atomic contention, or worse full-frame critical path.
- [ ] Verify per-worker counters and trace rings are written only by their owner,
  merge deterministically after completion, and produce the same aggregate
  values as the serial reference without adding hot-path locks or allocations.
- [ ] Reject any layout or unsafe optimization whose saved inner-loop time is
  outweighed by extraction, conversion, publication, cache invalidation,
  synchronization, diagnostics, another output, or p95/p99 tail cost.

## Functional And Visual Validation

- [ ] Rebuild the affected runtime and editor projects, then run the focused
  Vulkan, render-graph, descriptor, recording, resource-lifetime, and OpenXR
  test slices.
- [ ] Exercise desktop, capture, probes, shadows, UI preview, OpenXR stereo,
  mirror, and supported two-to-four-view foveated/quad-view output through
  `FreshSerial`, serial packet recording, and enabled reuse.
- [ ] Verify resize, minimize/restore, swapchain recreation, topology changes,
  streaming publication, camera and light motion, camera cuts, masked geometry,
  TSR history, AO, shadows, probes, bloom, and post-processing.
- [ ] For desktop camera-motion acceptance, compare an independent wall-clock
  `frame_outputs.frame_id` delta with the reported render rate while no input is
  supplied. Require `scene_rendered=true`, `work_disposition=FreshRender`, and
  `skipped=false`; exclude screenshot/readback intervals from cadence results.
- [ ] Inspect screenshots and exported depth, normal, velocity, lighting,
  temporal, and final targets from at least two camera positions; compare
  serial/parallel/reuse output for equivalence.
- [ ] Run StandardValidation and SyncValidation across mixed-output,
  graphics-only, and selected graphics-plus-compute plans; investigate every
  engine-owned validation error, stale generation, destroyed-in-use object, or
  device loss.
- [ ] Add or finalize deterministic `Empty`, `OpaqueDense`, `MaterialDiverse`,
  `MaskedCoverage`, `Skeletal1`, `Skeletal8`, `Skeletal32`, `SkeletalCrowd`,
  `Overdraw`, `Occlusion`, `ClusteredLights`, `ShadowStress`, `Transparency`,
  `PostProcess`, `MixedSpecial`, `StereoAsymmetric`, and `CaptureConsumers`
  cohorts.
- [ ] Pin cohort cameras, animations, lights, assets, settings, random seeds,
  warmup, and scene revisions; keep assets repository-appropriate and regenerate
  unit-testing settings/schema through canonical tools after setting changes.
- [ ] Test classification with empty, background-only, one-kernel,
  many-material-one-kernel, many-kernel, checkerboard, tiny-triangle,
  masked-edge, invalid-payload, and overflow scenes; prove each valid pixel is
  assigned exactly once and dispatch count follows visible kernel coverage.
- [ ] Compare tile-only and compact pixel-list classification before enabling
  adaptive selection, and prove empty/offscreen materials generate no work.
- [ ] Validate native opaque/masked PBR plus material, light, shadow, AO, decal,
  GI, missing-resource, provider-switch, and background behavior against
  per-feature numeric/image tolerances.
- [ ] Capture OpenGL and Vulkan native-shading frames that show no production
  dependency on a classic GBuffer, deferred light accumulation, ordinary opaque
  Forward+, or light-combine stage.
- [ ] Validate sorted alpha, OIT, refraction, particles, water, fog,
  atmosphere, temporal reconstruction, HDR, and upscalers; prove frames with no
  transparency allocate and execute no transparency resources or work.
- [ ] Run desktop mono, emulated stereo, two-pass stereo, single-pass stereo,
  OpenXR, OpenVR, scene capture, mirror, probe, and editor-platform-window
  matrices.
- [ ] Validate eye independence with deliberately asymmetric occluders/content,
  and exercise resize, view-count change, runtime restart, swapchain recreation,
  pipeline hot selection, and at least two camera/head positions per visual
  issue.
- [ ] Capture original and advanced output from identical cameras/settings;
  define per-feature tolerances and inspect visibility, depth, reconstructed
  attributes, velocity, material work, shadows, AO, GI, transparency, temporal,
  and final targets.
- [ ] Use isolated MCP sessions and inspect saved PNGs; use RenderDoc when
  screenshots/logs do not identify the failing pass or resource, and retain
  unresolved findings under `docs/work/investigations/rendering/`.
- [ ] Inspect the editor frame tree/timeline for static, moving, resize, mixed
  output, OpenXR, slow-frame, and rejected-frame cases; confirm it identifies the
  dominating exclusive stage, wait reason, output, generation, and critical
  path without consulting raw logs.

## Performance, Allocation, And Tail Validation

- [ ] Record equivalent Deferred/Uber prepass-on/off captures and a pass/resource
  ledger containing geometry replay, attachments, copies/blits, transitions,
  barriers, dispatches, and CPU/GPU duration.
- [ ] Capture RenderDoc before/after frames for the prepass, recording, and
  probe/shadow paths; inspect named engine markers when supported.
- [ ] Run matched Release Vulkan/OpenGL `CpuDirect` low-, medium-, and
  high-count cohorts plus GPU indirect instrumented and zero-readback cohorts.
- [ ] Measure the complete stable lifecycle taxonomy: frame pacing, snapshot
  handoff, completion/retirement, output acquire, plan build, resource prepare,
  work scheduling, command recording, submit preparation, queue submit, output
  completion, and settlement, plus GPU time, allocations, readbacks, and reuse.
- [ ] Report root and stage p50/p95/p99/worst, inclusive/exclusive work,
  driver/external/wait time, aggregate worker CPU, worker wall span/overlap/
  imbalance, render-thread wait, required-output critical path, and unattributed
  time for every promoted cohort.
- [ ] Verify stable desktop command reuse after warmup, no static command-range
  rerecord from ordinary data changes, no required-pipeline deferral, and no
  steady-state recording allocation.
- [ ] Run the same deterministic full-Sponza camera path with directional lights
  off and on. Record visible draws, binding-artifact builds/reuses, scheduled/
  recorded/reused chains, primary decisions, frame-op preparation, packet
  construction, frame-data manifest/refresh, native encoding, submission, and
  actual no-input frame advance. Do not accept overlay FPS alone.
- [ ] Attribute every desktop frame above 5.00 ms and every RVC zero-readback
  frame above 8.33 ms; retain an explicit `Unattributed` failure bucket.
- [ ] Run matched instrumentation-disabled, aggregate, targeted-single-subtree,
  and slow-frame-trigger captures; satisfy the accepted observer-overhead budget
  and prevent intrusive targeted results from being used as clean promotion
  evidence.
- [ ] Compare serial and each supported worker count with identical immutable
  work; accept parallel recording only when the full-frame critical path
  improves and coordination, queue delay, merge, imbalance, and render-thread
  wait remain bounded.
- [ ] Verify stable-frame planning, scheduling, descriptor publication,
  command-reuse lookup, and retirement scale with changed/visible work rather
  than total registry, cache, material, or historical-operation counts.
- [ ] Run warm static and deterministic moving-camera Deferred/Uber desktop
  cohorts, controlled streaming churn, combined camera/physics/raycast/streaming
  stress, and bounded long-duration soak runs.
- [ ] Run at least three RVC zero-readback repetitions with freshly rendered
  desktop output and both freshly rendered eyes per submitted projection frame,
  with each supported foveation state.
- [ ] Run open, moderate, occluder-heavy, masked, static, and moving-camera
  scenarios for disabled, CPU-software, CPU-query, and GPU Hi-Z occlusion;
  record actual culls, work removed, CPU/GPU p50/p95/p99, and tail spikes.
- [ ] Verify occlusion conservatism for masked, near-plane, large-bound, and
  moving-camera content, and verify GPU Hi-Z performs no current-frame
  readback.
- [ ] Promote an occlusion mode only when it has positive target-scenario p95
  benefit and bounded ineffective-case overhead; record an explicit disposition
  for every mode.
- [ ] Establish matched original-versus-advanced Release baselines with VSync
  disabled and record the 8.33 ms 120 Hz budget plus the approved desktop
  high-refresh target; do not accept average FPS as the promotion metric.
- [ ] Record CPU frame, update, animation, extraction, render-thread, recording,
  submission, and present timing plus named GPU stages, p50/p95/p99, hitches,
  warmup, samples, run duration, draw/dispatch/barrier/pipeline-bind counts,
  visible pixels, active kernels, deformation/material work, readback bytes, and
  managed allocations.
- [ ] Demonstrate bounded deformation/submission scaling across skeletal
  cohorts, material-work scaling by visible kernel coverage rather than
  registered material count, and two-phase visibility benefit without
  unbounded low-occlusion overhead.
- [ ] Confirm production classification and shading perform zero same-frame
  readback and warmed per-frame rendering performs zero managed heap allocation.
- [ ] Confirm command-reuse misses name only topology, capacity, binding,
  shader, or resource-generation changes.
- [ ] Reject promotion when a GPU optimization merely moves a larger cost into
  CPU recording, synchronization, descriptors, resource churn, or tail latency.

## Stability And Lifecycle Validation

- [ ] Run long camera-motion, animation, resize, feature-toggle, shader-reload,
  asset-streaming, editor-interaction, and pipeline-switch sessions.
- [ ] Run at least 30 minutes of continuous interactive resize with randomized
  drag extents, maximize/restore, minimize/restore, monitor/DPI movement where
  supported, and concurrent camera/light/asset/editor activity; require no
  crash, hang, device loss, mixed extent/generation, or unbounded recreation.
- [ ] Run a two-hour mixed-output churn soak and an eight-hour Release soak on a
  reference rig with camera motion, shadows, streaming, shader reload, captures,
  ImGui, and supported XR/mirror activity; retain bounded memory, retirement,
  descriptors, command artifacts, queues, and frame latency.
- [ ] Verify there is no routine device-wide idle, cross-context resource
  churn, unbounded retired generation, descriptor leak, stale history,
  command-rerecord storm, or latched interactive-resize callback.
- [ ] Validate supported device-loss handling and swapchain recreation.
- [ ] Inject failure at every lifecycle boundary and after every acquired
  ownership transition; require first-failure preservation, no post-loss GPU
  work, one terminal frame outcome, exactly-once settlement, and bounded
  shutdown or explicit restart request.
- [ ] Validate empty, missing-resource, shader-pending, capacity-overflow, and
  backend-capability failure paths with explicit structured outcomes.
- [ ] Inspect OpenGL, Vulkan, rendering, profiler, and shutdown logs while
  separating steady-state failures from teardown-only noise.

## Promotion Evidence

- [ ] Record the standard manifest for every retained run: commit and dirty
  state, GPU/driver/API, Vulkan SDK/layers, enabled extensions/features, OpenXR
  runtime, settings/diagnostic preset, scene hash, resolution, refresh rate,
  duration, and frame count.
- [ ] Record Vulkan shadow and UI-preview baselines plus alternating
  capture/OpenXR-eye and rapid-resize/probe/OpenXR stress evidence.
- [ ] On supported hardware, collect `VK_KHR_device_fault` and vendor diagnostic
  artifacts; capture TDR-risk and memory-budget evidence during optional-work
  stress.
- [ ] Confirm the final desktop p95, RVC p95, p99 tail, allocation, readback,
  and no-fallback contracts against their current approved budgets.
- [ ] Publish the final reproducible source inventory and prove every facade,
  lifecycle-spine, complete-core, directory-depth, file-size, method-size,
  dependency-direction, and single-authority gate from the target architecture
  passes with no unrecorded exception.
- [ ] Publish compatible lifecycle timing tables for desktop,
  presentationless, and supported XR cohorts containing every stable stage,
  root/critical-path p50/p95/p99/worst, wait/driver/external breakdown, worker
  overlap, allocations, observer overhead, and unattributed coverage.
- [ ] Publish the final hot-data layout/unsafe audit, consumer-access matrix,
  AoS/SoA/AoSoA comparison results, CPU counter evidence, arena safety results,
  and a list of every retained unsafe block with its native contract and measured
  justification.
- [ ] Retain representative frame-tree/trace artifacts proving a normal frame,
  a CPU-work slowdown, a driver/external wait, parallel worker imbalance, resize
  recreation, a rejected frame, and device-loss containment are immediately
  distinguishable.
- [ ] Publish the final before/after tables, RenderDoc artifacts, hardware
  manifest, risks, and explicitly deferred follow-ups in the investigation.
- [ ] Prove every valid visibility pixel is assigned exactly once, material
  diversity within one kernel causes no per-material submission fan-out, and
  overflow remains correct, bounded, and observable.
- [ ] Prove standard opaque/masked PBR shades directly from visibility to HDR,
  uses shared froxel/shadow records, applies decals and supported AO/GI through
  explicit advanced contracts, and performs no per-instance pipeline or
  descriptor bind.
- [ ] Prove transparent and special content composes over native opaque HDR and
  final visibility depth, late paths remain explicit exceptions, disabled
  late/post features resolve no unused resources, and the temporal/post chain
  consumes dense advanced depth, velocity, and reactive inputs.
- [ ] Prove desktop Advanced, RVC-owned OpenXR eyes, and capture consumers share
  one logical scene/material/feature contract while per-eye visibility, motion,
  histories, and output generations remain independent.
- [ ] Record passing static, moving, material-diverse, skeletal, transparent,
  stereo, XR, and capture correctness/performance budgets before production
  cutover.
- [ ] Confirm `AdvancedRenderPipeline` promotion leaves zero production
  same-frame readback, zero warmed managed hot-path allocation, and no classic
  deferred/ordinary opaque-forward production graph.
- [ ] Do not close the program until a developer can use the documented editor
  profiler or exported trace to explain every retained slow frame without
  reconstructing timing or ownership across legacy `VulkanRenderer` partials.

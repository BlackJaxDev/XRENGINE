# Collect-Visible Render Wait Decoupling TODO

Last Updated: 2026-07-01
Owner: Rendering
Status: Implemented through Phase 1; Phase 2 gated on a clean collect-hot capture
Target Branch: none; implemented on current branch per user request on 2026-07-01

Evidence source:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-fps-drops.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-cpu-frame-2026-07-01-12-50-30-583-7cbc9cb3.log`
- `XREngine/Core/Time/EngineTimer.cs`

Related local docs:

- [Engine Rendering Optimization Roadmap](engine-rendering-optimization-roadmap.md)
- [CPU Direct Fast Path TODO](cpu-direct-fast-path-todo.md)
- [CPU Async Hardware Query Occlusion TODO](cpu-async-hardware-query-occlusion-todo.md)
- [Rendering Profiler Counter Audit](rendering-profiler-counter-audit.md)
- [Default Pipeline GPU Hotspots TODO](default-pipeline-gpu-hotspots-todo.md)
- [Desktop And VR Shared Render-Thread Frame Pacing TODO](desktop-vr-shared-render-thread-frame-pacing-todo.md)
- [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)

## Goal

Make collect-visible timing readable and resilient when the render thread is
late. The collect-visible thread should not be mistaken for the root cause when
it is only waiting for render, and it should not amplify render stalls into
larger scheduling problems.

## Issue

The July 1 CPU frame dump shows thread 60 with high history numbers:

- Latest about 185.6 ms.
- Average about 171.9 ms.
- Max about 443.0 ms.

The FPS-drop log already attributes those samples to
`EngineTimer.CollectVisibleThread.WaitForRender`, because `CollectVisibleThread`
already wraps its phases in distinct profiler scopes: it dispatches visible
collection (`DispatchCollectVisible`), waits on `_renderDone` (`WaitForRender`),
resets it, then runs swap jobs plus the buffer swap (`DispatchSwapBuffers`), and
signals `_swapDone`.

That means the collect-visible thread is mostly blocked behind the slow render
thread. It is not the primary source of 200 ms frames in this run. The per-scope
instrumentation already exists, so the gap is at the aggregate level: the
top-level `EngineTimer.CollectVisibleThread` rollup and the thread-60 CPU frame
total still sum wait time into what looks like an independent heavy subsystem.

## Why This Matters

Visibility collection is a hot path, but optimizing it will not fix this
specific failure if the thread is asleep waiting for render. The engine needs
better attribution so work goes to the real bottleneck first.

There are two distinct failure modes, and they need different remedies:

- Mode A (this run): render is slow and collect is fast, so collect blocks in
  `WaitForRender`. Frame rate is render-gated, so getting collect further ahead
  of render does not raise throughput; it only adds latency. The remedy is to
  fix the render-thread stall (tracked in the render-pacing and GPU-hotspots
  TODOs) and to attribute the wait correctly.
- Mode B: collect is slow and render is fast, so render is starved. This is the
  case where reusing the last collected visibility or dropping stale collected
  commands actually helps.

So the decoupling work in this doc is primarily about attribution and a bounded
late-render latency policy, not about improving throughput when render is the
bottleneck.

## Fix Direction

- Build on the existing scopes rather than re-adding them.
  `DispatchCollectVisible`, `WaitForRender`, and `DispatchSwapBuffers` are
  already separate profiler scopes. The missing split is inside
  `DispatchSwapBuffers`, which currently times `ProcessCollectVisibleSwapJobs`
  (swap jobs) and the buffer swap under one scope. There is no separate idle
  phase to bucket: the loop has no throttle or sleep, so `WaitForRender` is the
  idle.
- Do not count `WaitForRender` as collect work in high-level summaries. This is
  an aggregate/rollup classification change, not new per-scope instrumentation.
- Add render/collect frame ids so the profiler can show how far collect is
  behind render.
- Add a Mode B late-render policy (see Why This Matters): reuse last collected
  visibility or drop stale collected commands when render is starving collect.
  This does not apply to Mode A, where collect is the thread that is blocked.
- If additional buffering of collected command lists is added so collect can
  proceed while render drains a prior frame, keep it strictly bounded and
  lossless. The current fence already bounds collect to about one frame ahead;
  do not reintroduce a lossy swap (the render-matrix path was deliberately moved
  off a lossy double-buffer to an accumulate-until-consumed queue for this
  reason).
- Keep correctness conservative. If collect data is stale or cannot be safely
  swapped, report the reason and render a safe fallback.

## Phase 0 - Attribution

- [x] Create dedicated branch `rendering-collect-visible-decoupling`.
  - Skipped by explicit user request: "Don't branch."
- [x] Split the existing `DispatchSwapBuffers` scope so
  `ProcessCollectVisibleSwapJobs` (swap jobs) and the buffer swap report
  separately. `DispatchCollectVisible` and `WaitForRender` are already distinct
  scopes and do not need re-adding.
- [x] Classify `WaitForRender` as downstream render pressure in the aggregate
  rollups: the top-level `EngineTimer.CollectVisibleThread` summary and the CPU
  frame-dump thread total should not present wait time as collect work.
- [x] Add frame ids for update, collect, swap, render, and present.
- [x] Add counters for wait duration, wait reason, skipped collect frames, and
  stale collect reuse.

Acceptance criteria:

- [x] A slow collect-visible sample says whether it was work or waiting.
- [x] The July 1 style failure would point to render-thread lateness rather
  than collect-visible as the root cause.

## Phase 1 - Avoid Stall Amplification

Note: the current loop already lock-steps collect to render. Collect runs one
`DispatchCollectVisible`, then blocks on `_renderDone` before it can swap and
loop again, so collect can only get about one frame ahead and cannot build an
unbounded backlog today. This phase is about preserving that bound if buffering
is added, and about defining behavior for Mode B (render fast, collect slow).

- [x] Define what render should do when collect has not produced a fresh frame:
  block, reuse previous visibility, or drop stale collected commands (Mode B).
- [x] If extra buffering is added, keep the one-frame-ahead style bound; do not
  let collect run unbounded frames ahead of render.
- [x] Add a bounded late-render policy setting for diagnostics.
- [x] Ensure scene mutation and render command ownership stay thread-safe under
  any additional buffering.

Acceptance criteria:

- [x] Any added buffering keeps collect bounded to a small, fixed number of
  frames ahead of render (the fence bounds it to about one frame today).
- [x] Reused or stale visibility is counted and visible in profiler output.

Implementation notes:

- `EngineTimer.CollectVisibleThread.ProcessCollectVisibleSwapJobs` and
  `EngineTimer.CollectVisibleThread.DispatchSwapBuffers` now report separately.
- CPU profiler thread totals are classified work time. Raw wall time and
  downstream render-pressure time remain visible as `WallTimeMs` and
  `DownstreamRenderPressureMs`.
- Frame lifecycle telemetry is emitted to profile capture NDJSON, UDP/in-process
  profiler packets, and the editor Render Stats panel.
- Default late policy remains `BlockUntilFresh`. `XRE_COLLECT_VISIBLE_LATE_POLICY=ReusePreviousVisibility`
  enables bounded stale visibility reuse after at least one real collect/swap
  has completed.
- No extra buffering was added; the existing collect/render fence remains the
  bound.

## Phase 2 - True Collect Optimization

- [ ] After render-thread stalls are fixed, capture a clean scene where
  `DispatchCollectVisible` itself is the hot path.
- [ ] Optimize spatial tree traversal, command emission, per-camera filtering,
  and per-frame allocations based on that clean capture.
- [ ] Validate CPU direct and GPU-driven command collection separately.

Acceptance criteria:

- [ ] Collect-visible optimization work is based on actual collection cost, not
  render-thread wait time.

Phase 2 remains intentionally open. This pass made the profiler distinguish
actual collection cost from render-thread wait time; collect-visible algorithm
optimization should wait for a clean capture where `DispatchCollectVisible`
itself is the measured hot path.

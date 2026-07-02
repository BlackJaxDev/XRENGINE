# Editor Profiler And UI Render Cost TODO

Last Updated: 2026-07-01
Owner: Editor / Rendering
Status: Proposed
Target Branch: `editor-profiler-ui-render-cost`

Evidence source:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-fps-drops.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-render-stalls.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/log_rendering.log`

Related local docs:

- [Engine Rendering Optimization Roadmap](engine-rendering-optimization-roadmap.md)
- [Rendering Profiler Counter Audit](rendering-profiler-counter-audit.md)
- [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md)
- [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)

## Goal

Make editor profiling and ImGui diagnostics cheap enough that observing a slow
frame does not create another slow frame. The profiler UI should explain
performance without becoming a top render-thread cost.

## Issue

The July 1 FPS-drop log shows editor UI and profiler rendering as recurring
hot paths:

- `UI.DrawProfilerPanel.CorePanels`: 81 drops, avg about 70.7 ms, max about
  163.6 ms.
- `UI.DrawProfilerPanel.ProcessLatestData`: recurring 75-82 ms samples.
- `Vulkan.FrameLifecycle.RecordImGuiOverlay`: 25 drops, avg about 190.3 ms,
  max about 430.6 ms.
- `Vulkan.FrameLifecycle.RecordDynamicUiTextOverlay`: 3 drops, avg about
  262.7 ms, max about 419.2 ms.
- A render stall recorded the last completed render hot path inside
  `EditorImGuiUI.RenderEditor > UI.DrawProfilerPanel > UI.DrawProfilerPanel.CorePanels`.

Hot-scope code locations:

- Panel entry and per-scope timing: `DrawProfilerPanel` in
  `XREngine.Editor/IMGUI/EditorImGuiUI.ProfilerPanel.cs`.
- Display-state rebuild: `ProcessLatestData` in
  `XREngine.Profiler.UI/ProfilerPanelRenderer.cs`.
- Overlay recording: `Vulkan.FrameLifecycle.RecordImGuiOverlay` /
  `RecordDynamicUiTextOverlay` in
  `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs`.

Caveat: the capture environment for these numbers (build configuration,
Vulkan validation layer state, enabled collection toggles) was not recorded.
A same-day triage (run pid37936) found `VkLayer_khronos_validation` loaded,
which inflates Vulkan command-recording costs such as `RecordImGuiOverlay`.
Phase 0 must re-establish these numbers under a controlled environment before
any numbers here are treated as engine cost.

The profiler panel is currently part of the render path it is diagnosing. If
it draws large histories, processes latest data, builds text, or records overlay
command buffers every frame, it can dominate the render thread and distort all
measurements.

## Why This Matters

Profiling tools must have bounded overhead. Otherwise performance work chases
the measurement surface instead of the engine. This is especially dangerous in
VR because a 70-160 ms profiler panel can make an otherwise useful XR test look
catastrophically slow.

## Current State - Existing Mitigations

The code already contains several of the obvious fixes. Do not re-implement
these; the open question is why costs remain despite them.

- Engine data collection already runs off the render thread on a throttled
  cadence: `CollectProfilerDataOnAppThread` runs on the app thread
  (`Engine.Time.Timer.UpdateFrame`) at ~10 Hz
  (`ProfilerCollectMinIntervalMs = 100` in
  `XREngine.Editor/IMGUI/EditorImGuiUI.ProfilerPanel.cs`).
- Panel visibility gating exists: `PanelVisibility` / `NeedsAnyData` skips
  collection and processing for hidden panels
  (`XREngine.Profiler.UI/ProfilerPanelRenderer.cs`).
- Display refresh throttling exists: `ProcessLatestData` self-skips when no
  new frame was collected and rate-limits display rebuilds via
  `UpdateIntervalSeconds`.
- Data-collection kill switches exist in the panel Settings window: Frame
  Logging, Component Timing, Stats Tracking, GPU Pipeline, and Alloc Tracking.
- UDP profiler mode already disables the in-editor panels entirely and defers
  to the external profiler app.

Given the above, the remaining 70-160 ms samples point at the cost of a
single refresh being unbounded (for example `UpdateRootMethodCache`,
`UpdateFpsDropSpikeLog`, `RebuildCachedRootMethodLists`, and full-table ImGui
draws scaling with scope/row count), not at missing throttling or missing
visibility gating.

## Fix Direction

- Make hidden or collapsed profiler panels do no collection, processing, draw,
  dynamic text, or overlay work beyond minimal state maintenance (verify the
  existing `PanelVisibility` gating actually covers every panel).
- Decouple profiler data ingestion from render-thread UI drawing. Ingestion
  (`CollectFromEngine`) is already on the app thread; the remaining
  render-thread work is the `ProcessLatestData` display-state rebuild - bound
  its per-refresh cost or move the rebuild off-thread and hand the render
  thread an immutable snapshot.
- Refresh throttling already exists (~10 Hz collection plus
  `UpdateIntervalSeconds` display refresh); the fix is bounding the cost of
  one refresh, not adding more throttling.
- Virtualize long tables and histories. Draw only visible rows and visible
  graph samples.
- Bound history sizes and string generation. Use reusable text buffers or
  cached formatted values when possible.
- Split profiler UI timing into durable scopes:
  data ingest, aggregation, graph preparation, table preparation, ImGui draw,
  text layout, dynamic text overlay recording, and Vulkan overlay recording.
- Build "measurement mode" on the existing infrastructure - the panel's Speed
  Profile capture (`Engine.TryStartSpeedProfileCapture`) and the
  `Measurement-*` VS Code tasks - rather than a new mechanism: one switch that
  disables profiler panels, dynamic UI text overlays, and diagnostic ImGui
  panels during benchmark captures.
- Keep a low-cost summary overlay available for live debugging, but make its
  cost visible.

## Phase 0 - Prove UI Self-Interference

- [ ] Create dedicated branch `editor-profiler-ui-render-cost`.
- [ ] Record the capture environment for every measurement: build
  configuration, whether `VkLayer_khronos_validation` is loaded (check
  `log_vulkan.log` for "Loading layer library"), and which profiler
  collection toggles (Frame Logging, Component Timing, Stats Tracking, GPU
  Pipeline, Alloc Tracking) were enabled.
- [ ] Capture a baseline with profiler panel visible.
- [ ] Capture the same scene with profiler panel hidden.
- [ ] Capture the same scene with all editor UI hidden or minimized.
- [ ] Compute drop rate, not just drop count: how many total frames produced
  the 81 `CorePanels` drops, and is the cost pervasive or episodic?
- [ ] Check whether `RecordImGuiOverlay` / `RecordDynamicUiTextOverlay` drops
  cluster at panel-open, dock-layout reset, window resize, or font-atlas
  upload rather than steady state; episodic one-offs need a different fix
  than steady-state cost.
- [ ] Add counters for profiler panel visible state, rendered row count, graph
  sample count, formatted string count, and dynamic text overlay op count.
- [ ] Confirm whether `RecordImGuiOverlay` cost correlates with profiler UI
  complexity, dynamic text overlay count, or general ImGui command count.

Acceptance criteria:

- [ ] The doc records the delta between profiler-visible and profiler-hidden
  frame time.
- [ ] Every recorded number states its capture environment (configuration,
  validation layer state, collection toggles).
- [ ] A frame dump can attribute profiler UI cost to data processing, ImGui
  drawing, text generation, or Vulkan overlay command recording.

## Phase 1 - Bound Work When Visible

Collection is already off the render thread and refresh throttling already
exists (see Current State); this phase bounds the cost of a single refresh
and a single draw.

- [ ] Instrument and bound the per-refresh cost of `ProcessLatestData`
  internals (`UpdateRootMethodCache`, `UpdateFpsDropSpikeLog`,
  `PruneRootMethodCache`, `RebuildCachedRootMethodLists`) so one refresh
  cannot scale unboundedly with scope count or history size.
- [ ] If a bounded rebuild is still too slow, move the rebuild off-thread and
  hand the render thread an immutable display snapshot.
- [ ] Draw only visible table rows and graph samples.
- [ ] Reuse formatted strings and buffers across frames.
- [ ] Add explicit cost counters to the profiler panel itself.

Acceptance criteria:

- [ ] `UI.DrawProfilerPanel.CorePanels` stays at or below 2 ms (Debug,
  validation layers off) in steady state; record the measured value here.
- [ ] `ProcessLatestData` no longer appears as a recurring 50+ ms render-thread
  leaf.

## Phase 2 - Overlay Recording Cost

- [ ] Re-measure overlay recording costs with validation layers confirmed off
  before optimizing; treat the pid42516 numbers as unvalidated until then.
- [ ] Split ImGui overlay recording from dynamic UI text overlay recording in
  profiler output.
- [ ] Cache stable overlay command buffers where legal.
- [ ] Avoid recording dynamic text overlays when content is unchanged or hidden.
- [ ] Add a per-frame overlay command count and text glyph/quad count.
- [ ] Ensure overlay command recording is skipped in benchmark mode unless
  explicitly requested.

Acceptance criteria:

- [ ] `Vulkan.FrameLifecycle.RecordImGuiOverlay` and
  `Vulkan.FrameLifecycle.RecordDynamicUiTextOverlay` no longer produce
  100-400 ms FPS drops in normal editor profiling.
- [ ] Benchmark mode reports that editor UI and dynamic text overlays are
  disabled or explicitly enabled.

## Phase 3 - UX And Diagnostics Contract

- [ ] Add visible UI state showing whether profiling is in low-overhead,
  detailed, or benchmark mode.
- [ ] Keep detailed profiler views available for investigations, but mark them
  as intrusive when they exceed budget.
- [ ] Add a warning when profiler UI itself is one of the top frame costs.

Acceptance criteria:

- [ ] The profiler can diagnose itself.
- [ ] A user can capture engine performance without the profiler panel
  materially changing the result.

## Completion

- [ ] Update this doc with final measurements, outcomes, and any follow-ups.
- [ ] Merge `editor-profiler-ui-render-cost` back into `main` after completion
  and validation.

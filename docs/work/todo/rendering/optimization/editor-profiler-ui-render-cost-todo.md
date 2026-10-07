# Editor Profiler And UI Render Cost TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
Validation: [GPU-Driven Submission Validation](../../../testing/rendering/gpu-driven-submission-validation.md), [Vulkan Core Validation](../../../testing/rendering/vulkan-core-validation.md)

## Current State
Profiler collection already runs off the render thread on a throttled cadence. Panel visibility gating, display refresh throttling, data-collection switches, and UDP profiler mode exist. The open work is to bound one visible refresh, virtualize large UI surfaces, reduce overlay command cost, and prove profiler self-cost without changing the benchmark contract.

## Open Code Items

### Profiler data and UI cost
- [ ] Ensure hidden or collapsed profiler panels do no collection, processing, drawing, dynamic text, or overlay work beyond minimal state maintenance. `EditorImGuiUI.ProfilerPanel.cs`, `ProfilerPanelRenderer.cs`. Done when: visibility gating covers every panel.
- [ ] Instrument and bound `ProcessLatestData` internals. `ProfilerPanelRenderer.cs`. Done when: `UpdateRootMethodCache`, `UpdateFpsDropSpikeLog`, `PruneRootMethodCache`, and `RebuildCachedRootMethodLists` cannot scale unboundedly with scope count or history size.
- [ ] Move display rebuilds off-thread if bounded render-thread rebuilds remain too slow. Profiler UI snapshot code. Done when: the render thread receives an immutable display snapshot.
- [ ] Draw only visible table rows and graph samples. Profiler UI drawing code. Done when: long tables and histories use virtualization.
- [ ] Reuse formatted strings and buffers across frames. Profiler UI drawing code. Done when: steady UI refresh avoids per-row string allocation.
- [ ] Add explicit cost counters to the profiler panel. Profiler stats. Done when: the panel reports data ingest, aggregation, graph preparation, table preparation, ImGui draw, text layout, dynamic text overlay, and Vulkan overlay costs.

### Overlay recording and benchmark mode
- [ ] Split ImGui overlay recording from dynamic UI text overlay recording in profiler output. Vulkan frame loop and profiler scopes. Done when: each overlay type has a separate scope and counter.
- [ ] Cache stable overlay command buffers where legal. Vulkan frame loop. Done when: unchanged overlay content can reuse recording without invalidating unrelated mesh command buffers.
- [ ] Skip dynamic text overlays when content is unchanged or hidden. Dynamic text overlay code. Done when: unchanged or hidden overlays do not record.
- [ ] Add overlay command count and text glyph or quad count. Profiler counters. Done when: captures include both counts.
- [ ] Skip overlay command recording in benchmark mode unless requested. Benchmark and editor settings. Done when: benchmark mode reports overlays disabled or explicitly enabled.

### UX and diagnostics
- [ ] Add visible profiler mode state. Editor profiler UI. Done when: users can see low-overhead, detailed, or benchmark mode.
- [ ] Mark detailed profiler views as intrusive when they exceed budget. Profiler UI. Done when: the UI warns about high observer cost.
- [ ] Warn when profiler UI is one of the top frame costs. Profiler diagnostics. Done when: the profiler can diagnose itself.
- [ ] Stop selected-pass GPU profiling from dirtying unrelated command buffers. Vulkan selected-scope timestamp instrumentation. Done when: a profiled pass changes record or reuse decisions only for command buffers containing the selected scope, and a unit test covers cache identity.
- [ ] Add CI regression enforcement for RenderBench Compare and Gate presets. `Tools/Benchmarks/Invoke-RenderProfileComparison.ps1`, CI workflow. Done when: CI runs Gate on a controlled hardware runner and fails on a regression after variance is acceptable.
- [ ] Correlate executed passes and realized resources with GPU timings for `AdvancedRenderPipeline`. GPU profile dump and camera post-process state capture. Done when: a GPU profile row names the executed pass, realized resources, and camera feature fields.
- [ ] Make one bounded shader, resource, or quality-policy fix for the most expensive attributed `AdvancedRenderPipeline` effect. Advanced pipeline effect code. Done when: the fix targets the attributed effect and keeps a matched image and temporal-quality gate.

## Decisions Needed
- [ ] Choose the controlled hardware runner for RenderBench CI Gate. Owner: rendering lead.
- [ ] Choose the default profiler mode for editor benchmark captures. Owner: editor lead.

## Out Of Scope
- Redefining the workstream-01 benchmark contract.
- Optimizing engine rendering cost that is not caused by profiler or overlay observation.

# Rendering Profiler Counter Audit

Last Updated: 2026-05-29
Branch: `rendering-profiler-benchmarking`

This audit was captured before adding the v2 profiler fields required by
`rendering-profiler-and-benchmarking-todo.md`.

## Existing Coverage

- `Engine.Rendering.Stats.Frame`: draw calls, multi-draw calls, triangles.
- `GpuReadback`: mapped buffer count and readback bytes.
- `GpuFallback`: GPU-to-CPU fallback events, recovered commands, forbidden fallback events.
- `GpuMeshlets`: meshlet strategy requests, production/fallback/skipped frames, task records emitted, frustum/cone/Hi-Z culled records, expansion overflow, resident buffer bytes, delayed instrumentation readback bytes, cache hits/misses/stale.
- `Vulkan`: rich Vulkan indirect, barrier, bind churn, descriptor fallback/failure, allocation, validation, lifecycle, and GPU-driven stage timings.
- `Vr`: left/right draw and visible counts, worker build time, submit/wait/end-frame timing, predicted pose deltas, missed deadlines, tracking loss, pacing idle/stall counters.
- `Vram`: VRAM allocation totals and FBO bandwidth/bind counts.
- `RenderPipelineGpuProfiler`: OpenGL timestamp-query pass timings with delayed resolution, pending-sample handling, throttling after slow query calls, and dump files.
- `Engine.ProfileCapture`: launch/runtime NDJSON frame capture and manifest, currently schema v1.
- `RenderStatsPacket`: MemoryPack render stats packet used by UDP and in-process profiler data sources.
- `Tools/Measure-GameLoopRenderPipeline.ps1`: strategy sweep harness with cache clearing, warmup/capture split, graceful shutdown, p50/p95/p99 summary, and zero-readback violation checks.

## Missing Or Misleading Coverage

- OpenGL renderer state churn was mostly invisible: no shader program switch, pipeline switch, VAO bind/skip, buffer target bind, texture bind/skip, active texture unit switch, uniform/sampler call, barrier-kind, indirect-count draw, redundant-state skip, active texture-binding rung, or strategy/pass split fields in v1 capture.
- Scene and asset attribution was too coarse: a high-material or skinned avatar could collapse FPS without a per-frame visible renderer/submesh/triangle/material/texture/skinning row that identifies the asset or cooked representation.
- Shader cache state was partially observable through logs but not exposed as requested/warming/linked/failed/disk-cache/generated counters in profile captures.
- Texture streaming/upload cost existed in texture diagnostics but was not surfaced as frame-level upload jobs, upload bytes, upload time, or resident texture memory in the render capture.
- GPU-driven OpenGL zero-readback paths did not expose active bucket work, empty-bucket skips, full scans, material scatter dispatches, compacted draw-count diagnostics, compaction overflow kinds, Hi-Z one/two-phase mode, or visibility-buffer counters in the generic capture.
- Delayed diagnostic readbacks were not explicitly marked as delayed diagnostics in the NDJSON schema, making zero-readback proof ambiguous.
- GPU timestamp instrumentation exposed timing readiness and pass timings, but not query count, query readback bytes, or whether dense diagnostic mode was enabled.
- VR captures could be mistaken for desktop captures because the manifest did not carry target refresh, frame budget, stereo mode, per-eye timing availability, VRS state, validation/debug warnings, or motion-vector coverage.
- Benchmark manifests omitted several reproducibility variables: backend/GPU/driver, scene label, camera, lights, viewport/render scale, cache mode, shader/texture cache mode, GPU clock policy, validation/debug state, and invalid-environment diagnostics.
- Environment override validation was split across ad hoc scripts and permissive runtime parsers; invalid enum values could be silently ignored in engine launch captures.

# Vulkan Pipeline Cache And Prewarm - 2026-07-16

## Problem

Vulkan graphics-pipeline jobs were created with a null cache, pipeline identity
was not stable across processes, and several mesh paths discovered a missing
pipeline only after command-buffer recording had begun. A pending asynchronous
compile could therefore make `RecordDraw` emit no commands for that frame. This
explained startup/streaming pop-in and made driver compilation cost look like
ordinary command-recording time.

## Root causes

- Background jobs did not use the renderer's persistent `VkPipelineCache`.
- Persisted program, pass, vertex-layout, descriptor-layout, and fixed-function
  identities used process-randomized `System.HashCode` values.
- The prewarm database described too little state and did not distinguish keys
  loaded at startup from entries first observed in the current process.
- Scheduled mesh packets prewarmed before their secondary began, but inline
  primary and dynamic-UI paths could still request a pipeline after
  `vkBeginCommandBuffer`.
- Completed compile jobs remained observable long enough to create redundant
  application-cache misses and queue pressure.

## Changes

- All background graphics-pipeline jobs use the persistent internally
  synchronized Vulkan cache. When supported, workers first use
  `VK_PIPELINE_CREATE_FAIL_ON_PIPELINE_COMPILE_REQUIRED_BIT` and report
  persisted hit, same-process hit, compile-required miss, queue depth/capacity,
  and creation duration independently from the application object-cache lookup.
- The compile queue is bounded, publishes successful results directly into the
  renderer-wide shared pipeline cache, and removes completed jobs.
- Stable deterministic 64-bit hashes replaced process-randomized hashes in the
  persisted program, pass, vertex, descriptor, feature, and fixed-state
  identities.
- The version-5 prewarm database includes device, driver, API, active feature
  profile, shader artifacts, descriptor/vertex layouts, render-pass formats,
  specialization-relevant feature state, and full fixed-function state.
- Primary, dynamic-UI, and scheduled-secondary mesh recording now prewarms every
  required topology before `vkBeginCommandBuffer`. Pending or capacity-limited
  work defers the whole recording attempt, so no partial command stream or
  silent missing draw is produced.
- Pipeline-cache and prewarm databases auto-save during normal startup rather
  than relying only on orderly teardown.

## Validation

- Runtime build: zero compile errors and no newly introduced warning. Existing
  Magick.NET advisories and two unrelated SurfelGI field warnings remain.
- Focused Vulkan command-chain/pipeline/descriptor suite: 100 passed, 0 failed.
- Focused P0.5 suite: 7 passed, 0 failed. It covers deterministic identity,
  queue bounds/publication, pre-begin ordering for primary/dynamic-UI/scheduled
  paths, imported-texture identity compatibility, motion-vector/material/
  fixed-state variants, and save/reload/device-profile rejection.
- Cold-cache evidence:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_16-02-16_pid524`.
  The normal worker path compiled asynchronously through a bounded queue; two
  capacity rejections were explicit, with zero VUID, validation error,
  `InvalidOperationException`, or device loss.
- Deterministic v5 database population:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_16-07-33_pid7720`.
  It loaded 3,393,588 persisted driver-cache bytes, created the v5 identity set,
  and completed with zero validation/device-loss failure.
- First identical v5 warm lookup:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_16-08-32_pid38124`.
  It loaded 162 descriptions from the prior process; representative program,
  vertex-layout, and descriptor-layout hashes matched exactly across launches.
- Final pre-begin warm run with `StandardValidation`:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_16-20-41_pid30512`.
  It loaded 3,430,526 persisted bytes and recorded 238 background
  `PersistedHit` probes, zero compile-required misses, zero runtime/unknown
  outcomes, zero draw that emitted no commands, zero VUID/validation error,
  zero `InvalidOperationException`, and zero device loss. Eight primary and one
  dynamic-UI attempts were intentionally deferred before
  `vkBeginCommandBuffer`; one full queue reported its capacity instead of
  dropping a draw. Four startup enqueue skips were explicit program/buffer
  preparation states during scene streaming, not pipeline-ready draws that
  disappeared during recording.

## Remaining work

P0.5 correctness is closed. P0.7 still owns repeated warmed Release camera
measurements and external trace evidence for final p50/p95/p99/worst-frame and
allocation claims.

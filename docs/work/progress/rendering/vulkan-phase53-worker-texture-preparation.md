# Phase 5.3: worker-owned texture preparation

## First slice

Make imported Vulkan texture preparation worker-only. Source decode, cache
parsing, resizing and mip generation already use bounded decode workers. The
remaining synchronous upload-preparation branch and incomplete worker cleanup
must not undermine that separation.

The render owner retains lightweight wrapper/job admission, nonblocking result
observation, graphics-queue submission and timeline-ordered publication. Image
and staging preparation stays on workers. A started job remains owned until its
result has been observed, including when its request becomes stale or the
backend retires. Retirement must not free resources a worker can still access.

`XRE_VULKAN_ASYNC_TEXTURE_UPLOAD=0` and
`XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER=0` no longer opt imported Vulkan uploads
into synchronous preparation. These existing diagnostic settings remain
parsable, but the imported path reports the override as ignored.

## Validation (2026-08-30)

The first slice passes its source review, narrow Vulkan build and isolated
Release Editor build (0 warnings, 0 errors; Editor 64.25 seconds). The clean
process launched with both legacy flags explicitly false and
`GpuIndirectZeroReadback`. All 78 preparation tasks completed, with zero
retained workers, pending preparation/transfers/publications or reported upload
failures after settling. Both requested flags read back as 0, WorkerOnly as 1,
and the ignored-disable diagnostic fired. Render-thread prep is a structural
zero because that path was removed, not a sampled execution-time measurement.

Nine additional normal-depth open/moderate/foliage Disabled/full/coarse cases
matched the prior raw albedo hashes, with 180 fresh completed GPU timing
samples. The new moderate capture was visually inspected: textured columns,
banners and foliage are present. This is narrow imported-upload and albedo
acceptance, not complete native Advanced shading or whole-engine parity.

Lifetime review found and fixed two worker-slot starvation paths (stale jobs
dropped before observation, and completed jobs excluded by required-manifest
priority) and a cleanup path that swallowed the quiescence timeout. A bounded,
nonallocating preselection pass now observes all completed owned workers;
pending results survive until accepted submission or cleanup. Direct device
cleanup requires successful quiescence before any native teardown and leaves
CleanedUp false on failure. Source review passed after these corrections.
Gate denial, stale/canceled work and timeout/device-loss ownership were reviewed,
but live fault-injection acceptance has not been performed and is not claimed.

Evidence under
`Build/_AgentValidation/20260830-124809-phase52-bounded-rendering/`:
`logs/phase53-worker-only-editor-build-launch.log`,
`reports/phase53-worker-only-runtime.json`, and
`reports/phase53-worker-only-normal-parity.json`. Release runtime category logs
were unavailable, so absence of VUIDs/exceptions in those logs is not evidence
of a zero-validation-error run. The subsequent scene-growth timer stop was
isolated to a canonical reverse-dependency manifest treating physically retained
tombstones as live owners, not an upload-worker failure. That prerequisite is
repaired and passes scene deactivate/reactivate, primitive mutation and 256-box
growth checkpoints; see the Phase 5.2 investigation.

The final integrated Release Editor build also passed (15.53 seconds, zero
warnings/errors), again settling all 78 worker uploads with both legacy flags
false. It includes the auto-exposure history-before-manifest repair. Eighteen
odd-resolution normal/reversed-depth mode cases matched their same-view control,
and native rendering continued through 1920x1080 → 1279x719 → 1920x1080. These
checks extend integration coverage; they do not add cancellation/device-loss
fault-injection evidence or complete the remaining Phase 5.3 items.

The existing `get_texture_streaming_summary` MCP response includes on-demand
`backend_upload_diagnostics`. It is backend-wide diagnostic telemetry, not an
atomic frame sample or a per-texture measurement.

This slice does not complete upload coalescing/chunking, descriptor publication
budgets, pipeline warmup/cache persistence or every Phase 5.3 checklist item.
Phase 5.2's runtime/performance acceptance is tracked independently in its
investigation; source completion is not permission to check unproven gates.

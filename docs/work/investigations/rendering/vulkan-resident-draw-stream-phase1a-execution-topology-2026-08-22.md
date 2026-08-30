# Vulkan Resident Draw Stream Phase 1A — Execution Topology

Date: 2026-08-22  
Status: Complete — Phase 1A accepted  
Tracker: [Vulkan resident draw stream and render task pool](../../todo/rendering/optimization/vulkan-resident-draw-stream-and-render-task-pool-todo.md)

## Problem statement

Introduce the process-wide `EngineExecutionTopology` and its startup settings
without changing Vulkan or OpenXR command-recording ownership. Invalid explicit
CPU oversubscription must fail visibly, while the default startup configuration
must preserve the existing general `JobManager` worker count and Vulkan output.

## Slice boundary

- Add the renderer-neutral topology resolver and immutable startup snapshot.
- Add engine/project/user/environment execution settings and startup diagnostics.
- Resolve the topology before the engine-owned `JobManager` is configured.
- Keep the existing Vulkan command-chain workers, OpenXR eye workers, command
  pools, recording order, and submission paths unchanged.
- Defer `EngineWorkScheduler`, pooled render batches, lane attachments, and
  worker migration to the next Phase 1 slice.

## Baseline

The immediately preceding meshlet Gate 7 Release Vulkan smoke used the same
working tree and Sponza fixture. It retained 131 samples, rendered through
`GpuMeshletZeroReadback`, consumed 7,074/7,074 selected Vulkan draws and
10,638/10,638 draws across all outputs, and reported zero generic readback,
fallback, forbidden fallback, and Vulkan VUID counters.

## Validation log

| Check | Result | Evidence |
| --- | --- | --- |
| Debug editor build after topology/settings integration | Pass | 0 warnings, 0 errors on 2026-08-22. |
| Default startup topology | Pass | Debug Vulkan startup resolved `processors=32 foreground=4 general=16 render=0 dedicated=0 total=20 oversubscribed=False`; the engine reached renderer initialization. Log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-08-22_16-45-25_pid53472/log_general.log`. |
| Invalid explicit oversubscription | Pass | A 96-thread explicit request on 32 effective processors failed before window creation and reported foreground/general/render/dedicated/total counts plus the diagnostic opt-in. Log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-08-22_16-45-55_pid74212/startup-failure.log`. |
| Settings/schema generation | Pass | `Tools/Generate-UnitTestingWorldSettings.ps1` completed. The schema also incorporated two pending meshlet-world settings from the preceding closeout work. |
| Focused Debug topology/job/persistence tests | Pass | 15/15. Evidence: `Build/_AgentValidation/20260822-163016-execution-topology/reports/tests/execution-topology-focused.trx`. |
| Read-only architecture review | Pass after fixes | No Vulkan/OpenXR ownership regression or startup-order blocker. The review found an unbound lazy compatibility pool, incorrect general-worker UI cascade columns, three environment value-kind classifications, and incomplete requested/effective diagnostics. All four were corrected without touching the Vulkan backend. |
| Focused post-review topology/job/settings tests | Pass | 18/18. Evidence: `Build/_AgentValidation/20260822-163016-execution-topology/reports/tests/execution-topology-review-fixes.trx`. |
| Broad Release topology plus Vulkan/meshlet regression tests | Pass | 104/104, including meshlet closeout, strict zero-readback/hardening, strategy resolver, cache/import, job manager, settings persistence, environment metadata, and topology tests. The final run followed the compatibility-pool partial-class split. Evidence: `Build/_AgentValidation/20260822-163016-execution-topology/reports/tests/execution-topology-vulkan-release-final-after-split.trx`. |
| Release editor build | Pass | 0 warnings, 0 errors. |
| Matched Release Vulkan `GpuMeshletZeroReadback` smoke | Pass | Five accepted runs, 680 retained samples, 36,720/36,720 selected draws and 55,458/55,458 draws across all outputs, 10 production frames and 10 mesh-task frame-op dispatches. Zero readback bytes, mapped buffers, fallback, forbidden fallback, VUIDs, capture-time meshlet-buffer rebuilds, or retirements. Evidence: `reports/vulkan-meshlet-matched`, `reports/vulkan-meshlet-repeat`, and `reports/vulkan-meshlet-repeat-gate7-cache` under the investigation run root. |
| Matched frame-time comparison | Pass | Five-run post-change median frame p50/p95/Vulkan-frame p50 = 9.986/11.518/8.805 ms, respectively 10.13%, 6.78%, and 8.75% below the preceding single-run Gate 7 baseline of 11.112/12.356/9.649 ms. Early post-change samples varied in both directions, so no sustained regression signal is present. |
| Final post-review matched Vulkan smoke | Pass | 123 retained samples; 6,642/6,642 selected and 10,044/10,044 all-output draws consumed; two production frames and two mesh-task frame-op dispatches; zero readback, mappings, fallback, forbidden fallback, VUIDs, rebuilds, or retirements. Evidence: `Build/_AgentValidation/20260822-163016-execution-topology/reports/vulkan-meshlet-post-review-fix/summary.json`. |
| Vulkan implementation boundary | Pass | No file under `XREngine.Runtime.Rendering.Vulkan` was edited by Phase 1A. The existing Vulkan/OpenXR worker, command-pool, recording, submission, and presentation paths remain the closeout-validated implementation. |

## Result

Phase 1A is complete. The engine now owns one immutable, source-attributed CPU
budget before renderer construction, applies its general-domain count to the
existing `Engine.Jobs`, and rejects invalid explicit oversubscription before a
native window is created. Default render-worker count is zero, and nonzero
render settings remain declarative reservations for Phase 1B.

The matched Vulkan evidence shows exact draw consumption and no strict
zero-readback, fallback, lifetime, or validation regression. ShippingFast does
not retain GPU timing history, so the harness's optional MCP GPU-timing dump
reported no available history; this is expected for the strict zero-readback
profile and did not affect its render/counter acceptance gates.

The matched warm-cache fixture is intentionally not a material-parity view.
`TryLoadStandaloneCookedMeshlets` assigns one hard-coded deferred red material
because the standalone cache persists geometry, LOD, and meshlet payloads but
not the source model's material set. Its inherited Gate 3 camera sits only
0.08 units from the translated model origin, while the normal perspective near
plane is 0.1 and the model scale is 0.01. That explains the red output and
apparently distant near plane seen during the run: the camera pose is inside a
tiny validation subject, not a projection regression. Keep this fixture for
matched counter/performance comparisons only. Human visual checks must use a
source-material import and a normally framed flying camera.

Two exploratory two-run repeat packs pointed at older Gate 3 standalone-cache
roots and were correctly rejected with `productionFrames=0`. They are excluded
from all comparison numbers. The final repeat used the exact Gate 7 Sponza
cache. The isolated MCP session manager was also unavailable because retention
cleanup encountered a stale locked stopped-session log, so the official profile
harness supplied the live Vulkan startup, camera positioning, capture, counter,
and graceful-shutdown evidence instead.

## Next slice

Phase 1B is complete. See the
[shared-scheduler investigation](vulkan-resident-draw-stream-phase1b-scheduler-2026-08-22.md)
for the process-wide `EngineWorkScheduler`, pooled render batches, lifetime
hardening, tests, and matched Vulkan parity evidence. At this investigation's
boundary Vulkan recording still used its existing workers; Phase 4.3 completed
the render-domain lane migration on 2026-08-29.

# Vulkan CPU-Query And Monado Regression Investigation

Date: 2026-07-14
Status: Correctness fixes implemented and short-smoke validated; throughput gate remains open
Related TODO: [Vulkan core hardening and device-loss TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md)

## Problem

Immediately after the Phase 5.2.4b closeout work, three symptoms remained in the
current worktree:

- desktop Vulkan throughput was poor;
- `CpuQueryAsync` appeared not to cull the main desktop viewport; and
- Monado eye images repeatedly flickered between rendered content and black.

The investigation used current-binary desktop and Monado runs rather than
assuming the previous 300-frame acceptance cohort still represented the
worktree.

## Findings

### Desktop CPU-query culling was stale on reusable primaries

The clean-primary reuse path refreshed ordinary reusable frame data but did not
refresh query frame operations. A reused primary could therefore preserve an
old query epoch and visibility decision set. The reuse path now prepares query
operations for the new epoch before accepting the cached primary.

A desktop-only current-binary run then reported 46-76 tested meshes, 32-52
culled meshes, and 14-38 rendered meshes while continuing to submit and resolve
queries with a bounded pending set. In the same scene, the diagnostic disabled
mode rendered all 393 meshes. The observed p50 frame time was approximately
25 ms with `CpuQueryAsync`, versus 92.3 ms with culling disabled. This proves the
main desktop viewport is doing useful current-view culling; it does not yet
prove sustained query convergence under the much heavier independent-desktop
plus OpenXR workload.

### Black eye flicker was rejected work, not compositor presentation

The black frames correlated with Vulkan submission rejection. Three resources
were captured from an earlier output plan and later referenced by the OpenXR
eye command buffer:

1. Generated `DummyShadowMapArray` upload recreated and retired its own
   compatible dedicated image immediately after allocation.
2. `HBAOPlusBlurIntermediateTexture` in a descriptor snapshot still referred
   to the desktop physical plan.
3. `DepthStencil` framebuffer targets still referred to the desktop physical
   plan after the eye plan became active.

Compatible full-texture uploads now preserve their existing dedicated Vulkan
image. During external-target preparation, named pipeline textures and
framebuffer targets are rebased through the active output resource registry
after the output-specific plan has been published. Material-owned textures are
not rebased.

The final short Monado run recorded zero rejected queue submissions, zero
`ErrorValidationFailed` results, eight successful strict-SPS submissions, and
no sequential fallback. All five retained frames contained projection layers.
The only harness failure was the independent output ledger exceeding its
16-entry diagnostic capacity.

### Remaining low frame rate is CPU/output orchestration

The GPU workload alone does not explain the stalls. The final smoke reported
roughly 1.8-22.8 ms of GPU work while retained total-frame time ranged from
18 ms to 9.8 seconds. `FullIndependentRender` requested both a 300x170 desktop
preview and a 1537x865 main desktop render in addition to the 896x1007x2 true
SPS eye render. The output-request ledger grew from 10 to 17 entries per frame.

During the worst retained frames, tracked descriptor sets grew as high as
26,664 and live resource records as high as 42,868. A CPU profile found render
dispatch p50 near 119 ms, intermittent 500-800 ms stalls, and command-recording
allocation commonly in the multi-megabyte range, with a representative value
near 20 MB. Hot scopes included pipeline lookup/creation, descriptor binding,
pass transitions, and submit-fence waits.

An experiment that enabled one cached secondary command chain per draw reduced
some GPU time but expanded to 2,451 command buffers and 5,144 live resources,
then timed out. That design was rejected. The current safeguard keeps query
brackets inline, skips external CpuQueryAsync command-chain construction, and
records schedules larger than 64 chains inline. A bounded grouped-secondary
arena is still required.

## Implemented Changes

- Refresh query epochs before accepting a clean reusable primary command
  buffer.
- Keep query brackets inline while allowing unrelated bounded command chains.
- Give command-chain keys structural identity that is stable when the visible
  subset changes, and back off when recording outpaces reuse.
- Cap the current one-pool/one-buffer-per-chain schedule at 64 chains.
- Do not construct an OpenXR command-chain schedule when `CpuQueryAsync` must
  record inline.
- Preserve a compatible dedicated image during generated full-texture upload.
- Rebase external-target pipeline-resource descriptors and framebuffer targets
  to the active output plan before wrapper refresh and recording.

## Evidence

| Run | Result |
|---|---|
| Desktop current-binary query run, engine session `xrengine_2026-07-14_10-58-00_pid17376` | Nonzero tested/culled/rendered work, bounded query progress, live screenshots visually correct |
| `Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/monado-safe-bounded/` | Dummy shadow fix held; exposed stale HBAO descriptor resource |
| `Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/monado-rebased-plan/` | HBAO rejection removed; exposed stale `DepthStencil` framebuffer target |
| `Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/monado-target-rebase/` | Zero submission rejections, eight strict-SPS successes, retained projection layers; output-ledger capacity was the sole smoke failure |
| `Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/monado-cpu-profile/` | CPU allocation, descriptor, pipeline, and submission hotspots captured |

Desktop screenshots inspected during the query run are under the current task's
`mcp-captures/` folder as `Screenshot_20260714_110032.png` and
`Screenshot_20260714_110104.png`. The final Monado summary is
`monado-target-rebase/reports/openxr-smoke-summary.json`; its filtered Vulkan
log is `monado-target-rebase/logs/engine/log_vulkan.log`.

## Gate And Next Work

Do not treat the current worktree as ready for Phase 5.2.5 merely because the
black flicker is gone. First close the new regression hold in Phase 5.2.4b with
a longer current-binary run that proves:

- zero retired-resource or validation submission rejection;
- sustained independent desktop and SPS query submission/resolution/culling;
- bounded output records, descriptors, resources, and command buffers; and
- visually continuous desktop and both-eye images.

The remaining throughput root cause belongs to the already planned Phase 5.2.5
through 5.2.7 architecture: persistent versioned output arenas, one immutable
scene snapshot grouped into compatible view families, and deadline-aware output
scheduling. In particular, grouped secondary buffers must replace the rejected
per-draw pool/buffer model, and duplicate output telemetry/render requests must
be separated and deduplicated before capacity checks.

## F5 Freeze Follow-Up

A later current-worktree F5 run, engine session
`xrengine_2026-07-14_12-14-08_pid31772`, remained executable but the editor UI
was effectively frozen. Repeated debugger pauses landed in
`TryGetPendingImageAccessState` while it compared an image handle and range.
That condition was the inner loop of a reverse linear scan over every image
access delta accumulated by the command buffer, not a blocking Vulkan call.

The workload amplified the scan severely. Lifetime telemetry grew from 9,092
live records and 3,308 descriptor sets to 39,171 and 22,768 respectively, while
the command-buffer count remained 29-33. The session emitted 8,289 buffer
allocation events, including 5,771 uniform-buffer allocations, and render-thread
uploads waited 41-65 seconds. Repeated descriptor/barrier state queries against
the growing delta history therefore approached quadratic CPU work.

The command-local tracker now keeps a latest-state dictionary keyed by atomic
`(image, mip, layer, aspect)` identity. Recording updates that index and retains
the compact range-delta list only as the publication journal. Lookups combine
only the requested indexed subresources, reject missing or incompatible
multi-subresource state, and never scan historical deltas.

The focused Phase 5.2.4 tracking tests passed 11/11. A
current-binary Monado smoke then completed successfully in 43 seconds with five
retained frames, eight strict-SPS submissions, zero submission rejections, and
zero warnings/failures. Frame recording stayed between 0.95 and 1.20 ms. The
first four retained frames stayed at 1,820-1,822 live records and 452 descriptor
sets; the fifth rose to 3,157 and 940 as additional workload arrived. Total
frame time was 17.6-40.7 ms for those first four frames and 142.5 ms for the
fifth, rather than the prior multi-second freeze.

Raw evidence is under
`Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/image-access-index-live/`.
The run still accumulated 35,730 live records and 24,680 descriptor sets during
later startup/teardown work and logged render-thread waits up to 4.5 seconds.
The indexed lookup removes the quadratic freeze, but the separate descriptor,
uniform-buffer, and multi-output resource growth remains an open throughput
issue for the Phase 5.2.5-5.2.7 architecture work.

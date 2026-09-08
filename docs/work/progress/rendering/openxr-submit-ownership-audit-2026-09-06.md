# OpenXR Vulkan submit ownership audit (2026-09-06)

Baseline: `8b104bf7a` (source-only audit; no build, test, or runtime claim).

This inventory closes the ordinary `SubmitAndWaitOpenXrCommandBuffer(s)` wrapper
callsite question for XR-A01. A repository-wide `rg` search found no production
caller of these wrappers: the only matches outside their declaring file are
string assertions in `XREngine.UnitTests/Rendering/OpenXrTimingPipelineContractTests.cs:239`
and `XREngine.UnitTests/Rendering/VulkanCommandChainDataModelTests.cs:1171`.
The actual XR paths call the richer `SubmitAndWaitOpenXr(VulkanOpenXrSubmissionInput)`
entry point directly.

## Production XR route ownership

| Route / sender | Reservation and acceptance receiver | Failure / completion settlement |
|---|---|---|
| Ordinary single eye, `VulkanFrameLoop.TryRenderOpenXrEyeSwapchain` (`VulkanFrameLoop.OpenXR.EyeRendering.cs:17`) | Reserve `:37`; register `:81`; central submit `:108`; acceptance sink is `VulkanCommandRuntime.OpenXrSubmission.cs:207` | `CancelReservation` `:51` and `CancelPreparedSubmission` `:139`; tracker poll/retire `OpenXrVulkanSubmissionTracker.cs:614,679,699` |
| Ordinary paired eyes, `TryRenderOpenXrEyeSwapchains` (`EyeRendering.cs:144`) | Reserve `:148`; register `:258`; central paired submit `:285`; same acceptance sink `OpenXrSubmission.cs:207` | paired upload cancellation and ticket cancellation `EyeRendering.cs:316-327`; tracker poll/retire |
| Parallel worker eye recording, `VulkanFrameLoop.OpenXR.EyeRecordWorkers.cs:12` | Reserve `:12`; worker registers in `VulkanOpenXrEyeWorkerCommandService.cs:101`; worker submits at `:135`; central acceptance sink | Worker prepared-ticket cancellation `EyeRecordWorkers.cs:131` / `VulkanOpenXrEyeWorkerCommandService.cs:153`; tracker poll/retire |
| Strict true-SPS external swapchain, `VulkanXrGraphicsBinding.Implementation.cs:1628,1668` | Route selects true-SPS and render/publish; underlying batched eye path registers and enters central submit; output receipt recording `:1932-2017` | Strict no-fallback rejection/failure branches `:1361-1541`; prepared tickets and tracker retirement settle the submission |
| Mirror single eye, `TryRenderOpenXrEyeMirrorFrameBuffer` (`VulkanFrameLoop.OpenXR.MirrorPreview.cs:18`) | Reserve `:40`; `SubmitTrackedOpenXrMirrorSubmission` `:546`; register `:563`; central submit `:579` | Upload cancellation `:88,103`; ticket cancellation `:116`; tracker poll/retire |
| Mirror paired eyes, `TryRenderOpenXrEyeMirrorFrameBuffers` (`MirrorPreview.cs:121`) | Reserve `:125`; same tracked helper/acceptance receiver `:546-579` | Batch/upload cancellation `:186,201`; ticket cancellation `:216`; tracker poll/retire |
| Mirror render plus publish, `TryRenderAndPublishOpenXrEyeMirrorFrameBuffers` (`MirrorPreview.cs:221`) | Reserve `:232`; same tracked helper `:546-579` | Upload cancellation `:310,342`; ticket cancellation `:359`; tracker poll/retire |
| True-SPS array render plus publish, `TryRenderAndBlitTextureArrayLayersToOpenXrSwapchainImages` (`MirrorPreview.cs:364`) | Reserve `:381`; same tracked helper `:546-579` | Upload cancellation `:482,526`; ticket cancellation `:541`; tracker poll/retire |
| Preview-only temporary commands, `VulkanCommandRuntime.OpenXrMirrorPreview.cs` | Reservations `:347,439`; `SubmitTrackedOpenXrTemporaryCommand` calls `:322,404,525`; register `:551`; central submit `:563` | Ticket cancellation `:336,418,539`; local polls `:346,438`; tracker retirement |

| Wrapper | Declaration and exact parameters | Callsite status | Ownership behavior |
|---|---|---|---|
| Single command | `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanCommandRuntime.OpenXrSubmission.cs:14` — one `CommandBuffer`, `out bool completed`, optional diagnostic context | No production callsite found | Builds a one-buffer input and delegates to the central submit path; therefore source-defined settled/rejected/accepted behavior is inherited, but this overload is currently unused externally. |
| Paired commands | `.../VulkanCommandRuntime.OpenXrSubmission.cs:21` — two `CommandBuffer` values, `out bool completed`, optional diagnostic context | No production callsite found | Builds a two-buffer input and delegates centrally; currently unused. |
| Pointer range, basic result | `.../VulkanCommandRuntime.OpenXrSubmission.cs:28` — `CommandBuffer*`, count, `out bool completed`, optional diagnostic context | No production callsite found; only internal forwarding from line 33 | Rejects null or counts outside 1..3 at `:49`; otherwise delegates to the richer overload. |
| Pointer range, disposition result | `.../VulkanCommandRuntime.OpenXrSubmission.cs:41` — pointer, count, `out bool completed`, `out EVulkanQueueSubmissionDisposition`, `out EOpenXrStrictSpsFaultInjectionStage`, optional diagnostic context | No production callsite found | Initializes outputs to incomplete/not-submitted/no-fault at `:46-48`; invalid pointer/count returns false at `:49`; valid 1..3 count delegates to central submit at `:52-59`. |

## Shared central lifecycle

The actual XR eye, paired-eye, worker-parallel, mirror, preview, render-plus-
publish, and true-SPS paths use the central entry point in
`VulkanCommandRuntime.OpenXrSubmission.cs:87`. Reservation and registration are
owned by `OpenXrVulkanSubmissionTracker.TryReserveSubmission` (`OpenXrVulkanSubmissionTracker.cs:271`)
and `RegisterSubmission` (`:338`). Native acceptance is committed through
`AcceptedSubmissionSink` (`:111-131`) and `CommitAcceptedSubmission` (`:496`).
Completion settlement is polled at `VulkanCommandRuntime.OpenXrSubmission.cs:343`
and retired by `OpenXrVulkanSubmissionTracker.cs:614,679,699`.

Settled cases represented in source are:

- accepted native submit: accepted sink commits the tracked submission, then
  polling retires it on completion;
- rejected or pre-submit failure: callers invoke
  `CancelPreparedSubmission`/`CancelReservation` (for example ordinary eyes in
  `VulkanFrameLoop.OpenXR.EyeRendering.cs:51,139,327` and mirror paths in
  `VulkanFrameLoop.OpenXR.MirrorPreview.cs:116,216,359,541`);
- invalid wrapper input: pointer overload returns `false` with
  `NotSubmitted`, `completed=false`, and no injected fault stage;
- exceptional teardown: XR binding drains the tracker at
  `VulkanXrGraphicsBinding.cs:477`.

The unused ordinary wrappers are an API boundary rather than evidence of a
runtime path. Their declarations provide deterministic rejection and delegation
semantics, but no runtime validation can be attributed to them until a real
caller exists. This document intentionally records source ownership only.

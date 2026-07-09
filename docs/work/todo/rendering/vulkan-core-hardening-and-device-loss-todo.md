# Vulkan Core Hardening And Device-Loss TODO

Last Updated: 2026-07-09
Owner: Rendering
Status: Phase 2 Frame-Op Context Isolation Implemented; Hardware KHR/OpenXR Stress Pending
Target Branch: `rendering-vulkan-core-hardening`

## Goal

Make Vulkan robust enough for normal editor, OpenXR, scene-capture, light-probe,
shadow, UI-preview, and diagnostic rendering without recurring
`VK_ERROR_DEVICE_LOST` failures caused by cross-context resource churn, stale
descriptors, unsafe resource retirement, or oversized GPU submissions.

This work is not about hiding Vulkan failures behind silent fallbacks. It should
make invalid states visible earlier, isolate independent frame operations, and
turn device-loss investigations into actionable diagnostics.

## Recent Trigger

The July 9, 2026 light-probe capture crash showed the current failure shape:

- `LightProbeBatch` was active and reached probe 10 of 36.
- OpenXR Vulkan eye rendering then reported device loss from an eye fence wait.
- Vulkan logs immediately before device loss showed heavy capture-sized
  resource-plan churn for `SceneCaptureEnvDepth`, `LightProbeEnvColor`,
  G-buffer resources, post-process resources, bloom, exposure, and final output.
- Scene/light-probe capture had a caller-owned cubemap FBO, but still entered
  the full viewport/post/temporal command path.

The immediate mitigation is to route Vulkan light-probe capture through the
direct FBO path. This todo tracks the broader engine fixes so the same class of
failure does not reappear in another auxiliary render path.

## Scope

- Vulkan renderer command submission, frame-op modeling, resource planning,
  descriptor binding, image-layout tracking, and resource lifetime.
- OpenXR Vulkan eye/mirror rendering, especially synchronization between XR
  swapchain work and auxiliary renderer work.
- Scene capture, light probes, reflection/GI probes, shadow captures, UI preview
  viewports, and diagnostic framebuffer captures.
- Tooling for validation layers, RenderDoc/Nsight/RGP captures, crash
  breadcrumbs, profiler counters, and log summaries.

## Non-Goals

- Do not replace Vulkan with OpenGL fallback behavior.
- Do not require DX12 work to land first.
- Do not rewrite every render pipeline before isolating the crash-prone paths.
- Do not accept a fix that only catches `VK_ERROR_DEVICE_LOST` after the GPU is
  already unrecoverable.
- Do not treat in-process logical-device recreation as part of the first
  hardening pass. First make loss containment deterministic, preserve diagnostic
  evidence, and exit or restart the renderer through an explicit operator policy.

## Evidence And Reproducibility Rules

- Treat the first observed failing Vulkan/OpenXR call as the detection point,
  not automatically as the root cause. Preserve validation messages, resource
  churn, allocation pressure, and submission history leading up to it.
- Every baseline and stress result must record:
  - engine commit and dirty-worktree state,
  - GPU vendor/device/driver and Vulkan API version,
  - Vulkan SDK and validation-layer versions,
  - enabled instance/device extensions and features,
  - OpenXR runtime name/version and active graphics requirements,
  - diagnostic preset and relevant environment/settings snapshot,
  - scene/settings hash, resolution, refresh rate, and run duration/frame count.
- Store durable summaries and exact reproduction commands in tracked work docs.
  Large captures and raw logs remain under the per-run
  `Build/_AgentValidation/<run>/` root.
- Keep a machine-readable result manifest for each validation run so two runs
  can be compared without scraping prose logs.

## Invariants

- Vulkan/OpenXR device loss must stop further GPU work immediately and report a
  precise reason, last submitted frame-op context, and known in-flight resource
  generations.
- Device-loss transition is first-writer-wins and thread-safe. Preserve the
  original failing API/result/context; later failures are secondary fallout.
- After confirmed device loss, no queue submit, wait, allocation, mapping,
  descriptor update, command recording, or resource-planner publication may
  begin. Only fault collection and best-effort teardown paths are permitted.
- Main viewport, OpenXR eyes, scene captures, probes, shadows, and UI previews
  must not accidentally mutate each other's resource-planner state.
- Explicit failures are preferred over silent CPU or lower-feature fallbacks.
- Descriptor image info must match the actual image view, sampler, and expected
  layout at the time commands are recorded and submitted.
- Resource destruction must be timeline/fence-safe. Destroying image views,
  framebuffers, descriptor references, buffers, or images still referenced by
  in-flight work is a bug.
- Long GPU work must be budgeted and sliced so Windows TDR and OpenXR frame
  timing are respected.
- Hot paths must avoid per-frame heap allocation, LINQ, captured closures,
  boxing, and string formatting except behind explicit diagnostics.
- Diagnostic features are capability-gated and additive. Unsupported standard
  or vendor extensions must be reported explicitly, not silently treated as
  successful diagnostic coverage.

## Phase 0 - Branch, Baseline, And Crash Taxonomy

Phase 0 implementation note: the first code/doc slice landed on
`rendering-vulkan-core-hardening`. Durable evidence and repeat-run manifest:
[vulkan-core-hardening-phase0-2026-07-09.md](../../progress/rendering/vulkan-core-hardening-phase0-2026-07-09.md).

- [x] Create dedicated branch `rendering-vulkan-core-hardening`.
- [ ] Record baseline Vulkan behavior for:
  - [ ] editor desktop viewport,
  - [x] OpenXR Vulkan eye rendering failing baseline from July 9 logs,
  - [ ] OpenXR mirror rendering,
  - [x] light-probe batch capture failing baseline from July 9 logs,
  - [ ] scene capture,
  - [ ] shadow rendering,
  - [ ] UI preview rendering.
- [x] Define a deterministic repro manifest for the July 9 scenario, including
  world settings, probe count/resolution, OpenXR runtime, headset refresh rate,
  render resolution, and exact launch command.
- [x] Preserve the July 9, 2026 probe crash summary in this document or a linked
  durable work note; do not depend on ignored `Build/_AgentValidation` files.
- [x] Categorize existing device-loss risks:
  - [x] stale descriptor/image view,
  - [x] layout transition mismatch,
  - [x] unsafe resource retirement,
  - [x] command buffer recorded for wrong frame-op context,
  - [x] OpenXR swapchain synchronization,
  - [x] oversized/long-running GPU submission,
  - [x] driver/runtime teardown race,
  - [x] incomplete framebuffer or render-pass metadata,
  - [x] device/host memory pressure or fragmentation,
  - [x] descriptor-pool exhaustion or invalid update-while-in-use,
  - [x] GPU virtual-address fault or shader out-of-bounds access,
  - [x] application race/external-synchronization violation.
- [x] Add a short renderer log summary that reports device loss with the last
  known frame-op context and submission kind.
- [ ] Define an explicit renderer device state machine such as `Healthy` ->
  `LossDetected` -> `CollectingFaultData` -> `Quiesced` -> `Disposed`, including
  which calls are legal in each state and how producer threads are cancelled.

Acceptance criteria:

- [ ] There is a repeatable baseline for at least one light-probe batch capture
  and one OpenXR Vulkan session.
- [x] Device-loss logs name the active frame op, output target, dimensions,
  command-buffer generation, and timeline/fence values where available.
- [ ] Repeated runs use the same repro manifest and produce a comparable result
  manifest rather than relying on visual memory.

## Phase 1 - Diagnostic Modes, Fault Data, And Validation Presets

- [x] Add a first-class Vulkan diagnostic resolver with named presets:
  - [x] `Off`,
  - [x] `StandardValidation`,
  - [x] `SyncValidation`,
  - [x] `GpuAssisted`,
  - [x] `BestPractices`,
  - [x] `CrashDiagnostics`,
  - [x] `RenderDocFriendly`.
- [x] Model the underlying checks as explicit flags/settings rather than one
  mutually exclusive enum. Log the resolved matrix, incompatibilities, and
  overhead warnings. In particular, keep standard validation, sync validation,
  GPU-assisted validation, vendor crash tooling, and capture-tool compatibility
  independently describable.
- [x] Wire presets to launch/environment settings, editor preferences, and logs.
- [x] Log every enabled Vulkan layer, extension, validation feature, and disabled
  feature with reason.
- [x] Prefer `VK_KHR_device_fault` when supported, with
  `VK_EXT_device_fault` compatibility where required by current bindings/drivers:
  - [x] enable the device-fault feature at logical-device creation,
  - [x] use `vkGetDeviceFaultReportsKHR` for asynchronous and internally
    recovered fault reports when KHR support is active and callable bindings are
    available; the current Silk.NET 2.23 path logs KHR exposure and falls back
    to the EXT compatibility collector because the KHR report/debug-info
    bindings are not exposed locally,
  - [x] call `vkGetDeviceFaultDebugInfoKHR` only after device loss when callable
    bindings are available; on the EXT compatibility path, query counts and then
    `vkGetDeviceFaultInfoEXT` only after the device is in the lost state,
    - [x] EXT compatibility path queries device-fault counts after device loss,
  - [x] persist the human-readable description, address/vendor records, and
    vendor binary before teardown,
  - [x] handle `VK_INCOMPLETE` and unavailable vendor binary data explicitly.
- [x] Optionally correlate fault address ranges with Vulkan objects through
  `VK_EXT_device_address_binding_report` when available.
- [x] Add capability-gated vendor diagnostics:
  - [x] `VK_NV_device_diagnostic_checkpoints` command-stream markers,
  - [x] `VK_NV_device_diagnostics_config` / Nsight Aftermath where licensed and
    locally available,
  - [x] equivalent AMD/Intel tooling hooks when available without making vendor
    tooling a runtime dependency; current implementation reports no native
    AMD/Intel runtime dependency and names the standard fallback artifacts.
- [x] Add debug names for:
  - [x] Vulkan images and image views,
  - [x] framebuffers,
  - [x] descriptor sets,
  - [x] command buffers,
  - [x] semaphores/fences,
  - [x] frame-op contexts,
  - [x] OpenXR swapchain image views.
- [x] Add a lightweight crash breadcrumb ring buffer:
  - [x] current frame id,
  - [x] command buffer id,
  - [x] frame-op context id,
  - [x] pass name,
  - [x] output target,
  - [x] active resource generation,
  - [x] image layout transitions,
  - [x] descriptor table generation.
- [x] Keep breadcrumb writes allocation-free and assign stable IDs whose backing
  storage remains valid until the associated submission completes. Include
  queue, submit serial, batch index, draw/dispatch/blit identity, and the first
  failing Vulkan/OpenXR API.
  - [x] Breadcrumb writes are allocation-free and include queue, submit serial,
    frame context, output target, command buffer, fence, timeline, caller,
    command marker, batch index, layout serial, descriptor generation, and first
    failing API.
- [x] Add a diagnostic report command or log footer that dumps the breadcrumb
  ring after device loss.
- [x] Emit validation messages in a structured form keyed by VUID/message ID,
  object handle/stable ID, frame-op context, and first/last occurrence; retain a
  bounded sample while counting duplicates.

Acceptance criteria:

- [x] A validation launch can be started from documented commands.
- [x] Device loss produces a concise breadcrumb summary without requiring a
  debugger.
- [x] Normal non-diagnostic runs keep validation overhead disabled.
- [x] Unsupported fault/checkpoint extensions are listed in the report, and a
  successful diagnostic run never implies that an unavailable check ran.

## Phase 1.1 - KHR Device-Fault Shim

Implement direct `VK_KHR_device_fault` support even while the current Silk.NET
2.23 generated bindings only expose the older `VK_EXT_device_fault` wrapper.
Keep the current EXT path as a compatibility fallback.

- [x] Add a small local KHR shim partial, for example
  `VulkanRenderer.KhrDeviceFault.cs`, containing only the missing KHR structs,
  enums, constants, and delegates needed by this renderer.
- [x] Define local KHR structure types and keep them aligned with Vulkan
  Registry values:
  - [x] `VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FAULT_FEATURES_KHR = 1000573000`,
  - [x] `VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FAULT_PROPERTIES_KHR = 1000573001`,
  - [x] `VK_STRUCTURE_TYPE_DEVICE_FAULT_INFO_KHR = 1000573002`,
  - [x] `VK_STRUCTURE_TYPE_DEVICE_FAULT_DEBUG_INFO_KHR = 1000573003`.
- [x] Define local layout-compatible structs for:
  - [x] `VkPhysicalDeviceFaultFeaturesKHR`,
  - [x] `VkPhysicalDeviceFaultPropertiesKHR`,
  - [x] `VkDeviceFaultAddressInfoKHR`,
  - [x] `VkDeviceFaultVendorInfoKHR`,
  - [x] `VkDeviceFaultInfoKHR`,
  - [x] `VkDeviceFaultDebugInfoKHR`,
  - [x] `VkDeviceFaultVendorBinaryHeaderVersionOneKHR`.
- [x] Query KHR feature support through the `vkGetPhysicalDeviceFeatures2`
  pNext chain when `VK_KHR_device_fault` is advertised.
- [x] Prefer enabling `VK_KHR_device_fault` at logical-device creation when
  advertised and supported; otherwise use the existing `VK_EXT_device_fault`
  compatibility path.
- [x] Enable KHR feature bits by diagnostic policy:
  - [x] `deviceFault` for crash diagnostics,
  - [x] `deviceFaultVendorBinary` when supported,
  - [x] `deviceFaultReportMasked` for `CrashDiagnostics`,
  - [x] keep `deviceFaultDeviceLostOnMasked` disabled by default unless an
    explicit aggressive diagnostic flag requests it.
- [x] After device creation, load KHR function pointers with
  `Vk.GetDeviceProcAddr`:
  - [x] `vkGetDeviceFaultReportsKHR`,
  - [x] `vkGetDeviceFaultDebugInfoKHR`.
- [x] Add a KHR report-drain path that calls `vkGetDeviceFaultReportsKHR` with a
  zero timeout during diagnostic footer generation and optionally a short,
  bounded timeout from a future fault-watcher path.
- [x] Persist KHR report data separately from EXT fault data, including:
  - [x] report flags,
  - [x] group ID,
  - [x] human-readable description,
  - [x] fault address info,
  - [x] instruction address info,
  - [x] vendor info,
  - [x] whether results were truncated by `VK_INCOMPLETE`.
- [x] After confirmed device loss, call `vkGetDeviceFaultDebugInfoKHR` and
  persist vendor binary crash-dump data before logical-device teardown.
- [x] Add logs that clearly state which path is active:
  - [x] `DeviceFaultKHR active`,
  - [x] `DeviceFaultEXT compatibility active`,
  - [x] `DeviceFault unavailable`,
  - [x] `KHR advertised but function pointer unavailable`.
- [x] Keep KHR polling out of normal non-diagnostic runs.
- [x] Add source-contract tests proving:
  - [x] local KHR shim structs/constants exist,
  - [x] KHR is preferred over EXT when advertised,
  - [x] KHR function pointers are loaded with `GetDeviceProcAddr`,
  - [x] KHR reports and debug-info artifacts are persisted,
  - [x] EXT fallback still remains wired.
- [ ] Validate with:
  - [x] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`,
  - [x] focused Vulkan Phase 1/1.1 tests,
  - [ ] one `CrashDiagnostics` launch on a machine whose driver advertises
    `VK_KHR_device_fault`, recording whether KHR reports are actually returned.
    Pending hardware/runtime validation after this source patch.

Acceptance criteria:

- [x] A driver that advertises `VK_KHR_device_fault` uses the KHR query path
  without requiring a Silk.NET package upgrade. Source path is implemented; the
  hardware launch above remains the runtime proof.
- [x] Device loss still persists EXT-compatible artifacts when KHR is absent or
  the KHR function pointers cannot be loaded.
- [x] Normal runs do not poll for KHR fault reports.
- [x] Logs make KHR-vs-EXT coverage unambiguous.

## Phase 2 - Frame-Op Context Isolation

- [x] Define a canonical `FrameOpContext` contract for every command-producing
  render operation:
  - [x] context kind (`MainViewport`, `OpenXrEye`, `OpenXrMirror`,
    `SceneCapture`, `LightProbeCapture`, `Shadow`, `UiPreview`,
    `DiagnosticCapture`),
  - [x] viewport identity,
  - [x] output FBO identity,
  - [x] output target image identity,
  - [x] display/internal dimensions,
  - [x] stereo/multiview flags,
  - [x] pass metadata,
  - [x] resource generation,
  - [x] descriptor generation,
  - [x] submission queue family.
- [x] Include a monotonically increasing context ID and immutable recording
  fingerprint. Do not use raw Vulkan handle reuse as resource identity.
- [x] Audit all paths that currently call `XRViewport.Render`,
  `XRRenderPipelineInstance.Render`, `VPRC_RenderQuadToFBO`,
  Vulkan blits, compute dispatches, and OpenXR eye rendering.
- [x] Require command-buffer recording to capture the exact `FrameOpContext`.
- [x] Reject or rerecord command buffers when the active context no longer
  matches the recorded context.
- [x] Make mismatch behavior explicit by build mode: fail-fast in diagnostics;
  discard and rerecord only when the operation is still valid and the device is
  healthy. Never submit a known mismatch.
- [x] Split resource-planner runtime state by frame-op context where dimensions,
  output target, pass metadata, or resource lifetime differ.
- [x] Ensure frame-op context switching cannot overwrite live OpenXR eye planner
  state during scene/probe capture.
- [x] Add tests that simulate alternating OpenXR eye rendering and light-probe
  capture contexts.
- [x] Add logs that distinguish metadata-only graph changes from allocation
  signature changes per context.

Acceptance criteria:

- [x] Alternating capture and OpenXR eye frames do not churn the same physical
  resources unless an explicit shared-resource policy says they may. Source
  isolation is implemented; live OpenXR/light-probe hardware stress remains the
  runtime proof.
- [x] A command buffer recorded for one context cannot be submitted under another
  context silently.

## Phase 3 - Dedicated Capture Pipeline

- [ ] Define capture policies for:
  - [ ] generic scene capture,
  - [ ] light probes,
  - [ ] reflection probes,
  - [ ] GI probes,
  - [ ] thumbnails/UI previews,
  - [ ] diagnostic FBO capture.
- [ ] Create a minimal Vulkan-safe capture pipeline path that renders only what
  capture actually needs:
  - [ ] pre-render hooks,
  - [ ] background/sky policy,
  - [ ] opaque deferred or opaque forward policy,
  - [ ] masked/transparent policy,
  - [ ] optional shadows,
  - [ ] no temporal history,
  - [ ] no auto exposure,
  - [ ] no bloom,
  - [ ] no TSR/TAA,
  - [ ] no vendor upscale,
  - [ ] no viewport final-output path.
- [ ] Decide whether capture uses a dedicated `RenderPipeline` type or a
  `DefaultRenderPipeline` capture variant.
- [ ] Keep `DefaultRenderPipeline` and `DefaultRenderPipeline2` behavior
  consistent while both exist.
- [ ] Make capture viewports opt into direct FBO commands by policy rather than
  by ad hoc property mutations.
- [ ] Add a debug overlay/log line that reports the effective capture policy.
- [ ] Confirm capture output orientation and clip-space policy for Vulkan and
  OpenGL.
- [ ] Validate that light-probe IBL generation uses only explicit fullscreen
  probe passes after cubemap capture.

Acceptance criteria:

- [ ] Light-probe capture no longer allocates or refreshes post-process,
  temporal, bloom, exposure, or final-output resources during cubemap face
  rendering.
- [ ] Probe captures still produce valid cubemap, octahedral, irradiance, and
  prefilter textures.
- [ ] Capture behavior is documented and controlled by explicit policy.

## Phase 4 - Resource Lifetime And Retirement

- [ ] Audit every Vulkan object destruction path:
  - [ ] images,
  - [ ] image views,
  - [ ] samplers,
  - [ ] framebuffers,
  - [ ] buffers,
  - [ ] descriptor sets/pools,
  - [ ] command buffers,
  - [ ] pipelines,
  - [ ] query pools.
- [ ] Distinguish CPU ownership, submitted GPU use, completed GPU use, external
  OpenXR ownership, and pending destruction in the resource state model.
- [ ] Replace immediate destruction with timeline/fence-retired destruction
  where any in-flight command can still reference the object.
- [ ] Track the last submit/timeline value for resources used by each frame-op.
- [ ] Block or defer descriptor-reference release if the owning resource is still
  in-flight.
- [ ] Add diagnostics for retirement queue depth, oldest retired generation age,
  and forced destruction.
- [ ] Add assertions that resource handles are not recycled into a new frame-op
  while old commands can still reference them.
- [ ] Define device-loss teardown separately from normal retirement. A lost
  device may never advance its timeline/fences, so teardown must not deadlock
  waiting for normal retirement completion or falsely mark work complete.
- [ ] Audit descriptor-set update/free/reset lifetime, including descriptor
  pools and update-after-bind behavior; resource retirement alone is not enough
  if a live descriptor set is mutated illegally.
- [ ] Audit `ReleaseDescriptorReferencesForPhysicalResourceDestruction` and all
  call sites that dirty command buffers due to resource destruction.
- [ ] Add stress tests for rapid resize plus probe capture plus OpenXR eye
  rendering.

Acceptance criteria:

- [ ] Resource destruction is tied to completed GPU timeline/fence values.
- [ ] Validation does not report destroyed-in-use objects in the targeted stress
  scenarios.

## Phase 5 - Image Layout And Barrier Correctness

- [ ] Centralize image layout state tracking by image subresource:
  - [ ] aspect,
  - [ ] mip,
  - [ ] layer,
  - [ ] queue family,
  - [ ] last access mask/stage,
  - [ ] expected descriptor layout.
- [ ] Track recorded, submitted, and completed state separately where command
  buffers can overlap. Do not publish a layout transition globally merely
  because a barrier was recorded into an unsubmitted or discarded buffer.
- [ ] Standardize new synchronization on Vulkan 1.3/
  `VK_KHR_synchronization2` stage/access semantics and maintain one reviewed
  mapping from engine access intent to barrier state.
- [ ] Audit transitions for:
  - [ ] color attachment to sampled,
  - [ ] depth/stencil attachment to sampled depth view,
  - [ ] transfer source/destination,
  - [ ] storage image read/write,
  - [ ] OpenXR swapchain images,
  - [ ] generated mip chains,
  - [ ] cubemap face rendering,
  - [ ] texture-array rendering.
- [ ] Audit queue-family ownership transfers and externally owned images,
  including acquire/release pairs when graphics, transfer, present, or OpenXR
  ownership differs.
- [ ] Validate that descriptor `ImageLayout` matches tracked layout at command
  recording time.
- [ ] Add debug-only assertions when a sampled descriptor points at an attachment
  layout or a stale transfer layout.
- [ ] Add explicit post-transfer restoration for sampled sources.
- [ ] Audit mipmap generation for capture and IBL textures so all mips end in a
  sampled-compatible layout.
- [ ] Add tests for cubemap face render -> mip generate -> octa blit -> IBL
  fullscreen pass ordering.

Acceptance criteria:

- [ ] Sync validation is clean for light-probe capture and OpenXR eye rendering.
- [ ] Descriptor layout mismatches are caught before submit in diagnostic mode.

## Phase 6 - Descriptor And Binding Robustness

- [ ] Define descriptor fingerprint inputs for all descriptor-affecting resources:
  - [ ] image handle,
  - [ ] image view handle,
  - [ ] sampler handle,
  - [ ] image layout,
  - [ ] buffer handle,
  - [ ] range/offset,
  - [ ] descriptor type,
  - [ ] array index,
  - [ ] shader reflection binding identity.
- [ ] Add stable allocation/resource generation IDs to fingerprints so Vulkan
  handle reuse cannot make a stale descriptor appear current.
- [ ] Ensure program-bound SSBOs and sampled images update descriptor
  fingerprints when resources are recreated.
- [ ] Replace placeholder descriptor use with explicit diagnostic counters:
  - [ ] texture not generated,
  - [ ] resource pending,
  - [ ] image view missing,
  - [ ] layout invalid,
  - [ ] descriptor set stale,
  - [ ] device lost.
- [ ] Define which bindings may legally use null/dummy descriptors. Required
  bindings must prevent recording/submission rather than sampling a placeholder
  that masks a publication or lifetime bug.
- [ ] Audit descriptor-pool capacity, reset/free synchronization, and exhaustion
  telemetry. Include pool generation and remaining capacity in the last-frame
  descriptor dump.
- [ ] Cap repeated placeholder warnings by binding/resource key to keep logs
  readable during crashes.
- [ ] Add validation that `LightProbeIrradianceArray`,
  `LightProbePrefilterArray`, and probe SSBO bindings do not collide with
  G-buffer bindings.
- [ ] Add descriptor-state dump for the last successfully submitted frame before
  device loss.

Acceptance criteria:

- [ ] Descriptor sets are invalidated when any referenced Vulkan handle changes.
- [ ] Stale descriptor use is diagnosable from logs without stepping through the
  renderer.

## Phase 7 - OpenXR Vulkan Submit And Synchronization Hardening

- [ ] Audit OpenXR Vulkan frame phases:
  - [ ] acquire swapchain image,
  - [ ] wait image,
  - [ ] render eye(s),
  - [ ] release image,
  - [ ] submit frame,
  - [ ] mirror/update runtime state.
- [ ] Track every OpenXR swapchain image with an explicit state machine:
  `Available` -> `Acquired` -> `Waited` -> `Rendering` -> `GpuComplete` ->
  `Released`; log and reject illegal transitions.
- [ ] Ensure rendering and memory writes are complete before
  `xrReleaseSwapchainImage`, and keep the runtime-required image layout and any
  queue-family ownership transitions explicit.
- [ ] Ensure auxiliary capture work cannot run inside an unsafe OpenXR image
  ownership window unless explicitly scheduled.
- [ ] Separate OpenXR eye command buffers from capture command buffers in
  diagnostics and resource state.
- [ ] Review batched eye rendering fallback to sequential rendering after submit
  failure; never continue normal rendering after device lost.
- [ ] Improve `SubmitAndWaitOpenXrCommandBuffers` logs with:
  - [ ] eye index,
  - [ ] swapchain image index,
  - [ ] fence/timeline value,
  - [ ] command buffer labels,
  - [ ] frame-op context.
- [ ] Add a policy for draining/deferring retired resources before OpenXR eye
  rendering when frame slots are pending.
- [ ] Add smoke tests for OpenXR Vulkan with capture work queued during eye
  rendering.
- [ ] Test loss/exit paths at every OpenXR boundary (`xrWaitFrame`, image
  acquire/wait/release, `xrBeginFrame`, `xrEndFrame`) independently from Vulkan
  device loss so runtime/session loss is not mislabeled.

Acceptance criteria:

- [ ] OpenXR submit failures distinguish runtime/session loss, swapchain image
  errors, and actual Vulkan device loss.
- [ ] The renderer never attempts fallback eye rendering after a confirmed
  logical device loss.

## Phase 8 - GPU Work, Memory Budgeting, And TDR Protection

- [ ] Add per-subsystem GPU work budgets for:
  - [ ] light-probe cubemap faces,
  - [ ] IBL irradiance convolution,
  - [ ] IBL prefilter mip chain,
  - [ ] shadow refresh,
  - [ ] texture uploads,
  - [ ] compute-heavy GI passes,
  - [ ] pipeline/shader warmup.
- [ ] Slice light-probe batch capture so it cannot monopolize multiple OpenXR
  frame intervals.
- [ ] Move IBL convolution to an explicit queued job with budgeted steps:
  - [ ] cubemap mip generation,
  - [ ] octa conversion,
  - [ ] irradiance pass,
  - [ ] prefilter mip pass per mip.
- [ ] Add profiler counters for queued, running, completed, failed, and deferred
  probe-capture work.
- [ ] Add TDR-risk diagnostics for unusually long command submissions.
- [ ] Record GPU timestamps around probe capture and IBL passes.
- [ ] Add a throttle for batch capture when OpenXR frame timing degrades.
- [ ] Add memory-budget telemetry using core/available budget facilities such as
  `VK_EXT_memory_budget` where supported:
  - [ ] heap budget and usage,
  - [ ] VMA allocation/block totals and fragmentation indicators,
  - [ ] pending allocation bytes by frame-op context,
  - [ ] retired-but-not-yet-freed bytes,
  - [ ] descriptor pool/set pressure,
  - [ ] high-water marks immediately preceding device loss.
- [ ] Define soft and hard admission thresholds for optional capture resources.
  Defer work with an explicit reason before allocation pressure becomes an OOM
  or device-loss cascade; do not silently reduce requested quality.
- [ ] Keep the production policy inside Windows' normal TDR budget. Registry TDR
  changes may be documented only as controlled diagnostic experiments, never as
  the fix or a normal launch prerequisite.

Acceptance criteria:

- [ ] A 36-probe batch can run while OpenXR Vulkan is active without a long
  single submission or repeated missed frame budget.
- [ ] Probe capture progress remains visible in logs/profiler.
- [ ] Stress logs show bounded memory/retirement growth and no allocation or
  descriptor-pool exhaustion before, during, or after capture.

## Phase 9 - Tests, Stress Runs, And Tooling

- [ ] Add source-contract tests for:
  - [ ] frame-op context capture before recording,
  - [ ] direct FBO capture policy,
  - [ ] capture pipeline exclusions,
  - [ ] descriptor fingerprint inputs,
  - [ ] resource retirement gates,
  - [ ] image layout assertions.
- [ ] Prefer behavioral/unit tests over string-only source-contract tests for
  new state machines, fingerprints, retirement gates, and preset resolution.
  Keep source-contract tests only as temporary migration guards.
- [ ] Add runtime smoke tests for:
  - [ ] Vulkan editor startup,
  - [ ] Vulkan unit-testing world,
  - [ ] OpenXR Vulkan startup,
  - [ ] light-probe batch capture,
  - [ ] OpenXR Vulkan plus light-probe capture,
  - [ ] rapid resize during capture,
  - [ ] device-lost handling path with an injected mock failure if possible.
- [ ] Add deterministic fault injection at submit, fence/timeline wait,
  allocation, descriptor update/publication, and OpenXR acquire/wait/release
  boundaries. Assert first-error preservation, producer cancellation, zero
  post-loss submissions, and non-blocking teardown.
- [ ] Add RenderDoc capture recipe for the failing and fixed probe path.
- [ ] Add MCP/editor iteration checklist for capture validation:
  - [ ] capture from at least two camera positions,
  - [ ] inspect viewport screenshots,
  - [ ] inspect exported probe textures,
  - [ ] inspect Vulkan logs after shutdown.
- [ ] Fix stale source-contract tests in
  `VulkanDeferredProbeGiFixesTests` so the whole class can be used as a
  regression suite again.
- [ ] Add CI-safe tests that do not require a physical OpenXR headset.
- [ ] Add bounded soak/stress definitions with exact duration/frame count,
  resize cadence, capture cadence, and pass/fail log counters. Avoid acceptance
  criteria based only on one successful run.

Acceptance criteria:

- [ ] Focused tests cover each hardening area.
- [ ] The broader Vulkan probe/deferred test class is no longer stale.
- [ ] At least one automated stress path exercises OpenXR Vulkan plus queued
  capture work.
- [ ] Fault-injection tests prove that all GPU-work producers quiesce after the
  first device-loss transition and teardown cannot wait forever on dead fences.

## Phase 10 - Documentation And Operator Workflow

- [ ] Update `docs/architecture/rendering/vulkan-renderer.md` with the frame-op
  context model.
- [ ] Update `docs/architecture/rendering/openxr-vr-rendering.md` with Vulkan
  submit/device-loss behavior.
- [ ] Add a capture pipeline note under `docs/architecture/rendering/`.
- [ ] Document validation preset launch commands.
- [ ] Document device-loss triage:
  - [ ] what logs to inspect,
  - [ ] what breadcrumbs mean,
  - [ ] when to use RenderDoc,
  - [ ] when to use validation layers,
  - [ ] how to distinguish teardown noise from render failure.
- [ ] Document driver/OS evidence collection on Windows, including Event Viewer
  display-driver/TDR events, without treating OS-level timeout changes as an
  engine fix.
- [ ] Add editor UI/help text only where needed in diagnostics panels; avoid
  normal in-app instructional clutter.
- [ ] Keep `docs/work/todo/rendering/vulkan-deferred-and-probe-gi-fixes-todo.md`
  cross-linked if probe GI remains a separate work item.

Acceptance criteria:

- [ ] A contributor can reproduce a diagnostic Vulkan launch and know where to
  find the relevant logs.
- [ ] The device-loss workflow is documented enough that future incidents do not
  start from scratch.

## Final Acceptance Criteria

- [ ] Vulkan light-probe batch capture completes in the unit-testing world while
  OpenXR Vulkan is active.
- [ ] No `VK_ERROR_DEVICE_LOST` occurs in the targeted capture/OpenXR stress
  runs.
- [ ] Validation and sync-validation diagnostic runs are clean for the targeted
  paths or have documented external-driver exceptions.
- [ ] Resource planner logs show stable per-context resource plans instead of
  cross-context physical image churn.
- [ ] Device-loss injection or simulated submit failure stops rendering cleanly
  and reports a useful breadcrumb summary.
- [ ] Hot-path allocation checks remain clean for command recording and
  submission.
- [ ] The final validation matrix names exact hardware/runtime configurations,
  run duration/frame counts, diagnostic coverage, warning/VUID allowlist, peak
  memory, maximum submission time, and OpenXR missed-frame threshold.
- [ ] Device-fault and vendor diagnostic artifacts are collected when supported;
  unsupported capabilities are explicit in the result manifest.
- [ ] Documentation, source-contract tests, and smoke tests are updated.
- [ ] Merge `rendering-vulkan-core-hardening` back into `main` after validation.

## Open Questions

- [ ] Should capture use a dedicated `CaptureRenderPipeline` class, or should
  `DefaultRenderPipeline` expose capture variants through explicit policy?
- [ ] Should OpenXR Vulkan auxiliary capture work be forbidden during eye image
  ownership windows, or only scheduled through a queue with explicit barriers?
- [ ] Should probe IBL convolution move to compute, fullscreen graphics, or stay
  current-pass based with better budgeting?
- [ ] Which validation preset should be the default for local Vulkan crash
  investigations?
- [ ] Should standard validation, sync validation, GPU-assisted validation, and
  crash diagnostics be represented as composable flags with curated presets
  rather than a single mode? (Recommended: composable flags plus presets.)
- [ ] Is controlled renderer/editor restart after device loss a later product
  requirement, or is deterministic shutdown the v1 policy?
- [ ] Should resource planner state be keyed only by frame-op kind and dimensions,
  or also by output target identity and pass metadata hash?
- [ ] What is the minimum acceptable light-probe capture throughput when OpenXR
  is active?

## Suggested First Implementation Slice

- [ ] Fix stale source-contract paths in `VulkanDeferredProbeGiFixesTests`.
- [ ] Add `FrameOpContext.Kind` and log it for every command-buffer recording.
- [ ] Add context mismatch assertions for command-buffer reuse.
- [ ] Convert light-probe and scene-capture direct-FBO policy into an explicit
  capture policy object.
- [ ] Add validation preset launch docs and a simple breadcrumb dump on device
  loss.
- [ ] Add the device-loss state machine and first-error-wins transition before
  expanding fault collection, so diagnostics cannot race continued GPU work.
- [ ] Add `VK_KHR_device_fault`/`VK_EXT_device_fault` capability detection and a
  structured result manifest.
- [ ] Re-run the July 9 light-probe capture scenario with Vulkan/OpenXR enabled.

## External Technical References

- [Khronos Vulkan validation overview](https://docs.vulkan.org/guide/latest/validation_overview.html)
- [Khronos synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html)
- [`VK_KHR_device_fault`](https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_device_fault.html)
- [`vkGetDeviceFaultReportsKHR`](https://docs.vulkan.org/refpages/latest/refpages/source/vkGetDeviceFaultReportsKHR.html)
- [`vkGetDeviceFaultDebugInfoKHR`](https://docs.vulkan.org/refpages/latest/refpages/source/vkGetDeviceFaultDebugInfoKHR.html)
- [`VkDeviceFaultInfoKHR`](https://docs.vulkan.org/refpages/latest/refpages/source/VkDeviceFaultInfoKHR.html)
- [`VkDeviceFaultDebugInfoKHR`](https://docs.vulkan.org/refpages/latest/refpages/source/VkDeviceFaultDebugInfoKHR.html)
- [`VK_EXT_device_fault`](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_device_fault.html)
- [`vkGetDeviceFaultInfoEXT`](https://docs.vulkan.org/refpages/latest/refpages/source/vkGetDeviceFaultInfoEXT.html)
- [`VK_EXT_device_address_binding_report`](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_device_address_binding_report.html)
- [`VK_NV_device_diagnostic_checkpoints`](https://docs.vulkan.org/refpages/latest/refpages/source/VK_NV_device_diagnostic_checkpoints.html)
- [LunarG Khronos validation-layer settings](https://vulkan.lunarg.com/doc/view/latest/windows/khronos_validation_layer.html)
- [Microsoft WDDM timeout detection and recovery](https://learn.microsoft.com/windows-hardware/drivers/display/timeout-detection-and-recovery)

# Vulkan Core Hardening And Device-Loss TODO

Last Updated: 2026-07-10
Owner: Rendering
Status: Phase 5.1 Correctness Gate Complete; Phase 5.2 Multi-Output Throughput Gate Open
Target Branch: `rendering-vulkan-core-hardening`

## Goal

Make Vulkan robust and fast enough for normal editor, OpenXR, scene-capture,
light-probe, shadow, mirror, UI-preview, and diagnostic rendering without recurring
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
  - [x] editor desktop viewport,
  - [x] OpenXR Vulkan eye rendering failing baseline from July 9 logs,
  - [x] OpenXR mirror rendering,
  - [x] light-probe batch capture failing baseline from July 9 logs,
  - [x] scene capture through the light-probe capture path,
  - [ ] shadow rendering,
  - [ ] UI preview rendering.
- [x] Define a deterministic repro manifest template for the July 9 scenario,
  including
  world settings, probe count/resolution, OpenXR runtime, headset refresh rate,
  render resolution, and exact launch command.
- [x] Populate that template with one complete, machine-readable July 9
  equivalent rerun manifest; remove all placeholder values and record its
  tracked path alongside the raw log-session path. See
  [vulkan-core-hardening-phase21-validation-2026-07-09.json](../../testing/rendering/vulkan-core-hardening-phase21-validation-2026-07-09.json).
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
- [x] Define an explicit renderer device state machine such as `Healthy` ->
  `LossDetected` -> `CollectingFaultData` -> `Quiesced` -> `Disposed`, including
  which calls are legal in each state and how producer threads are cancelled.

Acceptance criteria:

- [x] There is a repeatable baseline for at least one light-probe batch capture
  and one OpenXR Vulkan session.
- [x] Device-loss logs name the active frame op, output target, dimensions,
  command-buffer generation, and timeline/fence values where available.
- [x] Repeated runs use the same repro manifest and produce a comparable result
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
    recovered fault reports when KHR support is active through the local
    `GetDeviceProcAddr` shim,
  - [x] call `vkGetDeviceFaultDebugInfoKHR` through the local shim only after
    device loss; on the EXT compatibility path, query counts and then
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
  - [x] active resource/planner generation from the submitted context,
  - [x] image, subresource, old/new layout, and caller for recent layout
    transitions,
  - [x] descriptor table generation.
- [x] Keep breadcrumb writes allocation-free and assign stable IDs whose backing
  storage remains valid until the associated submission completes. Include
  queue, submit serial, batch index, draw/dispatch/blit identity, and the first
  failing Vulkan/OpenXR API.
  - [x] Breadcrumb writes are allocation-free, thread-safe under parallel eye
    recording, and include queue, submit serial,
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
- [x] Add behavioral tests that simulate alternating OpenXR eye rendering and
  light-probe capture contexts.
- [x] Add logs that distinguish metadata-only graph changes from allocation
  signature changes per context.

Acceptance criteria:

- [ ] Alternating capture and OpenXR eye frames do not churn the same physical
  resources unless an explicit shared-resource policy says they may. Allocator
  ownership is now isolated and the post-fix stress run did not reproduce
  cross-eye descriptor invalidation or device loss, but growing probe arrays
  still produce intentional allocation churn that needs a narrower steady-state
  measurement before this criterion is checked.
- [x] A command buffer recorded for one context cannot be submitted under another
  context silently.

## Phase 2.1 - Audit Remediation: Planner Ownership, Queue Serialization, And Diagnostics

The July 9 post-implementation audit found that the Phase 0-2 source slices are
present, but several ownership, synchronization, and diagnostic-fidelity claims
are not yet safe to accept. Complete this corrective phase before Phase 4 or
before treating Phase 2 hardware stress as authoritative.

Implementation and hardware evidence:
[vulkan-core-hardening-phase21-validation-2026-07-09.json](../../testing/rendering/vulkan-core-hardening-phase21-validation-2026-07-09.json).
All source tasks below are complete. The final aggregate live-validation
criterion remains open because unrelated synchronization, query lifecycle,
cube-view, and teardown VUIDs are still present.

Planner state ownership and isolation:

- [x] Redesign `ExternalResourcePlannerReadbackScope` so a cache miss builds the
  new context from an empty isolated `ResourcePlannerRuntimeState`; it must not
  mutate, retire, or destroy resources owned by the state captured for restore.
- [x] Activate an existing cached planner state before calling
  `PrepareResourcePlannerForFrameOps`, including the single-key path. Do not
  update the shared active planner first and cache the result afterward.
- [x] Separate command-recording identity from physical-allocation ownership.
  Keep descriptor/resource generations in recording invalidation where needed,
  but do not create a new allocator owner solely because a descriptor generation
  changed while the physical allocation signature stayed constant.
- [x] Give every cached allocator state explicit unique ownership or shared
  ownership with reference counting. Prune, replacement, and teardown must
  destroy a physical allocator only after its final owner is removed.
- [x] Deduplicate allocator destruction in
  `PruneFrameOpResourcePlannerStatesToCapacity`,
  `DestroyFrameOpResourcePlannerStates`, and OpenXR planner-state teardown.
- [x] Add debug assertions that a restored planner state does not reference a
  retired/destroyed allocator, physical image group, buffer group, framebuffer,
  or descriptor generation.
- [x] Add an explicit purpose/context-kind field to
  `OpenXrViewResourcePlannerContextKey` so eye, mirror, publish, and prewarm
  states cannot collide when foveation is disabled.

Queue access and terminal device state:

- [x] Add one renderer-owned queue-operation gateway that serializes every host
  operation on each Vulkan queue and records the operation/result/context.
- [x] Route `SubmitAcquireSemaphoreBridge`, normal swapchain submits, OpenXR
  submits, one-shot submits, uploads, readbacks, presents where applicable, and
  all `QueueWaitIdle` calls through that gateway.
- [x] Replace the unchecked dedicated-transfer `QueueWaitIdle` calls in buffer
  and image uploads with checked, serialized operations that mark device loss on
  `VK_ERROR_DEVICE_LOST` and abort the transfer path.
- [x] Implement the Phase 0 renderer device state machine (`Healthy` ->
  `LossDetected` -> `CollectingFaultData` -> `Quiesced` -> `Disposed`) and gate
  submit, wait, allocation, mapping, descriptor update, command recording, and
  planner publication at their common entry points.
- [x] Cancel or quiesce render, OpenXR eye, upload, readback, and capture producer
  threads on the first device-loss transition; preserve first-writer failure
  context while classifying later errors as fallout.

Breadcrumb and fault-data correctness:

- [x] Populate submission diagnostics with the actual planner revision,
  frame-op signature, frame-op context ID, resource generation, and descriptor
  generation for swapchain, OpenXR eye, eye batch, mirror, and publish submits.
- [x] Replace the global last-command marker with a bounded, thread-safe mapping
  from command-buffer stable ID/generation to its latest recorded frame-op
  marker; resolve markers from the command buffers in the actual submit.
- [x] Publish breadcrumb-ring entries atomically or under a diagnostic lock so a
  concurrent device-loss reader cannot observe a torn record.
- [x] Replace the layout-transition counter-only breadcrumb with a bounded ring
  containing image stable ID, aspect/mip/layer range, old/new layout, queue
  family, command buffer, and caller.
- [x] Encode NV checkpoint serials as stable opaque marker values, or retain
  per-submission marker storage until fence/timeline completion. Never overwrite
  a marker slot that an in-flight command buffer can still report.
- [x] Add configurable hard caps for KHR/EXT address records, vendor records,
  report counts, and vendor binary bytes before allocating crash-path storage.
- [x] Handle `VK_ERROR_NOT_ENOUGH_SPACE_KHR`, `VK_INCOMPLETE`, count growth, and
  truncation explicitly when collecting KHR debug info; never label a failed or
  partially initialized binary as a complete capture.
- [x] Drain KHR reports in bounded batches up to the configured global cap and
  record how many reports remained unavailable or intentionally truncated.

Tests, evidence, and documentation:

- [x] Add behavioral planner tests for alternating main viewport, scene capture,
  light probe, OpenXR eye, and OpenXR mirror contexts. Assert stable allocator
  identity per context, no cross-context destruction, and intentional sharing
  only through an explicit policy.
- [x] Add a regression test where descriptor generation changes without an
  allocation-signature change, then prune states and prove the surviving state
  still owns valid physical resources.
- [x] Add concurrent queue tests proving submit/wait operations are serialized
  and that no queue operation begins after `LossDetected`.
- [x] Repair the broader Vulkan planner/OpenXR contract suite. Audit baseline:
  65 passed and 7 failed; two failures directly reference stale planner source
  locations/field names, while five are stale OpenXR/image contract assertions.
  The repaired suite passes 71 of 71 tests.
- [x] Replace Phase 0-2 source-string checks with focused behavioral tests where
  the relevant planner, key, state-machine, and diagnostic logic can be tested
  without a live GPU.
- [x] Update the Phase 1 progress note so it no longer claims callable KHR
  device-fault support is unavailable after the Phase 1.1 shim landed.
- [x] Run the runtime-rendering build, all Phase 0-2 focused tests, the repaired
  planner/OpenXR contract suite, and `Test-VulkanPhase3-Regression`. Runtime and
  editor builds passed; the focused lane passed 93/93 and the repaired contract
  suite passed 71/71. Two stale Phase 3 source-contract assertions were repaired,
  and that regression lane now passes 96/96.
- [x] Run `SyncValidation` desktop, OpenXR eye/mirror, and light-probe stress
  sessions using complete machine-readable manifests and preserve the resulting
  validation/device-fault summaries. No run reproduced device loss. The post-fix
  40-frame eye/probe run eliminated
  `VUID-vkQueueSubmit2-commandBuffer-03874`; other VUID classes remain open.

Acceptance criteria:

- [x] Creating, activating, pruning, or destroying one frame-op planner context
  cannot retire resources owned by another context.
- [x] No physical allocator is destroyed more than once or while any cached,
  active, recorded, submitted, or externally owned context can reference it.
- [x] Every host operation on a Vulkan queue uses the shared serialization and
  terminal-state gate. The isolated OpenGL upscale bridge's private Vulkan device
  uses its own instance of the same queue lease/state-machine pattern.
- [x] Device-loss breadcrumbs identify the actual submitted context, planner and
  resource generations, command marker, and recent image transitions without
  torn or stale cross-thread data.
- [x] Fault collection remains bounded and accurately reports complete,
  incomplete, truncated, unavailable, and failed artifacts.
- [ ] Focused and broader Vulkan planner/OpenXR tests are green, and repeated
  live OpenXR plus light-probe stress does not reproduce cross-context resource
  churn, validation errors, or device loss. Phase 2.1 focused and repaired
  planner/OpenXR suites are green, and device loss plus the cross-eye descriptor
  invalidation are not reproduced. This aggregate item remains open for
  steady-state churn measurement and the unrelated live VUID classes recorded
  in the manifest.

## Phase 3 - Dedicated Capture Pipeline

- [x] Define capture policies for:
  - [x] generic scene capture,
  - [x] light probes,
  - [x] reflection probes,
  - [x] GI probes,
  - [x] thumbnails/UI previews,
  - [x] diagnostic FBO capture.
- [x] Create a minimal Vulkan-safe capture pipeline path that renders only what
  capture actually needs:
  - [x] pre-render hooks,
  - [x] background/sky policy,
  - [x] opaque deferred or opaque forward policy,
  - [x] masked/transparent policy,
  - [x] optional shadows,
  - [x] no temporal history,
  - [x] no auto exposure,
  - [x] no bloom,
  - [x] no TSR/TAA,
  - [x] no vendor upscale,
  - [x] no viewport final-output path.
- [x] Decide whether capture uses a dedicated `RenderPipeline` type or a
  `DefaultRenderPipeline` capture variant.
- [x] Keep `DefaultRenderPipeline` and `DefaultRenderPipeline2` behavior
  consistent while both exist.
- [x] Make capture viewports opt into direct FBO commands by policy rather than
  by ad hoc property mutations.
- [x] Add a debug overlay/log line that reports the effective capture policy.
- [x] Confirm capture output orientation and clip-space policy for Vulkan and
  OpenGL.
- [x] Validate that light-probe IBL generation uses only explicit fullscreen
  probe passes after cubemap capture.

Implementation note (2026-07-09): capture uses an explicit
`RenderCapturePolicy` variant of the existing default pipelines. Minimal
capture profiles select the caller-owned direct-FBO command branch and the
`DefaultRenderPipeline` `MinimalDirectCapture` resource feature returns an
empty managed viewport layout, so cubemap faces do not materialize G-buffer,
post-process, bloom, temporal, exposure, AA, upscale, or final-output
resources. Both default pipelines gate the same capture pass groups. Capture
textures retain backend framebuffer-native orientation and use the shared
engine clip/depth/texture-Y policy; no capture-only flip is applied. Probe IBL
conversion and convolution remain explicit post-capture fullscreen passes.
Focused validation: runtime-rendering build passed and 8 capture-policy tests
passed. `Test-VulkanPhase3-Regression` passed 94/96; its two failures are stale
unrelated source-contract assertions for the renamed parallel-secondary and
ImGui swapchain-handoff paths. Live Vulkan/OpenGL visual capture remains part
of the later hardware validation matrix.

Acceptance criteria:

- [x] Light-probe capture no longer allocates or refreshes post-process,
  temporal, bloom, exposure, or final-output resources during cubemap face
  rendering.
- [x] Probe captures still preserve the cubemap, octahedral, irradiance, and
  prefilter textures.
- [x] Capture behavior is documented and controlled by explicit policy.

## Phase 4 - Resource Lifetime And Retirement

Implementation and live evidence:
[vulkan-core-hardening-phase4-live-validation-2026-07-09.md](../../investigations/rendering/vulkan-core-hardening-phase4-live-validation-2026-07-09.md).
The source work and bounded OpenXR smoke passed, but the final validation
criterion remains open: teardown reported invalid image/memory destruction and
live device children, and the stress run exposed unbounded retirement growth.

- [x] Audit every Vulkan object destruction path:
  - [x] images,
  - [x] image views,
  - [x] samplers,
  - [x] framebuffers,
  - [x] buffers,
  - [x] descriptor sets/pools,
  - [x] command buffers,
  - [x] pipelines,
  - [x] query pools.
- [x] Distinguish CPU ownership, submitted GPU use, completed GPU use, external
  OpenXR ownership, and pending destruction in the resource state model.
- [x] Replace immediate destruction with timeline/fence-retired destruction
  where any in-flight command can still reference the object.
- [x] Track the last submit/timeline value for resources used by each frame-op.
- [x] Block or defer descriptor-reference release if the owning resource is still
  in-flight.
- [x] Add diagnostics for retirement queue depth, oldest retired generation age,
  and forced destruction.
- [x] Add assertions that resource handles are not recycled into a new frame-op
  while old commands can still reference them.
- [x] Define device-loss teardown separately from normal retirement. A lost
  device may never advance its timeline/fences, so teardown must not deadlock
  waiting for normal retirement completion or falsely mark work complete.
- [x] Audit descriptor-set update/free/reset lifetime, including descriptor
  pools and update-after-bind behavior; resource retirement alone is not enough
  if a live descriptor set is mutated illegally.
- [x] Audit `ReleaseDescriptorReferencesForPhysicalResourceDestruction` and all
  call sites that dirty command buffers due to resource destruction.
- [x] Add stress tests for rapid resize plus probe capture plus OpenXR eye
  rendering.

Acceptance criteria:

- [x] Resource destruction is tied to completed GPU timeline/fence values.
- [ ] Validation does not report destroyed-in-use objects in the targeted stress
  scenarios. The July 9 live run reported seven invalid `vkDestroyImage` calls,
  five invalid `vkFreeMemory` calls, and nine live child objects at
  `vkDestroyDevice`; see the linked investigation.

## Phase 5 - Image Layout And Barrier Correctness

Implementation and live evidence:
[vulkan-core-hardening-phase5-live-validation-2026-07-09.md](../../investigations/rendering/vulkan-core-hardening-phase5-live-validation-2026-07-09.md).
Source implementation and the bounded OpenXR smoke are complete. Sync-validation
acceptance was closed by Phase 5.1: all engine-owned sampled-attachment,
cross-pass synchronization, acquire/present, and retirement errors are gone.
The precisely attributed SteamVR exception is documented in the linked evidence.

- [x] Centralize image layout state tracking by image subresource:
  - [x] aspect,
  - [x] mip,
  - [x] layer,
  - [x] queue family,
  - [x] last access mask/stage,
  - [x] expected descriptor layout.
- [x] Track recorded, submitted, and completed state separately where command
  buffers can overlap. Do not publish a layout transition globally merely
  because a barrier was recorded into an unsubmitted or discarded buffer.
- [x] Standardize new synchronization on Vulkan 1.3/
  `VK_KHR_synchronization2` stage/access semantics and maintain one reviewed
  mapping from engine access intent to barrier state.
- [x] Audit transitions for:
  - [x] color attachment to sampled,
  - [x] depth/stencil attachment to sampled depth view,
  - [x] transfer source/destination,
  - [x] storage image read/write,
  - [x] OpenXR swapchain images,
  - [x] generated mip chains,
  - [x] cubemap face rendering,
  - [x] texture-array rendering.
- [x] Audit queue-family ownership transfers and externally owned images,
  including acquire/release pairs when graphics, transfer, present, or OpenXR
  ownership differs.
- [x] Validate that descriptor `ImageLayout` matches tracked layout at command
  recording time.
- [x] Add debug-only assertions when a sampled descriptor points at an attachment
  layout or a stale transfer layout.
- [x] Add explicit post-transfer restoration for sampled sources.
- [x] Audit mipmap generation for capture and IBL textures so all mips end in a
  sampled-compatible layout.
- [x] Add tests for cubemap face render -> mip generate -> octa blit -> IBL
  fullscreen pass ordering.

Acceptance criteria:

- [x] Sync validation is clean for engine-owned light-probe capture and OpenXR
  eye rendering. The stable SteamVR-owned `xrEndFrame`/teardown exception is
  bounded by exact message ID and runtime/layer version in the evidence.
- [x] Descriptor layout mismatches are caught during command recording in Debug
  diagnostic runs, before queue submission. The remaining mismatches are logged
  with descriptor set, binding, view, image, owner, expected, and tracked layout.

## Phase 5.1 - Sync-Validation Remediation Gate

This correctness gate is complete. It addresses every validation message in the
final Phase 5 run rather than carrying known synchronization, layout,
frame-lifecycle, teardown, tooling, or runtime defects forward. Complete the
Phase 5.2 throughput gate below before starting Phase 6.

Evidence:
[vulkan-core-hardening-phase5-live-validation-2026-07-09.md](../../investigations/rendering/vulkan-core-hardening-phase5-live-validation-2026-07-09.md)
and
[vulkan-core-hardening-phase5-validation-2026-07-09.json](../../testing/rendering/vulkan-core-hardening-phase5-validation-2026-07-09.json).
The 1,878 final-run errors are partitioned as follows:

- 1,804 engine synchronization/layout reports:
  - 1,315 `SYNC-HAZARD-WRITE-AFTER-READ`,
  - 339 `SYNC-HAZARD-READ-AFTER-WRITE`,
  - 150 `UNASSIGNED-CoreValidation-DrawState-InvalidImageLayout`.
- Eight desktop acquire/present lifecycle reports.
- 47 retirement/teardown reports, including duplicate image/memory destruction,
  threading diagnostics, and live children at device destruction.
- Six validation-layer/toolchain compatibility reports caused by old layers
  interpreting newer Vulkan feature/property chains.
- 13 SteamVR/OpenXR-runtime-owned image/synchronization reports emitted during
  `xrEndFrame`; keep these separate from engine-owned errors until independently
  reproduced and attributed after tool/runtime upgrades.

Status: complete on 2026-07-09. The rerun used portable LunarG SDK/VVL 1.4.350
against Vulkan runtime 1.4.341 and current SteamVR build `23791826`. All five
isolated/bounded lanes report zero engine-owned validation errors. Each OpenXR
lane independently reproduced the same bounded SteamVR-owned 14-message set: 7
`VUID-VkImageCreateInfo-pNext-01443`, 6 compositor `vkCmdCopyImage` WAW reports
from unlabeled runtime command buffers inside `xrEndFrame`, and 1
`VUID-vkDestroyDevice-device-05137` for nine runtime children. No broad allowlist
was added. Commands, handles, ownership evidence, versions, and log paths are in
the linked investigation and machine-readable manifest.

Exact subresource initialization and access-state tracking:

- [x] Stop treating aggregate `VulkanPhysicalImageGroup.LastKnownLayout ==
  Undefined` as proof that an entire image is uninitialized. Mixed known states
  across mips/layers must remain distinguishable from never-used subresources.
- [x] Replace whole-image unknown-pass initialization with exact
  aspect/mip/layer queries that prefer the current command-buffer overlay, then
  the submitted canonical state.
- [x] Permit `Undefined` as the old layout only when the exact subresource has no
  recorded or submitted state for its current allocation generation.
- [x] Batch adjacent ranges with identical prior state and deduplicate initial
  transitions within a command buffer; remove the redundant unknown-pass
  fallback invocation.
- [x] Track actual last stage/access in the canonical image state instead of
  reconstructing source scopes solely from the old layout. Emit barriers for
  same-layout hazards when the prior access requires one.
- [x] Add tests for a five-mip image with a mixture of known and unknown states;
  only genuinely unknown ranges may receive an `Undefined` transition.

Ordered command-buffer state propagation:

- [x] Model each ordered submit chain explicitly, including upload -> scene ->
  ImGui -> dynamic text -> present and the corresponding OpenXR/mirror paths.
- [x] Seed each later command buffer's initial image state from its predecessor's
  recorded end state rather than the last globally submitted state.
- [x] Do not overwrite an authoritative cross-command-buffer old-layout contract
  with a submitted-state fallback during barrier recording.
- [x] Include exact entry-state contracts and image allocation generations in
  cached-command-buffer fingerprints; reject or rerecord on mismatch.
- [x] Make all recording-time layout queries prefer the current command-buffer
  overlay, including FBO publication, dynamic transitions, attachment queries,
  blits, and legacy render-pass implicit transitions.
- [x] Keep global physical-image state immutable while recording. Publish only
  accepted submissions and update the canonical overlay for render-pass
  initial/reference/final layouts and queue-family release/acquire pairs.
- [x] Add a regression test proving scene `ColorAttachmentOptimal` output is the
  ImGui entry state and that the final transition reaches `PresentSrcKHR` in one
  ordered submission.

Render-graph and descriptor-consumer synchronization:

- [x] Resolve render-graph usages to physical image identity plus exact
  overlapping subresource before planning barriers; do not key correctness only
  by logical resource name or silently skip dedicated/external images.
- [x] Coalesce compatible usages within a pass. Sampling a depth subresource
  while attaching it read-only must produce one `DepthStencilReadOnlyOptimal`
  state with fragment-shader and early/late-depth read scopes.
- [x] Reject sampling and writable depth attachment use of the same subresource
  unless an explicit supported feedback-loop/local-read path is selected. Remove
  unused depth attachments from fullscreen passes.
- [x] Add dynamic descriptor resources, especially light-combine shadow maps, to
  pass dependencies and transition the exact produced ranges before sampling.
- [x] Add explicit producer-end to consumer-start barriers for color/depth
  attachment outputs such as velocity, GTAO inputs, lighting inputs, shadow
  outputs, and UI/offscreen viewport textures.
- [x] Derive ImGui texture dependencies from the exact draw data used by the
  overlay and transition those images before recording their draws.
- [x] Make descriptor-layout resolution a pure state query. Do not submit hidden
  one-shot layout transitions while preparing descriptor image info; schedule
  required transitions as explicit frame operations.
- [x] Restrict diagnostic descriptor validation to bindings consumed by the
  active reflected pipeline, and reject command recording in diagnostic builds
  when a consumed binding is stale or in an incompatible layout.
- [x] Add behavioral tests for sampled plus read-only depth, sampled plus
  writable depth rejection, dynamic shadow dependencies, attachment-to-sampled
  color transitions, and UI draw-data texture transitions.

Acquire, submit rejection, and cached-resource lifetime:

- [x] Maintain a reverse dependency map from resource/allocation generation to
  cached command-buffer variants. Retirement must invalidate and rerecord every
  cached command buffer that references the retired generation.
- [x] If validation or lifetime checks reject a frame after swapchain image
  acquisition, consume the acquire semaphore and release/present the image via a
  bounded recovery path, or safely unwind through swapchain recreation. Never
  return with an acquired image outstanding.
- [x] Treat `SuboptimalKhr` acquisition as successful: schedule recreation but
  continue through a valid submit/present or explicit recovery path.
- [x] Add tests for retirement-driven cached-command invalidation and every
  post-acquire, pre-submit rejection path.

Retirement and teardown correctness:

- [x] Add an image lifetime gate equivalent to the image-view gate, keyed by
  handle and allocation generation. Keep retirement deduplication active until
  destruction completes successfully.
- [x] Make duplicate/stale retire operations harmless and diagnostic. Clear
  moved handles in owner wrappers so later teardown cannot destroy them again.
- [x] Preserve allocator ownership: never call raw `vkFreeMemory` for VMA- or
  allocator-owned memory, and reject stale entries without invoking Vulkan.
- [x] Destroy OpenXR sessions, swapchains, imported image sidecars, command
  buffers/pools, synchronization objects, and allocator resources before the
  logical Vulkan device, after their work has completed or entered the explicit
  lost-device teardown policy.
- [x] Add tests for duplicate retirement, generation-safe handle reuse, allocator
  ownership, and device teardown with zero live children.

Validation toolchain and runtime-owned messages:

- [x] Upgrade the active Vulkan SDK/validation layers from 1.3.239-era layers
  to a version compatible with the Vulkan 1.4.341 runtime used by the validation
  machine, and record both versions in the rerun manifest.
- [x] Gate physical-device property and logical-device `pNext` structures by the
  negotiated API version, advertised extension, and queried feature support.
- [x] Upgrade/retest the active SteamVR/OpenXR runtime and validation layer before
  classifying the 13 `xrEndFrame` messages as external exceptions.
- [x] Verify that every engine-controlled OpenXR swapchain image is released in
  an attachment-compatible layout with correct queue-family ownership.
- [x] Do not add a broad allowlist. Any surviving runtime-owned message must have
  an exact VUID/message ID, runtime/layer version, reproduction boundary, and
  evidence that no engine command buffer or image is involved.
- [x] Keep query-pool redesign out of this remediation inventory unless a new run
  produces a genuine query-pool VUID; none of the final 1,878 reports did.

Validation sequence:

- [x] Run focused source/behavioral tests plus the narrow Vulkan rendering build.
- [x] Run isolated `SyncValidation` sessions in this order:
  - [x] desktop Vulkan without the ImGui overlay,
  - [x] desktop Vulkan with ImGui,
  - [x] OpenXR Vulkan without a probe capture,
  - [x] OpenXR Vulkan with one probe capture,
  - [x] the bounded Phase 5 light-probe/OpenXR batch.
- [x] Record structured counts by VUID/message ID and ownership boundary for each
  run so engine, loader/layer, driver, and OpenXR-runtime messages are not mixed.
- [x] Update the Phase 5 investigation and machine-readable manifest with the
  post-fix results, exact commands, hardware/runtime versions, and log paths.

Acceptance criteria:

- [x] All 1,804 engine-owned synchronization/layout reports are eliminated in
  the isolated and bounded target runs; there are no WAR, RAW, WAW, or invalid
  image-layout reports from engine command buffers.
- [x] No acquire semaphore/image remains outstanding after any rejected frame,
  and the target runs report no acquire/present lifecycle VUIDs.
- [x] Normal teardown reports no engine-owned duplicate/stale image or memory
  destruction, external-synchronization threading errors, or live Vulkan
  children. SteamVR's exact nine-child exception is bounded below.
- [x] Validation layers match the runtime closely enough that supported
  `pNext` chains are recognized and unsupported chains are not emitted.
- [x] Any surviving SteamVR/OpenXR-runtime message is independently reproduced,
  precisely attributed, and documented with a bounded exception; no
  engine-controlled validation error remains unaddressed.
- [x] Phase 5's sync-validation acceptance criterion is checked only after this
  complete sequence passes. Phase 5.1 closes correctness; proceed to Phase 5.2,
  not directly to Phase 6.

## Phase 5.2 - Multi-Output Render Throughput Architecture Gate

Complete this phase before starting Phase 6. Phase 5.1 made synchronization,
layout, and retirement failures visible and rejectable, but the resulting
runtime has not yet reached an acceptable performance baseline, including in
desktop-only rendering. This phase makes the correctness architecture cheap
enough to support several simultaneous or interleaved outputs:

- desktop/editor scene output;
- foveated OpenXR eye view sets, including wide/inset or multiview variants;
- VR pickup/handheld mirror views;
- desktop VR mirror composition;
- in-world 3D reflection mirrors;
- shadow, scene-capture, light-probe, reflection-probe, and IBL work;
- UI previews, thumbnails, and diagnostic targets.

Evidence:
[vulkan-cpu-framerate-regression-2026-07-09.md](../../investigations/rendering/vulkan-cpu-framerate-regression-2026-07-09.md).
The current default reuse-disabled Release cohort measured 56.86 render
samples/s, 15.169 ms p50, 28.800 ms p95, 6.226 GB of record-path allocation,
5,178 retired image views, and four rejected submissions. A settled experimental
reuse cohort on the same median workload measured 108.79 samples/s, 8.883 ms
p50, 11.031 ms p95, zero record-path allocation, zero retirement, and zero
rejection. This is an architectural diagnosis, not permission to promote the
reuse flag without the correctness matrix below.

The target data flow is:

`Immutable frame snapshot -> output requests -> compatible view families -> cached plans/resources -> record or reuse -> deadline-ordered submit DAG`

Ownership boundary:

- This phase owns output-neutral scheduling, stable identity, command reuse,
  targeted invalidation, local tracking, asynchronous retirement, and the
  multi-output performance contract.
- The [desktop frame-loop decomposition todo](vulkan-desktop-frame-loop-decomposition-todo.md)
  owns extraction and coordinator file layout. It must preserve these contracts
  and must not introduce a second desktop-only scheduler.
- The [dynamic-rendering migration todo](vulkan-dynamic-rendering-migration-todo.md)
  owns dynamic/legacy attachment-mode parity and dynamic-signature allocation
  cleanup. This phase consumes either mode through one output/plan contract.
- The [primary command recording fast-path todo](optimization/vulkan-primary-command-recording-fast-path-todo.md)
  remains the focused recording reference. Phase 5.2 owns the cross-output
  implementation order and promotion gate when the documents overlap.
- Phase 6 still owns descriptor correctness completeness. Phase 5.2 establishes
  the allocation-free, generation-driven publication seam that Phase 6 must use.

### 5.2.1 - Establish The Multi-Output Performance Contract

- [x] Define one `RenderOutputRequest`/equivalent contract with:
  - [x] output kind and stable output ID,
  - [x] view-family and view identity,
  - [x] target class, target generation, extent, format/sample/view-mask
    compatibility, and external-image slot,
  - [x] hard deadline or desired cadence,
  - [x] priority and maximum CPU/GPU budget,
  - [x] maximum acceptable content age/staleness,
  - [x] required quality features and explicit fallback policy,
  - [x] producer/consumer dependencies and completion requirement.
- [x] Define output classes and default scheduling priority:
  - [x] acquired OpenXR eyes and runtime-required composition layers,
  - [x] desktop/editor output and composition-only desktop VR mirror,
  - [x] visible pickup/handheld and in-world mirrors,
  - [x] shadows required by a deadline-critical view family,
  - [x] light/reflection probes, IBL, thumbnails, and diagnostic captures.
- [x] Treat quality reduction, stale reuse, cadence reduction, or skipped work as
  an explicit per-output policy decision with telemetry. Never silently replace
  an explicitly requested GPU path with a CPU fallback.
- [x] Extend the profiler manifest with render-target mode, primary-reuse
  policy, OBS-hook policy, ImGui-skip state, actual build configuration,
  output/view-family inventory, target extents, cadence/budget policy, and
  scene/settings hash.
- [x] Extend `Measure-VulkanFrameLoop.ps1` so its steady-state gate covers every
  retired resource kind, command-buffer records/reuses/dirty reasons,
  record-path allocations, rejected submissions, planner prune/replacement,
  global waits/force flushes, and stable workload identity.
- [x] Start capture from measured stability (asset/shader work quiet, resource
  generations stable, no retirement churn), not only a fixed warmup duration.
- [x] Add counters for scene snapshots, visibility builds, output requests,
  unique view families, compiled-plan hits/misses, shared-pass reuse, target
  variants, CPU/GPU budget deferrals, stale-result reuse, and missed deadlines.

Acceptance criteria:

- [x] One frame manifest can explain how much work was built once, shared,
  reused, re-recorded, deferred, or duplicated for every active output.
- [x] Performance capture fails when workload identity changes or when any
  unapproved output silently drops quality/work.

Implementation and validation (2026-07-09): schema-v4 frame-output manifests now
carry stable output/view-family/target identities, complete scheduling and policy
contracts, work-disposition accounting, scene/settings identity, and Vulkan
benchmark policy. The harness waits for a measured quiet window, covers all
retirement queues plus planner/global synchronization, and rejects identity
drift, unapproved output policy, or submission rejection. The editor and unit-
test projects build cleanly; 79 targeted contract/profiler/host tests pass. Two
short live Debug/Vulkan runs validated the gate: the first exposed and led to a
fix for nondeterministic duplicate-output ordering; the second held one stable
workload identity and correctly refused capture while texture uploads, retirement
churn, and a global in-flight wait remained active. Its v4 manifest contained
five real output entries and the required build/target/reuse/OBS/ImGui/scene
metadata. Live evidence is under
`Build/_AgentValidation/20260709-233000-vulkan-phase521/` and the engine session
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-09_23-29-11_pid55356/`.
The durable validation ledger is
[`vulkan-core-hardening-phase521-validation-2026-07-09.json`](../../testing/rendering/vulkan-core-hardening-phase521-validation-2026-07-09.json).

### 5.2.2 - Restore Correctness-Proven Command Reuse First

- [x] Replace the environment-only desktop primary-reuse policy with a normal
  capability/policy setting whose safe state can become the production default
  after this phase passes. Keep an explicit diagnostic disable switch.
- [x] Split structural generation from frame-data, target-slot, descriptor,
  resource-allocation, query, overlay, and profiler generations. Camera,
  transform, light constant, and material constant publication must not appear
  as structural command changes.
- [x] Partition each output into stable scene/pass ranges and genuinely volatile
  ranges. Queries, profiler timestamps, dynamic text, ImGui, uploads, and
  diagnostics must not dirty unrelated static scene work.
- [x] Use stable per-frame-slot/per-external-image command-buffer variants where
  Vulkan commands bake target handles. Rebind rotating acquired images through
  a bounded variant set rather than recreating structural plans.
- [x] Reuse static secondary command ranges for compatible target/pass/pipeline/
  descriptor-layout/mesh generations. Refresh dynamic frame data through
  preallocated frame-slot buffers without re-recording those ranges.
- [x] Keep volatile work in separately ordered command buffers or other Vulkan-
  legal partitions so rerecording it does not invalidate a cached scene primary.
- [x] Make every reuse miss report exactly one primary reason and the old/new
  generation or fingerprint field that changed.
- [x] Prove query/occlusion operation cadence cannot force a full primary record
  for every output or every frame.
- [x] Preserve Phase 5.1 ordered entry/end image-state contracts for every reused
  scene, overlay, mirror, eye, capture, and present command buffer.

Acceptance criteria:

- [ ] A stable desktop scene reaches at least 99% clean reuse after warmup; every
  remaining record is attributable to an intentional structural or volatile
  change.
- [ ] Camera motion and ordinary frame-data updates do not record static scene
  command ranges again.
- [ ] Reuse enabled and disabled produce validation-clean, visually equivalent
  output in explicit dynamic and legacy modes.

Implementation status (2026-07-09): the production reuse policy is now a
default-on Vulkan setting with a diagnostic environment override. Cached
variants record independent structural, frame-data, target-slot, descriptor,
resource-allocation, query, overlay, and profiler generations. Static command
ranges retain stable secondary handles while volatile secondary contents can be
rerecorded; frame data refreshes through the existing preallocated slot data.
Primary misses now choose one deterministic old/new field. Query-bearing cached
primaries establish a new host-reset query epoch before replay, so query cadence
no longer mandates primary recording. Focused validation passed 24/24 tests and
the runtime-rendering/editor builds completed without compiler errors.

The live 99% gate remains open for a measured, attributable dependency: the
Unit Testing World produced 125 records, 125 forced-dirty events, and zero clean
reuse because the Phase 5.2.3 image-view/buffer retirement and imported-texture
publication paths globally invalidated every variant throughout the capture.
No validation VUID was emitted in the retained representative run. Complete the
Phase 5.2.3 exact-resource invalidation work, then rerun the three acceptance
checkboxes here without changing the 5.2.2 reuse design. Machine-readable
evidence is in
[`vulkan-core-hardening-phase522-validation-2026-07-09.json`](../../testing/rendering/vulkan-core-hardening-phase522-validation-2026-07-09.json).

### 5.2.3 - Stabilize Resource Identity And Make Invalidation Exact

- [x] Intern/cache image views by backing image allocation generation,
  subresource range, format, view type, and component mapping. Do not create and
  retire equivalent views during steady recording.
- [x] Make mesh/deformation buffer collection diff-based. Compare stable backing
  handle, allocation generation, binding layout, range, and element type rather
  than wrapper/reference identity.
- [x] Resolve the oscillating indirect-atlas/index-buffer wrapper path before
  optimizing around its invalidations.
- [x] Change the retirement reverse dependency index from a diagnostic count to
  the source of truth for invalidation. Dirty only command-buffer variants and
  secondary chains that reference the retiring allocation generation.
- [x] Track target/view/descriptor/pipeline/mesh dependencies per cached range so
  one mirror, probe face, eye image, or swapchain slot cannot globally dirty all
  other outputs.
- [x] Publish resource and descriptor generations only when the structural
  binding actually changes; do not advance a global generation for an identical
  write.
- [x] Add per-owner invalidation counters for exact variants dirtied, unrelated
  variants preserved, and global-fallback invalidations. Global fallback must be
  diagnostic-only and treated as a failed steady-state gate.

Acceptance criteria:

- [x] Stable captures retire zero image views, images, buffers, samplers,
  framebuffers, pipelines, and descriptor pools.
- [x] Retiring one probe/mirror/eye resource cannot dirty an unrelated desktop,
  eye, mirror, or capture command cache.
- [x] `CollectBuffers`, indirect-index synchronization, and descriptor
  publication produce zero steady-state structural invalidations.

Phase 5.2.3 completed on 2026-07-10. Image views now use complete structural
identity and retain dormant slot variants until the backing image allocation is
retired. Mesh/deformation buffers compare allocation-backed identities, external
indirect index buffers remain authoritative, descriptor publication advances
only once per dirty epoch, and retirement invalidation consumes the exact reverse
dependency set. The strict post-warmup capture retired zero gated resources and
reported zero forced-dirty, exact-invalidation, and global-fallback events. A
separate active-retirement capture preserved 7,753 unrelated variants with zero
global fallback. Focused Phase 5 validation passed 39/39 tests; the editor build
completed without compiler errors. Evidence is recorded in
[`vulkan-core-hardening-phase523-validation-2026-07-10.json`](../../testing/rendering/vulkan-core-hardening-phase523-validation-2026-07-10.json).

The aggregate command-record/reuse ratio in the retained quiet capture was 0.5:
one clean scene-primary reuse and one expected volatile overlay recording per
frame. There were no forced-dirty events or dirty summaries. Phase 5.2.2's 99%
gate remains open until its metric separates reusable primaries from intentional
volatile command buffers.

### 5.2.4 - Move Lifetime And Layout Tracking Out Of Per-Draw Locks

- [x] Record resource dependencies and image access/layout deltas in
  command-buffer-local, capacity-backed storage while recording.
- [x] Bulk-deduplicate and publish the finished dependency/layout snapshot under
  one short lock or generation-checked handoff after successful recording.
- [x] Expand each descriptor set's referenced resources once per descriptor-set
  generation and command-buffer recording generation, not once per bind.
- [x] Cache Debug descriptor-layout validation by command-buffer generation,
  descriptor generation, and layout-state version while preserving exact
  Phase 5.1 failure diagnostics.
- [x] Replace per-aspect/mip/layer dictionary writes in the barrier hot path with
  compact range/delta recording, then coalesce before publication.
- [x] Replace submit-time full dictionaries scans with the submitted command
  buffers' touched dependency/subresource lists.
- [x] Remove global lifetime/layout locks from per-draw, per-descriptor-bind, and
  per-barrier steady paths. Add contention and touched-entry counters to prove
  the result.

Acceptance criteria:

- [x] Recording cost scales with unique changed dependencies/ranges, not total
  repeated binds or the global number of tracked resources.
- [x] Debug validation retains the same correctness coverage without recurring
  100+ ms `MainOpLoop` drop samples from bookkeeping.

Phase 5.2.4 completed on 2026-07-10. Command recording now accumulates resource
dependencies and compact image-access ranges in command-buffer-local,
capacity-backed batches. Descriptor reference expansion and Debug layout
validation are cached by descriptor/recording/layout generation, and successful
recording publishes retained touched lists under one short lifetime lock and one
short layout lock. Submit validation and layout publication consume only those
touched lists; the lists retain capacity and do not allocate `ToArray()`
snapshots each frame.

The retained desktop-only Dynamic Rendering capture passed the strict stable-
workload, resource-churn, and 99% clean-reuse gates. Across 369 samples it
reported 36,900 dependency binds compacted to 23,247 unique dependencies,
1,476 image-access writes/ranges, 4,059 descriptor-expansion cache hits, zero
lifetime/layout lock contentions, zero command-record allocations, zero
resource retirements, and 369/369 clean primary reuse. Command recording was
0.424 ms p95 and 0.628 ms max; no FPS-drop record occurred during the capture
window. A follow-up six-second moving-camera run kept 15/15 sampled full editor
window frames populated after narrowing the inline-primary safety key to actual
camera pose rather than TSR jitter. After a user-reported stop-boundary flicker,
the replay generation gained one explicit settle invalidation; a 45-frame
motion-plus-stop capture stayed populated and a final quiet capture retained
234/234 clean reuse with zero tracking contention or record allocations.
Machine-readable evidence is in
[`vulkan-core-hardening-phase524-validation-2026-07-10.json`](../../testing/rendering/vulkan-core-hardening-phase524-validation-2026-07-10.json).

### 5.2.4a - Eliminate Descriptor Lifetime Pressure And Retirement Scans

- [x] Add a descriptor-pool-to-owned-set reverse index. Pool retirement,
  mutation checks, reset, and destruction now visit only that pool's sets rather
  than scanning every tracked descriptor set for every pool.
- [x] Reserve descriptor-pool retirement identity before capturing tickets so
  duplicate retirement requests exit before any set traversal or allocation.
- [x] Key mesh descriptor allocations by cached descriptor-layout handles,
  schema, frame/draw shape, and set count. Program binding IDs and mutable
  resource fingerprints no longer create pool variants.
- [x] Allocate descriptor sets lazily for the frame/draw slot that is actually
  used, rewrite changed resources in place, and retain an LRU-bounded set of 32
  structural allocation variants.
- [x] Add resource-to-referencing-descriptor-set and
  resource-to-command-buffer reverse dependencies. Resource retirement now
  advances only affected descriptor generations and dirties only exact cached
  command variants.
- [x] Aggregate all command-buffer dependencies for a retiring descriptor pool
  into one exact invalidation pass; do not perform a global descriptor-cache
  release or one cache invalidation/log pass per owned set.
- [x] Convert late command-local lifetime publication races into pre-submit
  rejection without first-chance exceptions, then dirty the exact cached
  command buffer for rerecord.
- [x] Rate-limit retirement diagnostics by Vulkan object type so startup texture
  replacement cannot emit one expensive formatted log entry per resource.
- [x] Publish live-resource, tracked-descriptor-set, and descriptor-pool-create
  counters through profiler packets/captures. Add strict steady-state gates and
  `Tools/Validate-VulkanDescriptorLifetimePressure.ps1` for OpenXR/Monado.

Acceptance criteria:

- [x] No descriptor-pool lifetime path contains the prior O(pool count x global
  descriptor-set count) ownership scan.
- [x] A live Monado OpenXR single-pass-compatibility run reaches steady state
  with zero descriptor-pool creates/retirements, submission rejections, or
  global fallback invalidations across its retained 250-frame tail.
- [x] The same tail remains bounded at 4,669-4,727 live Vulkan resources and
  2,356-2,367 tracked descriptor sets, below the 50,000/25,000 pressure gates.
- [x] No render exception, first-chance lifetime exception, validation VUID,
  device loss, or debugger-visible descriptor ownership scan occurs after the
  fix.

Phase 5.2.4a completed on 2026-07-10. The original pause site compared each
descriptor set's pool while repeatedly scanning a global set table. Ownership
and reference reverse indexes remove that scaling failure. Descriptor allocation
identity is now structural all the way down: the final profiler run reported
zero pool creates and zero retired pools in its retained steady 250-frame tail,
versus 4,050 creates in the equivalent pre-fix tail. The run used Monado OpenXR,
Vulkan, `SinglePassStereo`, desktop/mirror output, and dynamic rendering. Three
startup submissions whose resources were replaced while command-local tracking
was still being published were safely rejected and exactly dirtied; no
first-chance exception was thrown, and the retained steady tail had zero
rejections. The generic stability harness did not auto-select a window because
the run intentionally exposed two alternating output workload identities, so
the same profiler fields were evaluated directly over the retained tail.
Evidence is recorded in
[`vulkan-core-hardening-phase524a-validation-2026-07-10.json`](../../testing/rendering/vulkan-core-hardening-phase524a-validation-2026-07-10.json)
and
[`vulkan-descriptor-lifetime-freeze-2026-07-10.md`](../../investigations/rendering/vulkan-descriptor-lifetime-freeze-2026-07-10.md).

### 5.2.5 - Make Render Plans And Resource Arenas Versioned And Nonblocking

- [ ] Redefine planner/cache identity around physical compatibility: output kind,
  view-family/pipeline identity, extent, pass graph, attachment signature,
  resource-plan generation, and queue family. Do not key the compiled plan on a
  rotating external swapchain/OpenXR image handle.
- [ ] Bind the current external target slot as a bounded plan/command variant;
  keep plan identity stable across acquisition rotation.
- [ ] Store immutable compiled plan generations. A replacement publishes a new
  generation while old plans, allocators, descriptors, and resources remain
  alive behind their last submitted timeline/retirement ticket.
- [ ] Remove `WaitForAllInFlightWork` and force-flush calls from command
  recording, planner-state prune, physical-plan replacement, imported-texture
  replacement, and normal cache eviction.
- [ ] Evict planner states incrementally only after their last-use timeline is
  complete. Cache capacity pressure must defer/retire work, not globally drain
  the device.
- [ ] Give each concurrently active output family a bounded persistent resource
  arena and transient aliasing plan. Reuse compatible allocations across frames
  and alias only resources whose scheduled lifetimes do not overlap.
- [ ] Separate runtime-owned external images from engine-owned allocation and
  retirement. External image rotation must not churn engine resource plans.
- [ ] Add plan-cache hit/miss, plan-generation, arena high-water, alias reuse,
  pending-retirement bytes, and eviction-defer telemetry by output family.

Acceptance criteria:

- [ ] No normal frame, eye render, mirror update, probe face, or cache eviction
  waits for all in-flight GPU work or force-flushes the device.
- [ ] Alternating OpenXR images, mirror targets, probe faces, and desktop
  swapchain images does not grow planner-state count or replace an otherwise
  compatible physical plan.
- [ ] The correlated 43.7-second planner-prune and 8.4-second plan-replacement
  failure shapes are no longer reachable in normal scheduling.

### 5.2.6 - Build One Immutable Scene Snapshot And View-Family DAG

- [ ] Publish one immutable render-world snapshot per engine frame containing
  stable scene objects, transforms, lights, materials, shadow/probe state, and
  GPU-scene references. Output workers must never reread mutable scene state.
- [ ] Group output requests into compatible view families before collection and
  command construction. A family owns shared scene/material/light publication
  and contains one or more view/projection/target variants.
- [ ] Build visibility once per compatible family or compute one conservative
  superset plus compact per-view masks. Do not repeat a full scene traversal for
  every eye, foveated inset, desktop mirror, or probe consumer.
- [ ] For foveated XR eyes, represent wide/inset views as one deadline-critical
  family. Use Vulkan multiview/single-pass and shared visibility/material data
  where feature/quality constraints permit; keep an explicit sequential path
  for parity and unsupported hardware.
- [ ] Compose the normal desktop VR mirror from already rendered eye/family
  outputs by default. Schedule a full independent desktop scene only when the
  selected mirror policy explicitly requires a distinct camera or quality.
- [ ] Model pickup/handheld mirrors and in-world 3D mirrors as view-dependent
  output requests with stable IDs, screen-coverage/visibility input, maximum
  update rate, content-age limit, resolution policy, recursion limit, and
  cacheable last result.
- [ ] Share view-independent work such as material publication, compatible
  shadow results, BRDF/LUT inputs, and GPU-scene buffers across families. Never
  share view-dependent results without an explicit compatibility proof.
- [ ] Model light/reflection probe faces, mip generation, octa conversion,
  irradiance, and prefilter mips as individually schedulable DAG nodes with
  persistent intermediate resources and resumable progress.
- [ ] Ensure one output's optional post-processing does not force unrelated
  outputs through the full desktop temporal/post stack. Apply the existing
  capture policy at graph construction time.

Acceptance criteria:

- [ ] Profiler counters prove one scene snapshot per engine frame and no
  duplicate material/light publication for compatible output families.
- [ ] A composition-only desktop VR mirror adds no scene traversal or independent
  full render.
- [ ] Adding compatible foveated views increases per-view cull/target work but
  does not multiply scene snapshot, material, descriptor, or stable command
  construction by view count.
- [ ] Cached mirrors/probes reuse their prior result within explicit age/quality
  policy and never masquerade as freshly rendered output.

### 5.2.7 - Add A Deadline-Aware CPU/GPU Output Scheduler

- [ ] Schedule the output DAG by deadline, dependency readiness, measured cost,
  and policy priority rather than invoking independent full render loops in
  callback order.
- [ ] Reserve the acquired OpenXR eye budget first. Do not start optional work
  that cannot finish or reach a legal preemption boundary before the eye
  submission deadline.
- [ ] Give desktop, pickup/in-world mirrors, shadows, probes, IBL, uploads, and
  diagnostics explicit rolling CPU/GPU budgets and maximum consecutive work.
- [ ] Use measured exponential/percentile cost estimates per node to decide
  start/defer. Record predicted versus actual time and correct bad estimates.
- [ ] Make auxiliary work resumable at legal graph boundaries: probe face,
  cubemap mip, octa conversion, irradiance pass, individual prefilter mip,
  shadow slice, and upload batch.
- [ ] Add adaptive mirror/probe cadence based on visibility, screen coverage,
  motion, content age, and available slack. Preserve an explicit fixed-cadence
  mode for validation and user requests.
- [ ] Keep GPU submissions bounded for TDR and frame pacing. Batching outputs is
  permitted only when it reduces CPU overhead without creating an oversized,
  non-preemptible submission.
- [ ] Expose queued/running/deferred/completed/failed state, budget reason,
  content age, deadline miss, and accumulated work for every output.

Acceptance criteria:

- [ ] Optional mirrors/captures/probes cannot cause a deadline-critical eye frame
  to miss its CPU submit budget or wait behind a long auxiliary submission.
- [ ] A 36-probe batch and visible mirrors make bounded forward progress while XR
  remains active; neither monopolizes consecutive frames.
- [ ] No work is silently dropped. Every defer, stale reuse, cadence reduction,
  or quality choice is visible and policy-authorized.

### 5.2.8 - Decompose Submission And Remove CPU Serialization

- [ ] Split submit telemetry into queue-lock wait, diagnostic-context build,
  image-contract validation, lifetime validation, native submit, lifetime
  publication, layout publication, and completion advancement.
- [ ] Build one ordered submission DAG across uploads, shadows, scene/view
  families, mirrors, captures, overlays, and presentation. Preserve independent
  queue work where dependencies permit.
- [ ] Use timeline/frame-slot completion for engine queues and OpenXR image
  reuse. Remove per-output fence creation and indefinite CPU waits from normal
  eye/mirror/capture submission.
- [ ] Narrow queue-lock ownership to the native externally synchronized Vulkan
  operation and required adjacent state publication. Do not hold it while
  building diagnostics or scanning unrelated state.
- [ ] Batch compatible command buffers into a submit when it lowers CPU cost,
  but retain separate completion/deadline boundaries for XR-critical and
  auxiliary work.
- [ ] Build render packets and record dirty independent secondary command ranges
  on workers from the immutable frame snapshot. The render thread performs only
  final ordered validation/reuse choice and submission.
- [ ] Give workers persistent per-thread scratch, command pools, and capacity-
  backed packet/recording buffers. Do not allocate or contend on shared global
  collections in the worker hot path.
- [ ] Measure worker record time, render-thread wait-for-worker time, queue-lock
  time, submit count, command buffers per submit, and CPU/GPU overlap by family.

Acceptance criteria:

- [ ] Submit p95 has an attributable breakdown and contains no full global
  dependency/layout scan.
- [ ] OpenXR runtime updates, desktop submission, and auxiliary recording do not
  serialize on a broad engine queue lock.
- [ ] Parallel recording improves or preserves p95 without changing output,
  submission order, fallback policy, or device-loss containment.

### 5.2.9 - Remove Remaining Hot-Path Allocation And Superlinear Work

- [ ] Remove per-FBO attachment-layout arrays, dynamic UI split arrays,
  exact-length frame-op drain arrays, image-layout snapshot arrays/sorts,
  planner registry merge arrays, and retirement-drain temporary arrays from
  steady paths.
- [ ] Complete dynamic-rendering Phase 1.1 bounded inline format-signature
  storage and use the same allocation-free identity representation in planning,
  inheritance, pipeline lookup, and command-cache keys.
- [ ] Replace repeated render-graph sorting with one deterministic compile/sort
  per structural generation. Replace O(n^2) context-order and clear-lifting
  scans with indexed/linear algorithms.
- [ ] Preallocate output/view-family DAG nodes, dependency edges, packet lists,
  touched-resource lists, and telemetry rows to measured high-water marks.
- [ ] Add per-scope allocation counters for snapshot build, family grouping,
  visibility, plan lookup/compile, packet build, primary/secondary recording,
  submit validation/publication, retirement drain, and diagnostics.

Acceptance criteria:

- [ ] Stable desktop and mixed-output frames allocate zero managed bytes in
  collection, planning, recording, submission, and retirement hot paths.
- [ ] Work scales approximately with changed nodes, unique view families, and
  actual draws, not total cached outputs times total scene size.

### 5.2.10 - Validation Matrix And Promotion Gate

- [ ] Run validation-disabled performance and validation-enabled correctness as
  separate cohorts with identical workload/settings manifests.
- [ ] Capture at least three 60-second stable repetitions for:
  - [ ] desktop only;
  - [ ] desktop plus ImGui/dynamic text;
  - [ ] OpenXR foveated eye family without a desktop mirror;
  - [ ] OpenXR eyes plus composition desktop mirror;
  - [ ] OpenXR eyes plus pickup/handheld mirror;
  - [ ] desktop with one and four visible in-world mirrors;
  - [ ] light-probe batch without XR;
  - [ ] OpenXR eyes plus mirrors plus a 36-probe batch;
  - [ ] explicit dynamic and legacy render-target modes;
  - [ ] `CpuDirect` and a separate `GpuIndirectZeroReadback` cohort.
- [ ] For every cohort record CPU frame p50/p95/p99/worst, observed render
  throughput, GPU family/pass p50/p95, missed XR deadlines, output content age,
  scene snapshots/visibility builds, record/reuse/dirty counts, allocation,
  resource retirement, plan churn, queue waits/submits, and quality/fallback
  events.
- [ ] Capture and inspect screenshots from at least two camera positions for
  desktop, each eye/foveated region, composition mirror, pickup/in-world mirror,
  and representative probe faces/final IBL output.
- [ ] Run StandardValidation and SyncValidation over the mixed-output matrix and
  confirm Phase 5.1 ordered state/lifetime contracts remain clean.
- [ ] On the RTX 3090 / 0.67-scale desktop diagnostic scene, require CPU frame
  p50 <= 10 ms, p95 <= 12 ms, and p99 <= 14 ms, or approve and document a new
  workload-equivalent baseline before promotion.
- [ ] Require dynamic rendering to remain within 5% of legacy p50/p95/p99 across
  repetitions unless an explained GPU-side win justifies a measured CPU cost.
- [ ] Require >=99% clean command reuse, zero stable record-path allocation,
  zero steady resource retirement/plan replacement, zero rejected submissions,
  and zero global waits/force flushes for static-output cohorts.
- [ ] Define and meet an XR missed-deadline threshold before running auxiliary
  work. The threshold must be based on the active runtime refresh rate and
  separately report runtime/compositor-owned misses.
- [ ] Require a composition-only desktop VR mirror to add no independent scene
  render and no more than an approved sub-millisecond CPU p95 delta.
- [ ] Require mixed mirror/probe workload cost to follow the configured scheduler
  budget rather than grow linearly with every potential output each frame.
- [ ] Update the CPU framerate investigation with final before/after tables,
  manifests, screenshots, raw-log paths, and any approved exceptions.

Final acceptance criteria:

- [ ] Desktop-only rendering has an accepted CPU baseline and no longer relies
  on a diagnostic environment flag for acceptable command reuse.
- [ ] Multiple outputs consume one shared frame snapshot and reuse compatible
  visibility, material, plan, resource, and command work.
- [ ] Deadline-critical XR eyes cannot be blocked by optional mirror, probe,
  capture, upload, or diagnostic work.
- [ ] Correctness-safe reuse and targeted invalidation remain clean under
  retirement, resize, hot reload, target rotation, and device-loss injection.
- [ ] No tested workload hides a requested accelerated path behind a CPU fallback
  or unreported quality/cadence reduction.
- [ ] Only after all Phase 5.2 acceptance criteria pass may Phase 6 begin.

## Phase 6 - Descriptor And Binding Robustness

Build on Phase 5.2's allocation-free, generation-driven descriptor publication
seam. Descriptor hardening must not restore per-draw fingerprint construction,
global command-cache invalidation, or identical-write generation churn.

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

Use the Phase 5.2 output scheduler, timeline completion, view-family, and submit
DAG as the generic execution path. This phase owns OpenXR-specific image/runtime
state-machine correctness and failure attribution, not a second eye-only
submission architecture.

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

Extend Phase 5.2's deadline scheduler and resumable auxiliary nodes with Vulkan
memory admission, TDR protection, and subsystem-specific quality policy. Do not
introduce a parallel probe/capture queue that bypasses the shared output DAG.

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

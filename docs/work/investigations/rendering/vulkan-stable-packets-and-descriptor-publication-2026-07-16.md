# Vulkan Stable Packets And Descriptor Publication - 2026-07-16

## 2026-07-31 Command-Recording Optimization Wrap-Up

### Problem

Clean Release Vulkan shader reload reproducibly terminates the editor while a
desktop primary command buffer is recording a non-indexed mesh draw. The same
reload path survives with Vulkan validation enabled, so validation timing masks
the defect and a validation-clean run is not sufficient acceptance evidence.

### Findings And Attempted Solutions

- Crash dumps for editor PIDs 42632, 290928, 40820, and 292620 all report
  `System.ExecutionEngineException` at `Silk.NET.Vulkan.Vk.CmdDraw`, reached
  through `VkMeshRenderer.RecordDrawNoLock` and
  `VulkanRenderer.RecordCommandBufferLifecycle`.
- Shader dependency notifications formerly invalidated Vulkan programs on the
  render thread while workstream-04 could already be producing the next frame
  package. Invalidation now publishes as one batch at the collect/render
  frame-swap barrier, and `VkRenderProgram` link/interface mutation is
  serialized.
- Mesh enqueue now captures program selection and binding state as one
  renderer-local publication transaction. `PendingMeshDraw` also carries the
  captured program link generation, and consumers reject a stale generation
  before relinking or recording.
- These changes pass the focused shader, binding, stable-packet, and pipeline
  selection (143/143), but the newest clean Release reload still crashed after
  invalidating 61 shaders. The attempted solutions therefore improve the
  publication contract but do not close the defect.

### Evidence

- Dumps:
  `C:/Users/dnedd/AppData/Local/CrashDumps/XREngine.Editor.exe.42632.dmp`,
  `XREngine.Editor.exe.290928.dmp`, `XREngine.Editor.exe.40820.dmp`, and
  `XREngine.Editor.exe.292620.dmp` in the same directory.
- Focused test result:
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/reports/shader-link-generation-publication.trx`.
- The named `cmd-record-arch-opt` editor session is stopped.

### Next Isolation Step

Capture the first failing clean Release reload under RenderDoc or API-level draw
tracing and freeze the exact pipeline, pipeline-layout, descriptor-set, vertex
buffer, render-scope, and command-buffer identities immediately before the
failing `vkCmdDraw`. Compare those handles with their retirement tickets and
the preceding successful frame. The repeated stack proves that scheduling the
managed invalidation alone is insufficient; the next pass should identify the
specific native handle or command-buffer lifecycle transition that becomes
invalid rather than add another broad synchronization layer.

User verification: not yet reported.

## Problem

Desktop Vulkan recording keyed large primary variants on the complete visible
draw set. Command-chain lowering then produced one packet and one secondary per
draw, so camera movement created primary-cache churn without amortizing Vulkan
recording. Imported-texture publication also treated descriptor content changes
like command structure changes.

## Changes

- Mesh draws now lower into deterministic contiguous packets of 10-64 compatible
  draws, grouped by pass, target, view, pipeline/material program, descriptor
  schema, transparency, and scheduling context.
- Scheduled execution records every draw in a packet into one secondary and
  executes one secondary handle per packet. Query-bracketed and dynamic overlay
  work remains inline.
- Each frame slot reuses one command-chain primary execution list across camera
  membership changes instead of caching a primary for every visible-set
  signature.
- Scheduled secondary caches are bounded to 128 entries per frame slot and
  report evictions.
- Packet recording uses persistent workers with stable chain-to-worker
  assignment and one command pool per worker/frame-in-flight slot. Pools are
  reset only through the frame-slot completion path.
- Packet prewarm happens before `vkBeginCommandBuffer`; a pending graphics
  pipeline leaves the packet `NotReady` with a pipeline-generation dirty reason
  instead of beginning and abandoning an invalid partial secondary.
- Fresh non-cached mesh secondaries use `ONE_TIME_SUBMIT`. A later exact
  artifact-lifetime audit restored `SIMULTANEOUS_USE` for reusable frame-slot
  packets because recorded primaries may retain the same secondary generation
  across more than one pending execution. Removing it again requires a
  single-pending ownership proof and measured benefit.
- Resource changes are explicitly classified as frame data, compatible content
  publication, binding identity, or structural layout. Compatible descriptor
  generation changes retain the secondary and refresh the stable per-frame
  descriptor contents after slot completion.
- Non-update-after-bind descriptors use per-frame-slot copy-on-write
  publication. Compatible content changes preserve allocation/layout identity;
  binding or structural changes invalidate only the affected packet family.
- Packet, render-pass group, and schedule objects now retain geometrically
  grown backing storage and are reused by frame slot. Lowering no longer creates
  one `RenderPacket` reference object or exact-sized draw/key/group array per
  stable packet rebuild.
- Command-chain lowering retains its draw scratch, packet pool, structural
  occurrence map, and distinct-view set. Vulkan indirect bucket submission uses
  a value-type state scope, avoiding a reference allocation and interface boxing
  for every bucket.

## Validation

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  completed with zero compile errors. Existing NuGet vulnerability and two
  unrelated Surfel GI field warnings remain.
- Focused command-chain, pipeline, and descriptor tests: 100 passed, 0 failed.
- The first live run exposed and reproduced the former freeze pattern: the
  internal schedule validator still required one chain per source op, rejected a
  27-op/2-packet group, threw `InvalidOperationException`, and repeatedly forced
  swapchain recreation. The validator now sums packet source ranges instead.
- A 55-second desktop Vulkan rerun with `StandardValidation`, command chains,
  and internal command-chain validation enabled reported no VUID, validation
  error, command-record failure, `InvalidOperationException`, device loss, or
  swapchain-recovery loop. Evidence session:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_14-03-15_pid21460`.
- A later dynamic-rendering run with the persistent workers active remained
  validation-clean and reused stable packets:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_15-25-45_pid18428`.
- The equivalent legacy-render-pass run reported zero VUID, validation error,
  `InvalidOperationException`, or device loss:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_15-35-25_pid45352`.
- Focused tests cover compatible texture publication, material binding/layout
  edits, resize/swapchain rotation, hot reload, frame-slot publication delay,
  and retirement behind completion.
- The broader imported-texture contract selection passed 101 tests and exposed
  five pre-existing source-contract failures: four refer to the old pre-folder-
  split command-buffer path and one expects an older streaming-manager source
  phrase. None failed in the new packet or descriptor tests.
- On 2026-07-17, the focused stable-packet suite passed 12/12. Its warmed
  container-rebuild regression performs 1,000 packet/group/schedule resets and
  asserts exactly zero bytes from `GC.GetAllocatedBytesForCurrentThread()`.
  The rendering project also built with zero errors through redirected output
  while the user's live editor retained the normal build output lock.
- A broader indirect/command-chain selection passed 234 tests and failed 18
  inherited source-contract/state-isolation tests. A serialized indirect
  resolver/zero-readback subset passed 78 and failed three already-stale
  source-contract tests. None of those failures touches the new reusable
  packet containers or value-type indirect state scope.

## Remaining runtime evidence

The code, deterministic contracts, validation-enabled desktop smoke runs, and
the isolated steady-state container allocation regression are clean. The
measurement harness now accepts explicit occlusion modes, Vulkan diagnostic
presets, and command-buffer-label enablement and records those selections in
its machine-readable manifest. A warmed Release desktop camera-path capture is
still required to quantify end-to-end primary/secondary record counts, total
record-path allocation, and CPU/GPU p95 before closing the performance
acceptance criteria. That capture cannot be validly collected while another
editor instance owns the GPU workload and normal output files.

## 2026-07-17 Release follow-up

- The focused Vulkan/OpenXR contract selection now passes 80/80 after the live
  allocation and indirect-counter changes.
- `GpuIndirectZeroReadback` stable evidence at
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_06-47-55/summary.json`
  records zero readback bytes, zero mapped buffers, exact requested/consumed
  draw parity, 2,250 indirect API calls, 144,000 submitted indirect draws, and
  zero allocations in every measured Vulkan stage except frame-data refresh.
- The three-lane warmed smoke at
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_06-56-41/summary.json`
  records zero VUIDs for `CpuDirect`, `GpuIndirectInstrumented`, and
  `GpuIndirectZeroReadback`. Its render p50/p95 values were 9.779/11.123 ms,
  17.849/18.905 ms, and 15.817/17.065 ms respectively.
- A non-perturbing EventPipe allocation trace then identified the remaining
  indirect refresh allocation under compute auto-uniform resolution:
  `Enum.TryParse` initialized reflection metadata and `Array.GetValue` boxed
  value-array elements. Span-based engine-uniform matching plus typed compute
  array writers remove both sources. The stable evidence at
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_07-10-18/summary.json`
  records zero allocation in every measured Vulkan stage over 76 samples.
- The CPU-direct trace identified pipeline-fingerprint LINQ/iterators, common
  interface enumeration, and generated `StencilOpState` equality boxing. The
  stable rerun at
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_07-16-03/summary.json`
  records zero allocation in every measured stage over 198 samples, zero
  capture command-buffer records, and 9.940/11.181/15.014 ms render
  p50/p95/worst.
- End-to-end zero-allocation is closed for the warmed production desktop lanes.
  Parallel-performance parity and the repeated 60-second static/moving-camera
  matrix remain open; short smoke runs are not substituted for either gate.

## 2026-07-17 Device-loss continuation

- Renderer-family worker affinity now hashes the mutable `VkMeshRenderer`
  identity, keeps every family of that renderer on one fixed worker pool, and
  routes heterogeneous chains through serial recording. Dirty worker batches
  are invalidated before dispatch so an incomplete batch cannot retain an old
  executable secondary.
- Every core renderer-owned `vkResetCommandBuffer` now passes an engine lifetime
  preflight first. Cached command-chain secondaries whose exact submission is
  incomplete are replaced and retired instead of being reset in place.
- Worker planner readback scopes are serialized because they temporarily swap
  renderer-wide resource-planner state. This reduced one failure from startup
  frame 2-5 to frame 46 but did not close the device loss, proving it was a real
  race but not the sole cause.
- The focused stable-packet/OpenXR/command-chain selection passes 157/157, and
  the Release editor builds with zero compile errors. Existing Magick.NET
  vulnerability warnings and unrelated Surfel GI field warnings remain.
- A worker-disabled StandardValidation run completed 158 frames, passed its
  stability gate, and reported zero VUIDs:
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_11-16-28/summary.json`.
  A later worker-enabled proof also completed 178 frames cleanly:
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_11-26-02/summary.json`.
  Neither single run is accepted as causal or sufficient.
- Repeated worker-enabled cohorts remained nondeterministic. The 30-second
  acceptance attempt at
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_11-28-53/summary.json`
  completed only repetition two. The retained repetition-three exception names
  pending `SwapchainPrimary[1]`, `ImGuiOverlay.Primary[1]`, and
  `texture upload command buffer` objects immediately before `vkQueueSubmit`
  returned `VK_ERROR_DEVICE_LOST`.
- An explicit per-swapchain-image submission-fence experiment did not help and
  was reverted. Its three launches failed at
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_11-40-18/summary.json`;
  one failure reported no validation error before device loss, while another
  observed the graphics timeline at `ulong.MaxValue` only after the device was
  already failing. This is treated as fallout, not a proven signal source.
- Parallel command-chain workers are now quarantined while the serial
  command-chain secondary path remains active. The quarantine cohort still
  failed nondeterministically with a validation-clean `vkQueueSubmit` device
  loss:
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_11-47-14/summary.json`.
  Therefore worker recording is independently unsafe but is not the sole
  originating cause.
- The RenderDoc skill workflow could not produce a capture: `rdc-cli` is not
  installed, and the installed `renderdoccmd.exe` fallback launched an injected
  process without a visible editor frame/capture boundary. The bounded attempt
  root is
  `Build/_AgentValidation/20260717-vulkan-device-loss-renderdoc/renderdoc/`;
  it contains no valid `.rdc` and is not GPU evidence.

Current conclusion: P0 remains blocked by a validation-clean, startup-time GPU
command/submission failure. The next useful step is an interactive RenderDoc or
equivalent capture that can identify the last valid pass and inspect the
zero-readback indirect command/count buffers. More slot, timeline, or worker
affinity changes without that GPU evidence are not justified.

## 2026-07-17 Strategy-wide zero-readback containment

- A profiled command-chain run survived 163 frames with exact requested/consumed
  indirect parity, zero readback, zero fallback, and zero VUIDs, but profiling
  forced primary re-recording. An unprofiled run continued to reset the driver.
- Quarantining clean primary reuse and indirect-draw secondaries did not close
  the reset. Entering play mode still produced `nvlddmkm` event 153 followed by
  a `vulkan-1.dll` `0xc0000409` fail-fast. The remaining flaw was scope: static
  command-buffer segments could still schedule persistent secondaries even when
  mutable zero-readback publications lived in another segment.
- Command-chain scheduling now rejects the resolved GPU zero-readback strategy
  for the entire frame. A second per-segment mutable-op guard remains as defense
  in depth. The renderer still executes GPU compute culling, scatter, and
  indirect-count draws on a freshly recorded primary.
- The same StandardValidation play-mode run then remained responsive for 20
  seconds with command chains requested and produced no new watchdog event.
  Capture:
  `Build/_AgentValidation/20260717-vulkan-device-loss-renderdoc/mcp-captures/zero-readback-fixed/os-window-playing-strategy-quarantine.png`.
- Visual parity is still open. The capture proves Sponza is loaded and active in
  the hierarchy but shows only the skybox. MCP accepted a known camera pose and
  the renderer remained responsive; however, OS compositing returned a black
  Vulkan client surface in that session, so it cannot prove geometry presence
  or absence. A GPU debugger or a correctly synchronized Vulkan readback is
  required to inspect the final target and indirect buckets.
- Vulkan MCP screenshot readback is now rejected explicitly before transfer
  because the synchronous path independently reproduced a watchdog reset.
- Validation: Release editor build succeeded with zero errors. The focused P0
  source/runtime contracts pass 53/53. The larger selection passed 67 and failed
  17 stale P1 source-contract path/token assertions after the command-buffer
  folder split; no full-suite-clean claim is made.

Revised conclusion: the immediate zero-readback/command-chain device-loss risk
is contained by an explicit strategy-wide quarantine. P0 remains open for
missing-Sponza visual parity, repeated Standard/SyncValidation cohorts, external
marker proof, and the required performance/motion/resize/hot-reload matrix.

## 2026-07-30 OpenXR concurrent-recording closeout

- Removed the renderer-wide OpenXR eye-primary recording lock. Persistent left
  and right workers now record directly into their separately owned primary
  command buffers, and mutable upload discovery uses thread-local collections.
- Worker completion records native start/end timestamps and thread identity.
  The clean Monado synchronization-validation cohort under
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/phase6-monado-sync-validation-final/`
  submitted five stereo frames. All five paired recordings overlapped on
  distinct threads; overlap/span samples were 71.050/80.750 ms,
  48.634/57.270 ms, 38.195/52.427 ms, 61.019/62.844 ms, and
  34.780/331.838 ms.
- The run reported zero Vulkan VUIDs, synchronization hazards, submission
  failures, or teardown failures. A preceding fresh-session cohort submitted
  thirteen frames while cycling every per-eye runtime swapchain image, also
  without image-journal failures.
- Submission publication is success-gated, generation-checked, and ordered
  `[left, right]`; failed recordings are abandoned and swapchain image
  recreation clears exact recorded and submitted subresource state. The focused
  logical-device, OpenXR, per-view resource-shape, and image-lifecycle contract
  selection passes 30/30.
- Monado's Vulkan-device path prepends the granular timeline-semaphore feature
  struct. OpenXR device creation now represents the Streamline-compatible
  Vulkan 1.2/1.3 subset with granular feature structs so aggregate and granular
  feature declarations are never mixed in one `pNext` chain. Unsupported future
  aggregate-only Streamline requirements fail explicitly.
- The OpenXR dynamic UI text-secondary path now defers before resetting its
  cached secondary when pipeline prewarm is incomplete and abandons any failed
  recording. This removed the unrecorded-secondary validation failure found by
  the synchronization-validation cohort.

Phase 6 of the command-recording architecture tracker is complete. The next
active slice is physical frequency-owned payload storage and descriptor
ownership; the broader P0 performance/visual acceptance matrix remains open.

## 2026-07-31 Frequency-owned descriptor publication

- Descriptor allocations now separate topology generation, stable resource
  content generation, and exact per-frame-source slot signatures. Each
  in-flight descriptor slot publishes the owner generations it contains.
- A stable owner lookup key resolves allocations independently of transient
  draw occurrence. Material numeric parameter edits advance
  `BindingValueVersion`; texture/resource edits advance the separate
  `BindingResourceVersion`.
- Full binding/resource fingerprints remain the exact slow-path correctness
  backstop. When the resolved fingerprint matches, the current owner generation
  is published so subsequent stable frames remain on the bounded generation
  check.
- Five consecutive validation-enabled samples from the isolated
  `cmd-record-arch-opt` Vulkan session each visited 25 reusable frame-data draw
  operations but reported zero descriptor records validated, zero descriptor
  records written, and zero descriptor owner-lookup, owner-generation, or
  frame-source-generation misses. The samples also reported zero Vulkan
  validation errors.
- The final viewport capture was visually inspected and contains the expected
  physics-test geometry:
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/mcp-captures/descriptor-owner-generation-final/Screenshot_20260731_021152_323_7ca1b1a4527d4673ba93eddceb2563f5.png`.
- The focused schema/publication/owner-generation selection passes 9/9. The
  existing Magick.NET advisory warnings are unrelated. The broader class run is
  not claimed clean because 15 source-contract tests still read stale
  pre-partial-file paths or tokens.

This closes stable descriptor proof and native write suppression. The next
active bottleneck is the remaining reusable-frame refresh loop itself: it still
visits every draw even when all material/object owners are unchanged.

## 2026-07-31 Prepared reusable-frame refresh requests

- Reservation-manifest construction now freezes reusable refresh work into
  immutable requests containing the exact planner key, source range, draw slot,
  and mesh/compute payload. The reuse consumer no longer walks the original
  operation array or reconstructs draw-slot and planner identities.
- Desktop arrays remain in retained recorder-thread scratch. OpenXR publishes a
  separate generation-checked array per eye; a worker read lease prevents the
  producer from replacing the array during concurrent primary reuse.
- Both retained stores clear retired reference-bearing entries when counts
  shrink, preventing bounded capacity reuse from extending mesh/program
  lifetimes accidentally.
- The focused binding-schema, descriptor-generation, frequency-publication,
  prepared-refresh, and OpenXR lease selection passes 11/11. The two
  prepared-refresh lifecycle tests also pass independently. Magick.NET advisory
  warnings remain unrelated.

This was the phase-boundary prerequisite for owner-filtered publication.

## 2026-07-31 Frequency-owner reusable refresh

- The producer now derives one retained work request for each distinct
  program/block-frequency/owner/content-generation tuple. The consumer
  refreshes those frame, view, pass, material, object, instance, or callback
  owners and the previously discovered conservative fallback indices rather
  than walking every prepared draw.
- Each primary or dynamic-UI batch retains an exact stable signature and its
  fallback indices. Signature disagreement performs one full refresh and
  replaces the retained state; an exact match enters the owner-only path.
- Mutable legacy renderer, material, scoped, and shadow callback writes now
  carry capture provenance. Their name topology participates in the stable
  batch signature, while their current values are published by frequency-owner
  work. Typed writes remove the mutable marker. Tests verify that unrelated
  values do not alter the selected callback-value signature and selected value
  changes do.
- The last static-scene blocker was the zero-filled
  `__FallbackDescriptorBuffer`. Owner eligibility now permits only that known
  unresolved-descriptor fallback engine UBO because its full-publication bytes
  are always zero and its exact descriptor generation is separately signed.
  Other unclassified engine UBOs still force the conservative draw path.
- Six consecutive StandardValidation samples from the isolated
  `cmd-record-arch-opt` session each reported clean primary reuse, zero command
  recording, zero primary prepared-frame-data draw visits, one dynamic-UI visit,
  zero descriptor records validated or written, zero owner lookup/generation/
  frame-source misses, and zero Vulkan validation errors. No owner-only blocker
  or command-recording failure was present in
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-31_03-26-59_pid292400/log_vulkan.log`.
- The final capture was visually inspected. The expected physics-test geometry
  and lighting differentiation are present without corruption:
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/mcp-captures/frequency-owner-final/Screenshot_20260731_032755_290_983d835be2324defbe77a50c8ed9159a.png`.
- The focused owner-publication, mutable-callback provenance, retained-state,
  and OpenXR lease selection passes 4/4. The existing Magick.NET package
  advisories remain unrelated.

This closes the Phase 1.3 bounded stable-publication and zero-static-draw-visit
acceptance items. The dynamic-UI count is deliberately separate because the
canonical UI text changes independently of the static scene.

## 2026-08-01 Directional-light freeze and viewport flicker

The reported directional-light toggle freeze was a rejected-frame failure,
not simply shadow-rendering cost. The isolated `cmd-record-finish-baseline`
session recorded four image-state submission rejections: an imported texture
was referenced by graphics work while an exclusive transfer-queue ownership
release from family 5 to family 0 remained pending. It also recorded two
resource-lifetime rejections because retired
`VkImageBackedTexture.ImportedUploadView` generations remained referenced by
per-material `Material.DescriptorSet.Frame0/Frame2` descriptor sets. The
submission guard correctly withheld those unsafe frames, which presented as
scene and ImGui flicker and could look like a complete renderer freeze.
Directional-light shadow passes increased the affected descriptor/pass work
and made the fault substantially easier to trigger.

Imported images now use concurrent graphics/transfer queue-family sharing when
a dedicated transfer family is selected. Their transfer completion barrier and
the later graphics visibility barrier therefore use ignored queue-family
indices instead of an unmatched exclusive ownership release/acquire pair.
Descriptor-set publication now pins the exact referenced resource generations,
including backing images and buffers, until that descriptor set publishes a
replacement snapshot or is removed. Retirement readiness includes those pins,
and descriptor-set cleanup releases them.

Agent validation after both fixes used the isolated `cmd-record-finish-fix1`
Release session with the RenderDoc-friendly diagnostic preset. Three additional
directional-light off/on cycles completed while the scene command count changed
from 216 to 232 and back as expected. The final renderer sample reported zero
submission rejections, zero Vulkan validation messages, zero dropped frames,
and no pending retired resources. A ten-frame 5 fps capture completed without
drops; every frame had the same SHA-256 digest and no black pixels. The viewport
and internal target were both 1920x1080, and the inspected capture filled the
whole target rather than only its upper-left corner. The retained contact sheet
is
`Build/_AgentValidation/20260801-vulkan-command-recording-finish/mcp-captures/ViewportSequence_20260801_233025_667_b544cf23f942496da465cf627d06c06a/contact-sheet.png`.

The scene-target capture does not include the ImGui overlay itself, so the
absence of visible ImGui flicker is supported by its per-frame render counters
rather than a compositor-inclusive image. The user-reported top-left rendering
was not reproduced after the synchronization/lifetime fixes. This is an
agent-validated fix awaiting user confirmation in their normal editor session.

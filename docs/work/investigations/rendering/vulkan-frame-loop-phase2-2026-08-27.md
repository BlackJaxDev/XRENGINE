# Vulkan Frame Loop Phase 2 Investigation (2026-08-27)

## Objective

Determine whether Phase 0/1 required production finalization, then implement
the Phase 2 tracked-submission and granular-invalidation substrate without
conflicting with the concurrent runtime modularization work.

## Phase 0/1 disposition

The implementation from commit `354b7d4d4` is sufficient for Phase 2 to begin.
The remaining unchecked Phase 0/1 rows are empirical promotion gates (hardware,
OpenXR, blocked-preparation, overflow, matched A/B, and frozen RenderDoc
evidence), not missing production-code prerequisites. They remain open rather
than being converted into inferred success.

## Phase 2 implementation

- Added allocation-free p50/p95/p99 histograms for every tracked-submission
  gateway stage, sealed hit/fallback/seal/parity counters, reason-coded fallback
  and seal-decline telemetry, and exact/broad resident invalidation counters.
- Added presealed submission contracts with immutable resource, descriptor,
  ordered image entry/exit, queue-family, and recording-generation manifests.
  Multi-command submissions validate through a fixed-capacity ordered exit-state
  overlay. Queue-ownership transfers stay on the full validator until their
  semaphore/completion contract can be represented without recomputation.
- Descriptor payload closure is expanded before a contract is published. Any
  later dependency refresh invalidates the old contract before mutating the
  authoritative pin vector. Secondary command buffers cannot be rerecorded
  while retained by a recorded primary.
- Kept only native `vkQueueSubmit` work inside the queue-operation lease;
  validation, pinning, publication, diagnostics, and cleanup remain outside it.
- Added canonical mutation domains for content, resource binding, layout
  topology, and recording topology. Material binding-version changes now emit a
  resource-binding mutation instead of a content-only mutation.
- Added resident draw dependency manifests and intrusive reverse heads for the
  canonical owners currently published by the Phase 2/early Phase 3 substrate.
  Packed material-layout rows are indexed when present but remain optional until
  the Phase 3 material publisher owns them. Missing or inconsistent manifests
  use a counted, reason-recorded broad correctness fallback.

## Phase 2 to Phase 3 native-resource bridge

- Native resource lifetimes now publish a compact
  `VulkanResourceSlotHandle(Slot, Generation)`. Sealed command artifacts retain
  immutable ordered slot vectors and resolve them directly through the flat slot
  directory, including ABA rejection, instead of re-walking native-handle maps
  on a stable hit.
- A successful pin acquisition returns a `VulkanSubmissionPinReceipt` containing
  the exact lifetime records that were incremented. Publication and failure
  cleanup therefore release the same generations even if a native handle is
  detached and later reused.
- Descriptor snapshots publish an immutable native-resource slot closure. Its
  `ResourceClosureGeneration` is distinct from `ImagePayloadGeneration`, and
  closure invalidation/reset is serialized with tracked-submission admission so
  no stable contract can observe a partially refreshed descriptor publication.
- Desktop and ImGui swapchains retain exact detached WSI image lifetime slots in
  their retired generation. Native-handle identity can be removed immediately,
  while recorded-command, descriptor, and submission pins keep the detached
  generation alive until exact completion authorities release it.
- Lifetime diagnostics include detached generations, so an old WSI slot cannot
  disappear from leak/pin accounting merely because its native handle was
  detached during recreation.

## Issues found and corrected during live iteration

1. The first fast-path implementation only handled one command buffer, while
   normal submissions commonly contain a batch. A batch-capable implementation
   was added, then review identified that validating each command against the
   same global image ledger was unsafe. Contracts now carry exit states and
   validation applies them in command order.
2. Record-boundary sealing initially captured only previously touched resources.
   Descriptor payload references could therefore be omitted from pins on a first
   fast hit. Sealing now expands the same descriptor closure as the full gateway.
3. The seal counter counted attempts rather than successfully published
   contracts. Successful seals and reason-coded declines are now distinct.
4. Requiring an unpublished Phase 3 material-layout sidecar prevented resident
   manifests from being created. The dependency is now exact when published and
   optional before that publication exists.
5. Material resource-binding changes were published through the default content
   domain. The publisher now tracks the prior binding resource version and emits
   `ResourceBinding` deltas.
6. The first slot bridge recycled old swapchain-image slots while their exact
   generations were still pinned. Swapchain retirement now owns detached image
   lifetime records and drains them only after the relevant completion
   authorities advance.
7. Recorded-command and descriptor cleanup initially released resources by
   native-handle key. Those paths now resolve either the current generation or an
   exact detached generation before decrementing its pin.
8. WSI backing images discovered through image views were initially registered
   as ordinary engine-owned images. Image-view tracking now preserves the
   output authority's external ownership metadata.
9. ImGui platform-window teardown queued command-pool retirement but destroyed
   its recorded view dependencies immediately. The tracked pool is reset first,
   then external image lifetimes are released before the native swapchain is
   destroyed.
10. Lifetime snapshots initially enumerated only native-handle keyed records.
    They now include detached slot generations as first-class live resources.

## Live evidence

The first isolated session, `20260827-113411-vulkan-phase2-frame-loop`, captured
three camera positions under
`Build/_AgentValidation/20260827-113311-vulkan-phase2-frame-loop/mcp-captures/`.
The latter two images show distinct views of the rendered scene, ruling out a
stale or uninitialized screenshot source. Its Vulkan logs contained no VUID,
validation error, submission rejection, or device loss.

The final isolated session used logical name
`vulkan-phase2-presealed-profiler`; its final process log is
`xrengine_2026-08-27_12-39-23_pid24608`. It stopped through the named session
manager with zero VUIDs, validation errors, tracked-submission rejections,
device loss, or desktop-frame failures. Representative full-gateway p95 stage
buckets were: image validation 0.0256 ms, queue ownership 0.0004 ms, lifetime
pins 0.0256 ms, state serialization 0.0002 ms, native queue admission 0.0032 ms,
native submit 0.1024 ms, lifetime publication 0.0128 ms, image publication
0.0128 ms, diagnostics 0.0002 ms, and cleanup 0.0064 ms.

The final bridge session used logical name `vulkan-slot-bridge`. An intermediate
process ran beyond 5,000 frames while four real HWND resize cycles advanced the
desktop output generation from 1 to 5. Every sampled recreation completed with
the device operational, zero device loss, zero validation errors, zero parity
mismatches, and zero pending retired or swapchain generations.

The telemetry-inclusive process, PID 39592, ran 6,109 tracked submissions and
two additional output generations. Its last sample was `Completed` and
reported 81 sealed hits, 6,028 full-path fallbacks, zero parity mismatches, zero
pending retired resources, and zero validation errors. The original telemetry
classified 6,023 of those fallbacks as `ResourceVector`, but a follow-up causal
audit found that classification conflated two distinct cases. Sampled full
validation refreshed a matching descriptor closure and then unconditionally
discarded the reusable seal; every later unchanged submit consequently arrived
without a contract and was mislabeled as a resource-vector mismatch.

The follow-up session `vulkan-phase23-seal-preservation` temporarily withdrew
the seal while refreshing the exact descriptor dependency vector and restored
the same immutable contract only after its resource slots and descriptor
generations matched exactly. At tracked submission 1,099 the first 1-in-1,024
forced-full sample increased `ForcedFull` by one while `ResourceVector` stayed
at 114; stable hits continued to 984 with one parity sample and zero mismatch.
An extended run crossed the second forced-full boundary with 1,438 hits, two
forced-full samples, and zero parity mismatches. Later resource-vector growth
coincided with real output/import generation churn rather than either sample.

Telemetry now reports `MissingContract` separately from `ResourceVector`. A
second isolated session, `vulkan-phase23-gateway-total`, added an allocation-free
end-to-end gateway histogram. Its 5,808-submit live sample reported aggregate
gateway buckets of 0.2048 ms p50, 0.4096 ms p95, and 0.4096 ms p99, zero
resource-vector mismatches, zero validation errors, and zero pending retired
resources. The sample was dominated by freshly recorded command buffers (5,719
`MissingContract` fallbacks), so it is valid instrumentation evidence but not a
claim that the unchanged-sealed `<0.25 ms` promotion target has passed.

RenderDoc was verified with `rdc doctor`. A capture was not taken because the
live screenshots changed with the camera and both Vulkan validation and render
logs were conclusive; there was no unidentified failing pass or resource to
inspect.

## Automated validation

- `dotnet build XREngine.Runtime.Rendering.Vulkan/... --no-restore`: succeeded,
  zero warnings and zero errors.
- Added `VulkanFrameLoopPhase2Tests` covering mutation domains, record-boundary
  sealing and parity sampling, ordered image exit overlays, descriptor closure,
  queue-lock scope, timing percentiles, reverse-index integrity, and secondary
  rerecord admission. Three additional bridge contracts cover direct ABA-safe
  resource-slot resolution and exact pin receipts, independently generated
  descriptor closures with reset admission, and exact detached WSI generation
  release including ImGui cleanup and diagnostic visibility.
- The nine targeted tests pass repeatedly in an ignored isolated harness
  referencing the Vulkan runtime project: 9 passed, 0 failed.
- The canonical `XREngine.UnitTests` project also passes the filtered class:
  9 passed, 0 failed. Two clean unrelated test call sites were synchronized with
  their concurrently changed APIs first: the imported-texture test now supplies
  the upload ticket, and the pipeline-compilation test supplies the foreground
  policy and promotion output.

## Remaining promotion/dependency gates

- Raise the stable-hit rate by giving the remaining freshly recorded submission
  producers reusable artifact ownership; `MissingContract` is now measured
  independently from a genuine resource-vector mismatch.
- Complete the reverse graph for pipeline, descriptor-layout/table,
  render-pass/output, shader, shadow, and probe owners when their canonical
  publishers exist.
- Prove the hardware/OpenXR/mutation-locality/zero-broad-fallback matrices and
  the unchanged-submission p95 target. These are intentionally left unchecked in
  the master TODO.

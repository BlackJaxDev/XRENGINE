# Vulkan 1.4 Performance And Shader Modernization TODO

Last Updated: 2026-09-09

Owner: Rendering / Vulkan

Status: Phases A–C, D1–D3, E1, E3–E5 and F1 complete; E2 native replay demonstrated with resize freshness still open; F2 implemented pending live validation, F3 open; D4 research and phases G–J open

## Objective And Evidence

Establish Vulkan 1.4 as the explicit Vulkan baseline, complete the native
descriptor-heap contract, and reduce measured frame preparation, recording, and
GPU synchronization costs. Add Slang incrementally while retaining existing
GLSL authoring and OpenGL 4.6 support.

The original findings describe the source reviewed on 2026-09-06. Phase A/B
implementation, reproduced failures, corrections and live validation are recorded
in the [2026-09-08 investigation](../../investigations/rendering/vulkan14-phases-ab-2026-09-08.md).
No runtime speedup is claimed. The historical "current state" descriptions below
explain the original gaps; use the checklist and investigation for current status.

Phases A/B were completed on 2026-09-09. The indirect device loss was caused by
common push-data writes overwriting heap descriptor indices. Ordinary heap
programs now reserve the existing 128-byte constant prefix, and indirect draws
publish constants before descriptors. Final build 52 passed without warnings or
errors. Desktop heap GPU rendering passed with multiple meshes, material edits,
movement and camera changes; compute, UI, resize and emulated sequential XR were
also exercised. Monado smoke 53 passed true single-pass stereo with clean Vulkan
validation and teardown. OpenGL and descriptor-indexing controls remain supported.
No binding-path fallback or diagnostic draw suppression is part of the result.
Physical SteamVR limitations and previously observed visual/teardown limitations
are recorded separately in the investigation.

This is a self-contained implementation backlog. Required technical rules are
stated here, and links point only to repository code or related project notes.
Writing this plan does not implement the changes or authorize dependency updates.

## Non-Negotiable Requirements

- Target Vulkan 1.4 in API negotiation, device selection, feature enablement,
  compiler configuration, and diagnostics. Never silently retry Vulkan 1.3,
  select an older capability tier, or switch renderers to conceal a failure.
- Preserve dynamic rendering, synchronization2, timeline completion, resident
  commands, GPU-generated indirect work, and accelerated features. A change
  must not reduce capabilities merely to improve an isolated timing counter.
- Keep OpenGL 4.6 as an explicitly selected, supported renderer. Existing GLSL
  continues through its current OpenGL driver compile/link path.
- Do not mass-convert GLSL to Slang. Existing GLSL can compile to Vulkan SPIR-V
  through Shaderc; new Slang can use a separate frontend. Rebuilding shader
  artifacts after a target/cache change does not require rewriting their sources.
- Distinguish required Vulkan 1.4 functionality from additional extensions and
  remaining feature/property conditions. Descriptor sets/indexing are valid
  Vulkan 1.4 strategies. An explicitly requested heap or accelerated variant
  must report unsupported requirements or fail visibly, not silently substitute.
- Preserve frame-slot ownership, resource generations, current-frame provenance,
  descriptor-slot lifetime, and independent presentation-completion proof.
- Keep per-frame preparation, recording, and submission allocation-free unless
  a measured exception is documented. Do not exchange native API savings for
  unmeasured managed allocations or repeated validation work.

## Execution Order And Completion Rules

| Phase | Work | Prerequisites |
| --- | --- | --- |
| A | Establish and verify the Vulkan 1.4 contract | Start here |
| B | Correct native heap pipelines, roots, and state | A1; validate under A's target |
| C | Record reproducible correctness and performance baselines | A; B for heap measurements |
| D | Improve wait placement and dependency scopes | C |
| E | Improve command reuse and heap publication | C; B for heap changes |
| F | Execute useful work on multiple queues | C and a dependency map from D |
| G | Evaluate address-based shader parameters | C; B/E when using heaps/reuse |
| H | Add Slang without replacing existing GLSL | A; independent of native heap adoption |
| I | Evaluate optional layout/address capabilities | A and C; D for layout hazards |
| J | Close out validated changes and deferred experiments | Each selected phase's evidence |

Work through tasks in their listed order unless their inputs are already
available. Design and inventory can run independently; runtime performance
claims require a valid baseline and the relevant correctness tasks first.

Every task has an ID and an observable completion condition. Check it only when
that condition is met. For measurement-led decisions, record **retain**, **reject**,
or **defer**, with evidence and rationale. A rejected experiment does not mark
its unimplemented follow-up tasks complete.

Record results in a durable note under `docs/work/investigations/rendering/`.
Keep captures, logs, and scratch output under the task's
`Build/_AgentValidation/` run directory. Use named isolated editor sessions
and stop only sessions started for this work. Follow the repository policy:
validate an implementation through the live/runtime path before adding or
modifying tests, and obtain explicit user clearance before that test work.

## A. Establish The Vulkan 1.4 Contract

### Current state and required behavior

The reviewed instance negotiation starts at Vulkan 1.3 and applies Streamline
and OpenXR requirements, including the XR runtime's maximum supported version.
The default capability tier is `Vulkan13Production`. The task/mesh compiler
explicitly targets Vulkan 1.3 / SPIR-V 1.6; ordinary stages leave the Shaderc
target implicit. These are migration gaps, not the intended final baseline.

Vulkan API version, SPIR-V module version, and enabled shader capabilities are
separate contracts. Vulkan 1.4 supports SPIR-V 1.0 through 1.6. An older compatible
module is not inherently an API downgrade. Use an explicit Vulkan 1.4 compiler
environment, prefer SPIR-V 1.6 for the modern profile, and preserve every
shader's required capabilities. Never lower existing mesh/task requirements to
make another compilation route work. Keep compatible cached artifacts only
when their target, ABI, and capability identities still match.

Promoted core functionality must not be rejected solely because an extension
name is absent. Query and enable the required feature bits and properties.
Avoid chaining both promoted aggregate feature structures and duplicate
extension feature structures. For dynamic rendering local read, Vulkan 1.4's
baseline support does not guarantee depth/stencil or multisampled attachment
reads; inspect the corresponding properties. Host image copy is also not
universally guaranteed just by the API label: a graphics implementation can
satisfy the relevant requirement through an additional transfer queue.

| Mechanism | Selection rule |
| --- | --- |
| Vulkan 1.4 core | Validate loader/instance, physical device, required features, and runtime integration constraints |
| `VK_EXT_descriptor_heap` | Query the extension, its dependencies including shader untyped-pointer support, feature bits, properties, native functions, and usable heap storage |
| `VK_KHR_device_address_commands` | Independently query address-based command support; it is not required merely to use shader buffer device addresses |
| `VK_KHR_unified_image_layouts` | Independently query and enable before relying on its image-layout performance guarantees |
| Mesh shaders and shader objects | Independently query their extension/features; neither follows automatically from selecting Vulkan 1.4 or Slang |
| Descriptor indexing / conventional bindings | Valid Vulkan 1.4 choices; retain for existing shaders and explicitly selected variants |

Sources:
[VulkanDeviceContext.Instance.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.Instance.cs),
`ResolveRequestedApiVersion`;
[VulkanFeatureProfile.cs](../../../../XREngine.Runtime.Rendering/Rendering/Vulkan/VulkanFeatureProfile.cs),
`RequestedCapabilityTier` / `RequestedDescriptorBackend`;
[VulkanShaderCompiler.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs),
`ConfigureTargetEnvironment`; and
[VulkanShaderCrossCompiler.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/VulkanShaderCrossCompiler.cs).

- [x] **A1 — Record the current capability and compile-path inventory.** Enumerate
  desktop/XR negotiation, device tiers, normal/generated/native shaders,
  cross-compilation, prewarm, and background compilation. Record actual defaults
  and active gates. **Done when:** the investigation note maps every route to its
  API target, SPIR-V target, required features, source symbol, and migration gap.
- [x] **A2 — Require Vulkan 1.4 consistently.** Update version negotiation and
  capability policy without silently clamping to an older XR/runtime maximum.
  Keep the separately selected OpenGL renderer available. **Done when:** supported
  configurations report Vulkan 1.4 and incompatible loader/device/XR constraints
  produce clear diagnostics without an older API or renderer substitution.
- [x] **A3 — Align core and extension enablement.** Audit maintenance5,
  maintenance6, dynamic rendering local read, and other used promoted features;
  verify remaining properties and additional extension requirements.
  **Done when:** capability reports distinguish supported, enabled, requested,
  and executable behavior, and feature-chain validation reports no duplicates
  or unsupported enables.
- [x] **A4 — Make every Vulkan shader target explicit.** Apply the Vulkan 1.4
  environment and recorded SPIR-V/capability profile to all A1 compiler routes.
  Include target/compiler/ABI identities in cache invalidation and validate
  generated modules with `spirv-val --target-env vulkan1.4`.
  **Done when:** each route produces attributable, validated artifacts; target
  changes cannot reuse incompatible cache entries; unsupported tooling fails visibly.
- [x] **A5 — Validate the baseline without reducing features.** Exercise desktop,
  relevant XR modes, and existing GLSL on Vulkan and OpenGL. Compare requested
  versus negotiated APIs, device features, binding paths, and compiler targets.
  **Done when:** the note records equivalent supported output and a before/after
  capability matrix with no unexplained feature loss.

## B. Correct The Native Descriptor-Heap Contract

### Current state and required behavior

The engine implements `VK_EXT_descriptor_heap` as an opt-in backend; descriptor
indexing is the default. The heap bridge maps reflected `(set, binding)`
declarations through `HeapWithPushIndex` and writes indices into a program
payload. Existing GLSL can use these mappings without adopting native heap
syntax. This extension is distinct from `VK_EXT_descriptor_buffer`; their
descriptor formats and APIs must not be treated as interchangeable.

The inspected graphics and compute creation paths retain `_pipelineLayout`
when adding `VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT`. Heap pipelines
require `layout = VK_NULL_HANDLE`
(`VUID-VkGraphicsPipelineCreateInfo-flags-11311` and its compute counterpart).
Conventional shader resource declarations still require complete mappings.
Internal engine identities must remain usable even when the native layout is null.

Prepared mesh draws also push conventional constants before heap push data.
`vkCmdPushDataEXT` invalidates conventional push-constant/push-descriptor state,
and those commands invalidate heap push-data state in turn. Earlier constants
cannot be assumed to survive. Whether this changes rendered output depends on
which values the shader reads; that impact remains unverified.

The implementation must obey the following root and heap rules:

- Use one defined root layout for constants, addresses, and descriptor indices.
  Heap push data is shader-visible through the SPIR-V `PushConstant` storage
  class. For the engine ABI, use four-byte-aligned offsets and sizes and keep
  every written range within the queried `maxPushDataSize`. Do not substitute
  `maxPushConstantsSize` for that limit.
- The push call copies CPU bytes into recorded commands. That temporary CPU
  value need only survive the call; referenced GPU allocations and descriptor
  slots must remain valid through execution. Changing recorded root bytes
  requires recording new commands or a separately designed GPU-generated path.
- Samplers and resources occupy separate heaps. Query their descriptor sizes,
  heap alignments, and implementation-reserved ranges. Preserve reserved storage
  for all command buffers using it; do not overwrite it as application payload.
- Heap binds invalidate conventional descriptor-set/buffer/offset state, and
  conventional descriptor state invalidates heap bindings. Track these mode
  changes and secondary-command inheritance/restoration explicitly.
- Native descriptor bytes are constructed by host descriptor-write APIs.
  GPU copying of descriptor bytes does not imply that shaders can construct
  arbitrary native descriptors. Retire or version slots before overwriting
  descriptors referenced by in-flight or reusable commands.
- Buffer device addresses carry no automatic descriptor bounds protection.
  The engine owns alignment, valid ranges, lifetime, and synchronization.

Sources:
[VulkanDeviceContext.LogicalDeviceBootstrap.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs),
`ResolveDescriptorBackendAfterDeviceCreate`;
[VulkanDescriptorLifetimeAuthority.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs),
`CreateDescriptorHeapProgramLayout`, `CreateHeapMapping`, `TryWriteDescriptorHeapBinding`;
[VkRenderProgram.GraphicsPipelines.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.GraphicsPipelines.cs);
[VkRenderProgram.Compute.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Compute.cs);
[VkMeshRenderer.Drawing.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs).

- [x] **B1 — Fix heap pipeline creation and identities.** Set the required null
  native layout, preserve complete resource mappings, and audit synchronous,
  background, native, and applicable shader-object paths.
  **Done when:** all selected heap variants create successfully with validation
  enabled, and cache/program identities support the null-layout contract.
- [x] **B2 — Define and use one heap root layout.** Reserve non-overlapping offsets
  for shader constants and heap references. Audit mesh, compute, native passes,
  and ImGui for mixed push APIs and binding modes.
  **Done when:** reflected shader offsets match the published payload, limits are
  checked, and every consumed value comes from valid state at draw/dispatch time.
- [x] **B3 — Verify the heap storage and mapping lifecycle.** Check native interop,
  flags, signatures, dependencies, descriptor sizes, reserved ranges, host writes,
  mode invalidation, and slot retirement against the selected SDK declarations.
  Preserve conventional GLSL mappings. **Done when:** supported bindings have
  valid mappings, referenced storage stays live, and unsupported cases fail visibly.
- [x] **B4 — Validate heap rendering before benchmarking.** Cover material edits,
  compute, indirect draws, UI, resize, and applicable XR output with validation
  logs and viewed captures. **Done when:** those cases preserve expected output
  and have no unresolved steady-state heap/push/pipeline validation errors;
  shutdown-only diagnostics are recorded separately.

## C. Establish Reproducible Measurements

Completed collection, fixture corrections, capture evidence and predeclared
comparison criteria are recorded in the
[2026-09-09 baseline note](../../investigations/rendering/vulkan14-phase-c-baselines-2026-09-09.md).
Sustained heap streaming currently fails on descriptor capacity and is excluded
from performance comparisons; its rejected-frame loop is not a speedup.
The note retains 34 accepted captures and five excluded attempts, with full
timing distributions, allocation/cache/wait observations and run-to-run variation.
Indexing material-edit, streaming and cold-start comparisons remain deferred
by their recorded failures; noisy GPU and heap moving-camera comparisons are
also deferred. Completing C records these limits rather than declaring them fixed.

A high reuse ratio, fewer API calls, or a newer feature does not establish a
speedup. Measure full CPU and GPU costs for equivalent visible work. Run
correctness checks with `XRE_VULKAN_VALIDATION=1` and
`XRE_VULKAN_SYNC_VALIDATION=1`; measure production performance
separately with diagnostics overhead removed.

Use static scenes, moving cameras, material edits, streaming, and volatile UI
as distinct workloads. Record cold startup/compilation and resize separately
from warm steady state. Keep resolution, render scale, settings, camera path,
presentation mode, and frame policy fixed between variants.

Sources:
[VulkanDiagnosticOptions.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanDiagnosticOptions.cs);
[Vulkan primary command-buffer reuse contract](../../../architecture/rendering/vulkan-primary-command-buffer-reuse.md).

- [x] **C1 — Capture the baseline and reproduction procedure.** Record hardware,
  driver/tool versions, scene, settings, launch configuration, warmup, sample
  duration, and exact selected API/features. Collect frame/CPU/GPU timing,
  allocation bytes, compilation/cache behavior, and relevant wait counters.
  **Done when:** another contributor can reproduce the run from the note and
  find its retained logs/captures without any external reference.
- [x] **C2 — Define comparison criteria before each change.** Select the workload,
  metric, acceptable output/latency behavior, and repeatable sampling procedure.
  Report timing distributions and run-to-run variation, not one FPS reading.
  **Done when:** each experiment has a comparable baseline and explicit
  retain/reject/defer criteria covering total cost and correctness.

## D. Improve Wait Placement And Dependency Scopes

### Current state and technical guidance

The ordinary desktop submission path calls
`WaitForNextDesktopFrameSlotBeforeCollect` before
`ReleaseCollectForDesktopFrame`. The D1 ownership review established that CPU
collection already precedes the timer's render-done wait; this release gates
swap jobs and render-side snapshot publication. The measured pre-publication
wait is 0.013–0.014 ms median, below 0.1% of frame cost. D2 therefore retains
the existing wait and ownership boundary.

The [phase D investigation](../../investigations/rendering/vulkan14-phase-d-waits-and-barriers-2026-09-09.md)
records the ownership map, direct wait telemetry, six Advanced barrier sites,
viewed synchronization validation and three matched control/candidate pairs.
The early-visibility specialization missed the 5% improvement threshold and
was removed. D4 remains deferred: production GPU timings were noisy, frame p99
did not satisfy the tail criterion, and actual GPU-gap evidence was unavailable.
No performance optimization is promoted by this experiment.

The [barrier research](../../investigations/rendering/vulkan14-barrier-performance-research-2026-09-09.md)
now explains the missed threshold: the tested boundary contains dependent
compute work with no proven local graphics overlap. D4 has no calendar wait;
its next experiment requires a measured overlap opportunity, complete consumer
coverage, matched barrier encoding, and stable paired controls. Nsight reaches
the renderer but currently requires NVIDIA performance-counter permission to
capture the missing scheduling evidence. Phase E proceeds independently.

Generic `ShaderStorage` and `ShaderImageAccess` barriers expand to
`AllGraphicsBit | ComputeShaderBit` in both directions with shader read/write
access. Known callers include early visibility to indirect generation and
native shading boundaries. A global `VkMemoryBarrier2` can be appropriate
without a resource list, but its stage/access masks still need to express the
actual hazard. Narrowing unrelated stages can expose GPU overlap.

For example, compute-written data subsequently read by another compute dispatch
needs the applicable compute shader write-to-read/write dependency. Indirect
command consumption instead needs the draw-indirect stage and indirect-command
read access; later shader consumers need their own applicable visibility.
Keep image layout transitions, queue ownership, and resource-specific hazards
in the graph. Do not narrow generic/query barriers merely because they use
`AllCommandsBit`; some broad scopes are intentional.

Sources:
[VulkanRenderer.FrameLoop.Submission.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Submission.cs),
`SubmitDesktopFrameCore`;
[VulkanRenderer.FrameLoop.FrameSlots.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.FrameSlots.cs),
`WaitForNextDesktopFrameSlotBeforeCollect`;
[VulkanRenderer.BarrierEmission.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Synchronization/VulkanRenderer.BarrierEmission.cs),
`ResolveBarrierScopes`;
[VulkanRenderer.CommandBufferRecording.Primary.Operations.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs),
`Advanced.Visibility.EarlyToIndirect`;
[VulkanRenderer.CommandBufferRecording.Primary.NativeShading.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.NativeShading.cs),
`RecordAdvancedNativeComputePayload`.

- [x] **D1 — Map collection ownership and wait cost.** Measure the
  `WaitNextFrameSlotBeforeCollect` timing counter; identify the first collection
  operation that touches slot-owned GPU-visible state. Separate CPU-only preparation from
  publication. **Done when:** the note contains an ownership boundary and a
  measured decision on whether deferring/splitting the wait is worthwhile.
- [x] **D2 — Move the wait only where D1 proves safe and useful.** Keep completion
  immediately before the first operation requiring safe slot reuse.
  **Done when:** camera motion, mutation, resize, and desktop/XR coexistence
  preserve slot ownership, with no increased latency or workload deferrals and
  a measured improvement in total preparation/frame timing. **Disposition:** D1
  found no useful relocation; no scheduling change was applied, so the
  conditional move and its changed-scheduling validation are not applicable.
- [x] **D3 — Build a producer/consumer dependency map.** Inventory reached generic
  barriers, including early visibility and native image shading; list all writes,
  reads, later consumers, layout changes, and graph-emitted dependencies.
  **Done when:** each proposed narrower barrier has a complete hazard explanation.
- [ ] **D4 — Specialize proven boundaries and measure overlap.** Change only the
  specific callers justified by D3, preserving generic API semantics.
  **Done when:** synchronization validation and viewed output pass, GPU timing/gap
  comparisons are recorded, and each change has a retain/reject/defer decision.
  **Disposition:** the first candidate passed viewed synchronization validation
  but failed promotion criteria and was restored to the generic barrier. GPU
  gap capture requires driver counter access. Research is reopened with explicit
  evidence gates and ranked experiments in the linked barrier report; no later
  phase or arbitrary date is a prerequisite.

## E. Improve Command Reuse And Heap Publication

Implementation and measurements are tracked in the
[phase E investigation](../../investigations/rendering/vulkan14-phase-e-reuse-and-descriptors-2026-09-09.md).

### Current state and technical guidance

Cached primary admission performs current-frame-data refresh and signature work.
Avoided recording must exceed that total cost. Recording every frame may win for
some workloads; a high reuse ratio may still lose if validation is expensive.
Respect output policies that intentionally require fresh recording.

The E implementation replaces heap-only incomplete indirect keys with exact
prepared native identities and prevents key/encoder generation mismatches.
Its live fixture verifies complete matching keys and material/root invalidation.
The new presentationless background exact-output contract permits the dedicated
indirect artifact to replay: 69 native reuses in 225 completed frames, versus zero
in the matching foreground control, with no Vulkan validation errors. Image
inspection found old pixels outside a shrunken viewport in both paths, so E2
remains open for full-output resize freshness. See the
[background replay and queue investigation](../../investigations/rendering/vulkan14-background-replay-and-queue-overlap-2026-09-09.md).

Measured payload churn came from changing mesh allocation lookup keys, not an
absence of compute scratch reuse. Heap allocation ownership is now stable per
renderer, with exact frame-slot resource proof. Global material textures use a
sparse leased arena, and exact per-command-buffer heap state suppresses redundant
native binds while retaining native-resource tracking. See the investigation
for accepted runtime evidence and the matched timing disposition.

Changing heaps or binding modes can be expensive. Rebinding the same heap is not
proof of a GPU stall, but redundant native calls are still a measurable cost.
Suppress binds only when command-buffer state proves the existing bindings valid;
resource tracking must continue even when an API call is skipped.

Sources:
[VulkanCommandRuntime.PrimaryRecording.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs);
[VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs);
[VulkanDescriptorManager.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorManager.cs),
`CreateHeapPushDataPayload`;
[VulkanTrackedCommandEncoder.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanTrackedCommandEncoder.cs).
Compute publication is in `VkRenderProgram.Compute.cs`, linked in phase B.

- [x] **E1 — Measure total reuse cost and rejection causes.** Compare allowed
  reuse with forced recording across C's workloads, including preparation,
  signatures, refresh, recording, submission, and allocations.
  **Done when:** results identify which workloads benefit and which validation
  or invalidation costs should change, without weakening output policy.
- [ ] **E2 — Make indirect secondary reuse understand heaps.** Define identities
  for root ABI/bytes, program generation, heap addresses/ranges/generations,
  descriptor content, and referenced-resource ownership.
  **Done when:** unchanged valid packets reuse safely and changes invalidate;
  the incomplete-key guard is replaced by proof, not simply removed.
  **Status:** the output contract, native replay and material/root/buffer
  invalidation are implemented and exercised. Full-output clearing after viewport
  shrink remains open; the foreground control exhibits the same retained pixels.
  No dependency on D4 counter access.
- [x] **E3 — Remove repeated heap payload allocation and publication.** Use
  recording-context/frame-owned reusable storage and existing generation tracking
  to avoid unnecessary descriptor writes. Keep worker scratch and retained
  prepared payloads isolated. **Done when:** representative steady-state heap
  dispatches add no payload object/array allocations and unchanged resources
  avoid redundant publication without stale bindings.
- [x] **E4 — Suppress redundant heap binds safely.** Track new recording, heap
  replacement, non-heap state, and secondary execution/inheritance boundaries.
  **Done when:** bind counters decrease for unchanged heaps, all invalidation
  cases restore correct state, and resource lifetime tracking remains complete.
- [x] **E5 — Compare completed heap and indexing paths under Vulkan 1.4.** Include
  descriptor writes, bind/push counts, reuse, allocation bytes, CPU time, and GPU
  pass/frame time. **Done when:** the chosen policy is supported by equivalent
  output and end-to-end measurements, with explicit requested-mode behavior.
  **Disposition:** retain the current default and explicit heap selection.
  Allocation and binding reductions are validated; mixed/noisy timings and
  intermittent indexing mutation rejections do not justify default promotion
  or a general FPS claim. The comparison is complete; those stability findings
  and the remaining E2 resize-freshness issue remain explicit follow-up work.

## F. Execute Useful Frame-Graph Queue Overlap

`SupportsFrameGraphMultiQueueSubmission` remains false for the generic graph
executor. A selected presentationless mono Advanced `GraphicsCompute` executor
has now been implemented with four native primaries, two queues in the graphics
family, binary dependencies and per-slot completion tracking. It has not yet
completed live execution: Advanced RenderBench startup currently publishes
canonical scene generation zero before its first render frame begins. The
default remains graphics-only and the candidate is explicitly disabled pending
validation. G2 still needs its own image-access journal publication after the
split. No overlap or performance gain is claimed.

Independent work is required. Visibility, depth-pyramid generation, and indirect
generation often have real same-frame dependencies; simply assigning them to
different queues can add overhead. A single queue can already overlap some
operations when dependencies permit.

Source:
[VulkanRenderer.QueueOverlap.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.QueueOverlap.cs),
`SupportsFrameGraphMultiQueueSubmission`.

- [x] **F1 — Select one worthwhile independent workload.** Use D3's dependencies
  and C's timings to estimate available overlap, submission cost, and ownership
  transfer cost on target hardware. **Done when:** the note names one candidate
  with evidence for an experiment, or records why this work is deferred.
  **Disposition:** WorkClassification can overlap GTAO, froxel construction and
  background shading, with a prior measured ideal overlap ceiling of 0.427 ms.
  Same-family queues avoid ownership-transfer cost; submission and contention
  costs remain to be measured.
- [ ] **F2 — Implement the candidate's actual native submissions.** Add semaphore
  dependencies, paired queue-family release/acquire operations where required,
  completion tracking, and failed/rejected submission handling.
  **Done when:** the executor owns the complete cross-queue lifecycle; changing
  the support gate is backed by working submissions rather than planner metadata.
  **Status:** executor implemented and reviewed, including exact resource pins,
  joined completion markers, timestamp-pool ownership and partial-submit draining.
  Release build passes; native execution and failure-path validation remain open.
- [ ] **F3 — Validate queue selection and net benefit.** Exercise supported queue
  configurations and compare total CPU/GPU timing against graphics-only execution.
  **Done when:** correctness passes, requested and executable modes are reported
  accurately, and the default choice follows measured benefit. An explicit
  unsupported multi-queue request must not silently run another mode.
  **Status:** requested/executed modes are in submission receipts and unsupported
  candidates reject explicitly. Live queue validation and paired CPU/GPU timings
  remain outstanding. Work stopped at the user's wrap-up request.

## G. Evaluate Address-Based Shader Parameters

The normal auto-uniform path already uses shared mapped storage, dynamic uniform
infrastructure, and span-based CPU writes. Preserve these benefits. Start with
one data structure whose remaining descriptor/publication overhead is measured;
this is not a requirement to convert all geometry to vertex pulling.

A useful root can contain a few immediate constants, stable frame/scene GPU
addresses, and material/draw indices. Large data can remain in persistent arenas.
Pushing a small immediate root avoids a dependent load of that root; pushing a
stable address can reduce changing command bytes and improve reuse. Evaluate
both costs for the actual workload. GPU-selected roots require an explicit
GPU-driven design; ordinary CPU `vkCmdPushDataEXT` does not fetch root bytes
from GPU memory.

Use 64-bit GPU-address fields with explicit layout and alignment, never host
pointers. Typed shader pointers avoid unnecessary integer-address arithmetic
requirements. Buffer addresses do not represent texture objects: texture and
sampler resources use appropriately typed descriptors and separate index spaces.
Keep allocations and slots valid until their final use completes.

Source:
[VkMeshRenderer.Uniforms.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs),
`EnsureEngineUniformBuffer` / `UploadUniform<T>`.

- [ ] **G1 — Select the data and define its root ABI.** Measure descriptor/uniform
  costs, then specify one versioned structure with fields, offsets, strides,
  GPU-address/descriptor types, valid ranges, alignment, and ownership.
  **Done when:** CPU/shader layout and the expected cost reduction are explicit.
- [ ] **G2 — Add a separately selected address-based variant.** Use existing
  arenas, preserve GPU-generated indirect arguments/counts, and integrate B/E
  lifetime and reuse rules. Keep the existing Vulkan 1.4 descriptor-backed
  GLSL variant. **Done when:** both variants render equivalent supported output
  and unsupported address/heap requirements produce clear diagnostics.
- [ ] **G3 — Decide from full CPU/GPU costs.** Compare root loads, shader time,
  descriptor work, preparation, and rerecording across representative scenes.
  **Done when:** the note records the chosen ABI/variant or rejects/defers it;
  geometry fetch strategy changes are evaluated separately on target GPUs.

## H. Add Slang While Preserving GLSL And OpenGL

### Compiler routes and required knowledge

| Source / variant | Vulkan 1.4 | OpenGL 4.6 | Authoring requirement |
| --- | --- | --- | --- |
| Existing GLSL | Existing preprocessing and Shaderc frontend, explicit Vulkan target, SPIR-V plus binding metadata | Existing GLSL preprocessing and driver compile/link | No source conversion; rebuild incompatible artifacts when needed |
| New portable Slang | Direct SPIR-V plus reflection and explicit capabilities | Generated GLSL only after validation, or an authored GLSL counterpart | Incremental adoption per shader/pass |
| Native heap/BDA variant | Explicit additional capabilities and a validated root ABI | Separate supported bindings or an existing GLSL counterpart for shared functionality | No changes required to unrelated GLSL |

Slang is an additional frontend, not a required intermediary for GLSL. Do not
assume arbitrary GLSL can be fed directly into Slang, and do not pass Slang through
the GLSL regex rewriting pipeline. Slang's OpenGL support is limited; emitted
GLSL must be validated for the engine's feature set. A shared pass cannot replace
its existing implementation until its OpenGL route is proven. Vulkan-only
features can remain explicit variants without lowering Vulkan's target.

Modules, interfaces, generics, and specialization can improve shader organization.
Reflection supplies physical type sizes, offsets, strides, and resource bindings;
it does not infer engine frame/view/material/runtime semantics. Preserve those
owners and update frequencies as explicit metadata.

C# and shader layout agreement must be generated or verified. Specify packing,
padding, array/matrix stride and order, fixed scalar widths, and GPU-address
representation. Use explicit-width integer fields for shared flags rather than
assuming language boolean layouts agree. A shared shader declaration does not
automatically establish a matching blittable C# structure.

Compiler cache identity must include source language, compiler version, backend,
API/SPIR-V target, required capabilities, stage/entry point, defines,
module/include/link dependencies, layout options, and physical/semantic schema
versions. A successful compile using one frontend is not a valid cache hit for
another merely because the source filename matches.

Shared Slang frontend session state is not reentrant. Serialize its use or use
independent sessions for workers. Do not rely on experimental concurrent backend
emission as the default threading contract. Pin the selected toolchain and verify
its required capabilities; a compiler/language change does not guarantee faster
compilation or faster GPU code.

Sources:
[GLProgramCompileLinkQueue.cs](../../../../XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Pipelines/GLProgramCompileLinkQueue.cs);
[IRuntimeShaderCrossCompiler.cs](../../../../XREngine.Runtime.Rendering/Rendering/Shaders/IRuntimeShaderCrossCompiler.cs);
[VulkanProgramBindingSchema.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanProgramBindingSchema.cs);
[VulkanShaderArtifactRuntimeFingerprint.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderArtifactRuntimeFingerprint.cs).
The current GLSL preparation/compiler is linked in phase A.

The existing [Slang cross-compile plan](../../design/scripting/slang-shader-cross-compile-plan.md)
contains a mandatory GLSL-through-Slang stage and a later default switch. Neither
is a prerequisite here. This backlog requires continued independent GLSL and
Slang frontends, with no automatic retirement of GLSL/OpenGL support.

- [ ] **H1 — Define the frontend contract and reconcile the older plan.** Specify
  requests/results for language, target, stage/entry point, capabilities, emitted
  artifact, layouts, bindings, semantic metadata, and file/line diagnostics.
  **Done when:** the local design and supported backend matrix agree on
  coexistence and do not require translating or retiring existing GLSL.
- [ ] **H2 — Add opt-in direct Slang compilation.** Integrate the chosen compiler
  through H1 without the GLSL rewrite path. Keep Slang selection independent of
  heap selection and report compiler/capability failures visibly.
  **Done when:** one native Slang shader produces validated Vulkan 1.4 SPIR-V
  while existing GLSL compilers remain usable without Slang.
- [ ] **H3 — Adapt reflection and verify the C# ABI.** Join reflected physical
  layouts with explicit binding-owner/frequency metadata. Generate or validate
  blittable CPU structures and reject ambiguous mappings.
  **Done when:** offsets, strides, matrix order, resource bindings, and semantic
  providers agree for pilot uniforms, arrays, textures, and GPU-address fields.
- [ ] **H4 — Integrate caching, diagnostics, and background compilation.** Apply
  the full cache identity above, dependency tracking, session ownership, and
  original source diagnostics. **Done when:** target/module/frontend changes
  invalidate appropriately, concurrent jobs do not share unsafe mutable compiler
  state, and errors identify the original shader location.
- [ ] **H5 — Pilot one compute shader and one material pass.** Start with
  conventional Vulkan bindings. Compare ABI, output, hot reload, diagnostics,
  warm/cold compile cost, CPU preparation, and GPU time against equivalent GLSL.
  **Done when:** results establish whether to retain/expand Slang, and each shared
  pass has a validated OpenGL route before replacing its existing implementation.
- [ ] **H6 — Verify continued GLSL and OpenGL support.** Exercise the representative
  shader corpus on both renderers: includes/macros, material parameters, UBO/SSBO,
  textures/images/samplers, matrix and clip-depth conventions, instancing,
  supported stages, and relevant stereo/multiview paths. Evaluate generated
  OpenGL GLSL separately; evaluate native heap/BDA variants only after B/G.
  **Done when:** the supported source/backend matrix is backed by viewed output
  and no mandatory shader-source conversion or hidden renderer substitution.

## I. Evaluate Optional Layout, Address, And Memory Policies

With `VK_KHR_unified_image_layouts` enabled, ordinary image use in `GENERAL`
can avoid many layout changes. It does not remove memory hazards, initialization
from `UNDEFINED`, presentation/external transitions, or all exceptional uses.
Image-specific barriers can remain necessary or useful even for
`GENERAL` to `GENERAL`. Retain graph resource knowledge; a simpler layout
policy must not broaden all synchronization or assume unsupported devices have
the same performance.

`VK_KHR_device_address_commands` can let index, indirect, and copy operands use
GPU address ranges instead of buffer-handle lookup/binding. It does not provide
automatic allocation ownership, bounds validation, GPU-produced roots, or useful
queue overlap. Preserve GPU-generated counts and the existing lifetime contract.

Mapped, host-visible, coherent device-local memory can suit some uploads and
descriptor heaps, but that combination is hardware-dependent and not optimal
for every resource. Coherence does not permit overwriting data still used by
the GPU. Keep existing VMA placement, persistent arenas, staging/batched uploads,
and completion-based retirement. Distinguish CPU-write traffic, GPU bandwidth,
and readback behavior when choosing memory. Do not make a heap-specific memory
requirement the universal policy for geometry, textures, and readback.

- [ ] **I1 — Inventory and benchmark a unified-layout variant.** Record extension
  support and select one pass chain with measurable transition cost; preserve
  all required hazards and exceptional transitions.
  **Done when:** correctness and GPU timing are compared with the existing
  layout policy, and support/performance assumptions stay device-specific.
- [ ] **I2 — Evaluate address-based command operands where lookup costs matter.**
  Record support, select one index/copy/indirect workload, and retain explicit
  range, ownership, count, and failure handling.
  **Done when:** measurements support a retain/reject/defer decision without
  reducing the existing GPU-driven or resource-lifetime behavior.
- [ ] **I3 — Verify memory choices for retained heap/address variants.** Compare
  the relevant existing staging/mapped/device-local placements on target hardware.
  Record allocation properties, traffic, lifetime, and failure requirements.
  **Done when:** any changed placement has a measured workload justification;
  unrelated resources retain their appropriate allocator policy.

## Existing Behavior To Preserve

These source-review observations establish useful constraints, not blanket proof
that the subsystem is complete. Recheck them during A1 and the affected phase.

| Area | Existing behavior and implementation constraint |
| --- | --- |
| Runtime/tooling | C#/Silk.NET is not itself evidence of a render-loop disadvantage; do not replace dependencies without a measured need |
| Allocation and geometry | VMA, reusable staging, mapped arenas, and existing geometry infrastructure already exist; parameter work does not mandate vertex pulling |
| Texture uploads | Staging, batched uploads, and mip-level handling exist; container choice alone does not establish a compression or upload optimization |
| Pipeline compilation | Persistent caches, background compilation, and prewarm exist; measure actual misses and cold stalls before introducing another mechanism |
| Desktop depth | A shared device-local, optimally tiled depth image is used; the normal swapchain scope discards depth storage afterward. Preserve safe ordering and avoid unnecessary duplication |
| Swapchain policy | Target-specific surfaces, HDR/SDR negotiation, and presentation modes exist; desktop requests minimum image count plus one, capped by a nonzero surface maximum |
| Acquisition | Desktop acquisition can block for presentation back-pressure; XR/resize paths use nonblocking acquisition. An acquire wait is not automatically wasted work |
| Submission | The desktop acquire semaphore wait uses `ColorAttachmentOutputBit`; submission arrays use stack storage. Broaden the wait only when actual earlier consumers require it |
| Event handling | Window events have a separate pump path; do not introduce avoidable GPU-wait coupling |
| Resource retirement | Timeline completion protects submitted resource use; old swapchain generation retirement also tracks presentation completion |
| Presentation semaphore reuse | Render submission completion alone does not prove a presentation wait has consumed its semaphore. Preserve image-indexed semaphores and independent presentation-completion evidence before reuse/destruction |
| Frame ownership | Frame slots, acquired swapchain images, and presentation operations have distinct lifetimes; never equate their indices or completion signals without an explicit contract |

Sources:
[VulkanDesktopSwapchainService.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanDesktopSwapchainService.cs);
[VulkanDesktopWsiTargetDriver.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Targets/VulkanDesktopWsiTargetDriver.cs),
`CreateFrameTargetLease`; and
[Vulkan renderer architecture](../../../architecture/rendering/vulkan-renderer.md).

## J. Closeout And Implementation Handoff

Coordinate with the existing
[core frame-loop and resident-rendering backlog](vulkan-core-frame-loop-and-resident-rendering-master-todo.md)
and [Present-Now readiness work](vulkan-core-frame-loop-and-resident-rendering-master-todo.md#foundation-carryovers).
Do not weaken their provenance, readiness, or ownership rules.

- [ ] **J1 — Record the outcome of every selected task and update project docs.**
  For each attempt, include task ID, changed behavior, reproduction steps,
  requested/executed API/features, correctness evidence, CPU/GPU measurements,
  user feedback when available, and retain/reject/defer status.
  **Done when:** accepted behavior has matching architecture/workflow docs,
  deferred work has a reason and next decision, Vulkan 1.4 and GLSL/OpenGL
  requirements remain satisfied, and no item is called complete from source
  edits or an isolated performance counter alone.

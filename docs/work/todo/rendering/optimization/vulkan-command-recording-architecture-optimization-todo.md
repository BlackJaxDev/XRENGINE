# Vulkan Command Recording Architecture Optimization TODO

Last Updated: 2026-08-17
Owner: Rendering / Vulkan Command Buffers
Status: In Progress; Pre-06 Implementation Detail; Acceptance Owned By The Combined 03-05 Gate

Current architecture:

- [Vulkan Primary And Secondary Command Recording](../../../../architecture/rendering/vulkan-command-recording.md)
- [Vulkan Primary Command-Buffer Reuse](../../../../architecture/rendering/vulkan-primary-command-buffer-reuse.md)

Implemented predecessor:

- [Vulkan Command Recording Worker Architecture Progress](../../../progress/rendering/vulkan-command-recording-worker-architecture-2026-07-30.md)
  records persistent workers, per-worker/per-frame-slot command pools,
  immutable planner snapshots, deterministic merge, bounded waits, and worker
  quarantine. This TODO extends that implementation rather than recreating it.

Owning pre-06 completion and acceptance gate:

- [Vulkan Optimization Workstreams 03-05 Validation](../../../testing/rendering/03-05-optimization-validation-todo.md)
  owns remaining workstream-04 implementation closure, workstream-03/05
  acceptance, and the decision to unblock workstream 06.

Related final closeout:

- [01-08 Optimization Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md)

Measured blocker:

- [Vulkan Editor Steady-Frame CPU Cost Investigation](../../../investigations/rendering/archive/vulkan-editor-frame-time-spikes-2026-07-30.md)

Related data-path plan:

- [CPU Direct Fast Path](cpu-direct-fast-path-todo.md)

Canonical final resident/task-pool architecture:

- [Vulkan Resident Draw Stream And Render Task Pool](vulkan-resident-draw-stream-and-render-task-pool-todo.md)
  owns the post-bridge per-template residency, centralized configurable worker
  budget, pooled render task graph, stable bins, indirect-count/GPU-culling
  path, native template leases, and deletion gates for the exact cohort cache.

## Goal

Make stable Vulkan submission nearly free on the CPU by carrying immutable,
frequency-separated binding data and backend-ready draw artifacts across the
workstream-04 handoff; simplify the command-recording correctness model; reduce
all consumers and workers to immutable inputs; improve cache and lifetime
ownership; remove the OpenXR eye-recording serialization point; and widen
secondary eligibility only where measured performance and Vulkan correctness
justify it.

This is an optimization backlog, not a description of current behavior. No item
is complete merely because the design is valid.

## 2026-07-30 Steady-Frame Blocker

A stable Debug diagnostic frame at 647 render commands took 399.580 ms:

- frontend scene/package consumption: 172.468 ms;
- mesh draw preparation: 162.502 ms;
- material/program binding emission: 60.265 ms;
- binding snapshot copy: 55.187 ms;
- outer swap/present output: 226.904 ms;
- backend frame-data refresh: 170.201 ms;
- reflected auto-uniform upload: 120.188 ms;
- descriptor validation: 30.187 ms;
- actual native present call: 0.113 ms.

All 22 scheduled command chains and the primary command buffer were reused.
There were zero chain recordings, zero worker dispatches, zero pipeline misses,
and no validation messages. Managed allocation in the measured mesh hot stages
was effectively zero.

The 226.904 ms outer Present output is not a native-present wait. It contains
the backend scene-processing, frame-data-refresh, and submission lifecycle.
The measured native present call was only 0.113 ms.

The architecture must therefore optimize data construction and publication,
not command-recording worker utilization. Command-buffer reuse currently avoids
native encoding but still performs a full visible-draw refresh.

## 2026-08-17 prepared-cohort/ingress bridge

The first backend-side architectural bridge is implemented and live-validated:

- retain one bounded, exact, fully materialized mesh cohort on the Vulkan render
  thread while still draining current raw requests every frame;
- patch only current transforms, view/shadow relevance, instances,
  billboarding, producer/context data, and auto-uniform publication on reusable
  entries;
- materialize unsafe callback, publisher, shadow, external-target,
  order-preserving, and mutable-snapshot entries as explicit holes;
- append the completed current-frame transaction directly to the numeric
  `FrameOperationStream`, bypassing `MeshDrawOp` rental, queue/drain, and
  object-to-stream lowering;
- finalize pass identity and relower descriptor/attachment dependencies only
  after swapchain-context coalescing;
- revalidate retained binding candidates through the exact captured program's
  material/program/publisher/engine/pipeline generation cache;
- disable the direct path for explicit/OpenXR production until that path owns a
  compatible partition/target/overlay contract; and
- preflight fixed ingress/resource-use capacity so overflow falls back before a
  hit can suppress scene content.

In the final dense Sponza Debug/MCP cohort, a stable hit reused 566 of 625 mesh
operations and materialized 59 safety holes. Twelve samples reported a 13.111 ms
median frame-op-preparation stage versus the earlier dense-view 19--25 ms range.
The reported GPU command-buffer median was 3.767 ms, while the whole-frame
median was 21.951 ms. Camera changes produced one intentional cold build and
then stable hits with correct output. There were zero reported Vulkan errors,
skipped draws, and dropped frame operations. The performance session did not
enable the Khronos validation layer, so the validation-enabled acceptance item
below remains open.

This is deliberately not the final cache granularity. An exact ordered cohort
is invalidated by one visibility/order change and still performs O(visible
draws) matching plus hole work. The next architecture slice must replace the
whole-cohort recipe with:

1. a persistent immutable draw template per
   mesh/submesh/material/pass/program-generation;
2. compact SoA current-frame records containing template ID plus dynamic
   object/instance/view data;
3. stable numeric pipeline/material/geometry bins;
4. once-per-owner frame/view/pass/material publication and dirty object ranges;
5. bindless/indexed immutable binding tables plus dynamic offsets where the
   capability and measurements justify them;
6. multi-draw indirect or indirect-count submission per compatible bin, with
   GPU culling/generation as a later measured extension; and
7. a small number of coarse primary/secondary recording jobs with per-thread,
   per-frame-slot pools, never one tiny secondary per draw.

## 2026-07-30 Implementation Progress

The first Phase-1 migration slices and the Phase-7 worker-policy migration are
now implemented, but Phase 1 and full acceptance are not yet complete:

- Normal non-shadow, no-callback material numeric parameters are captured into
  a persistent payload keyed by material layout/value/shader revisions and
  linked-program generation. Frame-local snapshots reference that immutable
  payload instead of copying its uniform dictionary every render frame.
- Auto-uniform writes compile a material revision plus reflected block into a
  cached byte template and a dynamic-member patch list. Qualifying draws copy
  the template and patch only render-scope members; they no longer perform a
  reflected member scan, material parameter lookup, and generic conversion for
  every material-owned member.
- Each qualifying auto-uniform block now remembers which compiled material plan
  was published into each stable buffer slot. An unchanged plan does not copy
  its static material bytes into that slot again. Dynamic member ranges are
  cleared and patched on each current draw so a missing or failed dynamic write
  cannot expose a stale value.
- A runtime uniform-name signature invalidates the compiled plan if scoped
  bindings override a material name. Callback, shadow, and other unclassified
  paths remain on the conservative capture/write path.
- Payload and plan caches are invalidated on material revision, program relink,
  block replacement, UBO destruction, and snapshot-content reuse.
- Allocation-free, frame-reset telemetry now reports material payload and
  frame-snapshot cache activity, payload/snapshot entry counts, parameter
  emissions and dictionary writes, auto-uniform plan hits/misses and byte/member
  traffic, fast/fallback draws, reusable frame-data draw visits, and descriptor
  records validated/written. The counters are available in the profiler stats
  packet, profile-capture NDJSON, and the MCP profiler `binding_data` group.
- Every successful Vulkan program link now publishes a versioned
  `VulkanProgramBindingSchema`. Reflected auto-uniform members compile into
  typed engine, temporal, mesh-state, or material/runtime operations with an
  explicit frequency owner, destination range, conversion policy, and fallback
  diagnostic. Reflected descriptors compile into typed set-tier/resource
  entries with array and topology/content dependency policy.
- The qualifying auto-uniform fast path consumes the compiled operations.
  Engine, temporal, and mesh-state values use direct typed source identities;
  material/default data remains in the persistent static template; unsupported
  structs, types, ranges, and arrays explicitly use the legacy writer.
- Command-chain worker admission now uses
  `EVulkanCommandChainWorkerEligibility` plus an allocation-free result instead
  of boolean gates. The same result selects serial fallback, remains on the
  chain, distinguishes permanent from transient rejection, and feeds exact
  per-reason profiler/profile-capture/MCP telemetry.
- The obsolete `XRE_VULKAN_PARALLEL_PACKET_BUILD` compatibility flag was
  already absent from runtime configuration and is now removed from canonical
  architecture documentation.
- Frame-reset telemetry now counts every native command-buffer reset,
  command-pool reset, allocation call and successfully allocated buffer, plus
  every `vkCmdExecuteCommands` call and invoked secondary. The tracked main
  renderer wrappers and the presentationless, headless-WSI, and upscaler
  sidecar call sites all feed the same profiler/profile-capture/MCP contract.
- Frame-reset telemetry now also reports visible mesh draws, unique visible
  materials, prepared mesh draws, and exact recorded-artifact retirements.
  Together with per-frequency publication/reuse/byte counters, schema counts,
  reflected lookup/conversion counts, and typed fallback reasons, this closes
  the Phase-0 count-coverage inventory.
- Auto-uniform schema and runtime fallback decisions now publish exact typed
  reason counts. If a compiled dynamic operation cannot resolve or write its
  value, the complete block is rewritten through the legacy path instead of
  leaving a silently cleared member.
- Qualifying auto-uniform blocks now group typed operations by declared
  frequency and keep an allocation-free, per-buffer publication ledger for
  frame, view, pass, material, object, instance, and runtime-callback domains.
  Stable domains are skipped; changed domains clear precompiled coalesced byte
  ranges and patch only their declared operations. Captured runtime uniforms
  publish a content signature with the snapshot, so unchanged callback output
  no longer republishes merely because a frame advanced.
- Frame-reset counters now report publications, reuses, and published bytes
  independently for every frame, view, pass, material, object, instance, and
  runtime-callback owner. Profile-capture NDJSON and the MCP profiler expose the
  same per-frequency contract without introducing a dependency from the core
  engine onto the Vulkan backend assembly.
- The Vulkan shader rewrite now emits one physical std140 auto-uniform block
  per declared frequency in the stage's reserved binding window. Linked
  artifacts preserve every block, descriptor schemas inherit the exact
  frequency owner, and bounded owner-slot tables select stable physical ranges
  and publish the selected range to prepared dynamic-offset encoding.
- Loose numeric shader declarations can now use
  `// XRENGINE_FREQUENCY(<domain>)` to override the default material owner.
  Invalid domains fail rewrite with an actionable diagnostic. GTAO gather
  settings use `View`, publish through a typed generation-owned binding
  publisher, and no longer dirty material data when the camera moves.
- Draw-slot-invariant descriptor tables now use an owner slot rather than
  multiplying allocation identity by every draw. The cutover is deliberately
  limited to single-element dynamic UBOs plus resource-fingerprinted
  image/texel bindings; fixed/storage buffers and descriptor-heap draws retain
  exact draw ownership, and the shared allocation key retains the full backing
  resource fingerprint.
- A linked-program generation change now drops the affected mesh renderer's
  local pipeline lookups, descriptor allocations, engine/auto-uniform payload
  views, owner-slot tables, and compiled material plans. Pipeline and prepared
  record identities already include the link generation, so unrelated linked
  programs remain intact while stale interface state cannot accumulate across
  shader reloads.
- Linked programs now index auto-uniform blocks by immutable descriptor
  coordinates. Qualifying mesh buffer creation, descriptor resolution, payload
  handle capture, and dynamic-offset publication use this exact index and no
  longer enter fuzzy reflected-name matching; fuzzy lookup remains available
  only to explicit legacy/material/compute compatibility paths.
- Scheduled mesh-chain recording now copies `VkPreparedMeshDraw` values into a
  reusable `VulkanPreparedFrameRecording`, associates the storage with the
  frame slot/generation, freezes it before dispatch, and gives workers only
  stable indices into that storage. Worker code no longer rereads the original
  `MeshDrawOp` array. Prepared draw counts are exposed in the same telemetry
  surfaces.
- The render-thread preparation boundary now resolves graphics pipelines,
  descriptor handles, vertex/index bindings, primitives, push constants, and
  frame-data slot/generation into `VulkanPreparedMeshDrawState`. Worker
  secondaries use a state-only encoder and no longer call mutable
  `VkMeshRenderer.RecordDraw`, traverse `XRMaterial`, mutate program bindings,
  or capture/read `ComputeDispatchSnapshot`.
- Conventional descriptor-set binds and dynamic offsets are now resolved into
  pooled prepared records. Indexed viewport/scissor arrays are copied into
  frame-owned pooled storage, and prepared descriptor-image transitions reuse
  the resolved handles without performing a second mutable draw prewarm.
- The prepared encoder is static over immutable state. Worker assignment now
  hashes the stable command-chain identity instead of pinning all chains from
  one `VkMeshRenderer` to one worker, allowing independent prepared chains to
  use different worker-owned pools without re-entering mesh preparation.
- Each enqueued mesh draw now publishes one immutable auto-uniform owner
  snapshot. It carries independent frame, view, pass, object, instance, and
  runtime-content generations plus the late camera/mesh scalar values consumed
  by typed writes. Block publication reuses those tokens instead of rehashing
  the complete matrix set per block or rereading mutable camera/mesh state.
- Prepared mesh draws now reference pooled immutable frame-data payload handles
  for their published uniform ranges. Each handle identifies the Vulkan
  storage/range, descriptor set/binding, frame and draw slot, arena generation,
  producer, frequency mask, and content generation for every referenced owner.
  The state-only encoder rejects an incomplete or stale handle before issuing
  draw commands.
- Prepared chain encoding no longer installs a resource-planner or rendering
  pipeline scope on workers. An allocation-free recording guard rejects nested
  planner scopes and compares the global planner identity/signature stamp
  before and after encoding; obsolete per-chain planner-state copies were
  removed from the dispatch batch.
- Primary recording now compiles the ordered `FrameOp` array into one reusable,
  typed `VulkanPrimaryCommandPlan` before native recording. The recorder
  dispatches on compact node kinds and consumes a typed action mask for
  barrier-batch evaluation, render-scope entry/exit, secondary-range execution,
  operation dispatch, final present preparation, and external-image release.
  Explicit terminal nodes follow the operation range for render-scope closure,
  presentation, and external ownership release. The plan publishes a stable
  identity over order, context, target, and node/action semantics. An
  independently maintained direct-recorder projection produces a separate
  emitted-command signature.
  Tests and debug builds compare the typed and direct command signatures, then
  combine both with the same authoritative dependency-identity components and
  require exact command/dependency equivalence before reuse decisions. The
  original operations remain the semantic payload and full dependency
  signatures remain authoritative during migration.
- Primary-plan barrier actions now identify passes whose image, buffer, or
  swapchain barriers contain queue-family ownership transfers. The independently
  maintained direct-recorder projection classifies the same work, and native
  recording rejects any disagreement between the typed action and the transfer
  count actually emitted.
- Reusable primary-command refresh now consumes a bounded array of immutable
  `VulkanReusableFrameDataRefreshRequest` values built beside the reservation
  manifest. Each request freezes its planner key, exact draw slot or compute
  snapshot, and source range, so reuse no longer walks `FrameOp` values or
  reconstructs draw-slot/planner identities. Desktop keeps the arrays in
  retained thread-local scratch; each OpenXR eye publishes them through
  generation-checked storage that rejects producer overwrite while a worker
  holds a lease. Both stores clear retired reference-bearing entries when
  request counts shrink.
- Primary plans, dependency snapshots, and nested secondary artifacts now use
  `VulkanCommandIdentityComponents`: separately hashed ordered nodes, resource
  generations, render-scope inheritance, queue assumptions, nested artifacts,
  primary-only fields, secondary-only fields, and data content. Primary reuse
  diagnostics report the first mismatched component instead of only an opaque
  aggregate hash.
- Each command-chain secondary is now represented by a reusable
  `VulkanRecordedCommandArtifact` slot. Native buffer/pool ownership, command
  level, frame slot, recording and artifact generations, frozen inheritance,
  dependency identity, exact referenced-resource generations, in-flight
  counters, lifecycle state, and invalidation reason are published together.
  Desktop and OpenXR primary identities consume the same explicit artifact
  reference, including its exact generation.
- Command-chain copy-on-write replacement and destruction now obtain an
  allocation-free `VulkanRecordedCommandArtifactRetirement` snapshot from the
  artifact itself. Deferred release retains the exact old native buffer,
  pool/arena owner, generations, dependency/resource identities, and pending
  counters even after the reusable artifact slot receives a replacement.
- Worker-owned per-frame command pools and their reusable artifact slots now
  live behind one `VulkanWorkerSecondaryCommandArena` per persistent worker.
  Artifact attachment/detachment makes arena ownership explicit and prevents a
  worker pool from being destroyed while cached slots remain attached. A
  zero-allocation recording lease serializes render-thread allocation, worker
  recording, and teardown for every arena; concurrent or destructive pool
  access now fails immediately with the owning thread in the diagnostic.
- Prepared worker draws carry the Vulkan backend and immutable mesh diagnostic
  name directly. The originating `VkMeshRenderer` is retained only as an opaque
  frame-data lease identity, so concurrent chains do not dereference mutable
  mesh-renderer state while encoding. The prepared-encoding guard rejects
  planner scopes and verifies that global planner identity/publication stamps
  remain unchanged.
- Each scheduled secondary now receives an ordered frozen
  `VulkanPreparedCommandChain` beside its prepared draw slice. The record carries
  resolved inheritance, the complete dependency signature, the exact writable
  artifact handle/generation lease, and the worker-eligibility result. Serial
  fallback and parallel workers use the same state-only encoder, which rejects a
  stale lease before resetting the native command buffer.
- Each prepared frame also takes a value copy of the complete ordered
  `VulkanPrimaryCommandPlan` and its identity. Reusing the thread-local plan
  builder cannot mutate the primary orchestration view frozen beside prepared
  draw resources, chain inheritance/dependencies, artifact leases, and
  eligibility.
- The OpenXR eye contract is now documented explicitly: preparation completes
  for both eyes before recording, queue submission is ordered
  `[left, right]` (or `[left, right, publish]` for mirror publication), and the
  tracked color, depth, render-graph, descriptor, upload, history, and foveation
  subresource classes are identified.
- Prepared-draw construction and recorded-secondary merge now have independent
  CPU-time, managed-allocation, and allocation-high-water stages. A live
  isolated Vulkan sample exposed both fields, reused all 18 scheduled chains,
  reported zero managed allocation in secondary recording and submission, and
  reported zero Vulkan validation errors. The construction and merge stages
  were inactive on that clean-reuse frame, so dirty-chain allocation acceptance
  remains open.
- Cross-queue image barriers now publish immutable range-, layout-, queue-,
  stage/access-, and generation-specific ownership requirements into each
  command-buffer journal. Successful release submission retains source
  ownership plus a pending release; a matching acquire is accepted only after
  producer completion or with the exact timeline-semaphore value and
  destination wait scope. Invalid queue roles, unpaired/mismatched acquires, and
  unsynchronized incomplete producers reject submission before
  `vkQueueSubmit`. Completion watermarks are snapshotted before taking the
  image-state lock to avoid lifetime/image lock inversion.
- Material and mesh callbacks can now opt into an
  `IRenderBindingPublisher` contract with one declared frequency and a non-zero
  content generation. Publishers emit typed numeric uniforms into the existing
  thread-private Vulkan capture; immutable snapshots retain per-uniform owner
  metadata and combined per-frequency generations, and the auto-uniform plan
  assigns callback overrides to their declared domains. Invalid or changing
  publisher contracts fail visibly, while unrestricted callbacks remain on the
  counted legacy path.
- Primary-plan compilation now resolves every operation pass index before
  hashing node actions. Native recording consumes the same published index, so
  sentinel inheritance or a late fallback cannot drift queue-ownership
  classification from the barrier batch that is actually emitted.
- Dynamic uniform arena exhaustion and late/unsealed frame-data lease requests
  now have focused contract coverage: both increment explicit exhaustion
  telemetry and fail without an owned-buffer or stale-range fallback. Typed
  binding publishers likewise reject descriptor-resource writes until those
  resources have a frequency-owned publication contract.
- Descriptor publication now has focused coverage for distinct schema/layout
  and resource-content identities, per-binding changed-content suppression, and
  descriptor-set/dynamic-offset resolution before recording. Changed writes
  pin the existing thread-local scratch spans directly instead of allocating
  four transient native-input arrays.
- Descriptor allocations now publish independent owner topology and content
  generations for every in-flight descriptor slot. Stable reuse resolves the
  owner independently of the transient draw occurrence, verifies the exact
  slot publication, and avoids per-draw resource-fingerprint validation.
  Material numeric-value changes advance the payload revision without
  invalidating descriptors; material texture/resource changes advance a
  separate descriptor-resource revision.
- Mutable frame-source descriptors retain an exact per-slot resource signature,
  while stable non-frame resources use the owner content generation. The
  complete resource fingerprint remains an exact slow-path backstop and
  republishes a matching owner generation after validation, so it is not
  rescanned on subsequent stable frames.
- Five consecutive validation-enabled live samples reported 25 reusable
  frame-data draw visits but zero descriptor records validated or written and
  zero owner-lookup, owner-generation, or frame-source-generation misses.
  This closes stable descriptor proof while leaving the broader Phase-1.3
  zero-draw-visit publication work open.
- Profile JSONL and MCP output now expose elapsed time, allocated bytes, and
  allocation high-water independently for prepared-draw construction, worker
  secondary recording, secondary merge, primary command encoding, and
  submission. The generic CPU-stage recorder derives every allocation value
  from the current thread around the exact measured scope.
- Vulkan CPU-stage telemetry now also retains profiler-gated process invocation,
  cumulative-time, and peak-time counters. This keeps rare invalidation work
  observable after its frame-reset sample has passed without adding work when
  profiling is disabled.
- In the isolated `cmd-record-arch-opt-phase5` desktop Vulkan session, a
  204-frame shader-reload window invalidated 61 shaders with zero submission
  rejections and zero validation errors. Full dependency comparison consumed
  4.2092 ms across 4,357 comparisons, dirty propagation consumed 9.5739 ms
  across 756 invalidation scopes, and cache scanning consumed 0.5309 ms across
  204 frames. The observed per-invocation averages were approximately
  0.00097 ms, 0.01266 ms, and 0.00260 ms respectively; the sampled current-frame
  stages allocated zero managed bytes.
- Those measurements do not justify another command-artifact reverse index.
  Exact resource retirement already obtains generation-matched command-buffer
  dependents from the lifetime tracker's reverse map, while the remaining
  variant/chain scans are sub-hundredth-millisecond work. The additional update,
  removal, and memory cost would exceed the scan cost in this cohort.
- Prospective compute/transfer queue-schedule metadata is now separated from
  executable ownership. Until the frame graph owns real non-graphics command
  buffers, semaphore edges, and ordered submissions, barrier planning remains
  graphics-owned. This removed invalid acquire-only transitions while retaining
  the existing separately submitted transfer-queue texture-upload contract.
- The same live run enabled the multi-queue sidecar, core validation, and
  synchronization validation. Shader reload recovered through explicit stale
  secondary invalidation; the Vulkan log contained no VUID, validation-error,
  queue-ownership-acquire, or submission-rejection messages.
- Command-buffer recycling telemetry now retains process-lifetime counts for
  reset, pool reset, allocation, successful buffer allocation, secondary
  execution, and invoked secondaries. Worker-arena reset, allocation, and
  replacement-allocation counts are reported separately so overlay and other
  non-chain command buffers cannot distort the recycling decision.
- `VulkanWorkerSecondaryCommandArena` now rejects a whole-pool reset whenever a
  frame-slot artifact remains executable, primary-referenced, submitted, or the
  arena is recording. Invalidating an artifact deliberately retains its primary
  lifetime pin until retirement; the focused policy test verifies that this
  still blocks pool reset.
- The reusable packet-secondary `SIMULTANEOUS_USE` flag was audited and
  retained. Exact artifact ownership proves that a recorded primary may
  continue to pin a dirty secondary generation, but does not yet prove
  single-pending execution. Removing the flag would weaken the current
  lifecycle contract without measured evidence.
- In the isolated `cmd-record-arch-opt-phase4` Vulkan session, a 116-frame
  stable window kept worker reset, allocation, replacement, and pool-reset
  counts flat while the primary reused cleanly. A timed reload invalidated 61
  shaders over frames 7,225-7,501: 62 worker buffers were individually reset,
  zero worker buffers were allocated or replaced, zero pools were reset, and
  347 secondary-recording scopes consumed 13.6189 ms cumulatively. The final
  frame reused its primary and reported zero validation errors and submission
  rejections. The bounded per-slot cache and zero allocation growth make clean
  reuse plus individual dirty-buffer reset the selected strategy.
- OpenXR eye recording no longer enters
  `ParallelEyePrimaryRecordSharedStateLock`. Each persistent eye worker records
  directly into its own primary command buffer, mutable upload collection is
  thread-local, and worker telemetry now reports native recording start/end,
  pair span, overlap, and recording thread identity.
- The clean synchronization-validation Monado cohort at
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/phase6-monado-sync-validation-final/`
  submitted five stereo frames with zero VUIDs, synchronization hazards,
  submission failures, or teardown failures. Every paired sample overlapped:
  71.050/80.750 ms, 48.634/57.270 ms, 38.195/52.427 ms,
  61.019/62.844 ms, and 34.780/331.838 ms overlap/span, with left/right native
  recording on distinct threads 90 and 91.
- OpenXR journal cleanup now seeds untouched runtime image state, abandons
  failed primary recordings, publishes predicted state only after a successful
  queue submission, rejects stale image generations, and removes every exact
  subresource journal entry when an image is retired or a swapchain is
  recreated. Two fresh Monado session/swapchain cohorts cycled the per-eye
  camera-dependent color and engine-owned depth targets cleanly. Focused
  OpenXR, logical-device, and lifecycle contracts pass 29/29.
- Reusable-primary data refresh now retains an exact batch signature,
  conservative fallback indices, and a deduplicated work list keyed by linked
  program, physical frequency block, owner identity, and owner content
  generation. Stable frame/view/pass/material/object/instance/callback
  publication therefore returns after bounded owner checks instead of visiting
  every reusable draw.
- Legacy renderer, material, scoped, and shadow callback writes now carry
  explicit mutable provenance through capture. Their name topology remains
  structural, while current values are republished by the appropriate
  frequency owner. The zero-filled `__FallbackDescriptorBuffer` is the only
  unclassified engine UBO allowed on this path; every other such buffer retains
  conservative fallback behavior.
- Six consecutive StandardValidation samples reported clean primary reuse,
  zero command recording, zero static-primary prepared refresh visits, one
  independently changing dynamic-UI visit, zero descriptor validation/writes
  or owner-generation misses, and zero Vulkan validation errors. The final
  viewport capture retained the expected physics-test geometry without
  corruption. The focused retained-refresh/provenance/lease selection passes
  4/4.
- Non-shadow callback snapshots now remain eligible for the material fast path.
  Reflected struct trees compile into material-owned snapshot writes, omitted
  optional loose uniforms preserve zero initialization, and mutable callback
  values advance the material-owner content generation without making batch
  topology volatile. Six consecutive StandardValidation samples reported 51
  fast-path blocks, zero legacy fallback draws, zero schema fallback
  operations, zero reflected scans/lookups, zero legacy full-block bytes, zero
  descriptor validation/writes or owner misses, and zero validation errors.
  The Vulkan log also contained zero fallback diagnostics and synchronization
  hazards, and the focused schema/generation/Gate selection passes 6/6.
- Canonical `Invoke-VulkanPerf.ps1 -Preset Gate` captures now enable a hard
  binding-fallback gate. The measurement harness aggregates the fallback draw
  total and every typed reason into the retained summary and throws if any
  representative steady-state sample enters the legacy auto-uniform path.
- Equal `ShaderVar<T>.Value` assignments no longer emit change notifications or
  advance material binding revisions. Reusable-frame cohort signatures now
  exclude mutable owner content, so a material, view, or object generation
  change selects owner publication without rebuilding the retained batch.
- Compatible physical material blocks publish a semantic layout signature.
  Material-owned plans, global frequency reservations, publication ledgers,
  and reusable owner work share that identity across renderer-local linked
  programs while incompatible layouts remain isolated.
- In the 64-UnitBox/one-material StandardValidation cohort, a shared color
  mutation packed one material payload, emitted six uniforms once, built and
  published two genuinely distinct physical material blocks (176 bytes), made
  zero reusable-frame draw visits, reused the primary command buffer, and
  produced zero fallback draws or validation errors. Stable frames before and
  after the mutation performed zero material packing/publication. The reverse
  mutation produced the same counts, and both colors were verified from Vulkan
  viewport captures.
- Secondary command inheritance now freezes dynamic-rendering local-read
  attachment-location and input-index mappings plus the permitted rendering
  flags beside formats, samples, view mask, layers, and depth-read-only state.
  The complete snapshot participates in deterministic artifact identity,
  recording-time `pNext` rehydration, cache matching, and exact primary-scope
  validation. Focused contracts pass 2/2. A rebuilt StandardValidation
  64-UnitBox session reused one primary and all 82 scheduled secondaries with
  zero records, fallbacks, or validation messages; the inspected Vulkan
  viewport retained the expected scene.
- Indirect/count secondary admission now requires an explicit
  producer-complete backend scope. The CPU-built diagnostic reference path opts
  in only after its Vulkan argument buffer is bound, ready, and upload-complete;
  every draw freezes the exact indirect/count buffer identity and validates
  draw count, aligned stride, offsets, overflow, and uploaded/allocated bounds
  again at recording. GPU-produced zero-readback work has no scope and remains
  primary-owned. Typed telemetry distinguishes mutable, incomplete, changed,
  invalid-range, disabled, inheritance, and preparation fallbacks. The focused
  contract selection passes 3/3 and the Vulkan/editor projects compile with
  zero errors. A forced `GpuIndirectInstrumented` CPU-build capture confirmed
  the requested strategy and zero Vulkan validation messages, but its cached
  physics workload did not emit an indirect re-record during the retained
  window; representative benefit therefore remains open.
- Dirty-chain performance capture now has an explicit benchmark-only rerecord
  mode. It bypasses the fast schedule cache and zero-reuse stability guard,
  marks every scheduled chain with `BenchmarkForced`, flows through canonical
  cohort configuration, and is reported in frame profiles. Normal cache and
  stability behavior are unchanged when the flag is absent.
- The first parallel baseline exposed a pre-dispatch eligibility ordering bug:
  eligibility was evaluated while the render thread was still assembling the
  prepared frame, but the predicate required that frame to already be frozen.
  Eligibility now validates only the render-thread-owned published draw range;
  actual worker consumption remains frozen before dispatch. Focused contracts
  pass 14/14 and the Release editor builds with zero errors.
- The Release dirty-chain matrix now covers 128, 512, and 896 unit-box
  workloads in serial and parallel modes. Every retained frame recorded all
  145, 529, or 913 scheduled chains and reused zero. Parallel captures queued
  129, 513, and 897 chains, reached eight concurrent workers, and measured
  6.906, 38.334, and 28.368 ms median native-recording overlap, while serial
  captures queued no workers. All six summaries reported zero submission
  rejections.
- The one-repetition performance comparison is mixed: parallel recording
  changed record p50 by -11.17%, +9.11%, and -0.70% from small through large,
  while p95 changed -15.69%, +4.48%, and +6.44%. It proves real overlap but not
  a representative above-threshold material win, and dirty recording still
  allocates after warmup. Those acceptance boxes remain open. The retained
  evidence is under
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/reports/dirty-chain-baselines/`.
- The consolidated command-chain data-model, recording-dependency, and
  primary-reuse contract selection passes 165/165 tests. The suite now follows
  split Vulkan partial types without weakening file-local negative assertions
  and covers ordered prepared-frame freezing, deliberate order and inheritance
  mismatches, lifetime/publication state, dirty/cache identity, deterministic
  merge and typed-primary/direct-recorder equivalence, plus bounded worker
  failure handling.
- Frequency-owner publication now uses a fixed-capacity, allocation-free dirty
  range queue in each frame-slot ledger. It consumes the immutable owner content
  generation, coalesces only the changed owner's precompiled ranges, and
  collapses safely to the complete owner payload on overflow. An explicit
  publication identity separates layout, content, and recording-visible arena
  generations. The Vulkan project builds with zero compiler errors and the four
  focused reservation/identity/queue tests pass.
- Profile-capture NDJSON now exposes the already-instrumented mesh binding,
  binding-snapshot, enqueue, descriptor-validation, engine-uniform, and
  auto-uniform stages with time, allocation bytes/high-water, invocation count,
  cumulative time, and peak time. Together with per-owner publication counts
  and bytes, this makes the frontend, backend publication, encoding, submission,
  wait, and present boundaries independently reportable. The focused telemetry
  and publication selection passes 8/8 tests.
- Frame-data storage now has enforceable limits of 32 MiB per slot, eight
  in-flight slots, and 131,072 reservation keys. Arena recreation keeps a
  monotonic recording-visible generation and uses a separate active flag, so a
  stale prepared handle cannot become valid after teardown. Descriptor growth
  expectations are documented as owner/tier/frame-slot formulas over the
  existing allocation, pool, allocated-set, reserved-set, material, mapped-byte,
  and reserved-byte gauges.
- `XRE_VULKAN_AUTO_UNIFORM_PARITY=1` now runs the compiled frequency-owned
  serializer beside the authoritative legacy serializer for every qualifying
  block. It compares complete bytes, attributes the first mismatch to an exact
  schema entry, frequency, byte offset, and values, restores the authoritative
  bytes, invalidates the fast ledger, and records a visible fallback. Normal
  captures explicitly disable the validation flag. The focused parity and
  publication selection passes 6/6 tests.
- Release allocation tracing identified the former 11,424-byte-per-frame
  `frame_data_auto_uniform_upload` allocation as boxing of
  `LayeredShadowUniformState` during owner-identity hashing. The state now has
  typed equality and hashing, with a warmed allocation regression test. A fresh
  Release sample reported zero managed bytes in frame-data refresh,
  auto-uniform upload, packet lowering, prepared-draw construction, secondary
  recording, merge, and submission. Separate producer-side material/binding
  snapshot work still allocates, so the broader all-hot-path gate remains open.
- Shader dependency invalidation now publishes at the collect-visible/render
  frame-swap barrier, program interface link/destruction is serialized, and
  each pending draw captures the exact program link generation so a stale
  package is rejected before relinking or recording. The focused shader,
  binding, stable-packet, and pipeline contract selection passes 143/143.
- Clean Release shader reload remains a blocking lifetime defect. Four
  isolated attempts (PIDs 42632, 290928, 40820, and 292620) terminated with
  `System.ExecutionEngineException` in `Silk.NET.Vulkan.Vk.CmdDraw`; the newest
  crash occurred after invalidating 61 shaders even with frame-swap publication
  and captured link-generation rejection. Validation-enabled reloads remained
  clean, so shader-reload, resource-replacement, and final validation/stress
  acceptance stay unchecked. The durable investigation records the dumps and
  next isolation step.

This deliberately does **not** claim the full frequency-domain architecture is
complete. Pass/instance mutation cohorts, descriptor growth measurements, and
the remaining acceptance cohorts below are still required.

### Wrap-up validation checkpoint

- `XREngine.Runtime.Rendering.Vulkan.csproj` and
  `XREngine.Editor.csproj` build with zero compiler errors.
- The focused command-recording/lifecycle/material-cache test selection passes
  53/53 tests.
- The linked-schema, prepared-frame, typed primary-plan, recorded-artifact,
  worker-arena, artifact-retirement/allocation, and worker-policy selection
  passes 37/37 tests, including
  deterministic frequency ownership, independent
  per-buffer publication generations, coalesced domain ranges, runtime-uniform
  content generations, enqueue-time owner snapshots, immutable frame-data
  payload handles, planner-isolated prepared encoding, static prepared
  encoding, and chain-identity worker assignment.
- The command-buffer-local image-journal selection passes 24/24 tests. It
  covers command-buffer-local recording, exact mip/layer/aspect tracking,
  resource generation and queue-family entry contracts, ordered-submit
  validation, secondary-to-primary merge behavior, and publication only after a
  successful queue submission. The added contracts cover paired release/acquire
  identity, timeline wait value/stage matching, release-pending publication, and
  lock-order-safe completion snapshots, exact mip/layer ranges, and independent
  depth/stencil aspects. Adjacent focused contracts cover undefined/discard
  initialization, swapchain acquire/present state, and OpenXR acquire/release.
- The focused primary-reuse state-contract class passes 17/17 tests. OpenXR
  runtime color-image acquire ownership is published before primary reuse,
  release-pending ownership is recorded into the exact color subresource journal
  before command-buffer end, and ownership disagreement is a typed primary-entry
  mismatch. Predicted release state still publishes only after an accepted
  queue submission.
- Eleven adjacent prepared-frame, state-only encoder, primary-plan, and
  artifact-identity contracts plus the dedicated serial/parallel path contract
  pass. Dirty-chain serial fallback and persistent workers invoke the same
  prepared encoder, and the primary executes the resulting secondary array in
  frozen schedule order. The primary-plan checks include independently derived
  action equivalence and recorder consumption of all currently implemented
  typed orchestration actions, emitted-command signatures, and authoritative
  dependency-signature equivalence.
- The latest six-test telemetry/plan selection also passes. It covers the two
  new allocation stages, per-frequency dirty/reuse/byte publication surfaces,
  terminal primary nodes, immutable primary-plan copies, direct-recorder
  command/dependency equivalence, and recorder consumption of every currently
  implemented typed action.
- Four additional focused contracts pass for left/right OpenXR submission
  ordering, complete dependency-field classification, structural/binding
  agreement between a chain and its executable secondary artifact, and
  publication of the exact post-record secondary artifact generation in the
  primary identity.
- The corrected pre-telemetry Release CPU Direct run retained one stable
  workload identity across 96 samples. All 3,582 scheduled chains were reused;
  none were recorded.
- Its final six clean primary-reuse frames rendered in 7.242-9.746 ms overall,
  with 3.046-3.732 ms in the Vulkan frame and 1.655-1.887 ms in frame-data
  refresh. Each reused 43 chains, recorded none, and allocated zero bytes in
  command recording.
- That run is evidence of a much faster clean tail, not acceptance. Fifteen of
  96 captured frames re-recorded the primary, producing render p50/p95/p99 of
  17.726/152.954/195.214 ms and 27,370,648 total command-recording allocation
  bytes. Validation layers were not active, and the GPU timing dump failed.
- The final per-slot static-publication and per-frequency telemetry additions
  are build/test-validated but have not yet been remeasured in a canonical
  runtime cohort.
- The prepared encoder migration was smoke-tested in the isolated
  `cmd-record-arch-opt` Vulkan editor session. MCP reached readiness, two
  camera-separated viewport captures proved live image readback, the second
  capture rendered the unit-testing scene, and the runtime profiler reported
  zero Vulkan validation messages. The stable sample reused its primary and all
  18 scheduled chains with zero command-recording allocation; the scene still
  reported 18 legacy binding snapshots and therefore is not a Phase-1 cutover
  acceptance cohort.
- Compute dispatches and exact buffer copies now have independent, typed,
  serial-secondary admission. Both families require an outside-rendering
  scope, a matching capable graphics queue family, and a known primary barrier
  plan; transfer additionally revalidates frozen handles, usage flags, ranges,
  and overlap. Rejections publish a family-specific reason and retain the
  primary encoder. A synchronization-validation run with primary reuse
  disabled exercised one compute secondary per recorded frame with zero
  validation messages. That workload emitted no buffer-copy operation, so
  transfer runtime validation and measured-benefit acceptance remain open.

### Checklist reconciliation

The checklist was reconciled against the 2026-07-30 source tree and targeted
tests after the first binding-cache slice. Checked items below mean the exact
task is implemented or the requested evidence exists; they do not promote a
parent phase whose acceptance criteria remain open.

Current command-recording ownership is:

| Responsibility | Current owner |
| --- | --- |
| Upcoming-frame selection and ordering | `BackendReadyFramePackage`, `BackendReadyMeshSelection`, and `XRRenderPipelineInstance` |
| Vulkan planner state and context publication | `VulkanRenderer.ResourcePlannerContext.cs` and `VulkanRenderer.ResourcePlannerSwitching.cs` |
| Frame operation carrying a live mesh draw | `MeshDrawOp` and `PendingMeshDraw` |
| Identity-oriented lowered mesh packet | `DrawPacket` and `VulkanRenderer.CommandChainLowering.cs` |
| Mutable renderer draw/refresh entry point | `VkMeshRenderer.RecordDraw` in `VkMeshRenderer.Drawing.cs` |
| Command-chain schedule/cache state | `CommandChain`, `CommandChainSchedule`, `VulkanRenderer.CommandChainLowering.cs`, and `VulkanRenderer.CommandBufferRecording.cs` |
| Primary cache variants | `VulkanRenderer.CommandBufferCacheVariant.cs` |
| Native buffer/resource lifetime tracking | `VulkanRenderer.CommandBufferTrackingBatch.cs`, `VulkanRenderer.CommandBufferState.cs`, and `VulkanRenderer.ResourceLifetimeTracking.cs` |
| Persistent chain worker/pool ownership | `VulkanRenderer.CommandChainWorkers.cs`, `VulkanRenderer.OwnedCommandChainSecondaryPool.cs`, and the owned-pool state in `VulkanRenderer.CommandBufferState.cs` |
| OpenXR eye workers and ordered local journals | `VulkanRenderer.OpenXR.EyeRecordWorkers.cs`, `VulkanRenderer.OpenXrEyeRecordWorkerScheduler.cs`, and `VulkanRenderer.Synchronization.cs` |

Targeted reconciliation validation passed 53/53 tests on 2026-07-30,
including `VulkanArchitectureLifecycleGuardTests`,
`SwapchainContextCoalescingTests`, and the material payload/runtime topology
tests in `VulkanStablePacketAndDescriptorTests`. The Vulkan renderer project
and editor project also built successfully with zero compiler errors.

## Ownership Reconciliation

This plan now closes a gap between three workstreams:

- Workstream 04 owns the immutable upcoming-frame handoff. Its current package
  carries ordering, selection, revisions, and live references, but not packed
  binding data, descriptor identities, dirty ranges, or backend-ready draw
  artifacts. Its binding/data acceptance is reopened.
- The CPU Direct fast-path plan already calls for per-frame, per-view,
  per-pass, per-material, and per-object data separation, stable material
  tables, persistent mapped arenas, and dirty-range publication. This plan
  adopts that contract rather than creating a competing Vulkan-only model.
- Workstream 05 owns dirty command-chain recording. Its zero-worker behavior on
  a stable frame is correct. Workers become a consumer of immutable prepared
  draws after the data contract is fixed; they are not a fallback for repeated
  binding reconstruction.

No phase may claim success merely by moving the measured work from the render
thread to collect-visible or a worker. Acceptance requires that unchanged work
is not executed.

## External Validity Review

The proposals were checked against current authoritative Vulkan documentation:

- The [Vulkan threading guide](https://docs.vulkan.org/guide/latest/threading.html)
  confirms that a command pool must be externally synchronized and recommends a
  separate pool per recording thread.
- The Khronos
  [command-buffer usage and multithreaded recording sample](https://docs.vulkan.org/samples/latest/samples/performance/command_buffer_usage/README.html)
  recommends per-frame/per-thread resource pools, warns that many small
  secondaries can cost more than they save, and finds pool reset generally
  cheaper than frequent allocation/free or individual command-buffer reset.
- The [Vulkan profiling guide](https://docs.vulkan.org/guide/latest/profiling.html)
  identifies command recording, descriptor updates, state changes, and small
  queue submissions as the main CPU-side explicit-API costs; it recommends
  pipeline sorting, descriptor indexing/buffers, batched submission, and real
  CPU/GPU timeline instrumentation.
- The Khronos
  [descriptor-management sample](https://docs.vulkan.org/samples/latest/samples/performance/descriptor_management/README.html)
  demonstrates descriptor caching and dynamic-buffer reuse, while the
  [descriptor-indexing sample](https://docs.vulkan.org/samples/latest/samples/extensions/descriptor_indexing/README.html)
  demonstrates binding a large resource array once and selecting resources in
  the shader rather than rebinding per mesh.
- The Khronos
  [multi-draw-indirect sample](https://docs.vulkan.org/samples/latest/samples/performance/multi_draw_indirect/README.html)
  demonstrates compact GPU-generated draw streams and one multi-draw command,
  which is the intended eventual bin-consumer shape rather than a cache entry
  per native draw call.
- NVIDIA's [Vulkan dos and don'ts](https://developer.nvidia.com/blog/vulkan-dos-donts/)
  likewise recommends a task graph for command/resource/descriptor/pipeline
  preparation, few coarse command buffers/submissions, dynamic data buffers or
  push constants, fewer descriptor sets, and measured command-buffer reuse.
- AMD's [RDNA performance guide](https://gpuopen.com/learn/rdna-performance-guide/)
  warns that too many small command buffers can lose more than parallel
  recording gains and recommends per-thread/per-frame allocators, sufficiently
  coarse work, and minimal submissions. AMD's
  [Vulkan and DOOM](https://gpuopen.com/learn/vulkan-and-doom/) describes the
  complementary high-draw-count pattern: parallel command recording, pooled
  allocation, and descriptor tables indexed from compact draw data.
- The specification's
  [command-buffer chapter](https://docs.vulkan.org/spec/latest/chapters/cmdbuffers.html)
  defines primary/secondary lifecycle coupling, pending-state restrictions,
  the listed order of `vkCmdExecuteCommands` secondaries, state inheritance
  rules, and dynamic-rendering compatibility requirements. Command-buffer
  boundaries do not create memory dependencies by themselves.
- The specification's
  [synchronization chapter](https://docs.vulkan.org/spec/latest/chapters/synchronization.html)
  requires layout transitions to be ordered around all accesses and states that
  command-buffer boundaries do not create synchronization by themselves.
- The specification's
  [device and queue chapter](https://docs.vulkan.org/spec/latest/chapters/devsandqueues.html)
  ties command pools and their command buffers to a queue family and defines
  queue-family ownership transfers.
- The specification's
  [query chapter](https://docs.vulkan.org/spec/latest/chapters/queries.html)
  requires a query to begin and end in the same command buffer and adds
  inheritance rules when a primary executes secondaries inside an active query.
- The [Vulkan validation overview](https://docs.vulkan.org/guide/latest/validation_overview.html)
  recommends validation layers during development; synchronization validation
  is an additional required gate for the image-state work.

These sources validate the general direction, but they do not prove that an
engine-internal abstraction will improve XRENGINE. Allocation and performance
claims remain profile-gated.

## Proposal Review Summary

| Proposal | Verdict | Required qualification |
| --- | --- | --- |
| Frequency-separated binding payloads | Required before further worker expansion | Frame, view, pass, material, object, and instance data must have explicit owners, generations, and publication frequency. |
| Link-time compiled binding schema | Required | Reflection remains authoritative, but the frame loop must not interpret string names and generic values per member/per draw. |
| Change-driven packed-data publication | Required | Stable frames must process dirty owner lists, not scan every visible or recorded draw. |
| Stable descriptor tiers and offsets | Required with backend profiling | Descriptor topology and data offsets must survive stable frames; choose dynamic UBO, SSBO/table, descriptor-buffer, or other mechanisms by capability and measurement. |
| Legacy/new payload equivalence mode | Required migration guard | Compare bytes, resource identities, fallback decisions, and visual results before deleting the legacy snapshot path. |
| Immutable prepared-frame phases | Valid engine refactor | Must preserve render order and avoid allocating a large transient object graph every frame. |
| Immutable prepared mesh draws | Strongly justified | Extend the current identity-only selection/`DrawPacket`; do not duplicate it while consumers still read `MeshDrawOp`, live material state, or `ComputeDispatchSnapshot`. |
| Stable worker secondary arenas | Valid refinement | Per-worker/per-slot pools already exist. Optimize ownership metadata and recycling only after measuring reset/allocation cost. |
| Per-chain dependency versions | Conditionally valid | Keep full signatures as the correctness backstop; add a reverse index only if invalidation scans are measured bottlenecks. |
| Shared primary/secondary cache identity | Valid with narrower scope | Share identity primitives and nested-artifact references, not one monolithic key; primary and secondary dependencies differ. |
| Command-buffer-local image state | Valid but high risk | Merge subresource transitions in actual submission order and model queue-family ownership and external/OpenXR image contracts. |
| Immutable primary plan nodes | Valid engine refactor | The interpreter must emit the same barriers, rendering scopes, and execution order as the direct recorder. |
| Typed eligibility/quarantine results | Valid | Keep the result allocation-free and use one value for policy, telemetry, and diagnostics. |
| Remove or redesign inert packet flag | Valid local cleanup | Remove it unless a measured deterministic packet-build experiment is implemented. |
| Expand secondary operation families | Vulkan permits it conditionally | Respect render-pass scope, queue-family capability, synchronization, and query inheritance; add families independently. |
| First-class recorded artifacts | Valid with allocation constraints | Prefer pooled structs/owned slots; do not create a managed object per draw or per frame. |
| Layered acceptance gates | Strongly justified | Include core, synchronization, lifetime, deterministic-order, allocation, and hardware performance cohorts. |

## Architectural Invariants

The following are requirements, not implementation suggestions:

- Stable-frame cost must be proportional to changed owners, not
  `visible draws x reflected members`.
- A material used by many draws is packed once per dirty material and required
  frame slot, not once per draw.
- Frame, view, and pass data are published once per corresponding scope.
- Object and instance updates touch only dirty slots/ranges.
- Descriptor work is proportional to layout/resource topology changes, not
  stable draw count.
- Command encoding is proportional to dirty command artifacts; stable artifacts
  are reused.
- Binding data and recording state have distinct generations. A data-content
  change must not rerecord a command buffer when stable offsets and descriptors
  make rerecording unnecessary.
- Full dependency signatures remain a correctness backstop, but a successful
  stable fast path must not rebuild or rescan all dependency content.
- Every fallback is explicit, counted, and excluded from canonical fast-path
  acceptance.
- All new per-frame hot paths remain allocation-free after warmup.

The plan does not prescribe one Vulkan storage mechanism. Dynamic UBO offsets,
SSBO/material tables, push constants, descriptor buffers, or capability-gated
variants may be selected by measurement. They must all satisfy the same
frequency, lifetime, invalidation, and telemetry contracts.

## Phase 0 - Freeze Baselines And Contracts

- [x] Record current source ownership for prepared planner state, `DrawPacket`,
  `MeshDrawOp`, `VkMeshRenderer.RecordDraw`, command-chain caches, primary
  variants, tracked resources, worker pools, and OpenXR image-state locking.
- [x] Record the current workstream-04 selection/package, live render-command,
  material/program binding, snapshot-copy, reflected auto-uniform,
  reusable-frame refresh, and descriptor-validation dataflow.
- [x] Capture serial and parallel dirty-chain baselines for small, medium, and
  large workloads.
- [x] Capture stable-frame primary/secondary cache-hit behavior.
- [x] Capture one exact stable diagnostic frame with separate frontend material
  emission, snapshot copy, backend auto-uniform processing, descriptor
  validation, queue submit, acquire, fence-wait, and native-present timings.
- [x] Capture managed allocations for preparation, worker recording, merge,
  primary assembly, and submission independently.
- [x] Add frame-reset counters for material payload cache hits/misses,
  payload/uniform packing, material parameter emissions and dictionary writes,
  frame material snapshot cache hits/misses, binding snapshot captures/entries,
  fast/legacy snapshot counts, auto-uniform plan hits/misses, static/dynamic
  byte traffic, dynamic member patches, reflected member scans, fast/fallback
  draws, stable frame-data draw visits, and descriptor records
  validated/written.
- [x] Complete count coverage for visible/prepared draws, unique visible
  materials, frame/view/pass/object/instance payloads, dirty/reused slots,
  reflected-name lookups, generic conversions, descriptor schemas, command
  artifact retirement, and typed fallback reasons.
- [x] Report outer engine output scopes separately from native Vulkan calls so
  the swap/present wrapper cannot be mistaken for `vkQueuePresentKHR`.
- [x] Capture `vkResetCommandBuffer`, command-pool reset, command-buffer
  allocation, and secondary invocation counts.
- [x] Add or confirm deterministic schedule/merge tests before changing the
  intermediate representation.
- [x] Document the current OpenXR left/right command-buffer submission order and
  every shared image subresource it can touch.

Acceptance criteria:

- [x] Baselines distinguish scheduled concurrency from overlapping native
  command recording.
- [x] Baselines distinguish frontend binding construction, backend data
  publication, command encoding, submission, OS/GPU waits, and native present.
- [x] Every time counter that can scale with draws has a corresponding count
  and byte counter.
- [x] The cost being optimized is visible in profiler data.
- [x] Current correctness tests fail if render order, inheritance, or lifetime
  coupling is deliberately broken.

## Phase 1 - Frequency-Separated Binding And Data Publication

This phase is the missing workstream-04 handoff. It precedes worker-facing draw
records because an immutable copy of the current dictionary snapshot would
preserve the dominant cost instead of removing it.

### 1.1 Compile the binding schema

- [x] Compile shader reflection into an immutable, versioned binding schema at
  shader link/artifact materialization time.
- [x] Give every non-opaque value a typed source identity, frequency domain,
  destination set/binding/offset, size/stride, conversion operation, and
  default-value policy.
- [x] Give every opaque resource a typed resource identity, descriptor tier,
  array/indexing policy, and topology/content dependency.
- [x] Replace per-draw member-name source resolution with compact typed copy
  operations or direct typed writes.
- [x] Preserve reflection metadata for diagnostics and validation without
  interpreting it on every draw.
- [x] Reject or explicitly fall back when a shader declaration cannot be
  classified safely.
- [x] Cache schemas by shader/layout identity and generation; do not rebuild
  them on frame or draw boundaries.

Acceptance criteria:

- [x] A qualifying draw performs zero reflected-name lookups and zero generic
  type-dispatch operations in steady state.
- [x] Stable draws perform zero full mixed-frequency block copies and zero
  reflected-member scans; object updates use precompiled direct writes to dirty
  object slots.
- [x] Schema compilation is deterministic and produces actionable diagnostics
  for unclassified inputs.
- [x] Shader reload invalidates only affected schemas, pipelines, descriptor
  layouts, and prepared records.

### 1.2 Establish frequency-owned payloads

- [x] Define explicit frame, view, pass, material, object/draw, and
  instance/batch data domains.
- [x] Assign each binding schema entry to exactly one declared owner/frequency,
  with documented exceptions for aliases or backend transforms.
- [x] Split the current auto-uniform storage so a changing object value cannot
  force serialization of unchanged material, view, pass, or frame values.
- [x] Pack frame data once per frame slot, view data once per active view, and
  pass data once per pass generation.
- [x] Pack material data once per dirty material and required in-flight frame
  slot, regardless of draw/reference count.
- [x] Pack object and instance data into stable slots/ranges and update only
  dirty slots.
- [x] Define frame-slot ownership, publication, in-flight retention, and
  retirement for every payload domain.
- [x] Define how temporal history and previous-frame object data advance without
  forcing unrelated payload rewrites.

Acceptance criteria:

- [x] One material referenced by many draws is serialized once per dirty
  material/frame-slot publication, not once per draw.
- [x] Camera-only motion does not rewrite material or static object payloads.
- [x] Object-only motion does not rewrite material, frame, view, or pass
  payloads.
- [x] An unchanged frame performs no material/object serialization and reports
  zero dirty bytes for those domains.
- [ ] All payload publication is zero-allocation after warmup.

### 1.3 Publish dirty ranges from change owners

- [x] Add precise content generations and dirty-range queues to frame, view,
  pass, material, object, and instance owners.
- [x] Separate data-content generation from layout/topology generation and
  recording-visible generation.
- [x] Publish immutable payload handles containing storage identity, offset or
  index, length, generation, frame-slot lifetime, and owner identity.
- [ ] Use persistent mapped or equivalently bounded storage; select dynamic UBO,
  SSBO/table, push-constant, descriptor-buffer, or capability-specific layouts
  only after measuring representative hardware.
- [x] Coalesce dirty byte ranges without scanning all live or visible objects.
- [x] Make stable publication a bounded generation check that can return
  without visiting every reusable draw.
- [x] In the current auto-UBO migration path, retain the published material-plan
  identity per block and stable buffer slot, skip unchanged static material
  copies, and clear/patch only precompiled coalesced dynamic-domain ranges on
  each draw visit.
- [x] Preserve explicit failure for exhausted arenas or invalid owner lifetime;
  do not silently bind stale data.

Acceptance criteria:

- [ ] Publication CPU and bytes scale with dirty owners/ranges.
- [x] The stable static cohort visits zero draw operations for data refresh.
- [ ] Storage remains bounded across frame slots, resize, scene churn, shader
  reload, and shutdown.
- [x] Data-content-only changes reuse command artifacts when their stable
  binding location and recorded dynamic state permit it.

### 1.4 Stabilize descriptor topology

- [x] Define descriptor ownership by frame/view, pass, material, and
  object/instance domain instead of by accidental draw snapshot composition.
- [x] Make descriptor schema/layout generation distinct from descriptor
  resource-content generation.
- [x] Resolve descriptor tier handles and stable offsets/indices before command
  recording.
- [x] Publish descriptor writes only for changed resource content or topology.
- [x] Replace per-draw descriptor proof with owner-generation checks and
  precise invalidation lists.
- [x] Retain full binding/resource fingerprints as a validation backstop during
  migration, but remove their broad stable-frame scans from the accepted fast
  path.
- [ ] Measure descriptor variant, set, reservation, pool, mapped-byte, and
  reserved-byte amplification against unique materials/layouts/frame slots.
- [x] Set explicit bounded-growth expectations for descriptor and frame-data
  arenas.

Acceptance criteria:

- [x] An unchanged frame performs zero descriptor writes and no per-draw
  descriptor validation.
- [ ] A material texture change updates only the affected material resource
  records and dependent generations.
- [ ] Descriptor set/record counts scale with declared owners and in-flight
  slots, not draw/pass/frame cartesian products.
- [ ] Core and synchronization validation remain clean through resource
  replacement and retirement.

### 1.5 Constrain callbacks and legacy fallback

- [x] Require material/render callbacks that qualify for the fast path to
  declare a frequency domain and publish typed output with a generation.
- [x] Prevent qualifying callbacks from mutating a shared program dictionary
  during draw consumption.
- [x] Define an explicit legacy fallback for shaders or callbacks that cannot
  yet satisfy the contract.
- [x] Count fallback draws, material emissions/dictionary writes, snapshot
  captures/entries, and reflected full-block scans/bytes.
- [x] Count typed fallback reasons.
- [x] Make canonical acceptance fail if the representative scene silently uses
  the fallback.

Acceptance criteria:

- [x] Fast-path draws never call `ClearBindings()`,
  `SetMaterialUniforms(..., forceUpdate: true)`, unrestricted binding callbacks,
  or `ComputeDispatchSnapshot` capture during consumption.
- [x] Unsupported cases are visible and correct rather than stale or silently
  CPU-bound.

### 1.6 Dual-path equivalence and cutover

- [x] In validation builds, produce new packed payloads beside the legacy
  snapshot/serializer output.
- [ ] Compare uniform bytes, descriptor resource identities, offsets, dynamic
  state, fallback decisions, and draw order.
- [x] Add mismatch diagnostics at schema entry and payload-domain granularity.
- [ ] Capture representative render targets and viewport images for static,
  moving, camera-only, material-mutation, resize, and shader-reload cohorts.
- [ ] Remove the legacy path from qualifying draws only after byte/resource,
  visual, lifetime, and synchronization parity passes.

Acceptance criteria:

- [ ] The new path is equivalent where the legacy path is authoritative.
- [x] Intentional frequency-layout differences are covered by explicit expected
  mappings rather than ignored byte mismatches.
- [x] No canonical scene draw uses the legacy fallback.

## Phase 2 - Explicit Prepared Frame And Immutable Draw Encoding

### 2.1 Prepared-frame phase boundary

- [x] Introduce an allocation-bounded `VulkanPreparedFrameRecording` or
  equivalent frame-slot-owned structure.
- [x] Store ordered primary-plan nodes, resolved render scopes, inheritance,
  stable resource handles/generations, dependency signatures, referenced
  resources, and eligibility results.
- [ ] Build pure selection and binding inputs on the workstream-04 producer
  side; materialize only thread-affine Vulkan handles on their legal owner
  before worker dispatch.
- [x] Consume frequency-domain payload handles and dirty publications from
  Phase 1 rather than rebuilding draw bindings.
- [x] Give workers only indexed slices or handles into frozen frame-slot
  storage.
- [x] Assert that workers cannot publish planner or global renderer mutations.

### 2.2 Prepared mesh draw

- [x] Extend or replace the current identity-oriented `DrawPacket` with a
  compact `VkPreparedMeshDraw`.
- [x] Resolve all pipeline, descriptor, vertex/index/indirect binding, viewport,
  scissor, dynamic state, pass metadata, frame-data slot, and lifetime inputs
  before dispatch.
- [x] Reference stable frame/view/pass/material/object payload handles,
  generations, and dynamic offsets/indices from the prepared draw.
- [x] Stop worker code from rereading the original `MeshDrawOp`.
- [x] Stop worker code from calling mutable `VkMeshRenderer.RecordDraw`; route
  it through an encoder that consumes only prepared data.
- [x] Stop every qualifying consumer from rereading `XRMaterial`, clearing or
  mutating program bindings, or capturing/reading `ComputeDispatchSnapshot`.
- [x] Use frame-slot arrays, spans, or pools; do not allocate one managed object
  per draw.
- [x] Remove renderer-to-worker ownership pinning only after tests prove the
  prepared record is complete and independent.

Acceptance criteria:

- [x] Worker inputs are immutable by construction.
- [x] Consumer inputs are backend-ready by construction; steady consumption
  performs no live material traversal or reflected serialization.
- [x] Two chains derived from one renderer can record concurrently without
  accessing shared mutable renderer state.
- [x] Prepared-frame construction and worker encoding add zero steady-state
  managed allocations.
- [x] Serial and parallel recordings produce equivalent ordered command plans.

## Phase 3 - Typed Primary Plan And Shared Identity Primitives

### 3.1 Primary plan nodes

- [x] Represent primary orchestration with compact typed nodes such as
  `BarrierBatch`, `BeginRendering`, `ExecuteSecondaryRange`,
  `RecordInlineOperation`, `EndRendering`, `QueueOwnershipTransfer`, and
  `PreparePresent`.
- [x] Keep render-scope begin/end and barrier placement in the plan; do not move
  synchronization responsibility into arbitrary worker draws.
- [x] Implement a deterministic primary recorder over the plan.
- [x] Compare emitted command/dependency signatures with the existing direct
  recorder during migration.
- [x] Use the plan identity as an input to primary reuse only after equivalence
  is proven.

### 3.2 Shared identity vocabulary

- [x] Define shared identity components for ordered command nodes, resource
  handles/generations, render-scope inheritance, queue assumptions, and nested
  recorded artifacts.
- [x] Keep primary-only and secondary-only dependency fields separate.
- [x] Make a primary identity reference the exact secondary artifact
  generations it executes.
- [x] Preserve current full dependency signatures as a backstop during rollout.
- [x] Add mismatch diagnostics at the component level.

Acceptance criteria:

- [x] Primary and secondary caches cannot disagree about a shared dependency
  generation.
- [x] A secondary reset, replacement, or retirement invalidates every primary
  identity that references it.
- [x] Different valid primary and secondary dependencies are not collapsed into
  a misleading universal key.

## Phase 4 - Recorded Artifact And Worker Arena Ownership

### 4.1 First-class recorded artifact

- [x] Introduce a pooled `VulkanRecordedCommandArtifact` or equivalent owned
  slot containing the native buffer, command level, pool/arena owner,
  dependency identity, referenced-resource set, frame slot, generation,
  in-flight state, retirement state, and failure/invalidation reason.
- [x] Make primary-to-secondary lifecycle linkage explicit in artifact
  references.
- [x] Route deferred retirement through the artifact owner.
- [x] Ensure an artifact cannot be reset or freed while pending.
- [x] Avoid a managed allocation per artifact transition.

### 4.2 Worker secondary arena

- [x] Consolidate the existing per-worker/per-frame-slot command pool,
  reusable-buffer slots, signatures, referenced resources, and retirement
  metadata behind one arena owner.
- [x] Measure pool reset, individual reset, and reuse strategies on XRENGINE's
  actual cached-secondary workload.
- [x] Prefer pool reset only where it does not invalidate still-reusable or
  primary-referenced secondaries.
- [x] Audit `VK_COMMAND_BUFFER_USAGE_SIMULTANEOUS_USE_BIT`; remove it only where
  lifecycle tracking proves the same secondary cannot be pending through more
  than one execution and measurements show benefit.
- [x] Keep chain count and draw count large enough to amortize
  `vkCmdExecuteCommands` and per-secondary state setup.

Acceptance criteria:

- [x] Pool ownership remains one recording thread at a time.
- [x] No cached primary references an arena slot that can be recycled.
- [x] The selected recycling strategy wins on measured CPU cost without
  increasing memory unboundedly.
- [x] Small workloads remain serial when secondary overhead is not amortized.

## Phase 5 - Profile-Gated Dependency Versioning

- [x] Measure the cost of full signature comparison, dirty propagation, and
  cache scanning independently.
- [x] Add explicit generation fields for pipeline/layout, descriptor
  layout/content, geometry bindings, inheritance/target, indirect/count stream,
  and dynamic state only where ownership is unambiguous.
- [x] Keep the complete dependency signature as the correctness authority.
- [x] Add a reverse dependency index only if measurements show broad scans are
  material. The measured scans are not material, so no additional index was
  added.
- [x] Preallocate index storage or update it outside per-frame hot paths. Not
  applicable to the rejected additional index; the existing lifetime reverse
  map is updated with resource/command-buffer tracking outside recording.
- [x] Validate removal and retirement so the index cannot retain dead resource
  or chain references. Existing generation-matched lifetime reverse-map removal
  and exact retirement invalidation tests remain the authority.

Acceptance criteria:

- [x] A changed resource dirties all and only the chains that depend on its
  changed recording-visible state.
- [x] Version wrap, resource recreation, and handle reuse cannot produce a false
  cache hit.
- [x] No additional index is retained unless its measured scan savings exceed
  its update and memory cost; the current cohort rejected it.

## Phase 6 - Command-Buffer-Local Image State And OpenXR Unlock

This phase is correctness-first. Recording completion order must never become
image-state or submission order.

- [x] Define an immutable starting state per image subresource, including
  layout, access/stage history needed by the planner, queue-family ownership,
  and external/OpenXR ownership state.
- [x] Record a local transition/access journal for each independently recorded
  primary.
- [x] Validate and merge journals in the exact order the corresponding command
  buffers will be submitted, not worker completion order.
- [x] Emit explicit semaphore and queue-family ownership requirements when
  journals cross queues.
- [x] Reject conflicting journals or serialize their planning rather than
  guessing an `oldLayout`.
- [x] Commit predicted state to the renderer's submission-state model only when
  the ordered submission is accepted; retain rollback/rebuild behavior for
  failed recording or submission.
- [x] Cover `VK_IMAGE_LAYOUT_UNDEFINED`, discard transitions, split depth/stencil
  aspects, mip/layer ranges, swapchain acquire/present, and OpenXR acquire/release.
- [x] Remove `ParallelEyePrimaryRecordSharedStateLock` only after left and right
  eye recording overlaps in native timing and all journal tests pass.

Acceptance criteria:

- [x] Synchronization validation reports no layout, access, or ownership hazard.
- [x] Camera-independent and camera-dependent eye targets preserve correct
  subresource state across frames.
- [x] OpenXR eye primary recording overlaps without a shared recording lock.
- [x] Submission failure, resize, session restart, and swapchain recreation do
  not publish unexecuted predicted layouts as completed state.

## Phase 7 - Typed Eligibility, Quarantine, And Configuration Truth

- [x] Replace boolean worker eligibility with an allocation-free enum/result
  covering at least `Eligible`, `TooLittleIndependentWork`,
  `MutableRendererConflict`, `UnsupportedOperation`,
  `UnsupportedInheritance`, `PrimaryOwnedIndirectStream`,
  `WorkerQuarantined`, and `ResourcePreparationFailed`.
- [x] Use the same result for fallback policy, telemetry, and diagnostics.
- [x] Separate permanent unsupported cases from transient not-ready and faulted
  worker-domain cases.
- [x] Remove `XRE_VULKAN_PARALLEL_PACKET_BUILD` while packet lowering remains
  sequential.
- [x] If parallel packet lowering is reconsidered, require immutable
  partitions, deterministic output slots, zero steady-state allocation,
  sequential-equivalence validation, and a measured win. It was not
  reconsidered; lowering remains deliberately sequential.
- [x] Remove obsolete logs, settings, and tests that imply inactive
  concurrency.

Acceptance criteria:

- [x] Telemetry names the path that executed and its exact rejection reason.
- [x] No configuration flag claims parallel work that is serial.
- [x] Every rejection has an explicit safe fallback or visible frame failure.

## Phase 8 - Expand Secondary Eligibility Incrementally

Vulkan permits draw, dispatch, copy, and many query commands in secondary
command buffers, but every command retains its own render-pass-scope,
queue-capability, inheritance, and synchronization valid usage. Do not treat
"secondary supported" as "safe in the current command chain."

### 8.1 Additional graphics draws

- [x] Add direct mesh-draw variants whose complete state fits
  `VkPreparedMeshDraw`.
- [x] Validate dynamic-rendering formats, samples, view mask, mapping state, and
  render flags against the executing primary.

### 8.2 Immutable indirect work

- [x] Admit indirect/count commands only when their producer is complete before
  recording/execution and buffer identity/ranges remain stable.
- [x] Keep mutable zero-readback indirect/count streams primary-owned until a
  separate cross-vendor cohort proves the secondary contract.

### 8.3 Compute and transfer chains

- [x] Record them outside render-pass instances.
- [x] Allocate their command buffers from pools for a queue family that supports
  the commands and matches the primary/queue that will execute them.
- [x] Model resource reads/writes, barriers, and ownership transfers explicitly.
- [x] Do not label queue-schedule metadata as asynchronous multi-queue execution.

### 8.4 Queries

- [x] Add query work last.
- [x] Keep each begin/end pair in the same command buffer.
- [x] Model primary-active query inheritance, `inheritedQueries`,
  `occlusionQueryEnable`, query flags, pipeline statistics, reset placement, and
  result ordering.
- [x] Retain the primary path for unsupported query scopes.

Acceptance criteria:

- [x] Each family has independent enablement, tests, telemetry, and fallback.
- [ ] Core and synchronization validation pass for every family.
- [ ] Each family demonstrates a measured benefit on representative hardware.

## Phase 9 - Acceptance And Cutover

The combined 03-05 gate owns execution status for every pre-06 item below.
Map evidence there and do not unblock workstream 06 from this subordinate
architecture tracker alone.

- [ ] Binding-schema classification, std140/layout, array/struct, default-value,
  and shader-reload tests pass.
- [ ] Legacy/new payload byte and resource-identity equivalence passes for every
  qualifying shader family.
- [ ] Frame/view/pass/material/object frequency-isolation tests pass.
- [ ] A stable static frame reports zero material dictionary emissions,
  snapshot copies, auto-uniform template construction/full-block copies/member
  scans, material/object payload serializations, per-draw descriptor
  validations, and descriptor writes.
- [x] A single shared-material mutation serializes one material payload per
  required frame slot, independent of its draw count.
- [x] Camera-only and object-only cohorts touch only their declared dirty
  domains.
- [x] Prepared-frame determinism tests pass.
- [x] Dirty propagation and cache-identity tests pass.
- [x] Worker thread-safety, timeout, exception, and quarantine tests pass.
- [x] Deterministic merge and primary-plan equivalence tests pass.
- [ ] Primary/secondary pending-state and deferred-retirement stress passes.
- [ ] Dynamic-rendering and legacy inheritance matrices pass.
- [ ] Core, synchronization, and best-practices validation are clean.
- [ ] Desktop, OpenXR, resize, shader reload, scene churn, and device shutdown
  stress pass.
- [ ] Release small/medium/large dirty workloads show no regression below the
  declared threshold and a material win above it.
- [x] Stable workloads continue to reuse command buffers instead of invoking
  workers.
- [ ] All new hot paths report zero steady-state managed allocations.
- [ ] The representative approximately-647-draw Release stable-static cohort
  meets all of these p95 workstream-local budgets:
  - frontend binding/package consumption <= 0.15 ms;
  - frame/view/pass data publication <= 0.15 ms;
  - unchanged material/object publication <= 0.05 ms;
  - descriptor reuse validation/publication <= 0.10 ms;
  - command-artifact reuse validation <= 0.15 ms;
  - total Vulkan preparation/record/submit CPU, excluding separately measured
    OS/GPU waits, <= 1.00 ms.
- [ ] The declared Release moving-object cohort updates only dirty object ranges
  and keeps total Vulkan preparation/record/submit CPU, excluding separately
  measured waits, <= 1.50 ms p95.
- [ ] The canonical CPU Direct desktop render path remains at or below the
  workstream-01 5.00 ms p95 product gate.
- [ ] Every performance result reports build, hardware, scene, resolution,
  strategy, validation state, command/unique-material/dirty-owner counts, bytes
  copied, descriptor writes, fallback counts, and native wait/present time.
- [ ] Documentation and environment-variable references match the shipped path.

The local budgets are intentionally aggressive because the accepted stable path
must be dominated by bounded generation checks and a few scoped writes, not
draw traversal. If representative hardware proves a sub-budget unrealistic,
record the evidence and reallocate within the 5.00 ms product gate. Do not
relax the frequency/scaling invariants.

## Recommended Execution Order

1. Freeze baselines and correctness contracts.
2. Compile binding schemas and implement frequency-separated frame, view, pass,
   material, object, and instance payload ownership.
3. Add dirty-range publication and stable descriptor topology, then prove
   legacy/new payload equivalence.
4. Implement immutable prepared draws and the explicit prepared-frame boundary
   over those stable payload handles.
5. Compile primary plan nodes and introduce shared identity primitives.
6. Consolidate recorded artifacts and worker arenas.
7. Add typed eligibility and remove misleading configuration.
8. Implement command-buffer-local image journals and then unlock OpenXR eye
   recording.
9. Add dependency indexes only if profiling justifies them.
10. Expand secondary operation families one at a time, then complete the full
    acceptance and hardware performance matrix.

The binding/data phase has the largest proven CPU payoff and must precede
further worker expansion. The image-journal phase has the highest correctness
risk. The dependency-index and broader-operation phases have the weakest
guaranteed payoff and therefore remain explicitly measurement-gated.

# Vulkan Render Query System Upgrade TODO

Last Updated: 2026-07-13
Owner: Rendering
Status: Planned; implementation not started
Target Branch: `rendering-vulkan-query-system-upgrade`

## Related Work And Ownership

Related local documents:

- [CPU Async Hardware Query Occlusion TODO](../COMPLETED/cpu-async-hardware-query-occlusion-todo.md)
- [Vulkan Core Hardening And Device Loss TODO](vulkan-core-hardening-and-device-loss-todo.md)
- [Vulkan Dynamic Rendering Migration TODO](vulkan-dynamic-rendering-migration-todo.md)
- [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)

Primary specification references:

- [Vulkan Queries chapter](https://docs.vulkan.org/spec/latest/chapters/queries.html)
- [Vulkan physical-device features](https://docs.vulkan.org/spec/latest/chapters/features.html)
- [VkQueryPoolCreateInfo reference](https://docs.vulkan.org/refpages/latest/refpages/source/VkQueryPoolCreateInfo.html)
- [vkCmdWriteTimestamp2 reference](https://docs.vulkan.org/refpages/latest/refpages/source/vkCmdWriteTimestamp2.html)

This document owns the backend-neutral render-query contract, Vulkan query
capability mapping, result layouts, query-pool allocation, recording operations,
submission-safe lifetime, nonblocking readback, OpenGL parity, and migration of
the existing CPU async hardware-occlusion consumer.

The core-hardening todo continues to own the live Phase 5.2.4b strict-SPS and
desktop correctness gate, including final-image parity, owning-view occlusion
proof, output identity, bounded recovery, synchronization validation, and the
300-frame Monado cohort. Work here must feed that gate and rerun its affected
checks; it must not redefine or weaken them.

## Goal

Replace the current occlusion-shaped `VkRenderQuery` wrapper with a complete,
typed, allocation-conscious query system that supports the engine-relevant
Vulkan query families and reports unsupported specialized families explicitly.
The resulting system must:

- preserve all correctness, stereo ownership, recovery, and telemetry behavior
  established by the CPU async hardware-query occlusion work;
- return exact counts and multi-value results without collapsing them to a
  boolean;
- support point queries, begin/end scopes, indexed queries, and property-write
  queries through the appropriate recording operation;
- remain asynchronous by default and never block a render hot path implicitly;
- keep query pools and slots alive until every referencing submission completes;
- remain safe with cached primary command buffers, dynamic rendering,
  multiview, OpenXR/OpenVR outputs, and renderer resource retirement;
- avoid steady-state heap allocations in query recording, resolution, and
  occlusion scheduling; and
- expose capabilities and failure reasons instead of silently substituting a
  query with different semantics.

## Current Baseline And Known Gaps

- `XRRenderQuery` stores only mutable `CurrentQuery`; it does not describe a
  query, its result layout, pool slot, recording epoch, submission, or pending
  ownership.
- `EQueryTarget` is an OpenGL-shaped enum whose numeric values are OpenGL
  constants. It mixes begin/end targets with the point-in-time `Timestamp`
  operation.
- `VkRenderQuery` owns one native query pool, switches its pool type on demand,
  and uses a one-value result API.
- `TryGetResult` reduces every nonzero value to `1`. This is suitable only for
  boolean occlusion and loses exact samples, timestamps, primitive counts, and
  statistics.
- `TryGetResultAvailable` assumes each query record contains one result followed
  by availability. Transform feedback and pipeline statistics require more
  result values before availability.
- A Vulkan transform-feedback query returns both primitives written and
  primitives needed. The current one-value readback is therefore not a valid
  transform-feedback implementation.
- `SamplesPassed`, `AnySamplesPassed`, and
  `AnySamplesPassedConservative` all map to the same unqualified Vulkan
  occlusion operation. Exact sample counting must use the precise flag and the
  enabled `occlusionQueryPrecise` feature; boolean occlusion must remain on the
  cheaper non-precise path.
- Vulkan has no separate conservative-any-samples query type. The engine may
  preserve the high-level intent, but both boolean targets map to non-precise
  Vulkan occlusion and must not claim a stronger distinction.
- Multiview slot allocation is present for Vulkan occlusion, but result
  reduction is hardcoded to boolean-any. Exact counters need a defined sum or
  per-view policy, while boolean occlusion needs visible-if-any-view behavior.
- `Timestamp` returns raw device ticks today even though the engine enum
  documents nanoseconds. `TimeElapsed` is not implemented on Vulkan.
- `PrimitivesGenerated` is not mapped. An implementation-dependent pipeline
  statistic must not silently replace the deterministic
  `VK_EXT_primitives_generated_query` result.
- Query frame operations, bracket-depth state, command sorting, primary reuse,
  reset preparation, and diagnostics are named and structured around
  occlusion.
- Secondary-command-buffer inheritance currently disables occlusion queries and
  declares no inherited pipeline-statistics mask. A general query scope cannot
  silently cross those secondaries.
- `AsyncOcclusionQueryManager` pools raw `XRRenderQuery` objects and resolves a
  backend-specific boolean. It has no result ticket proving which recording and
  submission produced the value.
- The ongoing core-hardening occlusion remediation additionally depends on full
  `OcclusionViewKey` ownership, stable `OutputId`, command-set invalidation,
  coverage proof, forced-visible recovery intervals, and per-output telemetry.
  The query upgrade must retain those active contracts.

## Non-Negotiable Occlusion Invariants

Every phase that changes query ownership, recording, reset, or readback must
keep these invariants passing:

- Query availability is checked without waiting before a result is consumed.
- A pending query slot is never reset, repurposed, or released to another
  consumer.
- Predicted-visible opaque geometry establishes complete pass depth before
  depth-only AABB proxy queries are issued.
- Proxy draws write neither color nor persistent depth.
- Query begin, proxy draw, and query end retain submission order through frame
  operation sorting and command-chain lowering.
- Missing ownership, missing results, stale epochs, camera cuts, extent changes,
  command-set changes, and pipeline recreation fail visible for the affected
  owning view and schedule bounded reprobe/recovery.
- Query state and budgets remain keyed by the complete `OcclusionViewKey`,
  including stable pipeline/output identity and resource generation.
- Desktop/editor, sequential eyes, mirror/capture outputs, and true SPS do not
  consume or reuse one another's query results or budgets.
- Shared stereo and multiview visibility is conservative: a nonzero result in
  any required view keeps the mesh visible; a mesh may be omitted only with
  valid occlusion proof for every required view.
- OpenXR predicted-to-late pose changes reduce confidence or increase recovery
  priority; normal HMD motion does not become a global reset.
- `GpuIndirectZeroReadback` and other zero-readback submission modes do not
  acquire a new dependency on current-frame CPU query results.
- All forced-visible and unsupported states remain observable through
  per-view/per-output telemetry.

## Target Query Model

The exact names may change during implementation, but the architecture must
separate immutable query description, one recorded use, and result storage.

```csharp
public readonly record struct RenderQueryDescriptor(
    ERenderQueryKind Kind,
    EOcclusionResultMode OcclusionMode,
    ERenderPipelineStatistics Statistics,
    uint StreamIndex);

public readonly record struct RenderQueryTicket(
    ulong Epoch,
    uint PoolIdentity,
    uint FirstQuery,
    uint QueryCount,
    ulong SubmissionValue);
```

The public read API should accept caller-provided storage and return an explicit
status such as `Ready`, `NotReady`, `Unsupported`, `InvalidState`, or
`DeviceLost`. Typed convenience methods should sit above a raw multi-value read
without allocating.

The descriptor must be immutable while any backend object, recorded command
buffer, pending ticket, or result refers to it. A native pool must never be
destroyed and recreated merely because a pooled generic query object is asked
to represent another query family.

## Required Query Coverage

| Engine operation | Vulkan representation | Required result contract |
|---|---|---|
| Boolean occlusion | `QueryType.Occlusion`, no precise flag | OR/nonzero across active view slots |
| Exact samples passed | `QueryType.Occlusion` plus precise flag | Sum or explicitly expose view-slot counts |
| Timestamp | Timestamp pool plus `vkCmdWriteTimestamp2` where available | Convert valid device ticks using `timestampPeriod` |
| Portable elapsed time | Two timestamp slots | Wrap-safe delta converted to nanoseconds |
| Pipeline statistics | `QueryType.PipelineStatistics` plus nonzero statistic mask | One typed value per enabled bit in Vulkan bit order |
| Transform feedback | `QueryType.TransformFeedbackStreamExt`, indexed when needed | Primitives written and primitives needed |
| Primitives generated | `QueryType.PrimitivesGeneratedExt`, indexed when needed | Deterministic generated primitive count |
| Mesh primitives generated | `QueryType.MeshPrimitivesGeneratedExt` | Generated mesh primitive count |
| Acceleration-structure properties | Matching KHR/NV property query pool and write command | Typed compacted/serialization/size result |
| Micromap properties | Matching EXT property query pool and write command | Typed compacted/serialization size result |
| Performance counters | KHR or vendor provider with required profiling lock | Provider-defined typed result set |
| Video/result status | Matching video/status provider | Typed status/feedback layout and explicit video ownership |

QCOM elapsed-timer queries may be exposed as an optional native provider, but
portable engine `TimeElapsed` must work through paired timestamps wherever the
selected queue supports timestamps. Specialized property, performance, and
video operations must share pool/lifetime/readback infrastructure without being
forced through an invalid generic `BeginQuery` call.

## Phase 0 - Branch, Baseline, And Consumer Inventory

- [ ] Create and switch to the dedicated branch
  `rendering-vulkan-query-system-upgrade` before implementation changes.
- [ ] Create one bounded validation root under
  `Build/_AgentValidation/<timestamp>-vulkan-render-query-upgrade/` with
  `logs/`, `reports/`, `mcp-captures/`, and `mcp-output/` as needed.
- [ ] Record the exact working source baseline and preserve unrelated local
  changes; do not overwrite the active Vulkan/core-hardening or physics work.
- [ ] Inventory every `XRRenderQuery`, `EQueryTarget`, `GLRenderQuery`, and
  `VkRenderQuery` producer and consumer, including:
  - [ ] `AsyncOcclusionQueryManager` and `CpuRenderOcclusionCoordinator`;
  - [ ] `VPRC_OcclusionQuery` and CPU proxy rendering;
  - [ ] `BvhGpuProfiler` and `RenderPipelineGpuProfiler`;
  - [ ] Vulkan frame timing and dense timestamp instrumentation;
  - [ ] transform feedback and mesh-renderer probes; and
  - [ ] command-buffer cache/reuse and query-generation fingerprints.
- [ ] Record which existing timestamp systems should migrate to the shared
  infrastructure and which should remain specialized renderer-owned arenas.
- [ ] Capture current Vulkan device/queue query features, enabled features,
  loaded extensions, timestamp period, timestamp valid bits, and transform-
  feedback query support in a machine-readable baseline report.
- [ ] Run the focused existing occlusion and Vulkan query-related tests before
  changing contracts; record all pre-existing failures separately.
- [ ] Capture an occlusion-enabled desktop baseline and, when available, a true
  SPS/OpenXR baseline with per-output submissions, resolutions, forced-visible
  reasons, recovery age, coverage proof, and cull counts.
- [ ] Cross-link the run from the active Phase 5.2.4b investigation rather than
  duplicating its final live-acceptance ownership here.

Acceptance criteria:

- [ ] Every current query consumer has a named migration or explicit retention
  decision.
- [ ] Existing CPU query occlusion behavior and the open Phase 5.2.4b gaps are
  captured before structural changes begin.
- [ ] No implementation task proceeds with an unknown pre-existing test or
  validation failure.

## Phase 1 - Define Backend-Neutral Semantics And Results

- [ ] Replace the OpenGL-numbered `EQueryTarget` contract with backend-neutral
  query kinds and descriptors. Breaking API changes are allowed for a cleaner
  v1 architecture.
- [ ] Separate begin/end query scopes, point timestamp writes, property writes,
  result copies, reset operations, and host reads in the API.
- [ ] Make `XRRenderQuery` configuration immutable after generation, or replace
  it with an immutable descriptor plus a lightweight handle.
- [ ] Remove public mutable recording state such as `CurrentQuery`; keep backend
  recording state in validated command-recording context.
- [ ] Define result-layout metadata containing values per query, query count,
  view-slot count, availability position, integer width, semantic field names,
  and aggregation policy.
- [ ] Provide typed result structures for occlusion, timestamps, pipeline
  statistics, transform feedback, primitives generated, and specialized
  property queries.
- [ ] Provide an allocation-free raw API based on `Span<ulong>` or a more
  appropriate typed span plus `out` result metadata.
- [ ] Define explicit read status and error reporting. `NotReady` must not be
  indistinguishable from unsupported, invalid state, device loss, or an API
  error.
- [ ] Define whether typed timestamps are returned as raw ticks, nanoseconds, or
  both. Public engine time APIs must use nanoseconds consistently across OpenGL
  and Vulkan.
- [ ] Define an exact multiview reduction policy for every supported query
  family. Do not reuse boolean-any reduction for exact counters.
- [ ] Update the OpenGL backend to implement the same typed semantics without
  relying on a default enum fallback.
- [ ] Remove or replace `Task.Run` busy-wait helpers in query resolution; async
  completion must be integrated with render/frame polling or an explicit
  caller-owned wait outside hot paths.

Acceptance criteria:

- [ ] A descriptor completely determines native pool compatibility, recording
  operation, required capabilities, result layout, and aggregation policy.
- [ ] A caller cannot accidentally request a scalar boolean from a multi-value
  query without using an explicit typed projection.
- [ ] OpenGL and Vulkan expose matching engine semantics even when their native
  representations differ.

## Phase 2 - Vulkan Capability And Descriptor Mapping

- [ ] Add a centralized Vulkan query-capability snapshot covering:
  - [ ] `occlusionQueryPrecise`;
  - [ ] `pipelineStatisticsQuery`;
  - [ ] queue-family timestamp valid bits and supported stage masks;
  - [ ] host query reset;
  - [ ] transform-feedback queries and indexed stream limits;
  - [ ] primitives-generated queries;
  - [ ] mesh-shader queries;
  - [ ] acceleration-structure and micromap property-query extensions;
  - [ ] performance-query providers and profiling-lock requirements; and
  - [ ] video/result-status providers when the engine enables video queues.
- [ ] Distinguish feature advertised, feature selected for device creation,
  extension loaded, command loaded, and queue-family compatibility.
- [ ] Update logical-device feature enabling where a supported query family is
  part of the production engine contract. Do not report a query supported when
  its required feature was not enabled.
- [ ] Implement one descriptor-to-Vulkan mapping that returns query type,
  create-info statistic mask/pNext requirements, result layout, recording
  provider, control flags, and a precise unsupported reason.
- [ ] Map exact sample counts to precise occlusion only when the feature is
  enabled. Keep boolean occlusion non-precise.
- [ ] Map both high-level boolean occlusion intents to the same Vulkan boolean
  query semantics without claiming a native conservative variant.
- [ ] Reject unsupported `PrimitivesGenerated` explicitly; do not silently map
  it to clipping invocations or another approximate statistic.
- [ ] Validate statistic masks are nonzero, feature-compatible, and valid for
  the selected graphics/compute queue and pipelines.
- [ ] Surface the query capability matrix and failure reasons in Vulkan startup
  diagnostics and renderer capability reporting.

Acceptance criteria:

- [ ] Every descriptor either produces a fully specified valid Vulkan plan or
  one actionable unsupported reason before command recording.
- [ ] No Vulkan validation error can be produced merely by asking whether a
  descriptor is supported.
- [ ] Unsupported accelerated paths remain visible through diagnostics; no
  silent CPU or approximate-query fallback is introduced.

## Phase 3 - Query Pool Arenas, Slots, And Submission Tickets

- [ ] Replace one-native-pool-per-`XRRenderQuery` ownership with renderer-owned
  pool arenas keyed by native query type, pipeline-statistics mask, provider
  create-info compatibility, queue family, and result layout.
- [ ] Size pools in bounded chunks and suballocate query slots/ranges without
  steady-state managed allocations.
- [ ] Allocate contiguous ranges for multiview and multi-point operations such
  as elapsed timing.
- [ ] Associate every allocation with an epoch and a submission/fence/timeline
  ticket so an old result cannot be mistaken for a later recording.
- [ ] Track `Allocated`, `ResetRecorded`, `Recording`, `Ended`, `Submitted`,
  `Available`, and `Recyclable` states or equivalent validated transitions.
- [ ] Keep native pools alive through every command buffer and submission that
  references them; retirement must use the existing Vulkan resource lifetime
  tracker.
- [ ] Reset only unavailable-to-available slots that are no longer pending.
  Prefer queue-ordered command resets during fresh recording.
- [ ] Preserve safe cached-command-buffer replay. Host reset is allowed only
  after the prior result/submission is complete and while externally
  synchronized through the renderer's resource-mutation contract.
- [ ] Prevent pool type/mask mutation while any handle, command buffer, or
  result ticket refers to the pool.
- [ ] Handle pool exhaustion explicitly: grow within a bounded policy, defer
  optional queries, or report budget exhaustion. Never overwrite a live slot.
- [ ] Add shutdown/device-loss cleanup that retires pools safely and invalidates
  outstanding tickets with a typed status.
- [ ] Record arena capacity, high-water mark, pending slots, resets, growth,
  exhaustion, and retired pools in diagnostics.

Acceptance criteria:

- [ ] Hundreds of per-frame occlusion queries do not allocate hundreds of
  native query pools.
- [ ] Reusing a generic engine handle cannot destroy or reconfigure a pool with
  pending GPU work.
- [ ] Cached primary replay and fresh recording both establish a valid reset
  epoch without a global device wait.
- [ ] Pool and slot counts remain bounded with no positive steady-state drift.

## Phase 4 - General Query Recording And Command Scheduling

- [ ] Generalize `QueryOp`, enqueue methods, debug labels, structural hashes,
  query generations, and command-buffer reuse preparation beyond occlusion.
- [ ] Represent `Begin`, `End`, `WriteTimestamp`, `WriteProperties`,
  `CopyResults`, and any provider-specific operation explicitly.
- [ ] Carry the descriptor and allocated ticket/range through lowering; an end
  operation must not hardcode a query target.
- [ ] Validate begin/end pairing in the same command buffer and valid rendering
  scope. Reject mismatched descriptors, indices, streams, epochs, and command
  buffers with diagnostics.
- [ ] Treat query scopes as ordering barriers so draw/dispatch sorting cannot
  move work into or out of a scope.
- [ ] Generalize bracket tracking without introducing allocations or relying on
  one global active query variable.
- [ ] Define and test nesting policy according to Vulkan restrictions. Track
  active queries by compatible native type/index rather than silently ending an
  unrelated active query.
- [ ] Ensure resets occur outside prohibited render scopes and before the first
  use of every allocated range.
- [ ] Initially keep query-scoped work in the primary command buffer unless a
  secondary path has complete inherited-query support.
- [ ] For any secondary path that is allowed inside an active query, enable and
  validate `inheritedQueries`, `OcclusionQueryEnable`, `QueryFlags`, and the
  exact inherited pipeline-statistics mask.
- [ ] Make command-chain eligibility query-aware. A range that cannot inherit
  the active query must be lowered inline rather than silently losing counts.
- [ ] Include query descriptor, pool identity, slot epoch, view mask, and
  recording operation in command-buffer cache signatures where required.
- [ ] Keep recording and lowering allocation-free in steady state.

Acceptance criteria:

- [ ] Query scopes retain their intended draw/dispatch contents after frame-op
  sorting, render-pass transitions, compute/blit interruptions, and command-
  chain lowering.
- [ ] Primary reuse never replays a query against an unreset or pending slot.
- [ ] Secondary command buffers either inherit a query correctly or are
  deterministically excluded from that query scope.
- [ ] Validation-layer runs contain no begin/end, reset, pool, index, render-
  scope, or inheritance VUIDs.

## Phase 5 - Migrate And Revalidate CPU Async Hardware Occlusion

- [ ] Give boolean occlusion a stable immutable descriptor and typed
  `TryGetAnySamplesPassed` API. Do not route it through exact sample-count
  reduction.
- [ ] Migrate `AsyncOcclusionQueryManager` from raw object reuse to descriptor-
  compatible handles and submission tickets.
- [ ] Store the pending ticket/epoch in each coordinator query state so late
  results cannot update a newer command set, output, resource generation, or
  recovery interval.
- [ ] Preserve the existing result-available-first, never-wait resolution path
  for both OpenGL and Vulkan.
- [ ] Preserve query budgeting, visible-demotion versus occluded-recovery
  priority, pending-age handling, hierarchy grouping, and no-reuse-before-
  resolution behavior.
- [ ] Preserve the two-stage render contract: normal opaque depth first, then
  ordered depth-only/no-color-write proxy brackets.
- [ ] Preserve complete `OcclusionViewKey` ownership, including `OutputId`, POV,
  scope, coverage masks, declared view count, resource generation, and command-
  set identity.
- [ ] Preserve command-set-change, extent-change, camera-cut, stale-result,
  missing-ownership, pipeline-recreation, and discarded-pending-result behavior
  as bounded fail-visible recovery.
- [ ] Preserve recovery start/completion/age telemetry and ensure a migrated
  query result can close only the recovery interval that owns its ticket.
- [ ] For sequential-eye modes, keep per-eye tickets/results isolated and
  OR-combine only at the existing stereo ownership boundary.
- [ ] For true SPS/multiview, allocate the full active-view range and reduce a
  boolean query to visible when any occupied slot is nonzero. Cull only when
  all required views have valid zero-sample proof.
- [ ] Do not assume per-view query-result distribution is stable across Vulkan
  implementations; use only the specification-guaranteed aggregate needed for
  boolean visibility.
- [ ] Keep desktop/editor, OpenXR/OpenVR eyes, full-independent mirrors,
  captures, and SPS query budgets and epochs independent.
- [ ] Preserve conservative-visible behavior and telemetry for unsupported
  stereo modes or unavailable backend capabilities.
- [ ] Update query-related source-contract tests to the new architecture.
  Prefer deterministic state/result-layout tests over brittle source tokens.
- [ ] Keep existing user settings and ImGui occlusion diagnostics compatible,
  updating labels/docs only if semantics or names intentionally change.
- [ ] Re-run Phase 5.2.4b occlusion-off ground-truth comparison after migration:
  final-image and known-visible-sentinel parity, per-output candidate subsets,
  owning-view proof for every omitted mesh, and both-eye proof for SPS.

Acceptance criteria:

- [ ] The migrated desktop and VR/SPS occlusion paths produce no additional
  forced-visible, stale, pending, or recovery-age regressions against baseline.
- [ ] A result from one output, eye, command set, resource generation, or epoch
  cannot cull another.
- [ ] A camera cut or invalidation restores visibility immediately and normal
  queried state returns within the declared maximum recovery age.
- [ ] Every omitted SPS candidate is proven occluded for both views; visibility
  in either eye keeps the mesh visible.
- [ ] Query submissions remain budgeted and nonblocking, and zero-readback mesh
  submission modes remain independent of CPU query resolution.

## Phase 6 - Exact Occlusion, Timestamp, And Elapsed Queries

- [ ] Implement exact `SamplesPassed` with the precise control flag and explicit
  capability rejection when precise occlusion was not enabled.
- [ ] Return exact 64-bit sample counts without boolean collapse; sum occupied
  multiview slots only when the typed API requests aggregate samples.
- [ ] Implement point timestamps with synchronization2 stage masks where
  supported and validate stage/queue compatibility.
- [ ] Capture timestamp period and queue-family valid bits in immutable device
  capability state.
- [ ] Convert public timestamp results to nanoseconds consistently with
  OpenGL, retaining raw ticks in diagnostic/low-level results when useful.
- [ ] Implement portable `TimeElapsed` with two allocated timestamp slots,
  explicit start/end stages, valid-bit masking, wrap-safe subtraction, and
  conversion to nanoseconds.
- [ ] Decide whether existing Vulkan frame timing and pipeline profiling should
  share the new pool arena or remain specialized arenas using the shared result
  layout/lifetime helpers. Avoid a risky forced consolidation without measured
  benefit.
- [ ] Migrate `BvhGpuProfiler` to backend-neutral timestamp services so Vulkan
  can collect the same metrics where enabled.
- [ ] Add nonblocking timestamp-pair resolution and bounded pending-scope
  cleanup on shutdown/device loss.

Acceptance criteria:

- [ ] Boolean and precise occlusion have distinct validated semantics and
  capabilities.
- [ ] Timestamp and elapsed results match controlled CPU/GPU ordering tests
  within documented tolerance and never expose unconverted Vulkan ticks as
  nanoseconds.
- [ ] Timestamp recording and resolution add no steady-state hot-path heap
  allocations.

## Phase 7 - Pipeline, Transform-Feedback, Primitive, And Mesh Queries

- [ ] Add a flags enum covering every Vulkan pipeline-statistics counter the
  engine's enabled Vulkan version/extensions expose, including task/mesh shader
  counters when supported.
- [ ] Create pipeline-statistics pools with a nonzero exact mask and compute
  values-per-query from that mask.
- [ ] Decode result values in Vulkan-defined bit order into a typed result
  without boxing, reflection, LINQ, or per-read dictionaries.
- [ ] Validate graphics versus compute applicability and document undefined or
  implementation-dependent counters.
- [ ] Complete transform-feedback query support with indexed stream selection,
  two-value result layout, primitives-written and primitives-needed fields, and
  overflow interpretation.
- [ ] Implement deterministic primitives-generated queries through
  `VK_EXT_primitives_generated_query`, including indexed streams and feature
  checks.
- [ ] Implement mesh-primitives-generated queries when mesh-shader query support
  is enabled.
- [ ] Add GPU result-copy support where a buffer consumer can avoid CPU
  readback, including correct destination alignment, stride, availability, and
  synchronization.

Acceptance criteria:

- [ ] Every multi-value result uses the exact native layout, data size, stride,
  and availability offset.
- [ ] Transform-feedback results report both values and pass a deliberately
  undersized-buffer overflow test.
- [ ] Pipeline-statistics masks with one, several, and all supported counters
  decode deterministically.
- [ ] Unsupported primitive/mesh query features report the exact missing
  feature or extension and record no invalid commands.

## Phase 8 - Specialized Vulkan Query Providers

- [ ] Add a provider interface for query families whose recording operation or
  result representation does not fit generic begin/end scalar queries.
- [ ] Add acceleration-structure compacted-size, serialization-size, and
  maintenance-size providers when the engine's ray-tracing backend needs them.
- [ ] Add micromap compacted/serialization-size providers when opacity
  micromaps are enabled.
- [ ] Add KHR performance-query support only with counter enumeration, typed
  storage, pass-count handling, profiling-lock lifetime, and explicit
  performance-impact diagnostics.
- [ ] Keep vendor performance providers isolated behind their own capability
  and decoder contracts.
- [ ] Add result-status and video-feedback providers only alongside a real
  Vulkan video queue/profile owner; do not create context-free video pools.
- [ ] Optionally expose native QCOM elapsed timers behind a provider while
  retaining paired timestamps as the portable engine contract.
- [ ] For every Vulkan query type not enabled by an engine subsystem, publish an
  explicit `Unsupported`/`SubsystemUnavailable` capability entry rather than a
  fake or approximate implementation.

Acceptance criteria:

- [ ] Specialized query support reuses common pool retirement, ticketing,
  availability, and result-copy infrastructure without distorting the generic
  render-query API.
- [ ] No optional extension is loaded, enabled, or exercised merely to make a
  capability table appear complete.
- [ ] Performance and video queries cannot be recorded without their required
  external ownership/lock/profile state.

## Phase 9 - Diagnostics, Performance, And Cleanup

- [ ] Add query telemetry for allocations, recordings by kind, skipped/
  unsupported queries, pending ages, ready/not-ready reads, waits, copied and
  host-read bytes, arena high-water marks, reset epochs, pool growth, and
  retirement.
- [ ] Count any explicit waiting read and identify its caller; production render
  hot paths must record zero waits.
- [ ] Add throttled diagnostics containing descriptor, pool/range, epoch,
  command buffer, queue family, output/view owner when available, and exact
  Vulkan result/error.
- [ ] Keep diagnostic string construction behind enabled/throttled paths.
- [ ] Profile query recording and result polling for managed allocations.
- [ ] Remove LINQ, captured closures, boxing, temporary arrays, and per-query
  native pool creation from steady-state paths.
- [ ] Remove superseded `CurrentQuery`, target-mapping fallbacks, scalar-only
  read helpers, per-object pool lifetime, and occlusion-specific generic query
  naming after all consumers migrate.
- [ ] Retain narrowly named occlusion coordinator APIs where they describe
  occlusion policy rather than generic query mechanics.
- [ ] Update XML documentation and architecture docs for query semantics,
  capabilities, lifecycle, threading, and nonblocking rules.
- [ ] Update user-facing settings/environment/docs only when names or behavior
  change; regenerate generated reports if any MCP or settings contract changes.

Acceptance criteria:

- [ ] Query recording and normal nonblocking polling allocate zero managed heap
  memory after warmup.
- [ ] Diagnostics explain every unsupported, invalid, exhausted, stale, or
  waited query without log spam.
- [ ] No legacy scalar/boolean assumption remains in a general query path.

## Phase 10 - Automated And Live Validation

- [ ] Add deterministic unit tests for descriptor mapping, feature rejection,
  values-per-query calculation, availability offsets, 32/64-bit result sizes,
  statistic bit ordering, multiview range sizing, aggregation, timestamp valid-
  bit wrap, and state transitions.
- [ ] Add OpenGL/Vulkan semantic parity tests for boolean occlusion, exact
  samples when supported, timestamps, elapsed time, primitives, and transform
  feedback.
- [ ] Add Vulkan tests for pool exhaustion/growth, pending-slot non-reuse,
  epoch mismatch, cached primary replay, host-reset synchronization, fresh
  recording reset, and device-loss invalidation.
- [ ] Add command scheduling tests proving query brackets survive sorting,
  target/pass transitions, command-chain lowering, and secondary eligibility.
- [ ] Keep and update focused occlusion tests covering motion tiers, command-set
  change, output identity, recovery intervals, stale pending results, query
  budgets, coverage proof, and visible-if-either-eye behavior.
- [ ] Run focused validation:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanRenderQuery|FullyQualifiedName~VulkanCpuDirectOcclusion|FullyQualifiedName~VulkanCommandChainDataModel|FullyQualifiedName~CpuRenderOcclusionCoordinator|FullyQualifiedName~CpuOcclusion" --no-restore /p:UseSharedCompilation=false
  dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly
  ```

- [ ] Run the broader Vulkan test filter after focused tests are clean:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Vulkan --no-restore /p:UseSharedCompilation=false
  ```

- [ ] Create or update an investigation under
  `docs/work/investigations/rendering/` for live query-system validation.
- [ ] Iterate on the editor with Unit Testing World, MCP, per-run logs, and
  captures from more than one camera position for any visually observable
  occlusion regression.
- [ ] Validate desktop mono CPU-query occlusion through still camera, slow
  motion, fast motion, camera cut, command-set mutation, resize, and pipeline
  recreation.
- [ ] Validate sequential stereo, OpenVR two-pass, OpenVR SPS, and OpenXR true
  SPS when their runtimes/hardware are available.
- [ ] Re-run the core-hardening Phase 5.2.4b strict-SPS occlusion evidence and
  final-image parity requirements; reference its artifacts rather than copying
  them into this todo.
- [ ] Run Vulkan validation and synchronization validation with zero new
  query-, command-buffer-, render-scope-, reset-, pool-lifetime-, or inheritance-
  related messages.
- [ ] Inspect captured desktop and both-eye images. Tool success and nonzero
  telemetry are not sufficient visual evidence.
- [ ] Run `dotnet restore`, `dotnet build XRENGINE.slnx`, and the full unit-test
  project before closeout; separate unrelated failures in the report.

Acceptance criteria:

- [ ] Every supported query family has deterministic layout/mapping tests and
  at least one backend execution test where hardware/API availability permits.
- [ ] Desktop and all available VR modes retain occlusion correctness, bounded
  recovery, per-output ownership, nonblocking behavior, and meaningful culling
  after warmup.
- [ ] The full query upgrade introduces no compiler warnings, Vulkan validation
  errors, device loss, global waits, or steady-state resource growth.
- [ ] Validation artifacts live under the bounded task run root and durable
  findings are recorded in the investigation or closeout doc.

## Definition Of Done

- [ ] The backend-neutral query API distinguishes descriptors, recorded tickets,
  result layouts, and typed values.
- [ ] Vulkan query pools are arena-managed, submission-safe, bounded, and
  nonblocking by default.
- [ ] Boolean and exact occlusion, timestamps, elapsed time, pipeline
  statistics, transform feedback, primitives generated, and mesh primitive
  queries work when their capabilities are enabled.
- [ ] Specialized Vulkan query families have correct providers or explicit
  capability reasons; none are silently approximated.
- [ ] Existing CPU async hardware occlusion behavior, settings, telemetry,
  output ownership, recovery, and stereo proof contracts pass after migration.
- [ ] OpenGL and Vulkan expose consistent engine-level query semantics.
- [ ] Focused, broader Vulkan, full build/test, validation-layer, desktop, and
  available VR evidence are recorded.
- [ ] Docs describe the final API and runtime behavior, and no required behavior
  depends on ignored validation artifacts.

## Final Task

- [ ] After implementation, validation, documentation, and the affected core-
  hardening acceptance gates are complete, merge
  `rendering-vulkan-query-system-upgrade` back into `main` with an intentional
  reviewed diff.

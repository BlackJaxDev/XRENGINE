# Next-Frame Preparation And Collect-Visible Handoff

Last Updated: 2026-08-17
Status: Package Foundation Complete; Render-Thread Prepared-Cohort Bridge
Validated; Persistent Template/Data Handoff Still Open
Owner: Rendering / Frame Scheduling / Vulkan

## Result

Collect-visible now prepares a bounded, double-buffered
`BackendReadyFramePackage` while the previous frame renders. Swap publishes
the completed package by ownership transfer. The render consumer validates
the package in bounded time before executing the pipeline. Published package
passes are the authoritative render-command lookup source, and Vulkan uses the
package's pass metadata as its resource-planning input.

The missing producer-side binding/data handoff and its validation are retained
in the [combined workstreams 03-05 gate](../../testing/rendering/03-05-optimization-validation-todo.md#workstream-04-completion-and-validation).

## 2026-08-17 render-thread prepared-cohort bridge

Vulkan now consumes an exact stable mesh cohort without rebuilding the
authoring-operation layer on every hit:

- `BackendReadyFramePackage` remains renderer-neutral and continues to own the
  pure producer-side visibility, order, selection, and revision data.
- The render thread always drains the current raw request cohort, so transforms,
  previous transforms, view/shadow relevance, instance counts, billboarding,
  and auto-uniform publication remain current.
- A bounded render-thread cache retains only exact, fully materialized Vulkan
  recipes whose program, material, pass, context, viewport/scissor, fixed state,
  and owner generations remain valid. Unsafe entries are retained only as
  structural holes and are materialized normally on a hit.
- A current-frame `VulkanPreparedMeshIngress` transaction appends successful
  hits directly to the numeric `FrameOperationStream`; it bypasses
  `MeshDrawOp` rental, queueing/draining, and object-to-stream conversion.
- Swapchain context coalescing runs before ingress finalization. Final pass
  normalization and descriptor/attachment resource-use lowering therefore use
  the context that recording will actually consume; no lowered resource list is
  replayed across frames.
- Immutable program-binding artifacts are candidates only. Every hit reacquires
  the same artifact from the exact captured program's bounded owner-generation
  cache. A renderer's mutable active-program slot is deliberately not used as
  the authority for multi-material/program meshes.
- Direct ingress is desktop-primary only. Explicit/OpenXR production continues
  through the existing materialize/enqueue path until it has its own partition,
  target, and dynamic-overlay contract.

The final dense Sponza Debug/MCP run reused 566 of 625 operations per stable hit
and materialized 59 safety holes. A 48-hit observation produced 27,168 reuses,
2,832 holes, zero rebuilds/full materializations, zero skipped draws, zero
dropped frame operations, and zero reported Vulkan errors. Camera changes each
produced one expected cold build and then returned to stable hits with correct
camera-dependent output. This performance session did not enable the Khronos
validation layer; validation-enabled acceptance remains open in the combined
03-05 gate.

This bridge closes backend object-lifecycle duplication for exact stable views,
but it does not complete the producer-side handoff. Its whole-cohort key is
intentionally conservative and invalidates on visible membership/order changes.
The remaining target is a persistent per-mesh/submesh/material/pass draw
template plus compact current-frame instance records and stable numeric bins,
so camera motion reuses unchanged templates instead of rebuilding the entire
cohort.

## Render-Thread Work Classification

| Stage | Class | Ownership after workstream 04 |
| --- | --- | --- |
| Scene traversal and visibility | Snapshot-dependent scene preparation | Collect-visible |
| Stable pass sorting | Pure command preparation | Collect-visible package producer |
| Pass membership/counts | Pure command preparation | Collect-visible package producer |
| Mesh/material/render-options selection | Pure dependency preparation | Collect-visible package producer |
| Command, pass, and dependency signatures | Pure dependency preparation | Collect-visible package producer |
| Render-graph pass metadata input | Pure resource-plan input | Collect-visible package producer |
| Package publication | Externally synchronized ownership transfer | Collect/swap after previous render releases ownership |
| Package validation | Bounded generation comparison | Render consumer |
| Vulkan resource-plan interpretation | Backend-thread-affine | Render thread, consuming prepared metadata |
| Backend handle resolution | Backend-thread-affine | Render thread |
| Command encoding | Backend-thread-affine | Render thread; workstream 05 |
| Queue submission and present | Backend-thread-affine/external | Render thread |
| Collect waiting for render | Mode A backpressure | Existing generation gate |
| Render waiting for collect | Mode B starvation | Existing generation gate |

## Package Contract

Identity captures:

- collect frame and generation;
- pipeline command generation;
- render-resource and descriptor generations;
- render-graph metadata revision;
- display and internal viewport extents;
- monotonic package generation.

Prepared content captures:

- sorted pass membership;
- total and per-pass command/mesh counts;
- stable command-set and dependency signatures;
- selected mesh, material, and rendering-parameter references;
- material binding-layout, shader-state, and Uber-state revisions plus prepared
  GPU-eligibility decisions;
- complete render-pass metadata for resource planning;
- shadow-caster membership/content signature.

The package deliberately contains no mutable Vulkan handles. Vulkan device,
descriptor-set, image, buffer, pipeline, command-buffer, and queue ownership
remain on their legal backend thread.

## Ownership And Lifetime

`RenderCommandCollection` owns two reusable package instances:

1. the producer package references only the updating command buffers;
2. the consumer package references only the rendering command buffers;
3. collect-visible may prepare the producer while render consumes the other;
4. after render releases ownership, swap exchanges command buffers and package
   instances together;
5. publication changes `Prepared` to `Published`;
6. only the now-unowned producer package is reset and reused;
7. viewport destruction cancels both packages after callback subscriptions are
   removed.

The producer cannot overwrite an in-flight consumer package. Lookahead remains
one frame and storage remains double-buffered, preserving the predecessor's
latency contract.

## Invalidations And Policy

The render consumer rejects a package with a countable reason when any of
these differ:

- consumed collect generation;
- pipeline command generation;
- resource generation;
- descriptor generation;
- display or internal viewport extent.

The existing `BlockUntilFresh` generation policy remains the default.
Authorized previous-visibility reuse remains explicit in the generation gate;
package age is reported independently. A missing or mismatched package skips
the command chain visibly instead of traversing live scene/material state or
silently consuming partial/stale data.

Mutation after explicit preparation increments the updating revision. Swap
detects the revision mismatch and performs a counted late preparation before
publication, preserving correctness for manual/custom collection paths. The
normal automatic path prepares before collect-visible enters its wait for the
previous render. Screen-space UI prepares its independent command collection
at the end of the same viewport collect callback, so this fallback is not the
steady-state path.

## Allocation And Telemetry

The package uses retained power-of-two arrays, concrete dictionary
enumerators, index-based command traversal, and allocation-free insertion
sorting for the small pass set. It contains no LINQ, captured closures, string
construction, or interface-enumerator boxing in its producer/publication hot
path. A bounded retained cache keyed by each command's stable query ID reuses
mesh/material selections until the selected references, instance/CPU-routing
state, material layout/shader revisions, or render options change. Render-side
GPU eligibility and excluded-mesh routing consume that prepared selection
instead of re-reading live material state.

Frame lifecycle telemetry now exposes:

- `frame_package_production_ms`;
- `frame_package_publication_ms`;
- `frame_package_validation_ms`;
- `frame_package_consumption_ms`;
- prepared, published, consumed, late-prepared, and rejected counts;
- package generation age;
- the existing collect-wait/render-wait reason and duration counters.

The counters are available in retained profiler frame streams and MCP render
profiler statistics.

## Targeted Validation

- `XREngine.Runtime.Rendering` Debug build: succeeded; only the pre-existing
  Magick.NET vulnerability warning was reported.
- `XREngine.Runtime.Rendering.Vulkan` Debug build: succeeded; only existing
  dependency warnings were reported.
- Release editor build: succeeded with zero compiler errors; warnings were the
  pre-existing Magick.NET advisory emitted by referencing projects.
- Isolated package smoke:
  `Build/_AgentValidation/renderer-root-trace/workstream04/temp-build/`.
  It proved sorted publication, double-buffer ownership, monotonic package
  generation, next-frame membership, and zero managed allocation across 128
  warm producer/publication cycles.
- Final isolated live editor session `ws04-frame-package-cache-final`: MCP reported
  `BlockUntilFresh`, package generation age 1 (inside the declared one-frame
  bound), 13,371 prepared and 13,371 published packages, zero late-prepared
  packages, zero rejected packages, visible
  production/publication/validation/consumption timings, zero frame-data
  refresh allocation in that snapshot, and zero Vulkan validation errors. The
  session was stopped through its named session manager; logs are under
  `Build/_AgentValidation/mcp-sessions/ws04-frame-package-cache-final/logs/`.
- Focused tests were added under
  `XREngine.UnitTests/Rendering/BackendReadyFramePackageTests.cs`.
  The repository test project is presently blocked before test execution by
  unrelated post-organization compile failures in Vulkan test fixtures
  (`EDesktopFramePhase`, `OpenXrViewResourcePlannerContextKey`, and nint
  `ShouldBe` calls).

## Remaining Completion And Validation

Canonical allocation captures, overlap comparison, static/moving visual
parity, streaming/mutation/resize/pause/resume/failed-submit/shutdown stress,
latency comparison, Vulkan validation, and desktop/RVC cohorts remain open
without waiver in the combined workstreams 03-05 gate.

## 2026-08-01 Rejected producer binding-input handoff attempt

An attempted final handoff added renderer-neutral material/mesh revisions and
typed publisher snapshots to `BackendReadyMeshSelection`, then scoped Vulkan
mesh preparation to the published selection. The focused Release selection
passed 16/16 tests, including the existing zero-allocation package loop.

Live validation rejected the change. Two launches of
`cmd-record-finish-fix2` failed deterministically with native `0xC0000005`
access violations at `vkCmdDraw`/`vkCmdDrawIndexed`. Disabling command-chain
scheduling did not change the failure. A context-disabled bisect still crashed,
which isolated the regression to the added producer data-model/capture slice or
the timing it introduced rather than the selection scope itself. The entire
attempt and its tests were rolled back; no crashing partial implementation is
retained. The producer binding-input checklist item remains open.

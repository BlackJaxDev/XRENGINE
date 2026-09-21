# S08 Gate Record: Index Preparation Before Draw Admission

Date: 2026-09-21
Status: Blocked on measured entry evidence, owning-project builds, lifetime review,
and live validation; implementation proposed in a draft pull request.
Base revision: `cd58220514ae6455af68a9526f2fa32d8d127e4a`
Test clearance: not granted; no regression tests added or modified.

This is the implementation proposal for
[S08 in the stall remediation checklist](../../todo/rendering/vulkan-stall-remediation-todo.md#s08-prepare-indices-before-draw-admission).
It does **not** mark S08 Validated or authorize S09. S07's accepted publication
mechanism remains in place.

## Entry Evidence And Falsifiable Hypothesis

Source inspection at the base revision identifies a possible foreground join:

1. `VkMeshRenderer.cs`'s queued-mesh resource-preparation path calls
   `TryPrepareForDrawEnqueue` with the materialization snapshot's
   `RequiresSynchronousGeometryPreparation` flag.
2. `TryPrepareForDrawEnqueue` forwards that flag to `EnsureBuffers`.
3. `GetIndexBufferForBinding` forwards it to `XRMesh.GetIndexBuffer`.
4. On a cache miss, `GetIndexBuffer` waits on the per-primitive ticket with
   `Completion.Task.GetAwaiter().GetResult()` when that flag is true.

The worker previously called `GetIndices(type)` and read `VertexCount` from the
live mesh. A revision check at publication rejected obsolete results, but did not
make the worker's source topology immutable.

Hypothesis: requesting CPU index preparation before program readiness and using
only nonblocking buffer resolution during admission eliminates this foreground
worker join without publishing incomplete indexed geometry. The existing pending
mesh/cohort mechanism must continue to converge on an exact complete frame.

**No baseline join duration, affected-mesh count, or speedup has been measured in
this environment.** The source path is evidence of a mechanism, not proof that it
explains a material stall. Obtain the conditional S08 entry measurement before
promotion; defer the candidate if the join is not significant or snapshot cost
merely replaces it on the critical path.

## Proposed Change

`XRMesh.Geometry.cs`:

- Requests triangles, lines, and points through the existing per-mesh cache and
  tickets, with no additional preparation queue.
- Copies scalar indices and captures the vertex count on the requesting topology
  owner before scheduling the worker. Each cold stream uses one `int[]`, rather
  than an additional temporary array allocation for each line/triangle. The snapshot
  is retained only by the work item, not by the cached completion ticket.
- Keeps the exact geometry-revision ticket checks before scheduling and at
  publication. Invalidated queued work exits before allocating a render object;
  invalidation during a running build is rejected at the publication boundary.
- Records snapshot or scheduling errors as terminal ticket failures. The existing
  Vulkan binding resolver still surfaces failures; they are not a nonindexed
  fallback or a fresh automatic retry on every frame.
- Rechecks index presence after synchronous invalidation retries, so removing all
  topology can return the nonindexed result. Explicit off-draw synchronous callers
  recheck ticket/cache identity after waking. Destroyed meshes reject new calls.
- Suppresses already-obsolete completion notifications before dispatch. Notifications
  remain readiness edges, not resource leases; owner-side consumers re-resolve.

`VkMeshRenderer.Preparation.cs`:

- Requests mesh-owned streams before `EnsureProgram` can return `ProgramsPending`.
  Existing warm state still takes its fast path; externally supplied index buffers
  retain their own preparation ownership.
- Does not pass synchronous-index permission to `EnsureBuffers`. The existing
  snapshot flag is interpreted locally as an exact-geometry admission requirement,
  not permission to join a worker. No materialization snapshot layout is changed.
- Returns `BuffersPending` for missing required indices. Existing buffer-readiness
  checks and frame-cohort admission still reject incomplete geometry. Shader-generated
  vertex draws continue to use their existing index-binding bypass.
- Leaves native resource upload policy, command recording, index widths, primitive
  order, draw counts, and deferred binding publication unchanged.

## Ownership And Lifetime Review

The existing mesh-editing contract still applies: callers serialize topology edits
with snapshot capture, invalidate the index cache after raw list edits, and publish
the mesh-changed notification after the edit is complete. This proposal does **not**
make arbitrary concurrent writes through the public `List` properties safe. Review
actual importer/editor call sites against that contract during the live gate.

The index-cache gate protects ticket lookup, snapshot acquisition, invalidation,
and publication identity. Copying the topology has a cold O(index count) owner-thread
cost under that gate. It must be measured separately; moving conversion off-thread
is not evidence that total preparation or time-to-first-frame improved.

Workers use only their owned scalar snapshot for index data and width selection.
They still read current mesh lifetime/revision state solely to authorize publication.
No buffer is installed unless the same ticket is current and its geometry revision
matches. Replaced tickets cannot install their results. Destruction uses the existing
`OnDestroying` cache invalidation and deferred-publication rollback.

`RenderObjectPublicationScope` continues to own construction rollback and wrapper
publication. Vulkan callbacks only set an atomic readiness edge; `EnsureBuffers`
reacquires the current cache on its owner. User callbacks run outside the cache gate.
A callback is not authority to retain a buffer across a later invalidation.

No new native lock, retirement queue, resource lease, or worker-upload path is added.
Review the concurrent snapshot/publication/destruction paths before removing draft
status, including destruction racing a new request. Existing ownership assumptions
must be established by evidence rather than inferred from a quiet log.

## Acceptance Defined Before Runtime Measurement

Correctness requires zero admitted draws with a missing required index stream,
zero stale-ticket publications, and exact equality of submitted primitive order,
index width, index count, and draw range against the base revision. The normal
admission path must perform **zero CPU index-ticket joins**, including when exact
geometry is required. Pending geometry must eventually complete or report a terminal
failure, never remain silently omitted from an otherwise accepted frame.

Use the existing S00 observer configuration and at least three matched cold/warm
runs per condition, with 60-second warmed observation windows. Before running the
changed binary, record baseline variability and numeric budgets for snapshot time,
cache-gate contention, `MeshDrawResourcePreparation`, recording p95/p99, first valid
frame, pending work, managed/private memory, and native retirement. Do not choose
latency tolerances after seeing the changed measurements. These performance budgets
remain unfilled, so the performance gate is not runnable to acceptance yet.

Required live cases, all still pending:

- Cold triangle, line, point, and supported patch streams, including both existing
  16-bit and 32-bit index-width branches; unchanged repeated use and shared consumers.
- Topology replacement while a ticket is queued/running, explicit invalidation after
  in-place edits, geometry revision during preparation, and delayed stale completion.
- Indexed-to-nonindexed and nonindexed-to-indexed edits, including a waiting explicit
  synchronous caller whose original stream is removed.
- Worker/snapshot failure, retry after explicit invalidation, destruction before
  completion, and owner teardown racing new preparation. Check rollback and retention.
- External index streams, shader-generated vertex draws, Vulkan/OpenGL switching,
  large imported geometry, and actual submitted draw ranges. Inspect saved images.
- Complete-cohort retention/retry under pending indices and sustained mutation, with
  no partial accepted scene, growing backlog, or unbounded retirement.

## Validation Performed Here

The two edited source baselines were reconstructed and verified against their
GitHub blob IDs before editing:

- `XRMesh.Geometry.cs`: `67729d4f43b1d51ec2bd30618d7ea2d2f3366dac`.
- `VkMeshRenderer.Preparation.cs`: `94189b337fabf13cac1893b9c9582431f1de8e40`.

The focused diff and whitespace were checked locally. This is static inspection,
not a compiler or runtime pass. The environment has no .NET SDK and no running
Windows/Vulkan editor, so owning-project builds, existing tests, visual inspection,
A/B captures, and live lifecycle/performance validation were **not run**. No test
files, dependency versions, launch settings, or later remediation items were changed.

Build the changed owning projects in the normal .NET 10 Windows checkout before
running the isolated editor gate:

```powershell
dotnet build XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj -c Release
dotnet build XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj -c Release
```

Record the exact changed commit and binaries with the eventual evidence. Keep the
main checklist unvalidated until its measured entry condition, design review,
focused builds, live correctness, performance, and regression gates have passed.

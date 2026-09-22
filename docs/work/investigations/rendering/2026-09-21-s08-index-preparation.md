# S08: Index Preparation Before Draw Admission

Date: 2026-09-21
Base revision: `cd58220514ae6455af68a9526f2fa32d8d127e4a`
Scope: [S08 in the Vulkan stall remediation TODO](../../todo/rendering/vulkan-stall-remediation-todo.md#s08-prepare-indices-before-draw-admission)
Status: **Blocked on build and live validation; draft implementation, not Validated.**
Test clearance: not granted; no regression tests added or modified.

## Entry evidence and hypothesis

At the base revision, `VkMeshRenderer.TryPrepareForDrawEnqueue` forwards the
materialization snapshot's synchronous-geometry flag through `EnsureBuffers`
and `GetIndexBufferForBinding` to `XRMesh.GetIndexBuffer`. A cold cache miss can
then execute `ticket.Completion.Task.GetAwaiter().GetResult()` during admission.
The index worker also reads `GetIndices(type)` and `VertexCount` from the live
mesh instead of a captured input. `MarkGeometryChanged` advances the geometry
revision but does not invalidate already cached index buffers after in-place
list edits.

These are source observations, not a measured reproduction of a meaningful
stall. The S08 measured-join entry gate remains open. Hypothesis: requesting an
owned index snapshot before program/resource readiness and polling its existing
ticket removes the admission-side worker join without losing required geometry.
Reject the hypothesis if the matching trace has no material join cost, if
snapshot capture merely moves the stall earlier, or if required content fails
to converge through the existing pending path.

## Implementation

- `XRMesh.RequestIndexBufferPreparation()` starts existing per-primitive work for
  triangle, line and point streams. It is a request, not a readiness assertion.
  Nonindexed primitives do not create jobs; completed buffers remain shared.
- A cold request captures primitive scalar indices into one owned array, without
  temporary arrays per triangle or line, plus the vertex count. The existing
  geometry-revision ticket guards the capture and publication. Workers use only
  captured inputs for conversion and reject out-of-range indices explicitly.
- The snapshot belongs to the worker closure, not the retained completion ticket.
  Obsolete workers reject their ticket before conversion and before publication.
  Snapshot and scheduling errors fault the existing observable ticket. Existing
  native wrapper creation and uploads remain backend-owner work.
- Vulkan requests indices before checking program readiness and before accepting
  draw resources. It checks cache/binding identity once per observed mesh revision
  under `_bufferStateSync`. Existing dirty-state and readiness publication are
  reused; no second queue, cache, or native retirement system is introduced.
- Exact geometry still must be ready, but it no longer authorizes a CPU worker
  join. Admission returns the existing `BuffersPending` outcome rather than
  throwing simply because asynchronous indices have not completed. Real build
  failures still reach the existing failure query/exception path.
- Explicit synchronous callers outside normal Vulkan admission retain their API.
  They recheck cache/ticket identity after completion and recheck nonindexed or
  destroyed state on retry. In-place topology notifications invalidate cached
  buffers and tickets before notifying consumers.

## Ownership and lifetime review

Topology edits and snapshot capture must be serialized by the mesh's CPU-data
owner. A copied array prevents later edits from changing a queued worker's input;
a revision check does **not** make unsynchronized edits to public `List<T>` values
safe. Integration must verify this owner contract in the actual cold/import and
mutation paths. No new claim of concurrent topology-authoring support is made.

The Vulkan preflight uses `_bufferStateSync` before `_indexBufferLock`, matching
buffer resolution. Workers take only the index-cache lock at ticket checks and
CPU-object publication, and do not acquire renderer-state locks. Existing
thread-affine deferred-publication scopes remain on the worker that created
them. Stale unpublished objects are handled by the existing aborted scope.
Ready-buffer disposal still uses the existing mesh cache invalidation path.

Destruction clears pending tickets through the existing `OnDestroying` path.
Completed-destruction checks reject new requests and worker publication. Immediate
destruction racing a fresh request, shared-consumer native retirement, teardown,
and renderer switching still need live lifetime review; source checks alone do
not certify those races. Do not merge before that review and the gates below.

Snapshot capture is linear in index count and executes on the requesting thread.
The Vulkan fallback hook is before program readiness within mesh preparation; it
is not importer-time prefetch. Earlier callers may use the same request API after
CPU topology completion. Large-mesh capture cost must be measured separately.

## Acceptance gates fixed before live comparison

Use the owning TODO's three matched runs per condition and at least a 60-second
warmed window. Preserve scene, backend, submission mode, camera path, profiler,
validation settings, cache identity and accepted draw counts. Set numeric latency,
retention and backlog tolerances from measured baseline variability **before**
collecting the changed cohort; no performance acceptance budget has been inferred
from this unbuilt patch.

Required invariants: zero admission-side index-task joins; zero stale index
publications or mismatched submitted ranges; zero silent required-content loss;
zero unbounded retry, worker or retirement growth. Track snapshot-copy cost,
worker conversion, native upload, total preparation, first complete output and
warmed frame distributions separately. A cheaper admission scope alone is not a
performance pass.

- [ ] Build `XREngine.Runtime.Rendering` and `XREngine.Runtime.Rendering.Vulkan`
      with the repository's .NET 10 SDK configuration and no new warnings.
- [ ] Review topology ownership, cache/publication lock order and immediate
      destruction versus fresh requests before live use.
- [ ] Capture cold and revised indexed meshes, plus unchanged warmed reuse.
- [ ] Exercise mutation while pending, stale completion, invalid index failure,
      failure recovery, pending disposal, and two consumers sharing one mesh.
- [ ] Exercise indexed-to-nonindexed changes, points, lines, triangles, patch
      control streams, external index atlases and shader-generated vertices.
- [ ] Verify exact submitted index widths/counts/ranges; required meshes and
      fullscreen composition must eventually appear, not be silently omitted.
- [ ] Repeat affected shared-code paths on OpenGL and switch renderers.
- [ ] View saved output and check applicable S15 motion/history/resize cases.
- [ ] Compare matched timing/allocation/backlog captures; reject or defer S08 if
      the measured benefit is absent or snapshot cost absorbs it.
- [ ] Record evidence, then request explicit test clearance before test changes.

## Validation performed in this editing environment

The three edited source baselines were reconstructed from connector reads and
verified against their exact Git blob SHA before modifications. Source diff and
whitespace checks are available; they are not compilation or runtime validation.
The environment has no `dotnet` executable, no full runnable checkout, and no
Windows/Vulkan editor session. Therefore no build, live rendering, performance,
image, teardown, or regression-test pass is claimed. The parent TODO's S08 status
and checkboxes are intentionally not promoted.

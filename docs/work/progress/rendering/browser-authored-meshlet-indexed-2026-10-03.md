# Browser authored meshlet indexed submission

The shared mesh command graph has a renderer-neutral compute meshlet route independent of hardware task/mesh shader capability and Advanced native material admission. `WebGpuRendererHost` continues to report hardware meshlet dispatch unsupported and the mesh shader dialect as `None`. Requested generic meshlet work is handled as ready, pending, or rejected before the hardware path; rejection never replays original indexed geometry or selects CPU visibility.

## Ownership and lowering

The existing `GPUScene` publishes a material-independent, leased resident source projection. It retains exact source mesh/payload ownership, material overrides, source bindings and publisher generations at the world swap boundary. It does not manufacture Advanced material rows or discard materials that are valid authored WGSL but ineligible for native shading. The renderer keeps each publication and all generated work storage pinned until its accepted queue prefix completes. Aborted frames release unpublished work; device teardown releases retained scene pins only after disposing the physical device.

Authored command selection and per-viewport meshlet overrides both request this projection. CPU exceptions and GPU submission consume the same frozen source-ownership lookup. Explicit `ForceCpuRendering` and `ExcludeFromGpuIndirect` remain authoritative; a source mixing CPU/GPU primitive ownership, or missing an authored primitive from publication, receives a precise diagnostic before whole-source replay. Checks apply only to the selected pass and nonzero authored instances. Native material eligibility never decides generic CPU ownership.

Each validated immutable version-three payload is uploaded once per backend physical generation. The compact storage arena retains the canonical 80-byte descriptor, uint32 vertex remaps and packed triangle bytes. A cold ownership-checked permutation matches cooked triangles back to the original source primitive and cyclic corner order. This is a topology permutation, not meshlet recooking or a substitute raster index stream.

The exact scoped cooked companions are:

- `meshlets::refit-bounds`: refits conservative current local bounds from the same GPU skin/morph position stream used by authored raster
- `meshlets::cull-expand`: performs GPU meshlet culling and emits uint32 indices at their original source primitive destinations; culled primitives become degenerate holes
- `meshlets::finalize-indexed`: validates completion and capacity/fault state and publishes all five `drawIndexedIndirect` words; any fault, incomplete dispatch or zero visible triangles zeros all five words

The retained indexed draw binds only the GPU-generated uint32 index stream. It uses `firstInstance = 0` and the exact authored vertex/fragment program. Common mesh preparation preserves material resolution, overrides, raster/depth coverage, engine/model/previous/normal uniforms, material uniforms, callbacks, typed binding publishers and GPU deformation. Original source indices are not replayed as a fallback.

All three companions use at most four storage buffers and one dynamic uniform. Bounds are evaluated through conservative object-space clip planes with finite-value/error-margin checks; cone culling is disabled because current deformation and authored raster state do not prove valid cones. Source-free built-in surfaces with validated cooked bounds can use finite rejection. An authored `IMeshletVertexBoundsProvider` can declare additional local displacement or disable rejection. Undeclared vertex behavior is conservatively unbounded and still uses GPU meshlet expansion and indexed-indirect submission. Authored culling opt-outs remain authoritative.

Renderer-owned position overrides and unproven vertex callbacks, scoped bindings, publishers or material transform parameters disable finite rejection unless an explicit bounds declaration covers their complete behavior. Canonical GPU deformation uses its current final position stream for bounds refit. The immutable payload compatibility token is cached once; hot ownership checks do not allocate a hash object per draw.

Submission diagnostics report CPU recording state and the number of unbounded-policy draws. They never map GPU arguments, counts, visibility or statistics. Zero visible GPU draws are valid output, never evidence for fallback. Indirect draws do not attest temporal history writes merely because their CPU draw list is nonempty.

## Exact deformation sources

Canonical deformation inputs and WebGPU output generations are owned by the renderer/mesh pair. Distinct authored submeshes retain their own bone order, inverse bind/root transform, normalized morph controls and active shape indices. `XRMeshRenderer.SetBlendshapeWeightNormalized(mesh, indexOrName, weight)` selects that mesh's control table. Shared palette composition and morph LOD policy remain in the canonical rendering layer; no auxiliary renderer or duplicate draw implementation is created.

CPU-direct rendering enumerates the same authored submeshes in list order through common material resolution, callbacks and draw preparation. Its retained draw cache distinguishes source geometry and renderer buffer generations. Generic GPU-driven/meshlet raster uses the exact source's current compute output for both raster attributes and conservative bounds refit. Advanced aggregate preparation likewise keys pose packing by renderer/mesh and retains its existing completion-owned current/previous output arena slots.

An external GPU palette can be shared only after finalized bone ordering, inverse binds and bind root prove identical. A different ordering requires an explicit mesh-specific palette publication. Such a publication stamps the source skinning generation and bind root; replacement requires republishing. Explicit GPU ownership remains authoritative even when the buffer has a CPU seed mirror. Generic deformation binds that GPU buffer directly. Packed Advanced aggregate inputs require an ordered GPU pose-copy producer before accepting GPU-owned palettes; copying the retained CPU seed is rejected explicitly.

Frozen mesh publications reject removed sources and changes between primary and distinct deformation ownership. Renderer/mesh controls and output caches retire removed live mesh assets through the existing deferred GPU destruction path. Primary pose settling observes distinct shared render-frame identities, and a GPU-to-CPU pose ownership transition restarts settling. A later authored callback changing the current input image records fresh deformation before its draw.

## Explicit remaining profiles

Dynamic LOD needs the shared GPU-selected mesh/LOD publication; the current path rejects a multi-LOD source precisely. View-dependent transparent ordering likewise requires a shared GPU sort publication. Multiple logical instances require published per-instance transforms and bounds. A submesh-local instance count other than one requires a defined composition with the command count and is rejected by CPU-direct and generic meshlet submission; neither silently ignores that authored count. These requirements remain open and are not silently approximated.

The initial bounded implementation retains three scene-publication slots and at most 256 generated draws per scene slot in one atomic frame. Selected device storage limits remain authoritative. Queue-owned slots are not reused or overwritten to meet capacity.

## Validation boundary

The integrated WebGPU, Editor and native Browser builds pass with zero warnings or errors. All three exact companions pass the shared production cooker within its 87-artifact inventory. Independent source review covered routing, conservative bounds, frozen ownership, primitive/corner preservation, fault-zeroed arguments and completion retention. These compile/cook and source checks do not establish browser output: live strategy, ordering, deformation and recovery acceptance remains open.

The exact submesh ownership extension passes the WebGPU build with zero warnings/errors and a disposable managed probe with 337 checks. The probe covers independent bone and morph ordering, bind-root and shape-generation replacement, aggregate pose offsets, frozen-source invalidation, removed live-asset retirement, explicit GPU authority with a retained CPU seed mirror, stale external-generation rejection/republication, identical-order palette reuse, primary pose-settle timing and disabled desktop morph parity. Two hundred warmed source preparations allocate zero managed bytes. This is source/managed producer evidence; live browser raster, multi-frame queue completion and temporal-output acceptance remain separate validation requirements.

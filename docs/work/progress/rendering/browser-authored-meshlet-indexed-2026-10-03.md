# Browser authored meshlet indexed submission

The shared mesh command graph has a renderer-neutral compute meshlet route independent of hardware task/mesh shader capability and Advanced native material admission. `WebGpuRendererHost` continues to report hardware meshlet dispatch unsupported and the mesh shader dialect as `None`. Requested generic meshlet work is handled as ready, pending, or rejected before the hardware path; rejection never replays original indexed geometry or selects CPU visibility.

## Ownership and lowering

The existing `GPUScene` publishes a material-independent, leased resident source projection. It retains exact source mesh/payload ownership, material overrides, source bindings and publisher generations at the world swap boundary. It does not manufacture Advanced material rows or discard materials that are valid authored WGSL but ineligible for native shading. The renderer keeps each publication and all generated work storage pinned until its accepted queue prefix completes. Aborted frames release unpublished work; device teardown releases retained scene pins only after disposing the physical device.

Authored command selection and per-viewport meshlet overrides both request this projection. CPU exceptions and GPU submission consume the same frozen source-ownership lookup. Explicit `ForceCpuRendering` and `ExcludeFromGpuIndirect` remain authoritative; a source mixing CPU/GPU primitive ownership, or missing an authored primitive from publication, receives a precise diagnostic before whole-source replay. Checks apply only to the selected pass and nonzero authored instances. Native material eligibility never decides generic CPU ownership.

Each validated immutable version-three payload is uploaded once per backend physical generation. The compact storage arena retains the canonical 80-byte descriptor, uint32 vertex remaps and packed triangle bytes. A cold ownership-checked permutation matches cooked triangles back to the original source primitive and cyclic corner order. This is a topology permutation, not meshlet recooking or a substitute raster index stream.

The exact scoped cooked companions are:

- `meshlets::select-lod`: publishes the selected resident mesh/level using the shared projected-radius policy
- `meshlets::refit-bounds`: refits conservative current local bounds from the same GPU skin/morph position stream used by authored raster
- `meshlets::cull-expand`: performs GPU meshlet culling and emits uint32 indices at their original source primitive destinations; culled primitives become degenerate holes
- `meshlets::finalize-indexed`: validates completion and capacity/fault state and publishes all five `drawIndexedIndirect` words; any fault, incomplete dispatch or zero visible triangles zeros all five words

The retained indexed draw binds only the GPU-generated uint32 index stream. It uses `firstInstance = 0` and the exact authored vertex/fragment program. Common mesh preparation preserves material resolution, overrides, raster/depth coverage, engine/model/previous/normal uniforms, material uniforms, callbacks, typed binding publishers and GPU deformation. Original source indices are not replayed as a fallback.

The instance-aware LOD cull companion uses six storage buffers and one dynamic uniform; the other companions use at most four storage buffers. Bounds are evaluated through conservative object-space clip planes with finite-value/error-margin checks; cone culling is disabled because current deformation and authored raster state do not prove valid cones. Source-free built-in surfaces with validated cooked bounds can use finite rejection. An authored `IMeshletVertexBoundsProvider` can declare additional local displacement or disable rejection. Undeclared vertex behavior is conservatively unbounded and still uses GPU meshlet expansion and indexed-indirect submission. Authored culling opt-outs remain authoritative.

Renderer-owned position overrides and unproven vertex callbacks, scoped bindings, publishers or material transform parameters disable finite rejection unless an explicit bounds declaration covers their complete behavior. Canonical GPU deformation uses its current final position stream for bounds refit. The immutable payload compatibility token is cached once; hot ownership checks do not allocate a hash object per draw.

Submission diagnostics report CPU recording state and the number of unbounded-policy draws. They never map GPU arguments, counts, visibility or statistics. Zero visible GPU draws are valid output, never evidence for fallback. Indirect draws do not attest temporal history writes merely because their CPU draw list is nonempty.

## Exact deformation sources

Canonical deformation inputs and WebGPU output generations are owned by the renderer/mesh pair. Distinct authored submeshes retain their own bone order, inverse bind/root transform, normalized morph controls and active shape indices. `XRMeshRenderer.SetBlendshapeWeightNormalized(mesh, indexOrName, weight)` selects that mesh's control table. Shared palette composition and morph LOD policy remain in the canonical rendering layer; no auxiliary renderer or duplicate draw implementation is created.

CPU-direct rendering enumerates the same authored submeshes in list order through common material resolution, callbacks and draw preparation. Its retained draw cache distinguishes source geometry and renderer buffer generations. Generic GPU-driven/meshlet raster uses the exact source's current compute output for both raster attributes and conservative bounds refit. Advanced aggregate preparation likewise keys pose packing by renderer/mesh and retains its existing completion-owned current/previous output arena slots.

An external GPU palette can be shared only after finalized bone ordering, inverse binds and bind root prove identical. A different ordering requires an explicit mesh-specific palette publication. Such a publication stamps the source skinning generation and bind root; replacement requires republishing. Explicit GPU ownership remains authoritative even when the buffer has a CPU seed mirror. Generic deformation binds that GPU buffer directly. Packed Advanced aggregate inputs require an ordered GPU pose-copy producer before accepting GPU-owned palettes; copying the retained CPU seed is rejected explicitly.

Frozen mesh publications reject removed sources and changes between primary and distinct deformation ownership. Renderer/mesh controls and output caches retire removed live mesh assets through the existing deferred GPU destruction path. Primary pose settling observes distinct shared render-frame identities, and a GPU-to-CPU pose ownership transition restarts settling. A later authored callback changing the current input image records fresh deformation before its draw.

## Explicit remaining profiles

Dynamic LOD follows the GPU-selected authored candidate profile below. Automatic GPU-requested residency and differing per-level primitive membership remain separate producer requirements. View-dependent transparent ordering follows the bounded full-resident publication described below. Multiple logical instances use the runtime publication described below. A submesh-local instance count other than one remains rejected: it belongs to the renderer-local indirect API, and no command-count multiplication contract exists. The RenderCommand path preserves its existing command count. These remaining profiles are not silently approximated.

The initial bounded implementation retains three scene-publication slots and at most 256 generated draws per scene slot in one atomic frame. Selected device storage limits remain authoritative. Queue-owned slots are not reused or overwritten to meet capacity.

## Validation boundary

The Editor browser dependency cooker first inventories enabled meshlet requests
through the ordinary cooked serializer's graph, then prepares each existing
resident mesh before the first binary write. It reuses a canonically fresh
payload or builds and validates one through `XRMesh.GetOrCreateMeshletPayload`;
no LOD generation or source raster/material replacement is involved. Disabled
submesh references place no restriction on a shared mesh. Conflicting enabled
requests for the same mesh fail before serialization, rather than allowing
traversal order to choose a payload. A stale request probes the Editor's desktop
meshoptimizer backend before native generation; a missing library or export
reports a browser cook error. This generic prepass does not reinterpret a
registered asset's custom serializer graph. The source change still needs an
Editor build and real Windows native cook/hydration check before it can
establish payload publication.

The smallest live meshlet check is a saved, static-only derivative of the
RenderingParity mapped panel with its one authored resident LOD and original
mapped material. Enable that submesh's meshlet setting, publish it through the
ordinary Windows Editor browser route, and request
`GpuMeshletZeroReadback` with a saved Default pipeline. Hydrate the emitted
world to verify a present owner-validated payload and unchanged source mesh,
LOD and material identity; then inspect the browser's actual meshlet selection,
GPU cull/finalize, indexed-indirect raster, pixels and ordinary read-map count.
The original RenderingParity world also contains a skinned ribbon, so it does
not by itself constitute this static-only acceptance fixture.

The integrated WebGPU, Editor and native Browser builds pass with zero warnings or errors. The original three companions passed the shared production cooker within its earlier 87-artifact inventory; the LOD addition below records all four current companions. Independent source review covered routing, conservative bounds, frozen ownership, primitive/corner preservation, fault-zeroed arguments and completion retention. These compile/cook and source checks do not establish browser output: live strategy, ordering, deformation and recovery acceptance remains open.

The exact submesh ownership extension passes the WebGPU build with zero warnings/errors and a disposable managed probe with 337 checks. The probe covers independent bone and morph ordering, bind-root and shape-generation replacement, aggregate pose offsets, frozen-source invalidation, removed live-asset retirement, explicit GPU authority with a retained CPU seed mirror, stale external-generation rejection/republication, identical-order palette reuse, primary pose-settle timing and disabled desktop morph parity. Two hundred warmed source preparations allocate zero managed bytes. This is source/managed producer evidence; live browser raster, multi-frame queue completion and temporal-output acceptance remain separate validation requirements.

## GPU-selected authored LOD candidates

The generic route now has a bounded GPU LOD producer for authored sources with one to four resident-table levels. This replaces the initial blanket multi-LOD rejection described above; it does not close the wider authored LOD acceptance requirement.

The shared logical-mesh registration resolves a normal renderable through `RenderInfo3D.OwnerRenderableMesh`, since its ordinary `Owner` is the scene component. At the same scene swap, the leased submission captures every resident level's actual renderer, mesh, material, source-binding membership, publisher versions, primitive instance count and immutable payload proof. The candidate records are additional geometry choices for an existing source; they do not enter the source-ownership primitive count as duplicate commands. Completion pins retain every candidate, and final lease release clears all candidate references.

The fourth canonical companion, `meshlets::select-lod`, writes the selected mesh ID and level on the GPU. Its projected world-sphere radius, inclusive threshold comparison, four-level table and nearest-resident search order match the shared GPU selector. It uses the active view's viewport and selected jittered/unjittered projection. Each resident candidate then enters the existing authored material, callback, binding and deformation preparation. Primary authored LODs retain their material render pass, and the leased publication includes all candidate passes even when the CPU-current source belongs to another pass. Explicit command passes and common material overrides remain authoritative. The generic meshlet route already executes configured passes independently of CPU visibility/pass-list emptiness, so each pass records its own matching candidates. Cull/expand consumes the GPU-selected mesh and level, and only that candidate can publish visible triangles and a nonzero indirect draw. Duplicate geometry appearing at multiple levels is selected by both identifiers, so it cannot emit twice. Source primitive and corner ordering remain unchanged.

The cull companion now declares five storage buffers and a 208-byte uniform block; its exact ABI rejects an old cooked artifact. LOD selection needs one storage buffer and a 96-byte uniform block. Single-LOD work uses the same producer with its frozen current mesh/level. Neither producer maps visibility, counts, decisions or request bits, and no original-index or CPU selection fallback is introduced.

Sparse residency preserves nearest-resident selection. Missing preferred-level request bits remain in the GPU result, but the generic zero-readback route does not service them through the desktop request-buffer mapping path. Explicit residency publication can add a level at a subsequent swap. Automatic demand-loading driven by those GPU requests remains unimplemented.

The primary command also seals component matrices and skinning policy before swap callbacks. Public live registration/update captures fresh component state rather than reusing a prior command seal. The scene keeps component and skinning history beside each source row, advancing it at the same publication boundary as model history, including row compaction and quiet-frame settlement. Candidate matrices retain the exact source command matrices whenever their transform conventions match. Switching a temporal image between static/component and world-space-skinned conventions requires proof that the source uses the captured default matrix; an explicit custom matrix is preserved for matching candidates and rejects an unprovable alternate reconstruction. Component-only motion participates in temporal settlement even when the CPU-current skinned LOD keeps an identity model matrix. Later callback mutations enter the next source snapshot.

Each source and candidate freezes Base/Shadow enablement using the same effective command override or primary renderer material as authored visible collection. Empty pass sets enable both; a nonempty set disables a missing or explicitly disabled Base/Shadow entry. GPU selection may choose a disabled candidate, in which case that requested pass emits no geometry. A no-shadow candidate remains valid in a normal view, and a disabled-base candidate remains valid in a shadow view. The same frozen policy applies to a single-level source. An effective override governs enablement and outline admission even when its pass set differs from the underlying renderer materials.

The following producer profiles remain explicit rejections, rather than permanent WebGPU capability limits: more than four authored levels; differing primitive membership across levels; mixed static/skinned temporal images without proven source/default transform correspondence; and enabled Outline passes without an exact modeled companion. Outline admission examines all authored levels, including nonresident levels, before CPU-exempt auxiliary replay and GPU recording in non-shadow views. The bounded canonical outline producer described below supplies candidate-specific material/options and geometry tied to the primary GPU selection. Multiple logical instances follow the runtime producer below; transparent order follows the bounded shared producer described below.

The retained budget remains 256 generated candidate draws per scene slot, including every resident level and repeated view/pass submission. A fully resident four-level source therefore uses four candidates, and three slots stay within the existing compute command-cache budget. Capacity is checked before recording that source's LOD producer; exhaustion rejects the atomic frame instead of dropping levels.

Production cooking succeeds for all four exact companions. A disposable managed witness passes 118 checks covering normal authored renderer/material/mesh ownership, shared LOD registration, sparse residency, explicit residency replacement, independent completion pins, prior-publication immutability, last-pin candidate cleanup, authored overflow visibility, common material overrides over null source materials, primitive-membership preservation, cross-pass source membership, explicit command-pass and matrix authority, mixed static/skinned candidate transforms, pre-callback history sealing, successive public live updates, CPU-current transform-family changes, temporal settlement, Base/Shadow enablement, sparse outline rejection, effective PassSet overrides in both directions, single-level source enablement, and runtime ABI validation of the actual cooked descriptors/WGSL. The combined Rendering/WebGPU build, including the concurrent render-info registration reconciliation, and witness build pass with zero warnings/errors. One hundred warmed full scene-publication swaps allocate zero managed bytes. These are managed publication and production-cook checks, not GPU execution evidence. Threshold-boundary raster output, mixed material appearance, queue reuse, device recovery and zero-readback browser acceptance remain open.

### Canonical outline material and selected auxiliary geometry

The browser producer now has four exact `UberOutlineV1` Slang/WGSL companions for canonical outline behavior with optional alpha masks and dissolve. The desktop factory remains unchanged; the cooked target uses an explicit pass identity and source profile before any shader-file load. The original base program still requires its independent complete authored companion. Arbitrary Uber base GLSL is not translated or replaced by the outline cook.

The admitted behavior includes local/world/screen expansion, vertex-color width, depth offset, canonical adjoint normal direction, base alpha/dissolve coverage, outline textures/mask, hue/tint, rim-strength/emission and distance fade. Reflection projection preserves external animated parameter aliases, complete source texture slots and settings, and exact pass/options metadata. Unsupported pre-outline features, static-literal overrides and custom coupled uniform behavior reject precisely. Enabled source defaults for forward ambient occlusion and forward shadows do not affect the canonical early outline output.

Each resident primary candidate retains its exact outline variant and a separately sealed binding closure. Auxiliary records reuse the same geometry, deformation owner, current/previous model and GPU-selected mesh/level. Disabled Base does not disable Outline, alternate outline render passes remain reachable, and shadow requests omit outline work. Outline displacement disables finite meshlet rejection. The linked CPU-current outline command is deferred; if any candidate cannot be admitted, the existing atomic frame is pending/rejected and cannot present a partial replacement. Other CPU commands retain their existing order. Exact opaque RGB replacement with commutative min/max alpha is admitted without altering blend state; other blending retains the explicit source-order producer restriction. Existing canvas stencil/alpha-to-coverage restrictions remain diagnostics.

The combined managed witness now passes 156 checks, retaining all 118 resident checks and adding selected outline geometry/pass/binding ownership, CPU auxiliary linkage, disabled-Base behavior, completion and scene-reset retirement, invalid screen dimensions, callback/scoped-binding rejection and companion mutation checks. One hundred warmed full outline publications allocate zero managed bytes. Four real production cooks and a 242-check Editor projection/roundtrip witness pass, including untouched canonical parameter admission and three unsuppressed disposal cycles returning to the exact object-registry baseline. Generated outline shader/source storage is owned and released; the existing cache-suppressed managed reset restores that storage without broadening ordinary render-object revival. Warmed binding validation across all four profiles allocates zero bytes. Independent source review found no remaining actionable defect in this bounded outline/frozen-view slice and verified the final 50-file source fingerprint set. These checks do not establish browser GPU output; live acceptance and the separate shared desktop compatibility decision remain open before broader closure.

### Shared LOD policy audit

This producer implements discrete geometric LOD selection, matching the current executable shared `GPURenderLODSelect.comp` policy. `SubMeshLOD` supplies minimum projected-radius thresholds; the shared table supplies normalized/default thresholds and resident mesh identities. Neither exposes mesh hysteresis, geometry shadow-LOD bias, nor an active transition-mode selector. Texture mip bias and shadow-atlas resolution hysteresis are separate contracts.

`GPURenderPassCollection.LodTransitionFrameCount` still defaults to eight, and desktop fragment-dither helpers remain in the source. However, the current selector does not consume `TransitionFrameStep`, does not advance time-progress, and the current batch/meshlet expansion emits no previous-LOD transition variant. Those are inactive legacy transition surfaces, not functioning authored fade/dither modes. The generic producer does not claim transition animation and does not invent a failure for the inert default. A future active transition contract will need its own frozen state, previous/current geometry expansion and authored fragment coverage before this route can admit it.

The selected view is one immutable pass selection shared by GPU LOD, meshlet rejection, authored raster uniforms and outline expansion. It reuses captured logical/temporal views or a camera descriptor captured at an auxiliary camera scope boundary, including shadow faces and nested UI/offscreen cameras. The pass freezes its jittered/unjittered family, matching temporal history, viewport, layer mask, orthographic screen dimensions and time before callbacks. Default WebGPU temporal accumulation exposes its updated immutable temporal sample. A disposable real-assembly witness passes 23 view/history/scope checks with zero allocation across 1,000 warmed selections; the combined Core/Rendering/WebGPU graph builds with zero warnings/errors. Stereo/multiview remains an explicit view-profile rejection; conservative-highest-detail multiview union is not approximated by one camera. No selected geometry hysteresis, shadow bias or functioning transition mode is silently replaced by this discrete policy.

### Shared registration compatibility review remains open

Resolving normal component-owned renderables into the existing shared LOD table also exposes that table to desktop consumers. The current desktop selector writes zero in its fourth word (`GPURenderLODSelect.comp`, final selection publication), while `HybridRenderingManager.TryAugmentIndirectFragmentShader` interprets that word as progress and discards current-variant fragments at zero coverage. `GPURenderBuildBatches.comp` emits only one selected variant. Thus a changed selection can meet incompatible legacy coverage semantics. The desktop GLSL and binding contract are unchanged by this work. The shared owner-resolution change requires a compatibility decision before promotion; the proposed completed-coverage repair has not been included.

## Authored traditional indexed-indirect submission

Generic traditional GPU-indirect requests now enter `IAuthoredIndexedBackendCapability` before the desktop GPU-pass/GLSL path. The shared command graph preserves `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`, `GpuMeshletInstrumented`, and `GpuMeshletZeroReadback` identities independently of diagnostics. Browser traditional passes request the same complete resident source publication as meshlets, including per-viewport overrides, and execute even when CPU-visible command collection is empty. Desktop shader generation and scheduling are unchanged.

`WebGpuRendererHost.Engine.AuthoredIndexed` owns one shared source/LOD/outline traversal and the completion-retained `WebGpuAuthoredIndexedFrameSlot` ring. Both primitive families use the exact frozen view, source ownership, material/override resolution, callback/binding publication, and per-source deformation path. Explicit CPU ownership is replayed separately under the same published ownership checks. A pending or rejected GPU candidate marks the existing atomic frame gate; earlier recorded work cannot present a partial replacement.

Traditional candidates need no meshlet payload. `WebGpuIndirectWork` retains the exact original uint16/uint32 index buffer and authored raster pipeline. `GpuMeshSubmissionSourceBindings` now separately captures ready index identity, scalar format, count and committed revision. A nonblocking cache observation does not initiate or wait for index preparation. The indirect consumer requests a missing original stream asynchronously and waits for the next resident publication; absent triangle topology, terminal preparation failures, mismatched scalar layouts, or callback-time index replacement/mutation receive precise diagnostics. The canonical uint32 cache uses validated nonnegative Int32 topology storage, whose unchanged four-byte encoding remains admitted.

The scoped `indirect::cull-primitive` companion now uses four storage bindings, one 208-byte dynamic uniform, 64 invocations and 2,304 bytes of shared memory. One workgroup reduces the current raster position stream to a conservative local bound, including interleaved attributes, admitted renderer position overrides and GPU skin/morph output. It never trusts potentially stale `mesh.Bounds`. Conservative object-space clip planes retain affine, shear, reflection and nonuniform-scale behavior. Invalid/nonfinite streams, undeclared vertex behavior and over-budget streams remain visible. Host admission disables refit before binding a position buffer that exceeds the device storage range or 1,048,576-vertex refit budget. This is a culling policy, not an alternate draw path: every admitted candidate still receives GPU-written arguments and an indirect indexed draw.

The producer replaces all five argument words every time, zeroing disabled/unselected/rejected geometry and keeping first index, base vertex and first instance zero. It neither expands nor reorders source indices. A CPU loop records the upper-bound resident candidates; GPU LOD/visibility decides their argument contents. No count, index, bounds or visibility buffer is mapped back to the CPU. Instrumented mode currently uses this same no-readback algorithm and reports its accurate strategy identity; it does not imply unavailable GPU diagnostic counters.

The shared budget remains three completion-owned scene slots, 256 source/view selections and 256 candidate draws per slot, counting repeated passes, resident LODs and modeled outlines. Whole-primitive refit adds a bounded GPU position scan per admitted finite candidate/pass, unlike meshlet-local culling; no performance equivalence is claimed without device measurements. Renderer-local primitive instance semantics, unproven source-order contracts, billboard/stereo profiles, and other explicitly rejected source profiles remain open. The existing desktop LOD compatibility decision above is unchanged.

The new companion passes production offline cooking. Source review identified and repaired stale-bounds rejection, unfrozen cached indices, indefinite terminal-index warmup, mismatched original-index scalar shape and storage-limit rejection before conservative refit. The managed and Browser build results and disposable production-method evidence are tracked separately from real GPU acceptance; this section does not establish rendered output, device timing, zero-readback runtime traces, queue reuse, or device-recovery acceptance.

The final affected Rendering/WebGPU graph and disposable witness build pass with zero warnings/errors after the index-padding correction. The witness passes 228 production-method checks: all 156 earlier resident/LOD/outline checks plus the exact primitive companion ABI, all four GPU strategy identities without diagnostics, frozen index replacement/revision/count/shape, padded and unpadded uint16/uint32 storage, overflow-safe extents, asynchronous versus terminal index preparation, refit capacity/storage admission, accepted-prefix completion ownership, aborted-frame release, and authored command/target-cache invalidation. The first new witness exposed the canonical 16-byte index padding case; the corrected proof captures that flag and validates the checked padded extent. One hundred warmed full resident and outline publication swaps each allocate zero managed bytes, as do 1,000 warmed exact-index captures. The production companion recooks successfully. The combined native Browser refresh also passes zero warnings/errors but precedes the final isolated padding-proof correction, which is covered by the subsequent affected managed graph and witness. Scratch evidence is under `Build/_AgentValidation/20261001-225000-lit-surface/authored-indirect/`; no tracked tests were added. Real browser output, queue/recovery behavior and device performance remain unverified for this authored traditional indirect route.


## Authored instance publication and conservative GPU visibility

The generic CPU-direct, original-index GPU-indirect and compute-meshlet routes preserve the RenderCommand instance count exactly. They neither multiply it by XRMeshRenderer.SubMesh.InstanceCount nor use that separate renderer-local indirect-buffer count as a replacement. Zero command instances remain a no-op; unsupported primitive-local profiles retain an explicit diagnostic. GPU producers write the complete native instance count into their checked indirect argument record and keep firstInstance zero. The single instanced draw therefore exposes native instance_index values from zero through count minus one, retaining primitive, corner and instance order without requiring the optional indirect-first-instance feature.

AuthoredMeshInstancePublisher is a reusable runtime-only producer attached to an existing renderer and exact mesh. The caller publishes a span of AuthoredMeshInstanceData rows containing real current and previous matrices and a conservative current pre-instance sphere. The physical row is 144 bytes: current matrix at byte zero, previous matrix at byte 64 and sphere at byte 128. Logical published count is separate from buffer allocation capacity, including empty publications. Updates use XRDataBuffer's existing committed upload owner; content and resource generations advance independently when row data, logical count or storage extent changes. The producer creates no identities for missing instances and never reads GPU visibility or count data. Registration and disposal are exception-safe even when buffer-collection observers throw, and replacement bindings belonging to another owner are preserved.

IAuthoredMeshInstanceProvider also permits explicitly owned compatible storage layouts. GpuMeshSubmissionSourceBindings freezes the exact descriptor, logical count, buffer identity, committed CPU revision, physical extent and existing publisher generations in its completion-retained publication. Ambiguous producers, malformed or short rows, changed sources and mismatched selected raster bindings fail precisely. Buffers remain borrowed by culling/raster; the existing source lease, binding-set dependencies and frame-slot retirement own their lifetime. There is no new serialized material, YAML or asset storage format.

The cooked raster must declare semanticSchemaIdentity xrengine.engine.authored-instances.v1, or xrengine.engine.authored-instances-temporal.v1 when it produces temporal output. Both schemas fix engine row-vector matrix bytes as WGSL columns, matching engine matrix uniforms. Runtime admission checks the read-only vertex storage binding's exact row stride, CurrentTransform and PreviousTransform column-major mat4x4 members and PreInstanceBounds vec4 member against the published offsets. Native instance_index addresses the same row used by culling. The raster transform is ModelMatrix times the current instance matrix times the final pre-instance position; the temporal schema uses the corresponding previous instance/model/view matrices. A material, depth/shadow override or auxiliary shader without the same explicit ABI is rejected rather than dropping the instance transform.

Both GPU culling companions read each actual current instance matrix and its declared full-instance sphere from that same bound storage. A candidate is retained when any row is visible. The original draw still contains every authored instance, so invisible members of a visible cohort may be drawn conservatively. Meshlet submission continues to produce ordered GPU indices but does not claim fine meshlet or cone rejection from the full-instance envelope. Pulling clip planes through the complete frozen ModelMatrix and instance matrix preserves conservative behavior under reflection, shear and nonuniform scale. Nonfinite matrices/spheres, negative-radius unbounded declarations and uncertain arithmetic retain visibility. Post-callback raster ModelMatrix and ViewProjection bytes must match the frozen culling values before geometric rejection is enabled; explicit bounds-provider and source culling opt-outs remain authoritative.

A scan budget of 1,048,576 instance-by-meshlet checks bounds geometric rejection work. Exceeding that budget disables rejection while retaining the requested GPU strategy and full native instance count. Untyped static/procedural vertex programs are likewise admitted conservatively with their original instance addressing. Known UI/debug packed storage and all instance-step vertex attributes keep exact count/extent checks. Generic direct draws use the existing bounded per-frame instance override; no distinct renderer or count-dependent draw-cache family is introduced.

Non-temporal shared skin/morph geometry is supported only when the producer explicitly certifies AcceptsSharedDeformedGeometry. The existing renderer/mesh deformation producer has one palette/morph input image and emits one current GPU position stream before raster instance addressing; ResolveAttribute binds that exact stream. The frozen source ModelMatrix convention is preserved, including identity for world-space skinning output. The authored instance matrix acts on those pre-instance coordinates; no second component transform or fabricated local-space conversion is inserted. A distinct instance palette/pose or other unproven instance-local deformation needs its own explicit producer. The temporal instance schema rejects shared deformation until an exact previous vertex stream is available; previous matrices alone do not manufacture previous deformed geometry.

The final focused managed build and disposable instance witness pass with zero warnings/errors. The witness has 58 production checks covering real producer data/counts, immutable publication, matrix byte/composition convention, exact cooked raster ABI, actual canonical skin/morph inputs and packed output attribute selection, malformed/short/overflow layouts, observer-failure cleanup and zero allocations across 1,000 warmed producer/publication cycles. The existing 228-check resident/LOD/outline/index/queue-ownership witness also passes against the new companion ABI. Seventeen actual JavaScript command preparation/replay checks cover generic direct-count ceilings, instance-step ranges, zero and full uint32 counts, native first-instance zero and conflicting ceiling owners. Five production compute companions and two representative authored raster programs (including an actual current/previous-matrix velocity consumer) pass the production cooker. Disposable evidence is under Build/_AgentValidation/20261001-225000-lit-surface/authored-instances/. These results do not establish browser pixels, device timings, queue reuse/recovery or runtime zero-readback traces; live GPU acceptance remains separate.

The combined native Browser build after the final instance and ordered dynamic-buffer changes passes with zero warnings and errors. This closes the implementation build boundary while leaving the rendered, recovery and runtime trace acceptance above open.

## Bounded resident transparent source ordering

The generic indirect and compute-meshlet routes now share a bounded ordering
producer for GPU-owned mesh sources, extended to supported explicitly direct
sources through the typed rank gates below. The
producer observes successful `AddCPU` insertions only inside an explicitly
identified full-resident GPU collection. It captures the actual post-callback
command order, command-level world AABB or fallback position, and the existing
authored transparent priority. It does not derive a tie token from the GPU
scene's compacted dense index or a CPU-frustum-visible subset. The consumed
backend-ready package freezes frame, collection generation, scene, camera and
view identities; incomplete or mismatched publications reject before source
ranking is recorded.

Pass, zero-instance and frozen camera-layer eligibility are applied consistently
before token lookup and candidate replay. A layer excluded by the authored
camera mask cannot require a token that the collection never inserted. GPU
geometric visibility, LOD selection and rank remain GPU-owned.

The desktop comparator and insertion-counter behavior are unchanged. Priority
sorts ascending where the shared transparent pass applies it, distance follows
the configured near/far policy, and equal-distance ties preserve actual
collection insertion order. CPU tree traversal and GPU-mode resident
enumeration can already visit equal-distance sources in different orders; this
producer preserves each selected collection's existing behavior and does not
claim cross-mode identical ties. GPU distance arithmetic uses the same nearest
AABB/fallback rule, including the comparator's NaN ordering, but finite
almost-equal distances still need rendered CPU/GPU parity evidence for device
rounding.

The `authored-indexed::rank-sources` compute companion compares at most 64
logical sources. The `mask-ranked-arguments` companion copies the exact
five-word native indexed argument record into one record per possible rank,
zeroing the inactive counts. Retained raster commands replay rank first and
candidate second. Within the selected source, primitive ordering remains the
authored primitive ordinal; LOD masks, original/generated index streams,
corners, instance counts and zero first-instance semantics remain authoritative.
No GPU visibility, argument count or rank is read back and no CPU rank sort
selects the raster order.

Callbacks, deformation and culling execute once per candidate. Immediately
after their producer commands, retained GPU buffer copies freeze every readable
raster vertex/index/storage input in candidate-owned storage. Uniform offsets
and raster binding groups are retained with those copies. Meshlet-generated
indices already belong to that candidate's completion-owned work slot.
Later callbacks can therefore reuse or overwrite shared palette, vertex or
storage buffers without changing an earlier candidate's deferred raster.
Sampled images are held read-only until replay completes; CPU mutation and
later framebuffer/storage-image writes reject explicitly. Writable raster
storage/images and nested callback raster submissions require separate exact
contracts. Retired original buffer generations do not invalidate their
already-captured copies; obsolete frozen raster dependencies retire through
the existing frame/queue lifetime.

The outer limits are 64 sources, 256 candidates and 4096 source-by-candidate
raster records per pass, with 32 copied input buffers per candidate and 8 MiB
of copied raster inputs per atomic frame. The existing 4097 total frame-command
limit also includes clears, LOD selection, culling/finalization, copies and
masks, so these outer limits do not promise admission of a 64-by-64 cohort.
Capacity failures reject the complete frame. Source ranking is quadratic and
heterogeneous raster pipelines are replayed at every possible rank; GPU memory,
copy and command costs require device measurements.

The cooked output's own pipeline requirements and viewport strategy now request
the existing full-resident collection even when the world retains its global
CPU-direct preference. Its CPU spatial tree remains available to other outputs;
the masked CPU-visible-subset optimization is bypassed only for the selected
authored GPU output. An explicit CPU-direct viewport override retains ordinary
CPU collection. This intentionally selects full-enumeration callback membership
and tie order for a browser viewport-only GPU request; desktop target policy and
global submission settings remain unchanged. Opaque state-bucket ordering and
unproven command subtypes retain specific missing-contract diagnostics.

Both new companions pass the production ShaderCooker. The disposable ordering
witness passes 33 checks of their exact runtime ABI, actual collection insertion
and CPU comparator behavior, immutable source inputs, and frame/scene/view or
incomplete-producer rejections, direct-source replay identity and output-local
collection selection. One thousand warmed source-publication copies
allocate zero managed bytes. The managed WebGPU graph passes without warnings.
Nine checks through the actual JavaScript
command preparation/replay methods cover native copy admission, immutable
shared vertex snapshots, ranked argument offsets, instance preservation,
callbacks once and dependency release. Evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/authored-ordering/`.
These results do not establish browser pixels, physical GPU shader execution,
queue recovery, runtime zero-readback traces or device performance.

## Mixed direct and GPU transparent replay

The shared command graph declares whether its GPU operation also owns no CPU
replay, mesh-only exemptions, or mesh and non-mesh exemptions. Ordinary mesh
passes and capture helpers preserve their full replay intent; depth/normal,
motion and overdraw auxiliaries preserve mesh-only replay and do not duplicate
their callbacks. The backend controls when that already-authorized replay runs.
An unordered pass replays exemptions once before GPU work. An ordered pass
invokes each supported explicit-direct command once through its original
`RenderWithGpuScope`, captures its original CPU-selected renderer/LOD, and joins
the same immutable candidate table as the GPU sources.

Explicit-direct V2 color/coverage materials use a separately cooked, verified
vertex companion. The companion exposes a read-only rank buffer at group zero,
binding two, and a 16-byte dynamic object uniform at binding three. Its source
index, active rank, source count and enable flag are frozen for each replay rank.
The original vertex calculation runs first. An inactive rank moves only the
final clip position outside the frustum; the active rank preserves the original
vertex output and fragment entry, including coverage discard, derivatives,
blend and depth state. No fragment rank discard is introduced. Plain,
directional-shadow-receiving and local-shadow-receiving StandardLitColorV2
outputs are supported, together with the exact generated AuthoredLitV2
local-shadow source whose canonical ABI and provenance match that companion.
Other source families require their own proven companion.

Typed admission verifies the complete canonical source/include hashes, original
descriptor identity, coordinates, vertex inputs, original resources and output
profile, then permits only the two exact gate resources. Native opaque vertex
companions and occupied gate slots cannot silently combine with this contract.
The original material, normal program, parameters, callbacks and authored
render options remain authoritative; surface publication targets the separate
gate program explicitly.

Direct candidates still issue native `drawIndexed` with the original unsigned
instance count and first instance zero. GPU candidates still issue their
original indexed indirect or compute-meshlet lowering. All routes share GPU
source ranks and rank-major replay without a readback or CPU rank sort. Direct
uniform images are captured once per rank after the one authored callback;
all other readable raster inputs use completion-owned GPU copies. The computed
rank buffer is retained through its exact gate resource contract. The existing
4 MiB uniform arena is an additional admission bound, so increasing ranks can
exhaust uniform capacity before the outer candidate/command limits.

Missing or incomplete source ownership, unsupported shader gates, immediate
raster emitted outside captured candidates, and selected non-mesh callbacks
reject the atomic frame. A mesh-only operation continues to omit non-mesh
callbacks by its explicit replay policy. An unfinished direct shader or resource
returns Pending before any ranked raster replay. These profiles retain their
declared routes and do not silently become another mesh submission strategy.

The direct-gate production witness passes 66 checks over seven actually cooked
artifacts, including all three original/gate pairs and a generated AuthoredLitV2
source. It rejects wrong companions, altered provenance, descriptor/ABI/limit
changes and occupied slots. One thousand warmed proof operations allocate zero
managed bytes. The JavaScript executor witness now has 15 checks, including
native direct replay with zero, one, 65537 and full uint32 instance counts.
Shader-gate execution and physical browser pixel parity remain unverified.
Gate evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/authored-direct-gate/`.

The browser content cooker and manifest reader admit the order-gate vertex
profile only for StandardLitColor version two, the forward-coverage pass and
the same three HDR output profiles. Descriptor hashing, declaration matching,
catalog identity and duplicate-key checks remain active. Production packaging
and manifest probes pass 51 cases: seven accepted packages using all three
original/gate artifact pairs, 21 rejected cooker inputs and 23 rejected browser
manifests. The negative cases include semantic/version misuse, unsupported
pass/profile combinations, malformed selectors, descriptor identity/hash or
declaration mismatches and duplicate keys. The standalone content cooker builds
without warnings. These packaging checks do not execute the shaders or establish
physical browser pixel parity; their retained evidence is
`authored-direct-gate/package-probe/` under the same validation run.

## Ordinary indirect startup ownership

The Linux browser job `111504788300` in run `37225704827`, at exact commit
`f628183b852c071ec25b7d6ee8c9daa1010a5121`, reached the selected
`engine-unlit-indirect` profile but failed at the first world swap, before any
draw or frame submission. `GPUScene.MeshSubmission.RendererMissing` came from
first-publication backfill reading a mesh command's render-buffer image before
that command had swapped. Registration had already accepted a valid update-side
renderer and written its resident row. The observed `CpuDirect` / `NeverSubmitted`
status was the untouched renderer diagnostic state, not evidence of a fallback.

The resident command lookup now retains the exact source snapshot supplied to
each accepted Add/Update, alongside its command and primitive identity. Initial
projection backfill uses that saved image with the existing resident metadata,
geometry and material owners. It does not reread live command selectors or force
a command swap. Row compaction moves the snapshot with its source; removal and
scene destruction clear it through the existing lookup lifecycle. Warm updates
replace value entries without allocating. The projection's existing capacity
limit and lazy binding/resource capture remain unchanged, although each resident
lookup entry now retains the additional source-snapshot value.

The same failed frame also recorded `WebGPU.DefaultPipeline.OutputUnavailable`.
The fixture assigned its camera pipeline and stepped collect/swap callbacks
outside the output renderer's owner scope. It now enters the existing concrete
`WebGpuRendererHost.OwnerScope` during construction and around the complete
caller-thread frame, matching production browser lifecycle ownership without
boxing the scope. `BindEngineViewport` only stores a viewport and cannot supply
that ambient renderer context. Both missing-owner guards remain active, and the
authored submission strategy and original Unlit raster sources are unchanged.

Exact Linux job `111516599587` in run `37229683052`, at commit
`051c32f8a0df3dd65bd2738d4125c6219fd9a36e`, compiles these ownership fixes and
reaches real `GpuIndirectZeroReadback` selection without either exception. The
fixture collects ten meshes and creates its HDR target, but still submits no
frames: it waits on `WebGPU.AuthoredIndexed.PublicationPending` because the
resident closure and rendered world have different frame identities. A tonemap
command is prepared, but the one recorded draw does not establish scene raster
or a submitted frame. CPU x1/x4/AO, effects, shadow and physics checks continue
to pass.

The remaining mismatch came from the fixture's parameterless world swap.
`EngineTimer.StepFrame` reserves the upcoming render identity before collection,
exposes it through `RuntimeRenderingHostServices.FrameTiming.CollectFrameId`,
then advances the ambient render clock after swapping. Production
`RuntimeWorldHost` already supplies that reserved identity to the explicit
`GlobalSwapBuffers(frameId)` overload. The fixture now uses the same overload
and reserved identity, aligning the resident closure with the collected package,
source ordering and subsequent rendered world. It does not derive an identity
from a raw collect/swap counter or weaken the backend's frame-equality guard.

Source inspection and `git diff --check` pass. Live validation of this frame
identity correction remains pending on the next exact-commit CI run; no local
.NET build or browser execution was available, and no tests, shader sources or
workflows were changed for this correction. The retained failed runs are under
`Build/_AgentValidation/20261001-225000-lit-surface/reports/ci-f628183b-linux/`
and `reports/ci-051c32f8-linux/` in the same validation run.

## Lifecycle hooks and first draw deferral

Linux job `111528803944` in run `37233797886`, at exact commit
`23dde12cf306c095ae35cf740e0158b20e875aa5`, advances beyond the publication
identity check. The indirect profile still submits no frames within the existing
45-second qualification window. Its last indexed status is
`WebGPU.AuthoredIndexed.CpuReplayPending`, while the renderer is ready, ten
meshes are collected and the last JavaScript resource operation creates the
`engine-unlit-color` binding layout. Earlier browser cases continue to pass.

The fixture does not author CPU-owned mesh islands. Its materials set
`ExcludeFromCpuOcclusion`, which is independent of `ExcludeFromGpuIndirect`,
and its commands do not force CPU rendering. Source inspection identifies a
different dispatch error: the Web Default command chain assigned its selected
GPU strategy to `PreRender`, even though the shared Default contract keeps
`PreRender` and `PostRender` CPU-only. `PreRender` is pass `-1`; the resident
indexed selector interprets a negative requested pass as all passes. That code
path selects scene geometry before the HDR framebuffer is bound; after program
preparation, the existing Unlit output guard rejects the missing framebuffer.
Subsequent empty GPU passes observe the same global pending bit and replace the
visible status with the misleading CPU-replay reason. No resource-lifetime churn
or implicit CPU fallback is established by this failure.

The Web Default hooks now explicitly use `CpuDirect` and set the command-local
`PreserveMeshSubmissionStrategy` option. This default-false option preserves
only explicitly marked command strategies against a viewport's scene-geometry
override; it does not reinterpret custom pass numbers or disable GPU overrides
on ordinary geometry. Scene passes retain the selected indirect strategy,
resident source ownership and all frame/output guards. Actual explicit CPU
islands retain their original replay path.

Indexed deferrals now supply their reason to the existing first-pending-draw
capture. Fixture status includes that first owner/reason and existing program
preparation diagnostics between frames, preserving the first captured deferral
alongside the last indexed status. No timeout, submission gate, shader or test
changes were made.
Independent source review and `git diff --check` are the available validation;
live indirect raster acceptance remains pending on the next exact-commit CI
run. Failed-run evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/reports/ci-23dde12c-linux/`.

## Submitted-packet diagnostic ownership

Linux job `111543054028` in run `37238768014`, at exact commit
`918b91af8fd569ff9d1383d4cf6c7fb68b728426`, reaches ordinary indexed-indirect
raster submission. The first x1 sampling pause records two submitted frames,
40 native compute dispatches, 20 native `drawIndexedIndirect` calls and zero
read mappings. The failure screenshot shows the colored material grid. These
observations establish that the earlier startup and lifecycle-hook blockers
are passed; the color, resize, restart and x4 assertions have not passed for
this route because the first execution-evidence snapshot fails.

The snapshot used the executor's reusable import arena after returning from
managed frame execution, and equated its packet sequence with `lastSequence`.
That watermark also advances on preparation-only submissions, which do not
replace the rendered scene. Managed frame cleanup can also retire retained
command handles before the later snapshot resolves them. The failed CI record
does not include the individual guard operands. A disposable witness through
the actual JavaScript acceptance methods reproduces the sequence mismatch
with one accepted scene followed by one accepted preparation-only packet.

The Unlit diagnostic now wraps only its own engine-frame instance's synchronous
scene-submission method. Immediately after successful submission and before
the import returns to managed command retirement, it copies the bounded
accepted packet's operation, raster-override and attachment metadata. Only the
latest detached metadata snapshot survives; no packet arena, GPU resource or
completion lease is retained. Sampling verifies the renderer owner, surface
generation, exact frame/wrapper identity and actual scene-submission counter.
Preparation-only work does not replace that evidence. Resize invalidates it,
and stop restores the original submission method and releases the snapshot.

A snapshot failure is retained for the sampling call instead of escaping from
an already accepted submission. This preserves the managed acceptance receipt
and completion-owned resource lifetime. Indirect argument values remain
unknown; no count or visibility readback is added, and the existing pixel and
raster assertions are unchanged. Production execution has no added observer,
branch or allocation. The explicitly instrumented Unlit diagnostic does create
bounded metadata snapshots for submitted frames and is not production timing
evidence.

Both touched JavaScript files pass syntax checks, `git diff --check` passes,
and independent ownership review finds no blocking issue. The disposable
JavaScript witness passes 15 focused checks using production acceptance and
command-encoding methods with mocked native WebGPU calls: preparation-only
sequence advancement, exact raster overrides, unknown indirect counts,
detached evidence after handle retirement/arena overwrite, immutable sampling,
latest-scene replacement, stale owner/generation/unobserved-submission rejection,
resize invalidation, stop restoration, and capture/submission failure ownership.
Evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/reports/submitted-packet-evidence-witness.json`;
failed browser artifacts are under
`Build/_AgentValidation/20261001-225000-lit-surface/reports/ci-918b91af-linux/`.
This source-level validation does not establish corrected browser acceptance.
Local browser launch remains blocked by the execution environment's Unix-socket
restriction, so rendered x1/x4 acceptance remains pending on exact-commit CI.

## Coherent accepted-frame sampling

Linux job `111551509123` in run `37241689117`, at exact commit
`996101dbefeca794b59e61870ec37ab8175558be`, passes the first x1 submitted-packet
snapshot and all nine authored HDR/display center comparisons. Maximum HDR
component error is approximately `0.0002297794`; display-byte error is zero.
The sampling pause again records two accepted frames, 40 compute dispatches,
20 indexed-indirect calls and zero read mappings. The nine explicit HDR copies
account for all nine later read mappings, with no rendering during the pause.

The next failure is a mixed-frame diagnostic observation. The retained accepted
packet has sequence 340, while the executor has processed 342 acceptance calls:
two scene frames and 340 preparation-only attempts. `GetUnlitState` reads the
current managed attempt, whose indexed reason is `CpuReplayPending` and draw
count is one. The pause previously required only historical successful frames,
so a later incomplete attempt could run between readiness polling and pause.

Source tracing rules out both a missing successful status update and an
indirect draw-counter omission. `IndexedResult` stores the successful `Ready`
reason at the end of each admitted indexed pass. `RecordIndirectCore` increments
`CountEngineMeshDraw` for each recorded indirect raster with nonempty scissor,
and `BeginEngineFrame` resets that counter for every attempt. The fixture's
successful `Frame` return itself requires at least all ten authored materials.
The later incomplete attempt overwrites those values; its global pending flag
can also be reported by a later CPU-exempt replay check. That last reason does
not establish that this fixture authored a CPU-owned scene island.

`pauseUnlitFrames` now waits for a new successful managed `Frame` with no retiring
resources and pauses its next scheduled frame in the same synchronous task.
The smoke sampler awaits that pause before reading managed selection metadata,
the retained packet, API counters or pixels. It therefore observes one accepted
frame without manufacturing a successful state or retaining stale managed
counters. The existing `Ready`, draw-count, packet and pixel assertions are
unchanged, as are all production submission paths and the synchronous effects
diagnostic pause.

The single pending pause owns an exact session, epoch, renderer, renderer owner
and surface generation. Failure, stop, resize, replacement or its fixed 45-second
deadline rejects the request and clears its timer; duplicate requests cannot
replace it. The successful path cancels the next animation callback before
resolving, preventing another authoring attempt during sampling.

JavaScript syntax checks and `git diff --check` pass. Independent review finds no
blocking callback, promise or retirement-lifetime issue. A disposable witness
using the actual diagnostic host methods passes 13 focused cases covering
pending-versus-accepted frame selection, settled resources, synchronous pause
before promise continuation, duplicate requests, timeout, stop, frame failure,
resize, owner/session/epoch/renderer/generation replacement and unchanged effects
pause behavior. Its controlled frame results are not GPU acceptance. Evidence is
under
`Build/_AgentValidation/20261001-225000-lit-surface/reports/unlit-accepted-pause-witness.json`
and `reports/ci-996101db-linux/` within that same task evidence root. Corrected
full x1/x4 selection, resize and restart acceptance remains pending on physical
exact-commit browser CI.

## Bounded partial-cache ownership during resize qualification

Linux job `111561772594` in run `37245259916`, at exact commit
`c8bf98f6cca664aaade0e3706dbdfb9ceccd2681`, confirms the synchronized pause works.
The initial 512×512 and resized 640×384 x1 stages both report `Ready`, eleven
recorded mesh draws, valid indexed execution and read-map accounting, and all
nine HDR/display centers pass. This is actual Chromium browser execution using
the explicitly selected software adapter, not physical-hardware acceptance.

The subsequent reuse assertion incorrectly equated a fixed logical pipeline
alias count with warmed native-object reuse. The initial sample contains 187
live resources, 32 buffers and eleven render-pipeline aliases. Before resize,
the resumed sample already contains 189 resources and 33 buffers, adding 32
logical buffer bytes. After resize there are 192 resources and twelve pipeline
aliases. Only `engine-unlit-color` gains a pipeline alias; its shader alias
count remains two, and the total shader count remains thirteen. All native
identities are reused, with eight shader-module cache entries and thirty
pipeline-cache entries unchanged. No initial pipeline handle survives resize.

The existing three-slot authored pool prepares work lazily and searches the
first available slot. A short overlap can begin preparing a spare slot's first
source before the frequently reused slot becomes available again. Its
completion-reclaimed work objects deliberately retain argument buffers and
prepared commands for reuse. A twenty-byte indexed argument record reserves
32 bytes, matching the observed buffer increment. Framebuffer retirement already
invalidates dependent draws in every slot, including partial draws, and a late
pipeline preparation retires its result when its draw was disposed. These
findings establish an invalid warmup assumption in the diagnostic; they do not
establish a production resource leak. Production selection, allocation and cache
retention are unchanged.

Cold diagnostics now report the existing slot/selection/work owners, including
published and ready-unclaimed buffer handles, zero handles for unfinished work,
each retained pipeline handle and its captured output generation. The getter
requires a between-frame boundary and does not generate or select resources.
Only the indexed Unlit diagnostic observes resource publication: a weak map
references the executor's existing dependency arrays, which normal release
clears. The cold inventory resolves their exact logical handles, including a
command's actual pipeline alias and each attachment's owning texture. It adds
no production observer or separate GPU-resource lease.

Qualification now accounts explicitly for all three slots, bounded by this
fixture's ten x1 or twelve x4 sources per slot and the existing production
capacity limits. Fixed-size selection/argument owners cannot retain two physical
generations. Each compute group must belong to the exact slot and source;
command fanout is bounded, every raster alias has one managed work owner, and
every retained attachment must reference a current target. Partial owners are
reported even before their first command exists. The union of those resources
is separated from the remaining inventory, which must remain identical across
resize and restart. No arbitrary resource-count allowance is used. Full logical
alias lists remain in the report; native identity sets, cache-entry counts,
ordinary zero-readback checks, pixels and teardown checks remain strict.

All four affected JavaScript modules pass syntax checks and `git diff --check`
passes. Independent review found no blocking source or lifetime issue; its
fixed-buffer-generation and same-source-ordinal tightenings are included. A
disposable witness exercises the actual JavaScript resource table, command
publication and diagnostic inventory, plus nineteen positive/negative ownership
checks. It covers partial unclaimed buffers, pipelines without commands,
distinct aliases of one native pipeline, orphan/multiple owners, stale outputs,
retired or missing dependencies, duplicate commands, capacity limits, unrelated
retention, changed native identities and observer teardown. Evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/reports/indirect-cache-ownership-witness.json`;
the prior browser artifacts are under `reports/ci-c8bf98f6-linux/` in that task
root. Managed compilation and the corrected complete x1/x4 browser matrix remain
pending on exact-commit CI. In particular, CI must confirm the cold managed and
JavaScript inventories agree if a spare pipeline finishes asynchronous
preparation near the sampling boundary. Physical-device acceptance remains a
separate requirement.

## Explicit validity for transparent source distances

Linux job `111593483025` in run `37256129926`, at exact commit
`08822920aca51af3a26328ede27471a69a669370`, reaches the new
`gpu-indirect-x4-blended` profile and rejects
`engine-authored-rank-sources:32:20`: `value nan cannot be represented as 'f32'`.
The shader constructs a constant NaN with `bitcast<f32>(0x7fc00000u)` when an
AABB, fallback position or camera component is NaN. This is an explicit WGSL
recipe and source, not Slang-generated output. The browser rejects the module
before any draw submission; shader validation correctly exposes the defect.

The distance helper now returns a function-local `SourceDistance` containing
the squared value and a validity bit. Unknown inputs and runtime NaN distance
results return finite zero with validity false. The comparator handles validity
before comparing numeric distances, preserving .NET `Single.CompareTo` NaN
placement: first for near-to-far, last for far-to-near. Two unknown distances
still use the exact insertion/source-index ties. Signed priority, numeric
distance arithmetic (including infinity ordering), the 64-bit insertion token,
dispatch bounds and buffer-capacity guards remain unchanged.

Distance validity is ordering information, not visibility. Every admitted
source still receives a rank; the existing candidate culling, LOD selection,
zero-count indirect arguments and rank masking remain authoritative. The new
structure does not cross a resource binding, so the source/rank/uniform ABI and
recipe are unchanged. An audit of the seven authored-ordering, indirect and
meshlet WGSL companions found no other constructed constant NaN or infinity.

`git diff --check` and independent source review pass. A disposable JavaScript
witness executes the extracted old and new comparator bodies and confirms
874,800 pair comparisons plus 3,456 source ranks across all three sort policies,
priority enabled/disabled, finite/nonfinite distances, signed-priority extrema,
64-bit token boundaries and duplicate-token source-index ties. Each bounded
64-source cohort still produces a unique rank permutation. The witness also
checks unchanged resource declarations, dispatch guards and the tie-breaking
tail. Its report is
`Build/_AgentValidation/20261001-225000-lit-surface/reports/authored-rank-validity-witness.json`.
This is source/logic evidence only. No local .NET shader cook, WGSL frontend or
browser/GPU execution was available; exact-commit CI must still qualify module
creation and transparent pixels through resize and restart. No tests or
validation suppression were added to the production tree.

## Buffer-backed positions during meshlet cooking

The first `StaticMeshletParity` publication at commit
`0ed9f0a601760b8537f14b42019f665e5c5fecf0` reaches the actual native meshlet
builder and terminates with access violation `0xC0000005` in
`NativeMeshOptimizer.MeshoptBuildMeshlets`. Windows job `111618308672` in
[run 37264491996](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37264491996)
had already published the other five game bundles. The static meshlet bundle
was not produced, so its browser comparison did not run.

Source inspection identifies a position-pointer/count mismatch. Modern cooked
mesh hydration restores `VertexCount`, topology and render buffers without
rebuilding the optional authoring `Vertices` array. The meshlet stream reads
those buffers through `GetPosition`, but native clustering previously packed
`Vertices` while passing the independent nonzero `VertexCount`. That can pin
an empty array and pass a null position pointer to the native builder. The
existing package's builder signature agrees with the managed declaration.

Position packing now reads the current render buffers for exactly
`VertexCount` entries, and the same packed array supplies clustering and bounds.
The native boundary rejects null or mismatched position/count inputs before
pinning, including a nonempty index stream with zero vertices. This keeps the
existing meshlet algorithm, source topology, native dependency and cooked
format. It does not force reconstruction of every cooked mesh's authoring
objects. The separate LOD-generation path that copies authoring vertices is
outside this repair and is not called by the browser dependency cooker.

Native cook provenance advances from `interop:2` to `interop:3`. A mesh with a
nonempty but stale authoring array could previously record the current buffer
hash while deriving cluster bounds from older positions. Existing validation
checks finite bounds and source identity, not whether those bounds enclose the
current positions. The provenance change invalidates such derived cook-cache
entries and makes publication rebuild them from the corrected source. Payload
layout, codec version, runtime compatibility and readers are unchanged; old
published bundles remain readable and need recooking to repair affected bounds.
Authored YAML is not automatically rewritten.

The position repair at `4dbec0d42fb2992fed6402a816476445b912439c` passes the
Linux build/pixel/physics job and Windows Editor job in
[run 37283823423](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37283823423).
Both static meshlet GPU and CPU worlds now cook and publish through the actual
native builder. Their bundle ZIP hashes are
`332cf6422d9e8511462b921f936c9f898c4ae9ee2f77078a61f1cb41e0b3f19f`
and `3475d165e9d137796777ee643c84bb186fe23f77e67655b8066919655cd031fc`.

Browser job `111689332709` executes the real meshlet selection, expansion and
finalization dispatches and indirect depth/color draws with zero read maps.
The initial GPU and CPU PNGs are byte-identical, SHA-256
`c04e0316e85eec5bbf3b108fd4e3a81b4c3e2db864444f3dbdf6d92544805149`;
visual inspection confirms the mapped lit checker panel. The job subsequently
fails its strategy assertion because it samples the next frame while raster
resources are preparing, after fifteen frames had been submitted. This is not
complete meshlet acceptance: GPU resize and the second startup were not reached.
The follow-up observer freezes its host and GPU evidence only when a real
submitted-frame counter advances with a ready meshlet result. Shader hashing
uses the frozen raster entries; after hashing, the observer rechecks the
selected renderer's current ownership, host health, session, epoch, surface
generation, extent and cumulative readback/direct-draw guards. A newer accepted
frame cannot replace the selected renderer identity during that check. Resize
requires a new submitted frame, surface generation and extent. Pixel, original
shader, requested-mode, two-startup and teardown assertions are retained.
Partial comparisons are saved before later assertions. Independent source
review, Node syntax checks and disposable frozen-raster/host-frame/owner-race
witnesses pass; exact-commit browser acceptance remains pending.

The provenance correction passes independent source review and whitespace
checks; its exact-commit compilation and cook-cache behavior remain pending.
No meshlet runtime acceptance checkbox is closed by these partial results.
The original Windows failure evidence ZIP has SHA-256
`4f94fbfe3b1ca97974f27634ec7a867c98d1342fb2cae89a21c440d426fb7649`.
The new browser evidence ZIP (artifact `11334932655`) has SHA-256
`9603b735a768d9e145ea444544ea6e0ce3c6c994abda5a431483cdf099ae5056`.

## Browser acceptance: bounded static Default meshlet cohort

On 2026-10-05, the exact published source commit
`2b97bda4075563efc9f2558832c6fbcf57611f31` passes
[run 37290314776, job 111708082004](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37290314776/job/111708082004).
The Windows Editor freshly cooks and publishes both CPU and GPU bundles for
the same static `StaticMeshletParityWorld`; the shared
`NativeMeshOptimizer.InteropVersion` 3 build is included. The browser uses
Chromium 153.0.8010.12 with the software WebGPU adapter. This closes only the
static Default meshlet acceptance leaf recorded in the unified browser TODO.

Two fresh GPU contexts each render the initial 977×550 extent and then resize,
one to 813×457 and one to 893×502. Each initial/resized GPU image is compared
with the matching CPU render: all four comparisons have mean RGB error 0,
zero mismatched pixels and silhouette overlap 1. The GPU path executes actual
`meshlets::select-lod`, `meshlets::cull-expand` and
`meshlets::finalize-indexed` dispatches followed by indexed-indirect draws;
there are zero direct mapped-scene draws and zero read maps in the GPU captures.
Fullscreen post-processing draws remain direct. Both
CPU and GPU captures use the exact authored Standard Lit local-shadows raster
program, with WGSL SHA-256
`197db6f431fdbc3340609ead56a415b06982408b98b6d41f751d21c0009c66a5` and the
same `standardLitVertex` / `standardLitFragment` entry points. The selected
output profile is `linear-hdr-local-shadows-v1`.

All three renderer teardowns (one CPU baseline and two GPU contexts) finish
with zero live or retiring resources, cached pipelines or shader modules, and
readback tickets. The host reports `aa=None`; its configured `msaa=4` is not
active multisample rendering in this cohort. The GPU snapshots report
`unbounded=true` because the vertex bounds are undeclared, so conservative
geometric rejection is disabled. This result makes no bounded-culling or
performance claim and covers only one static, single-LOD, single-instance
mapped panel on a software adapter. It does not qualify physical devices,
deformation, dynamic LOD, multiple instances, other custom or native Advanced
profiles, broader material output, or unsupported-profile diagnostics.

The acceptance summary and full smoke report are under
`Build/_AgentValidation/20261001-225000-lit-surface/reports/ci-2b97-static-meshlet/`;
that folder also contains the four GPU captures. Artifact `11337755080` has
ZIP SHA-256
`45df17ad7b23c90df11accda0d8b35cf2deeab14c5379e1baa869218b11c8477`.
The source contract still requires rejection of incompatible old meshlet
payload provenance, but this run did not separately exercise an old disk-cache
payload rejection.

## Pending physical-buffer ownership diagnostics

On 2026-10-06, Linux job `112130243687` in
[run 37419366197](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37419366197)
at commit `d3f17ddf3177ffbefca569681c5766937e8daa09` failed the unchanged
`BrowserSmoke.UnlitIndirectCacheOwnership` assertion. The second x4 lifecycle
failed after its return resize to 512×512, at accepted frame sequence 537,
pipeline resource generation 4 and executor output generation 3. The complete
230-resource inventory contains one unmatched 16-byte selection buffer,
handle `196612`, with usage `136`. Slot 1 contains its second selection owner,
but that owner reports both its published and pending handles as zero. All
eleven preceding captured x1/x4 ownership stages pass.

The physical creation journal publishes a candidate handle before its
asynchronous validation scopes finish. A submitted request returns no physical
handle to managed code until a later frame accepts its ready receipt. The
diagnostic pause can stop on a successful frame from another slot during this
interval. The spare slot still owns the request, while the old diagnostic
reports only published and ready-unclaimed handles. The retained report does
not include request identities, so it cannot prove the historical transport
identity separately. Its exact unmatched resource and the source receipt
contract identify this missing observation boundary; they do not establish a
production leak.

The diagnostic now includes each pending buffer's exact request identity,
backend generation, state and immutable descriptor. Its same-session executor
capture resolves that identity to the physical candidate and validates the
descriptor, request state, packed resource generation and resource-table owner.
It records the candidate only in the diagnostic pending-handle field. The
candidate remains unaccepted: the capture does not claim the request, change
readiness, advance preparation, submit a frame, wait for GPU completion or add
a resource lease. Production rendering and receipt acceptance are unchanged.
The existing complete-inventory, unique-owner, descriptor, dependency,
pending-resource and frame assertions remain unchanged.

Both affected JavaScript modules pass syntax checks and `git diff --check`
passes. The focused `XREngine.Runtime.Rendering.WebGPU` build passes with zero
warnings and errors using the existing .NET 10.0.401 SDK, local package cache,
single-node MSBuild and disabled workload resolution. Its log is
`Build/_AgentValidation/20261001-225000-lit-surface/reports/indirect-pending-ownership-build.log`.
This plain `net10.0` build does not compile the browser WASM host.
Exact-commit browser acceptance remains pending.
The required live boundary is the complete ordinary indirect x1/x4/AO matrix,
including resize, restart, retained request identity, zero ordinary read maps
and teardown. Source checks do not qualify browser output or physical hardware.
The retained failing artifact is `11393920638`, ZIP SHA-256
`dd0fd807a1a2b8355494808b2c015f9c34c3cbbe642244840f28817803f71159`.
Its report is under
`Build/_AgentValidation/20261001-225000-lit-surface/reports/bare-host-d3f17ddf/`.

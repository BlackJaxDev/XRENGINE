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

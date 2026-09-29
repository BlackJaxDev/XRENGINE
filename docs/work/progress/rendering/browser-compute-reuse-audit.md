# Browser compute reuse audit and integration

**Date:** 2026-09-29. **Status:** Source audit and implementation; execution and
physical-device qualification remain deferred at the user's request.

## Reuse decisions

The audit used the existing branch source before adding backend code. Desktop GPU
features already exist. The missing work is their WebGPU execution and portable
data boundary, not a new animation controller, scene database or occlusion design.

| Existing source | Decision | Browser implementation |
| --- | --- | --- |
| `XREngine.Runtime.Rendering/Rendering/SkinPaletteMatrix.cs` | Compile the same dependency-free 48-byte affine palette type into the portable assembly | The managed adapter writes canonical palette records directly; no competing matrix format |
| `Rendering/Compute/SkinningPrepassDispatcher.cs` and `Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepass.comp` | Preserve packed Core4 indices/UNorm8 weights, spill records, sparse quantized morphs and deformation semantics; do not import native renderer/global-state dependencies | `GpuSkinning` and WGSL lower those records to bounded WebGPU storage bindings and dispatch before rendering |
| `XREngine.Runtime.Rendering/Commands/GPUIndirectRenderCommand.cs` | Compile the existing `BoundsGpu` contract unchanged | Frame packets carry its 64-byte world bounds record |
| `Rendering/Commands/GPUScene/` publication, IDs, material/mesh maps and BVH | Keep their ownership canonical; do not create another browser scene database or ID allocator | Lower the focused pipeline's existing published/sorted candidates into transient backend draw/bounds buffers |
| `GPURenderPassCollection.Occlusion.cs::BuildHiZPyramid`, `GPURenderHiZInit.comp`, `HiZGen.comp` | Reuse standard-depth initialization and MAX reduction semantics | Separate WebGPU passes and mip views; conservative odd-dimension footprints include all source pixels |
| `Compute/Occlusion/GPURenderOcclusionHiZ.comp::HiZOccludedAabb` | Port the conservative eight-corner visibility path | World AABB projection, uncertain near/viewport edges kept visible, bounded mip sampling, nearest depth versus farthest pyramid depth with epsilon |
| Older `Compute/Culling/GPURenderHiZSoACulling.comp` | Do not use as the canonical occlusion port | Its simpler center/sphere test is not copied into the browser |

Paths abbreviated in the table are within the existing rendering project or shader
compute directory. The two shared C# source files are added to the portable compile
list, not copied into new browser-specific types. WGSL and GPU binding code are
backend implementations of those contracts; C#/GLSL code cannot be submitted
directly to WebGPU.

## Scope of existing sample duplication

`BrowserCpuAnimator`, `BrowserSkeleton`, `BrowserAnimationClip` and the two-bone
sample predate this audit. They remain bounded reference fixtures, not the engine's
production animation state machine or a replacement for its imported clips. The
compute path consumes the fixture's final palette through `SkinPaletteMatrix` and
canonical packed input records. The CPU fixture has matching quantized weights and
a small morph for later side-by-side comparison.

Do not grow these fixtures into a second production animator. Production animated
content should adapt the existing engine animation/mesh cooking output into the
packed deformation capability. The static cooked-content profile does not yet
admit arbitrary animated assets. Likewise, the small offscreen culling reference
remains a diagnostic fixture; the integrated scene path now consumes real published
draws instead of extending that fixture into another scene database.

## Integrated execution

The frame packet is version 2 with 304-byte draws: the original 176-byte draw,
canonical 64-byte world bounds, then a 64-byte per-view projection. Each view keeps
its viewport and ordering. Scene-node models must be affine. CPU sorting and
publication remain the existing path; the GPU receives the candidate set when a
GPU strategy is explicitly selected.

The backend writes one indexed indirect argument record per published draw, with
`instanceCount` zero or one and `firstInstance` zero. Each draw binds its own
instance slice. No draw-count readback, arbitrary bindless material table or
descriptor indexing is required. The original order preserves transparent sorting;
material bindings remain bounded and explicit. CPU-direct submission retains its
existing compatible-draw instancing.

Dirty skin/morph jobs run before shadow, occluder and color passes. Unchanged
deformation outputs are reused. The compute output supplies position/UV vertices
and transformed normal/tangent attributes. The selected flat-Lambert material still
uses geometric derivatives; richer smooth-normal materials are a separate profile.
Until conservative deformation bounds are supplied, deformed meshes stay visible
and cannot become occluders. This avoids using stale bind-pose bounds to reject a
skinned or morphed mesh.

Hi-Z uses a **dedicated current-frame depth pass** containing explicitly designated
opaque rigid occluders (`BrowserMeshComponent.IsOcclusionOccluder`). Those objects
are excluded from occlusion rejection. It does not use a whole-candidate forward
prepass to reject those same candidates. The built-in backdrop is designated; other
scene content must opt in. Transparent/masked candidates may be rejected behind
opaque depth without becoming occluders themselves. Every frame rebuilds depth and
the pyramid; there is no newly invented temporal visibility/history system.

Resize stages all raster targets, pyramid mip views/groups and visibility bindings
before publishing a replacement generation. Abandoned candidates retire without
replacing the current generation. Passes separate storage writes, depth writes,
sampling and indirect consumption. Device/owner checks and existing deferred
resource retirement govern the backend resources.

## Explicit selection and comparison

The browser's **Renderer counters** panel provides submission and skinning choices
with an explicit restart action. Equivalent startup parameters are:

| Selection | Behavior |
| --- | --- |
| `?strategy=Auto` or `CpuDirect` | Existing CPU-direct scene submission; CPU visibility checkbox remains available |
| `?strategy=GpuIndirect` | GPU initializes bounded arguments; candidates remain visible |
| `?strategy=ComputeCulling` | GPU world-AABB frustum rejection and bounded indirect submission |
| `?strategy=HiZ` | GPU frustum rejection plus dedicated-occluder depth pyramid and conservative occlusion |
| `&skinning=Compute` | Explicit compute deformation for an admitted animated scene |
| `&skinning=Cpu` | Existing CPU reference deformation |

GPU choices are explicitly experimental. Automatic selection stays CPU-direct with
CPU deformation until correctness and total-frame-cost evidence justify a change.
Unavailable device limits, unknown strategies, meshlet requests, or required
deformation on a scene without admitted data fail visibly. No required GPU mode
silently switches to CPU. Selecting a GPU mode does not claim the desktop
`GpuIndirectZeroReadback` capability family, GPU BVH or meshlet support.

Counter snapshots expose selected strategy, indirect/occluder draws, dispatches,
pyramid rebuilds, deformation work/residency, and existing resource/bridge costs.
CPU-side candidate counts are not GPU visibility results; determining visible
counts would require separately classified diagnostics. Rendering does not read
those counts back.

For physical-device comparison, keep world/camera/quality/instance count fixed,
compare CPU-direct with culling off/on, indirect without culling, compute frustum,
Hi-Z and CPU/compute deformation, then repeat under sustained load. Compare total
frame cost, allocations, memory and thermal behavior, not draw calls alone. This
workflow has not been executed and no timings or preferred GPU default are claimed.

## Remaining qualification and broader integration

Build/browser/shader execution, CPU/GPU deformation parity, normal/tangent and
negative-scale cases, conservative visibility, odd-size/resize/loss handling,
allocation budgets and sustained mobile measurements remain open. The source
delivery does not waive those acceptance requirements.

Full desktop GPUScene/BVH publication, meshlets, arbitrary cooked animated worlds,
reversed-Z and richer material support remain broader integration work. They must
reuse the canonical systems above rather than become independent browser copies.

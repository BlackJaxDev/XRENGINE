# Vulkan Editor Steady-Frame CPU Cost Investigation (2026-07-30)

Last Updated: 2026-08-04
Owner: Rendering / Vulkan
Status: Closed as a root-cause and implementation record on 2026-08-04. Broad
workstream acceptance is owned by the 03-05 validation ledger; the newer
directional-cascade stability regression is owned by the
[Directional Light Vulkan Stability Investigation](../directional-light-inspector-shadow-2026-08-03.md).

Related plans:

- [Workstream 04 completion and validation](../../../testing/rendering/03-05-optimization-validation-todo.md#workstream-04-completion-and-validation)
- [Workstream 05 validation](../../../testing/rendering/03-05-optimization-validation-todo.md#workstream-05-validation)
- [Vulkan Command Recording Architecture Optimization](../../../todo/rendering/optimization/vulkan-command-recording-architecture-optimization-todo.md)
- [CPU Direct Fast Path](../../../todo/rendering/optimization/cpu-direct-fast-path-todo.md)
- [01-08 Optimization Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md)

## Executive Conclusion

The current steady frame is not slow because Vulkan is recording command
buffers, compiling pipelines, waiting for a swapchain image, running validation,
allocating managed memory, or using too few command-recording workers.

It is slow because the same logical binding data is reconstructed and copied in
two consecutive serial phases:

1. The scene/package-consumption phase re-emits material, camera, light, time,
   viewport, and callback values into mutable program dictionaries for each
   draw, then copies those dictionaries into a draw snapshot.
2. The Vulkan frame-data refresh phase either constructs a reflected
   auto-uniform template or copies that complete template for each reusable
   draw, still scans the reflected members to find per-draw patches, and
   revalidates descriptor state.

The measured frame contained 647 render commands. Its dominant leaf costs were:

- 60.3 ms re-emitting material and other program bindings;
- 55.2 ms copying those bindings into draw snapshots;
- 120.2 ms processing reflected auto-uniform blocks: template
  construction/copy, reflected-member scan, and per-draw patching.

Those three leaf stages total 235.6 ms across the frontend and backend phases.
They must not be added to their parents as independent time.

The implementation therefore satisfies only the identity, ordering, and
lifetime portion of workstream 04. It does not satisfy workstream 04's
backend-ready binding-data handoff. Workstream 05 behaves correctly on this
frame: all stable chains are reused and no worker is invoked. Adding worker
recording cannot remove work that happens before or independently of command
encoding.

The required architectural change is to make binding data frequency-aware and
change-driven. An unchanged frame must reuse packed frame/view/pass/material
payloads, object slots, descriptor topology, and command artifacts without
walking materials, string-keyed dictionaries, reflected UBO members, or every
visible draw.

## Correction To The Initial “Present” Interpretation

The initial summary was directionally correct about duplicated binding work but
too literal about the profiler's `Present` output:

> 172.5 ms scene CPU is almost entirely redundant binding work. Present then
> blocks another 226.9 ms because the CPU starves submission.

`Present.present_cpu_ms = 226.9 ms` is the engine's outer swap/present output
scope. It contains the Vulkan `SwapBuffers` lifecycle, including scene command
processing, reusable-command frame-data refresh, and queue submission. It is
not 226.9 ms spent in `vkQueuePresentKHR`.

The actual measured Vulkan present call was 0.113 ms. Fence wait and image
acquisition were also negligible. The corrected interpretation is:

- 172.5 ms is frontend command/package consumption and draw preparation;
- 226.9 ms is the backend swap/present wrapper, dominated by 216.6 ms of scene
  command processing and, inside it, 170.2 ms of frame-data refresh;
- only 0.113 ms is the native present call.

This correction strengthens the CPU root cause. There is no large present wait
to tune away; almost the entire frame is engine-side CPU work before the final
present call.

## Measurement Manifest

The exact point cited in this document came from frame 1743 of the isolated
session `vulkan-black-close-fix-0730`:

- build: Debug;
- mode: Unit Testing World, desktop Vulkan;
- display: 1920 x 1080;
- internal viewport: approximately 1286 x 723;
- camera: stable inspection view at approximately `(20, 12, 20)`, looking at
  `(0, 2, 0)`;
- submission strategy: CPU Direct;
- command chains: enabled;
- occlusion culling: disabled;
- Vulkan validation messages: zero;
- GPU timing: disabled for this diagnostic capture.

The engine-owned session evidence remains under
`Build/_AgentValidation/mcp-sessions/vulkan-black-close-fix-0730/`.

This is a root-cause diagnostic point, not a canonical Release promotion
result. Debug amplification means the exact millisecond values must not be used
as final performance claims. The stage dominance, call graph, stable reuse
state, and source ownership are sufficient to reject the current architecture.
Canonical Release cohorts remain mandatory before acceptance.

## Exact Frame Decomposition

### Outer outputs

| Scope | CPU time | Meaning |
| --- | ---: | --- |
| Whole frame | 399.580 ms | Entire measured frame |
| Desktop scene render | 172.468 ms | Consume commands and prepare/enqueue Vulkan draws |
| Present output wrapper | 226.904 ms | Vulkan swap lifecycle, including backend scene processing and submit |
| Collect visible | 5.999 ms | Visibility/package producer work; reported separately from render CPU |

The scene and present-output scopes account for approximately 399.4 ms of the
399.6 ms frame. The render thread was not waiting for collect-visible.

### Frontend scene/package-consumption phase

| Nested stage | CPU time | Approximate cost per 647 commands |
| --- | ---: | ---: |
| Desktop scene render | 172.468 ms | 266.6 microseconds |
| Mesh draw preparation | 162.502 ms | 251.2 microseconds |
| Mesh draw resource preparation | 15.007 ms | 23.2 microseconds |
| Mesh draw binding preparation | 130.665 ms | 202.0 microseconds |
| Material/program binding emission | 60.265 ms | 93.1 microseconds |
| Binding snapshot copy | 55.187 ms | 85.3 microseconds |
| Mesh draw enqueue | 5.554 ms | 8.6 microseconds |

Material emission and snapshot copy are children of binding preparation.
Binding preparation and resource preparation are children of mesh draw
preparation. These values describe where time is spent; they are not
independent totals.

### Backend swap/present-wrapper phase

| Nested stage | CPU time | Interpretation |
| --- | ---: | --- |
| Vulkan frame lifecycle | 226.899 ms | Almost the entire outer Present output |
| Record-command-buffer wrapper | 216.600 ms | Includes reuse validation and frame-data publication |
| Record-scene-command-buffer wrapper | 216.598 ms | Stable scene processing; not necessarily native recording |
| Frame-op preparation | 20.769 ms | Per-frame operation preparation |
| Resource planning | 0.083 ms | Not a current bottleneck |
| Frame-data refresh | 170.201 ms | Dominant backend work |
| Descriptor validation | 30.187 ms | Revalidating reusable descriptor state |
| Engine-uniform upload | 4.836 ms | Engine-owned uniform publication |
| Auto-uniform upload | 120.188 ms | Template construction/copy, reflected-member scan, and per-draw patching |
| Dependency snapshot | 6.830 ms | Reusable dependency processing |
| Command-buffer reuse | 11.184 ms | Fast-signature/reuse checks |
| Queue submission | 9.964 ms | Submit-side CPU work |
| Fence wait | 0.059 ms | Negligible |
| Acquire image | 0.015 ms | Negligible |
| Native present | 0.113 ms | Negligible |

The profiler uses “record” scopes for the path that ensures a usable command
artifact. On this frame, the artifact was reused. The large 216.6 ms value is
therefore not evidence of 216.6 ms of `vkCmd*` encoding.

### Stable-reuse evidence

- 22 command chains were scheduled.
- All 22 secondary chains were reused.
- Zero chains were recorded.
- Zero command-recording workers were dispatched.
- The primary command buffer was reused.
- Pipeline activity was one cache hit, zero misses, and no compilation.
- Vulkan validation reported no warnings or errors.
- Managed allocation in the instrumented mesh-preparation stages was
  effectively zero (504 bytes in this sample).

This is the desired workstream-05 behavior for an unchanged frame. The
remaining cost is workstream-04/data-publication work that command-buffer reuse
does not currently reuse.

## Source-Level Dataflow

Primary source anchors:

- [`BackendReadyFramePackage`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/BackendReadyFramePackage.cs)
  and
  [`BackendReadyMeshSelection`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/BackendReadyMeshSelection.cs);
- [`XRRenderPipelineInstance`](../../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs),
  [`RenderCommandCollection`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs),
  and
  [`RenderCommandMesh3D`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandMesh3D.cs);
- [`AbstractRenderer` material binding](../../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs),
  [`VkRenderProgram` snapshot cache](../../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Bindings.cs),
  [`VkMeshRenderer` snapshot ownership](../../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs),
  and
  [`VkMeshRenderer` draw/refresh path](../../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs);
- [`VkMeshRenderer` reflected uniform writer](../../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs)
  and
  [`ComputeDispatchSnapshot`](../../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/ComputeDispatchSnapshot.cs);
- [`VulkanRenderer` reusable command processing](../../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanRenderer.CommandBufferRecording.cs);
- [`VulkanShaderAutoUniforms`](../../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderAutoUniforms.cs),
  its
  [`declaration/block rewrite`](../../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderAutoUniforms.DeclarationParsing.cs),
  and the
  [`descriptor tier constants`](../../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLayoutCache.cs).

### 1. Workstream 04 publishes identity and live references

`BackendReadyMeshSelection` records pass identity, a stable query key, the live
render command, live mesh and material references, render options, instance
data, eligibility flags, revisions, and a dependency signature.

`BackendReadyFramePackage` stores ordered passes and these selections. It does
not store:

- packed uniform bytes;
- a frequency-separated binding payload;
- a compiled binding-copy plan;
- resolved descriptor tier handles;
- stable dynamic offsets or object-data slots;
- pipeline artifacts ready for direct encoding;
- dirty byte ranges;
- callback output frozen for this frame.

The package is therefore “backend-ready” for selection and ordering, not for
binding-data consumption.

### 2. Package consumption still calls the original render command

`XRRenderPipelineInstance` profiles `Pipeline.CommandChain.Execute()` as frame
package consumption. `RenderCommandCollection` still invokes
`IRenderCommand.Render()`. For a mesh command, `RenderCommandMesh3D.Render()`
recomputes draw matrices and calls the live mesh renderer. The Vulkan backend
then receives `VkMeshRenderer.OnRenderRequested`.

Consequently, the 172.5 ms package-consumption scope is not bounded validation
of immutable data. It includes the old material and draw-preparation path.

### 3. Draw consumption relies on a current-frame mutable binding snapshot

On a snapshot-cache miss, `VkMeshRenderer.CaptureProgramBindingSnapshot()`:

1. clears program bindings;
2. calls `SetMaterialUniforms(..., forceUpdate: true)`;
3. visits material parameters and textures;
4. emits camera, light, time, viewport, engine, callback, and scoped values;
5. copies the result into `ComputeDispatchSnapshot`.

The snapshot owns string-keyed dictionaries for uniform values, samplers,
images, and buffers. Stable materials and stable frame data are re-emitted
because the cache is current-frame-only and the miss path force-updates the
draw-scoped program state. Snapshot capture then duplicates those bindings into
another data structure so they can survive until Vulkan consumes the pending
draw.

There is already a same-frame snapshot cache. It can share a snapshot when
uniform capture is disabled, the draw is not a shadow pass, no mesh/material
uniform handler participates, and an exact key matches material, pipeline,
camera, world, target, pass, render area, stereo/projection state, link
generation, and scoped binding revision.

This cache is useful but not the required workstream-04 handoff:

- it is cleared when the render frame ID changes;
- its first exact-key use still performs material emission and snapshot copy;
- callback/shadow/uniform-capture cases do not qualify;
- it caches one mixed-frequency dictionary snapshot rather than independently
  versioned frame/view/pass/material/object payloads.

This is the measured 60.3 ms material/program emission followed by 55.2 ms of
snapshot copying across the frame's cache misses.

### 4. Reusable-command refresh copies/scans the reflected block again

`VulkanRenderer.TryRefreshReusableCommandBufferFrameData()` visits reusable
frame operations and calls each mesh renderer's refresh path.

`VkMeshRenderer.TryRefreshReusableCommandBufferFrameData()` validates buffers
and descriptors, writes engine uniforms, and calls
`UpdateAutoUniformBuffersForDraw()`.

There is also an auto-uniform template cache attached to a shared snapshot.
The first template use clears a complete block, iterates every reflected
`AutoUniformBlockInfo.Member`, resolves its source, converts it, and writes
std140 bytes. Later uses of the same current-frame snapshot copy the complete
template into the draw buffer, then still iterate every reflected member,
normalize/classify its name with `IsPerDrawAutoUniformMember()`, and rewrite the
members classified as per-draw.

When the snapshot does not qualify for sharing, the path clears and resolves
the complete block for that draw. Source resolution includes temporal values,
engine values, the snapshot dictionary, indexed arrays, material parameter
lookup by name, and defaults.

The generic Uber uniform include currently contains 260 uniform declarations:

- 33 opaque sampler/image-like declarations;
- 227 non-opaque candidates for auto-uniform rewriting.

The exact reflected member count depends on shader stage and preprocessing, but
the source establishes the scale. The rewrite emits non-opaque auto-uniform
blocks into the globals descriptor set. Those blocks combine values that
actually change at different rates: frame/time, view/camera, pass/light,
material, object/transform, and callback values.

A change to one object matrix therefore shares a copy/scan unit with hundreds
of otherwise stable values. Template reuse avoids some repeated value
resolution, but it does not avoid the full block copy or full reflected-member
scan per qualifying draw, and it does not survive the current-frame snapshot
cache boundary. The 120.2 ms measurement covers this combined auto-uniform
processing stage; additional template hit/miss/copy/patch counters are required
to divide it further.

### 5. Descriptor reuse is still validated at draw granularity

The Vulkan descriptor model has global, compute, material, and per-pass set
indices, and it already contains a shared-material-tier path. The current mesh
refresh still validates cached buffers, descriptor variants, shared material
sets, frame-source sets, and binding/resource fingerprints per reusable draw.

The captured frame reported:

- 3,583 descriptor variants;
- 10,749 allocated/reserved descriptor sets;
- 62 pools;
- 20,414 mesh-frame-data reservations;
- 3 mapped mesh-frame-data arena chunks;
- 100,663,296 mapped bytes;
- 24,605,792 reserved bytes.

These counters do not by themselves prove a leak. They do show that current
ownership and validation scale with draw/pass/frame combinations far more than
the 647 visible commands. The architecture needs explicit counters for unique
materials, unique descriptor schemas, active frame slots, and dirty ranges
before memory amplification can be judged precisely.

## Workstream 04 Contract Audit

| Workstream-04 claim | Current implementation | Verdict |
| --- | --- | --- |
| Publish an immutable backend-ready package | Ordering, selection, references, and revisions are published; binding payloads and backend artifacts are not | Partial |
| Include descriptor and uniform update inputs | Live material/program state is still traversed and copied during consumption | Not met |
| Cache stable data by precise generations | Selection identities are cached; binding snapshots/templates are scoped to the current render frame and mixed-frequency blocks are still copied/scanned per draw | Partial |
| Eliminate render-thread scene/material traversal | `SetMaterialUniforms()` and callbacks still run per draw on the render path | Not met |
| Vulkan consumes only validated package data | Vulkan consumption re-enters live `RenderCommand`, mesh, material, program, and renderer objects | Not met |
| Render thread primarily validates, encodes, submits, and presents | 342.7 ms of the measured outer work is mesh preparation plus frame-data refresh | Not met |
| Zero steady-state managed allocation | The measured hot stages are nearly allocation-free | Substantially met, but allocation success did not produce acceptable CPU cost |
| Non-encoding preparation meets the workstream-01 budget | A 399.6 ms Debug frame is orders of magnitude above the 5 ms desktop render target | Not met; canonical Release validation still required |

Workstream 04 must be reopened for its binding/frame-data handoff. Its completed
selection and ordering work remains useful and should not be discarded.

## Workstream 05 Relevance

Workstream 05 is relevant as a consumer of the corrected prepared-draw
contract, but it is not the fix for this steady-frame bottleneck.

Its design correctly states that stable frames should reuse command buffers
instead of scheduling workers. This capture did exactly that: 22 of 22 chains
were reused and zero workers ran.

Attempting to hide the current cost with more workers would be the wrong
acceptance outcome because it would:

- preserve O(draws x reflected members) work;
- retain string dictionaries and mutable renderer access;
- add synchronization around program/material state;
- duplicate work on every stable frame;
- compete with command-recording workers on genuinely dirty frames;
- make a stable frame depend on scheduler throughput instead of reuse.

The architecture work must first make prepared binding data immutable and
change-driven. Workstream 05 can then encode dirty chains from those immutable
records without accessing `MeshDrawOp`, `XRMaterial`, mutable program
dictionaries, or `VkMeshRenderer` state.

## Root Causes

### Root cause 1: the handoff boundary stops at identity

The package answers “what should render and in what order?” It does not answer
“which immutable bytes, descriptor handles, offsets, and pipeline artifact
should Vulkan bind?” The consumer therefore reconstructs the latter answer.

### Root cause 2: bindings are modeled as a mutable draw-time dictionary

Material and engine values are published through string-keyed program mutation.
This is flexible but makes every exact-key cache miss a full binding event and
requires a mixed-frequency snapshot that is discarded at the next render frame.

### Root cause 3: reflection remains a per-draw block scanner

Reflection metadata should compile a binding schema once. On template misses,
the current hot path interprets source precedence, types, arrays/structs, and
defaults for every member. On template hits, it still copies the complete block
and scans/normalizes every reflected member to find the small per-draw subset.
Both modes retain O(draws x block size/member count) work.

### Root cause 4: data with different change frequencies shares one block

Frame, view, pass, material, and object values are mixed into auto-uniform
blocks. A high-frequency object value forces the engine to revisit low-frequency
material and frame values.

### Root cause 5: command reuse is not data-publication reuse

The engine reuses native command buffers but refreshes all recorded draw data by
walking the stable frame operation list. Reuse avoids `vkCmd*` calls without
avoiding the CPU work needed to rebuild and revalidate their inputs.

### Root cause 6: descriptor validation is pull-based per draw

Each reusable draw proves again that its buffer and descriptor resources match.
Stable descriptor topology should instead be published by versioned owners and
consumed by generation/handle, with work only on topology or resource changes.

## Ruled-Out Primary Causes For This Frame

- Native present blocking: 0.113 ms.
- Swapchain acquire/fence wait: approximately 0.074 ms combined.
- Command-chain worker starvation: no workers were needed.
- Native command recording: all chains and the primary were reused.
- Pipeline compilation: zero misses or compiles.
- Vulkan validation overhead: no messages, and the dominant source path is
  independent of validation.
- Resource planning: 0.083 ms.
- Managed garbage collection: the measured hot stages allocated almost
  nothing.
- Collect-visible starvation: collect-visible took about 6 ms while the render
  side took about 399 ms.

Cold pipeline compilation and coarse command-cache invalidation were observed
elsewhere in the investigation and remain valid separate problems. They do not
explain this stable frame.

## Required Target Architecture

### 1. Compile a binding schema once

Shader link/reflection should produce an immutable binding schema containing:

- typed source identity instead of repeated string lookup;
- destination set, binding, byte offset, array stride, and size;
- frequency domain;
- conversion/copy operation;
- default value;
- resource-kind and descriptor requirements;
- dependency generations.

Reflection remains the source of truth, but the frame loop executes compact
typed copy operations or direct struct writes. It must not interpret hundreds
of names per draw.

### 2. Separate data by update frequency

At minimum, prepared data needs explicit domains for:

- frame: render time and frame-global values;
- view: camera matrices, camera position, viewport, temporal jitter/history;
- pass: lights, shadow/pass resources, render-target-dependent state;
- material: scalar/vector parameters and material-owned resource indices;
- object/draw: model and previous-model matrices, skinning/object identifiers;
- instance/batch: instance ranges and indirect metadata.

A backend may implement these as dynamic UBO slices, SSBO/material tables,
push constants, descriptor-buffer records, or another measured layout. The
contract is frequency separation and stable ownership, not a mandated Vulkan
mechanism.

### 3. Publish packed payloads from change owners

- Frame data is packed once per frame slot.
- View data is packed once per active view.
- Pass data is packed once per pass or changed pass generation.
- Material data is packed once per dirty material and active frame slot.
- Object data is packed once per dirty object; moving cohorts update only
  object ranges.
- Stable values retain their published offset and generation.

A material used by 647 draws must not be serialized 647 times. One material
mutation must publish one material payload per necessary frame slot and update
only draws that reference its generation.

### 4. Make the prepared draw genuinely backend-ready

The immutable prepared draw should reference:

- a resolved pipeline artifact and generation;
- geometry/index/indirect handles and ranges;
- stable descriptor-tier handles/generations;
- frame/view/pass/material/object payload offsets or indices;
- dynamic state and render-scope inheritance;
- lifetime tokens;
- precise recording-visible and data-visible dependency generations.

It must not require the consumer or a worker to reread live material
parameters, clear mutable program bindings, invoke unrestricted callbacks, or
copy a `ComputeDispatchSnapshot`.

### 5. Make stable refresh dirty-list-driven

The unchanged fast path should validate a small set of owner generations and
return. It must not loop over every visible draw.

Separate:

- recording-visible changes, which invalidate command artifacts;
- descriptor-topology changes, which republish descriptor records;
- data-content changes, which publish dirty byte ranges without rerecording;
- lifetime changes, which retain or retire artifacts safely.

Full signatures should remain available as a correctness backstop and
diagnostic comparison, not as permission to rebuild all content.

### 6. Constrain callbacks

Material/render callbacks must either:

- declare the frequency domain and write into a typed prepared payload;
- publish an explicit generation when their output changes; or
- take a visible, measured fallback path that is excluded from the stable fast
  path.

An unrestricted callback that mutates a program dictionary during draw
consumption prevents deterministic preparation and must not silently qualify as
backend-ready.

## Invalidation Matrix

| Change | Required work | Work that must remain reused |
| --- | --- | --- |
| No change | Bounded generation checks only | Packed data, descriptor tiers, prepared draws, primary and secondary artifacts |
| Time/frame advances | One frame-domain write per frame slot | Material, static object, descriptor topology, command artifacts |
| Camera moves | One view-domain write per view; affected temporal data | Material and static object payloads; command artifacts unless recorded state changes |
| Object transform changes | Dirty object range only | Material/frame/pass payloads and descriptor topology |
| Material scalar changes | One material payload per dirty material/frame slot | Other materials, object payloads, command artifacts when offsets/layout stay stable |
| Material texture changes | Material resource record and descriptor content generation | Unrelated materials, geometry, primary plan |
| Shader/layout changes | Recompile schema and invalidate affected pipeline/descriptor artifacts | Unrelated shader families |
| Resize/target change | Affected view/pass resources and recording inheritance | Unrelated materials and objects |

## Performance Contract

Workstream 01's existing desktop CPU Direct target remains the product gate:
desktop render p95 at or below 5.00 ms for the canonical 200 Hz cohort.

To make binding cost “almost nonexistent,” the architecture plan should adopt
the following workstream-local Release targets for the representative
approximately-647-draw scene:

### Stable static frame

- frontend binding/package consumption: <= 0.15 ms p95;
- frame/view/pass data publication: <= 0.15 ms p95;
- material/object dirty publication: <= 0.05 ms p95 when unchanged;
- descriptor reuse validation/publication: <= 0.10 ms p95;
- command-artifact reuse validation: <= 0.15 ms p95;
- total Vulkan preparation/record/submit CPU, excluding measured OS/GPU waits:
  <= 1.00 ms p95;
- zero steady-state managed allocations;
- zero material dictionary emissions, snapshot copies, full-block template
  copies/scans, and descriptor writes for unchanged owners.

### Deterministic moving-object frame

- work scales with dirty object slots, not all material members;
- unchanged material/frame/pass bytes remain untouched;
- total Vulkan preparation/record/submit CPU, excluding measured waits:
  <= 1.50 ms p95 for the declared moving cohort;
- zero steady-state managed allocations.

These are design and implementation budgets, not a claim that the current
hardware already meets them. If canonical measurement proves one sub-budget
unrealistic, the plan must record the evidence and reallocate within the 5 ms
product gate; it must not relax the scaling invariants.

## Required Telemetry

The next implementation must report counts and bytes alongside time:

- visible draws and prepared draws;
- unique visible materials and material payloads serialized;
- frame/view/pass/object payloads serialized;
- dirty versus reused payload slots;
- bytes copied per frequency domain;
- reflected-name lookups and generic type conversions;
- program-dictionary writes and snapshot entries copied;
- auto-uniform template hits/misses, full-block bytes copied, reflected members
  scanned, and per-draw members patched;
- descriptor schemas, descriptor records validated, and descriptor writes;
- stable draw operations visited during refresh;
- command artifacts reused, rebuilt, or retired;
- explicit fallback reason counts.

For a stable static frame, every “serialized,” “copied,” “descriptor write,”
and generic lookup counter should be zero except the bounded frame/view data
that is intentionally dynamic.

## Migration And Validation Strategy

1. Add counters that prove current scaling and establish canonical Release
   static, moving, single-material-change, camera-only, and resize cohorts.
2. Compile typed binding schemas while retaining the existing serializer.
3. Produce frequency-separated packed payloads beside the legacy snapshot.
4. In a validation-only dual path, compare new payload bytes, descriptor
   identities, draw order, and fallback decisions against the legacy path.
5. Make prepared draws reference the new payload handles and generations.
6. Change reusable-frame refresh from a full draw scan to dirty-owner lists.
7. Remove the material-dictionary/snapshot/reflected-serialization path from
   qualifying draws only after parity and lifetime tests pass.
8. Keep an explicit legacy fallback for unsupported shaders/callbacks during
   migration; expose it in telemetry and fail acceptance if the canonical
   scene uses it.
9. Run core, synchronization, lifetime, visual, resize, shader-reload,
   material-mutation, shutdown, and hardware performance matrices.

## Acceptance Conditions

This investigation is resolved only when:

- workstream 04's package includes complete immutable binding/data inputs;
- an unchanged frame does not traverse live materials or program dictionaries;
- an unchanged frame does not walk reflected auto-uniform members per draw;
- an unchanged frame does not visit every reusable draw to prove descriptor
  stability;
- a single material mutation costs O(unique dirty materials), not O(draws);
- camera and object changes touch only their declared frequency domains;
- workstream-05 workers consume immutable prepared draws on dirty frames and
  remain idle on stable frames;
- Release canonical cohorts meet the local budgets and the workstream-01
  5.00 ms p95 desktop gate;
- zero-allocation, deterministic order, visual parity, synchronization,
  lifetime, resize, and shutdown gates pass.

## Actions Taken In This Pass

- Captured and decomposed the exact steady frame.
- Corrected the outer Present-scope interpretation.
- Traced the selection package, render-command consumption, program-binding
  snapshot, reflected auto-uniform serializer, descriptor refresh, and command
  reuse paths.
- Audited workstreams 04 and 05 against measured behavior.
- Extended the Vulkan command-recording architecture plan with the missing
  binding/data-publication work and acceptance budgets.
- Made no engine or renderer code changes during this original analysis pass.

## Implementation Follow-Up And Wrap-Up (2026-07-30)

The first migration slice was implemented after the analysis above. This
section supersedes the original pass status without rewriting its historical
record.

### Implemented

- Material-owned numeric uniforms are cached as immutable payloads keyed by
  material layout/value/shader revisions and linked-program generation.
  Frame-local binding snapshots reference the persistent payload rather than
  copying those material entries every frame.
- The auto-uniform path compiles each qualifying reflected block into static
  material bytes plus a dynamic-member patch list. Callback, shadow, and other
  unclassified paths retain an explicit conservative fallback.
- Each auto-uniform block tracks the compiled material plan published into each
  stable buffer slot. Static bytes are copied only when that plan changes;
  dynamic ranges are cleared and patched for the current draw.
- Cache invalidation covers material revision, program relink, reflected block
  replacement, UBO destruction, and binding-snapshot content reuse.
- Frame-reset counters now expose material payload and snapshot cache activity,
  payload/snapshot entry counts, material parameter emissions/dictionary
  writes, auto-uniform plan and byte/member activity, fast/fallback draw counts,
  reusable draw visits, and descriptor validation/write counts through the
  profiler packet, NDJSON capture, and MCP profiler output.

### Validation evidence

- Targeted Vulkan renderer build: passed with zero compiler errors.
- Editor-wide build: passed with zero compiler errors.
- Focused command-recording/lifecycle/material-cache selection: 53/53 tests
  passed.
- Corrected Release CPU Direct run:
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-30_16-32-42/summary.json`.
  Its engine log is
  `Build/Logs/Release_net10.0-windows7.0/windows_x64/xrengine_2026-07-30_16-30-56_pid7284/`.
- Across 96 captured samples, all 3,582 scheduled command chains were reused
  and zero chains were recorded. The final six clean primary-reuse frames
  measured 7.242-9.746 ms render time, 3.046-3.732 ms Vulkan frame time, and
  1.655-1.887 ms frame-data refresh. Each reused 43 chains, recorded none, and
  reported zero command-recording allocation.

### Why acceptance remains open

- The same Release capture had 15 primary re-records. Its render
  p50/p95/p99 was 17.726/152.954/195.214 ms, and command recording allocated
  27,370,648 bytes across the capture.
- Primary dirty evidence points to `DescriptorGeneration`,
  `ResourceAllocation`, `PrimaryFrameState`, exact-variant invalidation, and
  image-entry-state mismatch paths even while every secondary chain remained
  reusable. Those invalidations still need to be isolated and removed.
- Validation layers were not active in the retained samples, and the requested
  GPU timing dump failed. The run therefore cannot satisfy Vulkan validation
  or GPU-timing acceptance.
- The final per-slot publication and telemetry changes were build/test
  validated after that run but have not been measured in a fresh canonical
  cohort.
- The full frequency-owned storage split, dirty-owner queues, descriptor-owner
  publication, backend-ready prepared draws, image journals, OpenXR unlock,
  and Phase-9 cohort matrix remain incomplete.

The clean tail demonstrates that the original 399.580 ms frame was dominated
by avoidable CPU reconstruction and that the first cache slice removes most of
it when reuse is genuinely stable. It does not yet meet the <=5.00 ms p95
desktop gate or the requirement that steady CPU cost be almost nonexistent.
The next implementation checkpoint should begin with the primary re-record
causes, then rerun static, moving-object, camera-only, and single-material-change
cohorts with validation enabled and the new binding counters captured.

## 2026-07-31 Canonical Zero-Fallback Cutover

The remaining Unit Testing World legacy auto-uniform blocks were traced with
one-time exact fallback diagnostics. Two reflected material structs
(`DirLight` and `VignetteStruct`) were rejected as unsupported shader types, and
one optional Poiyomi material flag failed because the material intentionally
omitted the loose uniform. These were schema/write-contract gaps rather than
shader-specific incompatibilities.

The linked schema now compiles reflected struct trees into material-owned
snapshot operations. Their captured leaf fields write directly into the
frequency-owned payload, while an absent optional loose uniform with no authored
default keeps GLSL's zero-initialized value. Non-shadow callback snapshots stay
eligible for the material fast path; their mutable values participate in the
material-owner content generation so a value change republishes the payload
without changing the stable batch signature.

Validation evidence:

- `XREngine.Runtime.Rendering.Vulkan.csproj` built with zero compiler errors.
- The focused schema, mutable-generation, callback-provenance, material
  eligibility, retained-refresh, and canonical-Gate contracts passed 6/6.
- Six consecutive live StandardValidation samples in the isolated
  `cmd-record-arch-opt` session each reported:
  - clean primary reuse `1`, command records `0`;
  - fast binding snapshots `18`, legacy snapshots `0`;
  - typed fast-path blocks `51`, legacy fallback blocks `0`;
  - schema fallback operations, reflected member scans/name lookups, legacy
    full-block bytes, descriptor validation/writes, and descriptor owner misses
    all `0`;
  - static-primary data-refresh draw visits `0`; the independently dynamic
    ImGui overlay visit remained `1`;
  - Vulkan validation errors `0`.
- The corresponding log is
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-31_03-47-40_pid6008/log_vulkan.log`.
  It contains zero `[Vulkan.AutoUniformFallback]` records, validation errors,
  and synchronization hazards.
- The inspected viewport capture is
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/mcp-captures/zero-fallback-final/Screenshot_20260731_034919_290_5251ed3aa63149719ddb19f52f1c0b79.png`.
  The scene remains very dark as before, but both physics-test geometry groups
  and differentiated surfaces remain intact.

Canonical `Invoke-VulkanPerf.ps1 -Preset Gate` captures now enable
`FailOnSteadyStateBindingFallback`. The measurement harness aggregates the
fallback draw total and all typed fallback reasons into each retained summary
and fails the run if any steady-state sample enters the legacy auto-uniform
path. This closes the silent-fallback acceptance hole; the broader mutation,
dual-path parity, allocation, and Release performance matrices remain open.

## 2026-07-31 Shared-Material Dirty-Owner Cohort

A dedicated StandardValidation Unit Testing World cohort rendered 64 UnitBoxes
with one shared material. Two correctness issues were removed before measuring
the cohort:

- `ShaderVar<T>.Value` no longer raises `ValueChanged` when `SetField(...)`
  reports an equal assignment. This removed false material-version churn from
  repeatedly assigned UI parameters.
- Reusable-frame cohort signatures now contain only structural identities.
  Material/view/object content generations remain owner-publication inputs and
  no longer rebuild the complete retained batch when one owner changes.

Compatible physical auto-uniform blocks now publish a semantic layout
signature. The Vulkan material owns plans keyed by that signature and its
material/runtime revisions, while global frequency reservations and reusable
owner work use the same compatibility identity. This permits renderer-local
programs with equivalent blocks to share one material plan and one backing
range without allowing incompatible layouts to alias.

After camera placement exposed 83 visible mesh draws, three consecutive
baseline samples reported zero material payload misses/packs, plan misses,
material publications, reusable-frame draw visits, command records, legacy
fallback draws, and validation errors. Every frame cleanly reused the primary.

Changing the shared `BaseColor` from red to green produced exactly one dirty
frame:

- one material payload miss and one payload pack;
- six packed uniforms, one parameter emission, and six dictionary writes;
- two plan misses and two material publications (the two distinct compatible
  physical material blocks), totaling 176 published bytes;
- zero reusable-frame draw visits;
- clean primary reuse with zero command-buffer records;
- zero legacy fallback draws and zero validation errors.

The next five sampled frames returned all payload-pack, plan-miss, publication,
and draw-visit counters to zero. The green-to-red restoration produced the same
single-frame counts and retained primary reuse. The inspected Vulkan viewport
captures show all shared UnitBoxes changing to green and then back to red:

- `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/mcp-captures/Screenshot_20260731_044354_802_a077c2203cc646d4a963214e4338ea46.png`;
- `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/mcp-captures/Screenshot_20260731_044741_652_41603292d4f74671a4829a88b2f44d73.png`.

The focused equal-assignment, publication compatibility, reservation-key,
material-plan-key, and reusable-cohort-signature contracts pass 5/5. The
remaining frame/view/pass/object mutation cohorts and Release allocation and
performance gates remain open.

## 2026-07-31 Camera And Object Dirty-Owner Cohorts

Camera motion initially published one 36-byte material block per required
in-flight slot. Gated diagnostics identified the unnamed GTAO generation
material: loose `Radius`, `Bias`, and related gather uniforms defaulted to the
material frequency even though the legacy callback sourced them from the
active camera's AO settings.

The Vulkan shader rewriter now recognizes a trailing
`// XRENGINE_FREQUENCY(<domain>)` annotation on loose numeric uniforms, rejects
invalid domains explicitly, and uses the declared owner when forming physical
auto-uniform blocks. The shader artifact-cache schema advanced to version 4.
Both GTAO generation shaders declare their ten gather settings as `View`.

The GTAO generation framebuffer no longer installs a mutable legacy uniform
callback. Its fullscreen mesh owns a typed `View` publisher that emits only
the AO settings values. `AmbientOcclusionSettings.BindingGeneration` advances
monotonically for actual top-level and nested-mode changes, so static-camera
settings edits remain visible without assigning camera matrices, viewport
values, or GTAO settings to the wrong owner.

Focused shader-rewrite, GTAO defaults/generation, and visibility-bitmask tests
passed 27/27. The Vulkan renderer project built with zero compiler errors.

The live StandardValidation cohort rendered 83 visible mesh draws with one
material shared by 64 UnitBoxes. A clean baseline published four frame blocks
(28 bytes) for the normal time sources and zero view, pass, material, object,
or instance blocks. Moving the editor camera produced two required-slot
samples with:

- five view publications totaling 1,272 bytes;
- zero pass, material, object, and instance publications;
- zero material payload packs, reusable-frame draw visits, command-buffer
  records, chain records, legacy fallback draws, and validation errors;
- one clean primary reuse and zero primary records.

The following samples returned view publications to zero. A second camera move
repeated the same view-only result while retaining every command artifact.

Moving `UnitBox 1` from local X `-6.25` to `-6.0` produced exactly two
required-slot samples with one 68-byte object publication. View, pass,
material, and instance publications remained zero; the four 28-byte frame
publications stayed at their independent time-source baseline. Material
payload packs, full-draw refresh visits, command and chain records, legacy
fallback draws, and validation errors all remained zero. Subsequent samples
returned object publication to zero.

The validation log for this cohort is
`Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-31_05-11-34_pid45492/log_vulkan.log`.
The remaining pass/instance mutation, allocation, Release-performance, and
secondary-family expansion cohorts remain open.

## 2026-07-31 Complete Dynamic-Rendering Secondary Inheritance

The secondary artifact contract previously froze attachment formats, samples,
view mask/layers, and depth-read-only state, but omitted dynamic-rendering
local-read mappings and rendering flags. A cache hit could therefore fail to
distinguish two otherwise identical scopes with different
attachment-location/input-index mappings.

`DynamicRenderingLocalReadSignature` now takes an allocation-free value
snapshot of both color mappings plus optional depth/stencil input indices.
Scheduled, serial-fallback, indirect, and worker secondary recording all
rehydrate the frozen mappings into `VkCommandBufferInheritanceInfo` `pNext`
storage and carry the inherited rendering flags. Cache/artifact identity hashes
the exact mapping values deterministically, and primary-scope matching compares
the full value snapshot before executing a secondary. The primary-only
`CONTENTS_SECONDARY_COMMAND_BUFFERS` bit is added only when beginning the
primary rendering scope.

The two focused identity/recording contracts pass 2/2. The broader
source-contract fixtures remain independently red because many assertions
still target pre-split monolithic source files; this change does not hide that
suite drift.

The isolated StandardValidation session was rebuilt from the new source and
rendered the 64-UnitBox shared-material cohort. Its clean sample reported:

- 82 chains scheduled, zero recorded, and 82 reused;
- one primary command buffer reused and zero primaries recorded;
- 82 executable secondaries retained;
- zero worker/fallback failures and zero Vulkan validation messages.

The inspected Vulkan viewport capture is
`Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/mcp-captures/Screenshot_20260731_052855_120_989d53b20178406894b8b4a0070c94f2.png`.
The corresponding validation log is
`Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-31_05-28-30_pid41120/log_vulkan.log`.

## 2026-07-31 Producer-Complete Indirect Secondary Admission

The indirect secondary path previously had only a coarse global safety gate.
It could not distinguish a CPU-produced immutable argument stream from a
same-frame GPU-written zero-readback stream, so widening the gate would also
have widened the mutable path.

The runtime now exposes an allocation-free
`IIndirectDrawSecondaryRecordingBackendCapability` scope. The Vulkan
implementation accepts the scope only for the currently bound, ready,
upload-complete indirect and optional count buffers. Enqueued `IndirectDrawOp`
values freeze an `EVulkanIndirectSecondaryEligibility` result plus the exact
buffer identities. Before secondary recording the renderer rechecks producer
completion, buffer identity, draw count, stride alignment, offset arithmetic,
and uploaded/allocated bounds. Every rejection keeps the established primary
encoder and publishes its exact typed reason.

Only the CPU-built `GpuIndirectInstrumented` diagnostic reference path opts in.
GPU-produced zero-readback calls retain the default `MutableCurrentFrame`
contract and cannot enter a secondary. The initial implementation records
qualifying secondaries serially; it does not claim worker parallelism or a
measured benefit.

Validation:

- Vulkan and editor builds completed with zero compiler errors.
- `IndirectDrawSecondaryRecordingScope_IsAValueTypeToAvoidPerDrawAllocation`,
  `GpuIndirectCommandChains_KeepMutableArgumentStreamsOnPrimary`, and
  `ProducerCompleteIndirectSecondaryEligibility_HasTypedTelemetryAndPrimaryFallback`
  pass 3/3.
- A rebuilt isolated editor selected `GpuIndirectInstrumented` with
  `XRE_FORCE_CPU_INDIRECT_BUILD=1` and reported zero Vulkan validation
  messages/errors. The active cached physics workload did not produce an
  indirect re-record during the retained profile window, so the Phase 8
  representative-hardware benefit criterion remains open.

## 2026-07-31 Compute And Transfer Secondary Admission

The generic secondary bucket path had an unreachable compute branch and no
buffer-copy branch: its scheduler only produced blit/indirect buckets. It also
had no family-specific queue, barrier-plan, or resource-state result, so
widening the scheduler directly would have made primary fallback opaque.

The scheduler now produces only contiguous compatible compute-dispatch and
buffer-copy buckets. A typed allocation-free contract independently gates
compute and transfer. The executing primary closes rendering and emits its
compiled per-pass barriers/queue-ownership transfers before calling the
secondary. Eligibility then rechecks the graphics command-pool queue family,
known pass, compute workgroups/prepared state, or the copy's exact buffer
handles, transfer usage, allocated ranges, and same-buffer non-overlap.
`CmdExecuteCommandsTracked` merges secondary resource and image-layout
dependencies back into the primary. Every rejection is counted and executes
the existing primary encoder.

Validation:

- Vulkan and editor builds completed with zero compiler errors.
- Five focused contract tests passed, covering the two scheduler families,
  typed value contract, independent controls, queue/barrier/resource gates,
  telemetry, primary fallback, and graphics-family transfer capability.
- In the isolated `cmd-record-arch-opt` session, disabling primary reuse
  exposed 37 frame operations including one compute dispatch. The compute
  family reported `Eligible=1`, the frame recorded one primary, and the Vulkan
  profiler reported zero validation messages.
- Repeating that run with core and synchronization validation enabled produced
  the same `Eligible=1` compute result and no `VUID`, validation error/warning,
  or synchronization hazard in
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-31_06-15-46_pid45484/`.
- Camera-separated captures
  `Screenshot_20260731_061225_900_542f0d442769488b8feff797c3ab231b.png`
  and
  `Screenshot_20260731_061226_936_76544355a86745c3bbddedb90cfbfd1f.png`
  changed with the editor camera and showed live Physics Testing World
  rendering.

The workload emitted no `BufferCopyOp`, so transfer validation under a live
copy and the representative-hardware performance-benefit criterion remain
open. The implementation records serial secondaries and makes no asynchronous
multi-queue or worker-overlap claim.

## 2026-07-31 Query Secondary Admission

Query work is the final serial secondary family. The scheduler now classifies
all query frame operations so every attempted scope has telemetry, but only an
ordered `CopyResults` operation may enter a query secondary. Query-pool resets
stay in the primary preamble. Begin/end pairs, timestamps, and specialized
property writes retain their existing primary encoder because the prepared
query epoch is command-buffer-owned.

`VulkanQuerySecondaryInheritanceContract` explicitly carries the primary-active
state, enabled `inheritedQueries` feature, `occlusionQueryEnable`, query flags,
and pipeline-statistics flags. The admitted contract requires no active primary
query and disabled/empty inherited query state. Result-copy admission also
requires a preceding matching begin/end, timestamp, or property producer plus
the same destination and stride validity used by `VkRenderQuery.CopyResults`.
Every rejection records a typed reason and falls through to the original
primary path.

Validation:

- Vulkan and editor builds completed with zero compiler errors.
- Five focused tests passed for typed family scheduling, allocation-free
  inheritance, exact query ordering/reset/pair policy, telemetry, fallback, and
  queue capability.
- A validation-plus-synchronization-validation profile used
  `CpuQueryAsync`, disabled primary reuse, and swept the editor camera through
  three positions. It captured 128 frames with live query brackets. Each query
  frame reported `QueryPairPrimaryOwned` with eight begin/end operations, while
  `cpu_query_submitted_total` was nonzero.
- The 1,743-frame capture contains zero Vulkan validation errors. The retained
  profile is
  `Build/_AgentValidation/mcp-sessions/cmd-record-arch-opt/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-31_06-35-58_pid286492/profiler-render-stats.ndjson`.
- Camera-separated viewport captures
  `Screenshot_20260731_063434_880_cc88cfeef5da41c0bb3d0d9045003c39.png`
  and
  `Screenshot_20260731_063501_297_2ad4b5829fda45ec971e759aae5a684b.png`
  changed with the camera and showed live Physics Testing World rendering.

This cohort exercised the primary-owned query-pair fallback, not an eligible
GPU result-copy operation. Live transfer-copy and query-result-copy coverage,
plus representative-hardware benefit measurements for each family, remain
open.

## 2026-08-01 Directional-Light And Late-Overlay Instability

The reported directional-light slowdown, flicker, intermittent ImGui loss, and
freeze on light re-enable had two independent Vulkan lifetime defects plus one
remaining cold-start cost.

### Native Pipeline-Layout Cache Collision

The first real directional-light fullscreen draw crashed in
`VkMeshRenderer.RecordDrawNoLock` after a shader-program wrapper was relinked.
The new program owned a different native `VkPipelineLayout`, but the
renderer-level graphics-pipeline cache could reuse a pipeline created against
the old layout. Its key contained structural descriptor identity and the
program's per-wrapper link generation, but not the native pipeline-layout
identity; a replacement wrapper could therefore collide with its predecessor.

`VkMeshRenderer.PipelineKey` now includes `PipelineLayoutHandle`, and every
graphics-pipeline request supplies `_program.PipelineLayout.Handle`. Persistent
prewarm identity remains structural and stable; only the live native pipeline
cache is separated by the handle it was actually created with.

Standard-validation and GPU-assisted runs crossed the former crash boundary.
The replacement layout produced a new pipeline handle, the first directional
draw completed, and no validation or device-loss error was emitted.

### Per-Frame Dynamic-UI Secondary Copy-On-Write

The late dynamic-text overlay forced its secondary to rerecord before resetting
the previous overlay primary. That primary still owned a recorded reference to
the secondary, so mutability protection correctly selected copy-on-write on
every frame. The retired replacements could not drain until the unrelated scene
primary rerecorded.

In the failing run, command-buffer resources rose from 767 to 1,607 in 24
seconds and process working set approached 3 GiB. The log emitted one
`Replaced immutable dynamic UI secondary` message per frame. This explains
the progressive slowdown, presentation instability, and disappearing ImGui
reported after the renderer had been left running.

`TryRecordDynamicUiBatchTextOverlayCommandBuffer` now resets the overlay
primary, releases its tracked secondary reference, and drains deferred
secondaries before forcing the dynamic-text secondary rerecord. Reset failure
is checked and reported rather than ignored.

The rebuilt `cmd-record-overlay-reuse` session reported:

- zero copy-on-write replacement messages;
- command-buffer count stable at 143 after initial warmup;
- a one-time rise to 202 while three directional-light off/on cycles populated
  shadow variants, followed by a flat count;
- resource retirement queue depth stable at 123 and no growth in the live
  resource count during the retained steady window;
- zero `VUID`, device-loss, access-violation, fatal, or `VK_ERROR` messages;
- the light active after the final cycle and 89 frames advanced over the final
  two-second liveness probe.

The retained log is
`Build/_AgentValidation/mcp-sessions/cmd-record-overlay-reuse/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-01_22-38-16_pid29952/log_vulkan.log`.

### Follow-Up Correction And Current Status

The apparent top-band/full-window mismatch in the first OS capture was a
capture bug, not a swapchain crop. The capture process was DPI-unaware and
allocated a 1536x864 logical client image for a 1920x1080 physical client at
125% display scaling. A per-monitor-DPI-aware `PrintWindow` capture reports a
1938x1127 physical window and a 1920x1080 client, matching the swapchain. It
shows the scene and ImGui covering the full window.

The earlier directional-light timing also included
`XRE_VK_TRACE_DRAW=1`. That diagnostic logs every shadow draw and made primary
recording appear to cost roughly 75-102 ms per lit frame. A fully rebuilt
isolated session with draw tracing disabled and standard validation enabled
kept the active-light path on `ReusedClean`: zero primary records, zero dirty
summary, zero validation errors, and roughly 5.6-7.0 ms in scene command-buffer
reuse/record handling. The renderer remained responsive after disabling and
re-enabling the light.

The previous `-NoBuild` restart retained stale isolated artifacts and produced
a Bloom mip-2 `PrimaryFrameState` rejection that is not present in the fully
rebuilt session. Targeted state-publication instrumentation did not observe a
shader-read-to-color regression in the rebuilt runtime and was removed after
validation.

Visual evidence after the final off/on cycle:

- `Build/_AgentValidation/20260801-vulkan-command-recording-finish/mcp-captures/ViewportSequence_20260802_072407_092_f3cdb03180f4446694463687d18a2acf/manifest.json`
  contains eight consecutive 1920x1080 Vulkan readbacks with one content hash,
  zero changed pixels, zero dropped frames, and zero failed frames.
- `Build/_AgentValidation/20260801-vulkan-command-recording-finish/mcp-captures/FullWindow_DpiAware_afterLightToggle.png`
  shows the full scene plus ImGui in the 1920x1080 physical client.
- The final Vulkan log scan contains no `VUID`, `VK_ERROR`, device loss,
  access violation, fatal error, dynamic-UI copy-on-write replacement, or
  image-state-publication regression.

Cold shadow-pipeline materialization can still cause a bounded first-use
latency spike for previously unseen mesh-layout variants. It is now separate
from the fixed lifetime/caching faults and did not freeze the rebuilt runtime.

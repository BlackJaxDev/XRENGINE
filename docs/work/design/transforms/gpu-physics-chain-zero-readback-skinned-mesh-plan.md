# GPU Physics Chain Zero-Readback Skinned Mesh Plan

## Goal

Design a rendering path where GPU physics-chain simulation can drive skinned mesh deformation without reconstructing bone transforms on the CPU and without requiring GPU readback in the primary visible rendering path.

The design must support both existing skinning consumption modes:

- compute-skinned rendering through `SkinningPrepassDispatcher`,
- direct vertex-shader skinning for renderers that do not use the compute prepass.

The target outcome is straightforward:

- the physics chain remains authoritative for secondary-motion bone pose on GPU,
- renderers consume GPU-resident bone palette data directly,
- CPU bone synchronization becomes optional and is no longer required for visible deformation.

## Current State

### 1. Physics-chain GPU execution already exists

Relevant code:

- [XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs](../../../XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs)
- [XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs](../../../XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs)
- [XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs](../../../XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs)
- [Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChain.comp](../../../Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChain.comp)

What this already provides:

- GPU particle simulation for chain state.
- Batched dispatch across multiple chains.
- Optional asynchronous readback for CPU-side particle state application.
- A CPU-facing `GpuSyncToBones` mode that marks pending bone synchronization after GPU results are applied.

What it does not currently provide:

- a GPU-resident render-facing bone palette output,
- a direct render binding between chain simulation output and skinned mesh consumption,
- previous-frame GPU bone palette tracking for temporal rendering,
- a zero-readback visible path.

### 2. Compute skinning infrastructure already exists

Relevant code:

- [XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs](../../../XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs)
- [XRENGINE/Rendering/Compute/GlobalAnimationInputBuffers.cs](../../../XRENGINE/Rendering/Compute/GlobalAnimationInputBuffers.cs)
- [Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepass.comp](../../../Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepass.comp)
- [Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepassInterleaved.comp](../../../Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepassInterleaved.comp)

Important existing capabilities:

- compute skinning already consumes SSBO bone matrices and inverse bind matrices,
- global packed palette support already exists through `boneMatrixBase`,
- skinned output buffers are already GPU-resident and render-consumable,
- the renderer already has a compute-prepass integration seam.

Current limitation:

- the global packing path currently assumes source data originates from CPU-visible renderer buffers and is copied through CPU-side buffer memory before upload.

### 3. Direct vertex-shader skinning also already exists

Relevant code:

- [XRENGINE/Rendering/Generator/DefaultVertexShaderGenerator.cs](../../../XRENGINE/Rendering/Generator/DefaultVertexShaderGenerator.cs)
- [XRENGINE/Rendering/XRMeshRenderer.cs](../../../XRENGINE/Rendering/XRMeshRenderer.cs)
- [XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Buffers.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh%20Renderer/GLMeshRenderer.Buffers.cs)

Important observation:

- the direct vertex path still assumes the active bone palette starts at zero for that renderer,
- shader generation currently indexes `BoneMatrices[boneIndex]` and `BoneInvBindMatrices[boneIndex]`,
- there is no base-offset abstraction equivalent to compute prepass `boneMatrixBase`.

### 4. Skinned bounds still contain a GPU-to-CPU sync point

Relevant code:

- [XRENGINE/Rendering/Compute/SkinnedMeshBoundsCalculator.cs](../../../XRENGINE/Rendering/Compute/SkinnedMeshBoundsCalculator.cs)
- [docs/work/design/zero-readback-gpu-driven-rendering-plan.md](./zero-readback-gpu-driven-rendering-plan.md)

The current bounds calculator:

- dispatches compute,
- waits for GPU completion,
- reads skinned positions back to CPU,
- rebuilds CPU-side bounds data.

That means removing `GpuSyncToBones` alone is not sufficient for a fully zero-readback animated path.

## Problem Statement

`GpuSyncToBones` currently treats GPU simulation as an upstream producer for CPU transform state. That is correct for gameplay systems that require actual updated bone transforms on CPU, but it is the wrong model for rendering.

For rendering, the engine does not fundamentally need CPU bone transforms. It needs:

- current skinning matrices,
- previous skinning matrices when temporal data is required,
- inverse bind matrices,
- palette slice metadata.

The CPU transform hierarchy should therefore stop being the mandatory bridge between GPU simulation and skinned rendering.

## Design Principles

1. GPU simulation should produce render-consumable animation state directly.
2. CPU transform synchronization must be optional, not required for visible rendering.
3. The same GPU palette abstraction should feed both compute skinning and direct vertex-shader skinning.
4. Static inverse bind data should remain mesh-owned and reusable.
5. Temporal rendering paths should have explicit access to previous-frame GPU palette state.
6. Bounds and culling must not quietly reintroduce readback after visible skinning is moved to GPU.

## Proposed Architecture

## 1. Introduce an external GPU skin source abstraction

`XRMeshRenderer` should stop assuming that dynamic bone matrices always come from its own CPU-populated `BoneMatricesBuffer`.

Add a renderer-facing abstraction representing the active skinning source for the current frame.

Suggested logical shape:

- current bone matrices buffer,
- previous bone matrices buffer,
- inverse bind matrices buffer,
- palette base,
- palette count,
- source mode.

Suggested source modes:

- renderer-owned CPU-driven bones,
- external GPU palette slice,
- future shared global animation palette slice.

This is the key decoupling step. Once present, render code no longer needs to know whether bone data was produced by:

- ordinary transform hierarchy updates,
- animation sampling,
- physics-chain simulation,
- another future GPU deformation system.

## 2. Add a physics-chain bone-palette compute pass

After the main physics-chain solve, add a second compute stage that builds a skinning palette directly on GPU.

Inputs:

- chain particle positions and previous positions,
- chain tree topology / parent-child information,
- bind-pose rest orientation data for each driven bone,
- optional root or basis transform data,
- mapping from chain particle segments to target skinned bones.

Outputs:

- current bone matrices buffer,
- previous bone matrices buffer,
- optional packed atlas slice metadata.

This pass should not attempt to update `TransformBase` objects. It exists solely to translate simulation state into render-ready skinning matrices.

### 2.1 Output ownership

For non-batched chains, the simplest version is a per-chain or per-renderer palette buffer.

For batched chains, the preferred design is one shared GPU palette atlas owned by the physics-chain dispatcher, with each driven renderer assigned a stable slice:

- `paletteBase`,
- `paletteCount`.

That matches the existing compute skinning pattern more closely and avoids per-renderer dispatch fragmentation.

### 2.2 Previous-frame palette support

For motion vectors and temporal reconstruction, the palette builder should retain previous-frame data entirely on GPU.

Recommended approach:

- keep two palette buffers or two atlas pages,
- swap current/previous roles once per render frame,
- never reconstruct previous matrices from CPU transforms.

## 3. Route compute skinning through the active skin source

`SkinningPrepassDispatcher` should bind the renderer's active skin source rather than assuming `renderer.BoneMatricesBuffer` is always authoritative.

That requires two changes:

1. Buffer selection must come from the active skin source abstraction.
2. Global packing must support GPU-originating sources without CPU-side staging copies.

### 3.1 Preferred path

The best long-term option is for the physics-chain palette builder to write directly into the same packed palette space compute skinning wants to consume.

In that model:

- no extra repack is needed,
- `boneMatrixBase` already fits the contract,
- compute skinning simply receives slice offsets.

### 3.2 Acceptable intermediate path

If shared packed output is not implemented first, allow compute skinning to bind an external palette buffer directly for those renderers.

That is less optimal for global packing but preserves the central zero-readback goal.

### 3.3 Explicit non-goal for this phase

Do not copy GPU-generated palette data through `XRDataBuffer.Address` or other CPU-visible buffer memory just to fit existing packing code. That would preserve the exact synchronization pattern this design is trying to remove.

## 4. Extend direct vertex-shader skinning to support palette bases

The direct vertex path should be updated to consume the same external palette abstraction.

Required shader-side change:

- add `boneMatrixBase` support to the non-compute skinning path,
- optionally add previous-palette base support if a later pass needs it,
- index bone data as `boneMatrixBase + boneIndex`.

This is the missing compatibility layer between direct vertex skinning and a shared GPU palette atlas.

Without it, direct vertex skinning only works with per-renderer private palettes whose indices start at zero.

### 4.1 Consequence

Once this change is in place, both skinning modes consume the same conceptual data model:

- a buffer,
- a base,
- a count,
- inverse bind matrices.

That unifies the design and prevents separate special-case paths for compute and non-compute renderers.

## 5. Keep CPU bone sync as an opt-in mirror mode

`GpuSyncToBones` should no longer mean "required for rendering."

Instead, split responsibilities conceptually into:

- render-from-GPU mode,
- mirror-to-CPU-bones mode.

Examples where CPU mirror mode is still valid:

- gameplay systems that query actual post-simulation bone transforms,
- sockets or attachments resolved on CPU,
- editor gizmos and inspection tools,
- offline capture or debugging.

In those cases, CPU sync remains available, but it becomes an explicit extra cost rather than the default visible path.

## Data Flow

### Current path

1. Physics-chain compute updates particles.
2. Results are read or applied back to CPU-side particles/transforms.
3. Bone transforms are recalculated on CPU.
4. Renderer uploads bone matrices to GPU.
5. Skinning consumes uploaded matrices.

### Target path

1. Physics-chain compute updates particles.
2. Physics-chain palette compute builds current and previous bone matrices on GPU.
3. Renderer references the resulting palette slice as its active skin source.
4. Compute prepass or direct vertex skinning consumes that palette directly.
5. CPU sync happens only if explicitly requested by gameplay or tools.

## Renderer Integration Details

## 1. XRMeshRenderer responsibilities

`XRMeshRenderer` should become the place that resolves which skin source is active, not the place that always builds the dynamic palette itself.

It should continue to own:

- static inverse bind matrices,
- mesh-local bone-weight/index buffers,
- blendshape state,
- compute-skinned output buffers.

It should no longer hard-code that its dynamic bone matrices come from transform event invalidation only.

## 2. OpenGL binding path

The GL renderer binding path should bind the active skin source buffers for the current draw.

This affects:

- direct vertex-shader skinning SSBO binding,
- compute skinning input binding,
- any future bounds or culling kernels that need the same palette.

The backend should not need a physics-specific code path. It should only care about the resolved active skin source.

## 3. Compute prepass interaction

For renderers using compute skinning:

- compute prepass continues writing skinned positions, normals, tangents, or interleaved output,
- the only change is where its input palette comes from.

This keeps the downstream render path stable and limits the blast radius of the change.

## Bounds and Culling Implications

Removing CPU bone synchronization does not by itself produce a zero-readback animated path.

`SkinnedMeshBoundsCalculator` still blocks on GPU completion and reads skinned positions back to CPU. That becomes the next synchronization bottleneck as soon as visible deformation is GPU-driven.

Recommended direction:

1. move animated bounds computation to a GPU-resident buffer,
2. consume that buffer directly in GPU culling,
3. avoid `WaitForGpu()` and CPU-visible skinned-position reads in the shipping path.

If that work is deferred, use conservative animated bounds temporarily rather than reintroducing a render-thread stall through bounds readback.

## Proposed Phases

## Phase 1: Renderer abstraction

- Add active external skin source support to `XRMeshRenderer`.
- Keep existing CPU-driven bone upload path as one source mode.
- Do not change visible behavior yet.

Outcome:

- render code can resolve skinning inputs through one abstraction.

## Phase 2: Physics-chain GPU palette output

- Add a compute pass that converts chain particle state into current and previous bone matrices on GPU.
- Expose palette slice metadata to driven renderers.
- Do not require CPU transform updates for those renderers.

Outcome:

- physics-chain simulation can produce render-ready animation state entirely on GPU.

## Phase 3: Compute skinning integration

- Update `SkinningPrepassDispatcher` to consume the active skin source.
- Avoid CPU staging copies when the source palette is already GPU-generated.
- Prefer direct atlas output or GPU-only copies.

Outcome:

- compute-skinned renderers can be fully driven from GPU physics output.

## Phase 4: Direct vertex-shader integration

- Add base-offset support to direct vertex-shader skinning.
- Bind active skin source SSBOs for non-compute-skinned renderers.

Outcome:

- both skinning paths consume the same GPU-generated palette model.

## Phase 5: Zero-readback animated bounds

- Replace CPU-visible skinned bounds readback with GPU-resident bounds output.
- Feed GPU culling directly from GPU animated bounds.

Outcome:

- animated skinned rendering no longer quietly depends on GPU-to-CPU synchronization.

## Risks and Open Questions

### 1. Bone basis reconstruction quality

A chain segment provides positional information naturally, but bone matrices require a stable full frame. The palette builder needs a reliable way to reconstruct orientation from:

- segment direction,
- rest-pose local basis,
- optional authored twist reference,
- parent-space stabilization rules.

This needs explicit authoring rules so the GPU-generated pose matches the expected rig semantics.

### 2. Mapping granularity

One physics chain may drive:

- one contiguous bone range,
- sparse bones in a larger skeleton,
- multiple renderers.

The slice/mapping model should support sparse logical mappings even if the GPU palette storage remains contiguous.

### 3. CPU-side attachments

If a weapon socket or gameplay system depends on the driven chain bones, that system may still require CPU-visible pose data. The design should not forbid that, but it should make the cost explicit and opt-in.

### 4. Partial adoption

Some renderers may still use CPU-driven animation while others use GPU-driven chain palettes. The skin source abstraction must allow both to coexist without special-case renderer code all over the pipeline.

## Recommendation

Implement the zero-readback path in this order:

1. add the active skin source abstraction to `XRMeshRenderer`,
2. add GPU palette generation for physics chains,
3. integrate compute skinning first,
4. then extend direct vertex-shader skinning with palette-base support,
5. finally remove animated-bounds readback from the shipping path.

If a minimal first milestone is needed, require GPU-driven chains to use compute skinning initially. That produces the most immediate benefit with the least disruption, while preserving a clean path to full support for direct vertex-shader skinning afterward.
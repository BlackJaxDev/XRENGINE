# Indirect GPU Rendering Architecture (Meshlets and Batched Indirect Multi-Draw)

## Summary
- Exactly two GPU render paths:
  1) Meshlets via task/mesh shaders.
  2) Indirect multi-draw (MultiDrawElementsIndirect), batched by unique materials.
- Engine core remains API-agnostic. No direct use of GLMeshRenderer, GLDataBuffer, etc. All API specifics live behind AbstractRenderer.

## Goals
- Single consistent path for traditional rendering using indirect draws.
- Per-material batching to minimize pipeline state changes.
- Clean abstraction layer for graphics API ops.
- Deterministic buffer layouts and barriers.
- Safe fallbacks when extensions are unavailable.

## Render Flow
- Scene build:
  - GPUScene owns scene-level buffers: MeshDataBuffer, atlas index/vertex buffers, etc.
  - GPURenderPassCollection performs culling and builds the indirect command buffer and optional draw-count buffer; computes material batches.
- HybridRenderingManager:
  - If meshlets are supported and enabled: scene.Meshlets.Render(camera).
  - Else: dispatch compute to build indirect commands; then render via indirect multi-draw:
    - For each material batch: bind pipeline + state, bind VAO and buffers, issue MultiDraw for the batch range.

## Component Responsibilities
- GPUScene
  - Stores mesh metadata for compute to generate commands.
  - Provides scene-level atlas buffers/VAO (or references).
- GPURenderPassCollection
  - Culling, visibility, and indirect command buffer generation.
  - Outputs batches: contiguous draw ranges sharing MaterialID.
  - Optional DrawCountBuffer for GL 4.6/ARB_indirect_parameters.
- HybridRenderingManager
  - Chooses meshlet vs indirect path.
  - Dispatches compute and applies barriers.
  - Iterates batches and issues draw calls after binding the correct pipeline and state.
  - Never touches API wrapper objects.

## Renderer Abstraction (engine-facing)
Expose minimal, API-agnostic methods on AbstractRenderer. Backends implement them (OpenGL, Vulkan).

- Programs/pipelines
  - EnsureMaterialPipeline(material, stereoMode) -> ProgramRef
  - UseMaterialPipeline(ProgramRef)
  - SetEngineUniforms(ProgramRef, camera, modelMatrix, stereoFlags, billboardMode)
  - SetMaterialUniforms(ProgramRef, material)
- VAO and buffers
  - BindSceneVAO(SceneVAORef) or BindVAOForRenderer(RendererRef)
  - ConfigureVAOAttributesForProgram(SceneVAORef, ProgramRef, AttributeLayoutDescriptor)
  - BindIndexBuffer(IndexBufferRef)
  - BindDrawIndirectBuffer(BufferRef)  // GL_DRAW_INDIRECT_BUFFER on GL
  - BindParameterBuffer(BufferRef)     // GL_PARAMETER_BUFFER on GL
- Draw calls
  - MultiDrawElementsIndirect(drawCount, stride)
  - MultiDrawElementsIndirectWithOffset(drawCount, stride, byteOffset)
  - MultiDrawElementsIndirectCount(maxDrawCount, stride)  // uses currently bound parameter buffer
- Pipeline state and barriers
  - ApplyRenderParameters(RenderingParameters)
  - MemoryBarrier(ShaderStorage | Command)

## Data Contracts
- Indirect command element (std430 tightly packed):
  - Count: uint
  - InstanceCount: uint
  - FirstIndex: uint
  - BaseVertex: int
  - BaseInstance: uint
  - Stride = sizeof(DrawElementsIndirectCommand)
- MeshDataBuffer entry (example):
  - uint4 [IndexCount, FirstIndex, FirstVertex, Flags]
- Batches
  - DrawBatch { Offset (draw index), Count (draws), MaterialID }

## VAO Strategy
- Use a single scene-level VAO (atlas VAO) referencing attribute streams and a combined index buffer.
- Configure attribute layouts for the active ProgramRef before drawing (through abstract method).
- Compute writes indirect commands using atlas offsets that match the VAO.

## Batching and State Binding
- For each material batch:
  - Ensure and bind ProgramRef for the material.
  - Configure VAO attributes for ProgramRef.
  - Bind indirect buffer (and parameter buffer when supported).
  - Apply RenderOptions (depth, blend, cull, stencil) from material.
  - Issue MultiDraw for the batch’s [Offset, Count] range.

## Barriers and Fallbacks
- After compute writes indirect commands: MemoryBarrier(ShaderStorage | Command).
- If GL 4.6/ARB_indirect_parameters unavailable: use non-count path and pass CPU drawCount.

## Diagnostics
- Log program binding, VAO binding, index type, stride, batch offset/count, and which multi-draw variant is used.
- Debug toggles: meshlets vs indirect; Count vs non-Count; single material vs batched.

## Known Current Gaps
- No graphics program bound before multi-draw.
- Parameter buffer not bound to GL_PARAMETER_BUFFER for Count variant.
- RenderTraditionalBatched doesn’t bind material pipeline or state per batch.
- HybridRenderingManager directly references OpenGL types; must use abstract renderer methods.
- VAO attribute configuration must match the active program at draw time.

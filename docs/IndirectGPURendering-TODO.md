# TODO: Indirect GPU Rendering Enablement

## Summary
Fix indirect path to bind a graphics pipeline per batch, bind correct buffers, and call the right multi-draw variant without touching API wrappers. Add abstraction points to AbstractRenderer and implement for OpenGL. Ensure GPURenderPassCollection provides proper batches and material lookup.

## Work Items

1) AbstractRenderer surface
- Add:
  - EnsureMaterialPipeline, UseMaterialPipeline
  - SetEngineUniforms, SetMaterialUniforms [Done]
  - BindSceneVAO/BindVAOForRenderer, ConfigureVAOAttributesForProgram [Done]
  - BindIndexBuffer, BindDrawIndirectBuffer, BindParameterBuffer [Done]
  - MultiDrawElementsIndirect, MultiDrawElementsIndirectWithOffset, MultiDrawElementsIndirectCount [Done]
  - ApplyRenderParameters, MemoryBarrier [Done]
  - NEW: ValidateIndexedVAO to enforce an element array buffer is bound and report index type/stride before any MultiDrawElementsIndirect call. [Planned]
  - NEW: UnbindDrawIndirectBuffer / UnbindParameterBuffer helpers for state hygiene after each batch. [Planned]
- Implement for OpenGL using existing GL* classes internally. [Done]
- Acceptance: HybridRenderingManager compiles using only AbstractRenderer methods. [Done]

2) HybridRenderingManager refactor
- Replace OpenGLRenderer usage with AbstractRenderer calls. [Done]
- RenderTraditional:
  - Dispatch compute; MemoryBarrier(ShaderStorage | Command). [Done]
  - If no batches: create a single batch for the whole range with a default material (temporary). [Done]
- RenderTraditionalBatched:
  - For each batch:
    - Map MaterialID -> XRMaterial. [Done]
    - Ensure/Use pipeline, SetEngineUniforms/SetMaterialUniforms. [Done]
    - Configure VAO attributes for the program. [Done]
    - ApplyRenderParameters from material. [Done]
    - BindDrawIndirectBuffer; if supported, BindParameterBuffer and use Count variant; else pass CPU drawCount. [Done]
    - Issue MultiDrawElementsIndirectWithOffset for the batch range. [Done]
- NEW: Uniform type validation at program-use time (log uniform name and expected vs provided type to prevent GL_INVALID_OPERATION). [Planned]
- NEW: Assert DrawElementsIndirectCommand stride == 20 bytes and matches compute shader layout (unit test). [Planned]
- Acceptance: Per-batch pipeline + state bound; no GL wrapper types referenced. [MVP satisfied]

3) GPURenderPassCollection outputs
- Ensure it:
  - Produces IReadOnlyList<DrawBatch> for the current pass (contiguous by MaterialID). [MVP: single batch fallback]
  - Exposes a MaterialID -> XRMaterial map or getter. [Done]
  - Exposes DrawCountBuffer when available. [Done]
  - NEW: Expose index element type (u16/u32) along with index buffer for VAO setup/validation. [Planned]
  - NEW: CPU material sort to form contiguous batches (pre-GPU sort). [Planned]
- Acceptance: HybridRenderingManager receives batches and can resolve materials. [Done]

4) Scene VAO and buffers
- Provide a SceneVAORef (or RendererRef) and IndexBufferRef suitable for atlas rendering. [Existing _indirectRenderer built]
- Add ConfigureVAOAttributesForProgram to bind attributes for each ProgramRef as needed. [Done]
- Acceptance: Active program’s attributes are bound before draw; no warnings for missing attribute locations. [MVP]
- IMPORTANT: Keep element/index buffer (EBO) in sync with atlas
  - Maintain per-mesh (firstVertex, firstIndex, indexCount). [In progress]
  - Rebuild/upload EBO whenever IndirectFaceIndices changes (e.g., in RebuildAtlasIfDirty). [Planned]
  - Validate VAO has an element buffer bound and the element type matches MDI’s index type. [Planned]

5) GL Count variant correctness
- Capability check: GL 4.6 or ARB_indirect_parameters. [Implemented via AbstractRenderer.SupportsIndirectCountDraw]
- Bind draw-count buffer to GL_PARAMETER_BUFFER via BindParameterBuffer. [Implemented]
- Fallback to CPU drawCount when not supported. [Implemented]
- NEW: Unit test to force fallback path and validate drawCount path renders. [Planned]
- Acceptance: Count path issues draws when supported; fallback path works. [MVP]

6) Program/pipeline generation
- Ensure per-material pipeline contains vertex + fragment (or separable pipeline equivalent). [MVP: combined program only]
- If material lacks a vertex shader, generate default mesh vertex shader. [Done]
- NEW: Program attribute-link test to ensure no missing attribute locations when switching programs per batch. [Planned]
- NEW: Validate fragment outputs match bound FBO color attachments for forward path; log mismatch. [Planned]
- Acceptance: ProgramRef links successfully for all materials used in batches. [Partial]

7) Apply RenderOptions per batch
- Depth, blend, cull, stencil settings via ApplyRenderParameters. [Done]
- NEW: Unit test toggling depth/cull/blend/stencil across batches to ensure state changes apply and do not leak. [Planned]
- Acceptance: State changes reflect per-material settings. [MVP]

8) Tests and validation
- Unit/integration tests:
  - Single material, many draws: verifies multi-draw renders with CPU drawCount. [Planned]
  - Multiple materials, batches: verifies per-batch pipeline binds and correct counts and offsets. [Planned]
  - Count variant on supported GL: verify parameter-buffer path draws expected number of draws. [Planned]
  - Missing extension fallback: verify CPU drawCount path. [Planned]
  - NEW: Atlas/EBO correctness – Verify FirstIndex/IndexCount per MeshDataEntry produce correct triangles via MDI; growing atlas triggers VBO/EBO uploads. [Planned]
  - NEW: Attribute layouts – Switch programs between batches; verify no missing attributes and correct rendering. [Planned]
  - NEW: Uniform types – Intentionally mismatch types and assert detection/logging (prevent GL_INVALID_OPERATION). [Planned]
  - NEW: Indirect command stride/layout – Assert 5 uints and buffer stride == 20 bytes. [Planned]
- Acceptance: Tests pass and show non-zero fragments drawn in a debug scene. [Pending]

9) Diagnostics
- Add logs:
  - Which path (meshlets/indirect). [Existing logs]
  - Batch info: material, offset, count. [Done]
  - Program link/use, VAO bind, index element type, stride. [Partial]
  - Multi-draw variant and parameter-buffer usage. [Done]
  - NEW: On each indirect submission (debug mode): VAO ID, bound EBO ID, EBO index type (u16/u32), MDI stride, byteOffset when using WithOffset. [Planned]
  - NEW: Atlas stats: vertices/indices total; per-mesh offsets/counts. [Planned]
  - NEW: Uniform type mismatch with shader-declared type (name, expected, provided). [Planned]
- Acceptance: Logs present when debug flag enabled. [Partial]

10) Cleanup
- Remove or isolate legacy direct GL calls from engine layers. [In progress]
- Document the two supported GPU render paths in README/engine docs. [Pending]
- NEW: Document indirect buffer layout (5 uints), parameter-buffer semantics, VAO/index requirements, and atlas/EBO rebuild policy. [Planned]
- Acceptance: No engine-layer references to GLMeshRenderer/GLDataBuffer/etc. [In progress]

## Risks and Ordering
- Do 1) abstraction, then 2) manager refactor, then 3–5) data/VAO/Count correctness, then 6–7) program/state, then 8–10) tests/diagnostics/cleanup.
- Be careful with VAO attribute configuration consistency across programs.
- Ensure struct packing matches API expectations (DrawElementsIndirectCommand = 5 uints, 20 bytes; shader SSBO layouts match host structs).
- Keep EBO synced with atlas updates; validate index type across pipeline.

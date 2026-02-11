# Render Graph Migration Guide

This guide documents how pipeline commands should participate in the render-graph path used by Vulkan and validated by the OpenGL fallback executor.

## 1. Describe pass intent, not API calls

- Override `DescribeRenderPass(RenderGraphDescribeContext context)` on commands that produce rendering/compute work.
- Register the pass through `context.Metadata.ForPass(passIndex, name, stage)`.
- Declare all resources with `UseColorAttachment`, `UseDepthAttachment`, `SampleTexture`, `ReadBuffer`, `WriteBuffer`, `ReadWriteTexture`, etc.
- Add dependencies with `DependsOn(producerPassIndex)` when ordering is required beyond resource hazards.

## 2. Use schema-driven descriptors

- Each pass should reference descriptor schemas instead of backend-specific push logic:
  - `UseEngineDescriptors()` for camera/scene/global state.
  - `UseMaterialDescriptors()` for material textures/uniforms/storage.
  - `UseDescriptorSchema("CustomSchemaName")` for specialized passes.
- Register custom schemas in `RenderGraphDescriptorSchemaCatalog`.

## 3. Register logical resources through cache commands

- Use `VPRC_CacheOrCreateTexture` / `VPRC_CacheOrCreateFBO` to publish descriptor metadata.
- For temporary attachments and buffers, set transient lifetime:
  - `UseLifetime(RenderResourceLifetime.Transient)`.
  - This enables allocator alias grouping for transient-only resources.
- Set size policy (`UseSizePolicy`) to avoid viewport-size guessing.

## 4. Pass-index discipline

- Ensure runtime draw/compute commands execute inside a valid pass scope (`PushRenderGraphPassIndex`).
- If a command can run in multiple branches, keep pass indices deterministic and metadata-complete.

## 5. Validation workflow

- Vulkan path:
  - `VulkanRenderGraphCompiler` linearizes pass DAG metadata.
  - `RenderGraphSynchronizationPlanner` emits backend-agnostic sync edges.
  - `VulkanBarrierPlanner` converts those edges into image/buffer barriers.
- OpenGL path:
  - `OpenGLRenderGraphExecutor` validates the same metadata ordering and executes the existing sequential command chain.

## 6. Checklist for new feature passes

- Add `DescribeRenderPass` coverage.
- Declare descriptor schemas on the pass.
- Declare every logical resource read/write.
- Set transient lifetime for throwaway targets.
- Add explicit `DependsOn` edges where data flow is not obvious from resource names.

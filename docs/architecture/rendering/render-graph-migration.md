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

- Declare textures and framebuffers through `DescribeResources(...)`; commands consume the committed generation and never publish descriptor metadata.
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
  - OpenGL executes the authored command chain in order. It does not reorder passes.
  - `XRRenderPipelineInstance.ValidateRenderGraphExecutionAgainstMetadata` checks on every backend that each executed pass has metadata, and logs the passes that do not.
  - `OpenGLRenderer` publishes the recorded pass order as a plan snapshot through `RenderGraphRecordedPlanner`, so tools show the OpenGL order next to the Vulkan plan.
- Both paths:
  - `RenderPipelineValidator` runs the pipeline rules on the metadata, the resource layout, and the schema, for example `command.gpu-without-pass` and `resource.undeclared-read`. The Render Pipeline Workbench and the `validate_render_pipeline` MCP tool show the results.

## 6. Checklist for new feature passes

- Add `DescribeRenderPass` coverage.
- Declare descriptor schemas on the pass.
- Declare every logical resource read/write.
- Set transient lifetime for throwaway targets.
- Add explicit `DependsOn` edges where data flow is not obvious from resource names.

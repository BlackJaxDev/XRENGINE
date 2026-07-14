# Render Pipeline Resource Lifecycle

`XRRenderPipeline` now has an API-independent resource declaration path for
pipeline-owned render targets, views, buffers, framebuffers, and fullscreen
material helpers. Both default pipelines and the retained UI, test, and surfel
debug pipelines are declarative. Cache-or-create command types have been removed.

## Resource Layout

Pipelines declare resources by overriding `DescribeResources(...)`. The builder
produces an immutable `RenderPipelineResourceLayout` for a
`RenderPipelineResourceProfile` containing the effective display size, internal
size, stereo mode, HDR output mode, AA mode, MSAA sample count, external-target
kind, and feature mask.

Supported declared resource specs are:

- `TextureSpec`
- `TextureViewSpec`
- `RenderBufferSpec`
- `BufferSpec`
- `FrameBufferSpec`
- `QuadMaterialSpec` for generation-owned, attachmentless fullscreen helpers
- `ExternalResourceSpec` for imported window, caller, and XR targets

Owned specs carry size policy, lifetime, usage, format, sample count, layer count,
history policy, feature predicate, dependencies, and a factory when the resource
can be materialized up front. Layout construction validates duplicate names,
missing dependencies, invalid framebuffer attachments, and unsupported sizes,
then topologically orders resources before materialization.

The specs lower into the existing descriptor records used by
`RenderResourceRegistry` and the Vulkan planner:

- `TextureResourceDescriptor`
- `FrameBufferResourceDescriptor`
- `RenderBufferResourceDescriptor`
- `BufferResourceDescriptor`

This keeps OpenGL and Vulkan on the same logical resource contract. External
specs lower to no owned allocation: they record the expected resource kind,
owner, and synchronization boundary and are bound by the window, caller, XR
runtime, or backend.

## Custom Pipeline Authoring

A pipeline that owns GPU resources overrides `DescribeResources(...)`. Command
builders consume those names only; they never create or repair a missing entry.

```csharp
protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
{
    builder.Texture("Color")
        .Size(RenderResourceSizePolicy.Internal())
        .Usage(RenderPipelineResourceUsage.SampledTexture |
               RenderPipelineResourceUsage.ColorAttachment)
        .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
        .SizedFormat(ESizedInternalFormat.Rgba16f)
        .Factory(CreateColorTexture)
        .Add();

    builder.FrameBuffer("ColorFBO")
        .Size(RenderResourceSizePolicy.Internal())
        .Color(0, "Color")
        .Factory(CreateColorFbo)
        .Add();
}
```

Layout-affecting settings belong in `BuildResourceFeatureMaskForGenerationKey`
or another explicit generation-key field. History uses `History(...)` and must
define its first-frame behavior. Caller/window/XR-owned targets use
`External(...)` with explicit ownership and synchronization. Resource-free
pipelines such as `DebugOpaqueRenderPipeline` may render only to the imported
output target and return an empty layout. `CustomRenderPipeline` command assets
must likewise reference either declared names supplied by a specialized
subclass or imported targets; arbitrary command-time allocation is unsupported.

## Generations

`XRRenderPipelineInstance` owns resource generations:

- `ActiveGeneration` is the generation used by frame execution.
- `PendingGeneration` is prepared before command execution and must validate
  before it can replace the active generation.
- `RetiredGenerations` holds old generations until they can be disposed through
  the existing conservative GPU synchronization bridge.

Preparing a pending generation does not mutate the active registry. The instance
pushes a scoped build context so the existing `GetTexture`, `SetTexture`,
`GetFBO`, `SetFBO`, `GetBuffer`, `SetBuffer`, `GetRenderBuffer`, and
`SetRenderBuffer` helpers resolve against the pending registry during
materialization. Nested or cross-thread build contexts throw instead of
silently corrupting the live registry.

Commit is atomic from the pipeline's point of view: the pending generation must
materialize every required resource, validate framebuffer attachment identity,
validate framebuffer dimensions, sample counts, declared formats, and backend
completeness, and validate texture-view source identity, mip range, layer
range, format/aspect, and target interpretation. History resources marked
`SeedFromCurrentFrame` may commit without an initial concrete instance so they
can be populated from the first committed frame. On success, the pending
generation becomes active, the legacy integer `ResourceGeneration` stamp
increments, and the old generation is retired. On failure, the pending
generation is disposed and the active generation keeps rendering.

## Resize And Settings Changes

Internal-resolution resize, display-region resize, rendering settings changes,
and AA settings changes request a new generation instead of emptying the active
registry. The active generation remains available until the replacement
generation commits.

Resize-sized generation requests are debounced for 125 ms and capped at 300 ms
of coalescing so interactive window and scene-panel drags do not rebuild every
intermediate size. Initial generation is prepared immediately so the first frame
has a complete registry. Replacement generations are materialized incrementally
on the render thread, currently up to four declared specs or roughly 2 ms per
slice, and commit only after the final slice validates required resources and
FBO completeness. This avoids black-frame registry gaps while preserving the
active generation during longer replacement builds.

On GLFW/Silk.NET windows, the Windows move/resize modal loop can block the
normal render tick until the user releases the mouse. `XRWindow` therefore
processes pending framebuffer size changes and invokes the existing render
callback from Silk's repaint path while `DoEvents()` is inside that modal loop.
The workaround is window-pump level and remains renderer-independent: OpenGL
and Vulkan still use their normal render and present paths.

`XRWindow` tracks resize with separate native, presentation/output, and full
internal extents. Interactive resize may update presentation/output extents
without immediately rebuilding the full internal render graph. A pending full
internal generation is committed only after the matching viewport internal
resolution and render-pipeline generation are active; stale pending generations
are rejected. This keeps live window borders responsive while avoiding a
partially committed registry or stale exact-size diagnostics.

## Default Pipeline Coverage

`DefaultRenderPipeline` declares its complete profile-selected graph, including:

- depth/stencil textures and depth/stencil views
- GBuffer textures, transform IDs, and GBuffer FBOs
- lighting accumulation, HDR scene, BRDF, and
  auto-exposure resources
- deferred MSAA resources behind the effective MSAA predicate
- forward depth-normal prepass resources behind the prepass predicate
- post-process and final output resources
- stable transparency resources
- temporal history, velocity, motion blur, depth-of-field, and exposure-history
  resources
- FXAA and TSR resources behind their AA predicates
- bloom, SMAA, atmosphere, volumetric-fog, and exact-transparency resources
- ReSTIR, light-volume, radiance-cascade, surfel, and voxel-cone GI outputs
- overdraw and other debug visualization resources
- attachmentless fullscreen materials represented by `QuadMaterialSpec`

Mutually exclusive resources and attachment choices are selected by immutable
profile predicates. AO-disabled profiles receive the declared fallback texture;
AO-enabled profiles receive the declared mode-specific target, so light-combine
attachments never rebuild themselves during command execution. Neither default
command tree performs resource creation or descriptor publication during execution.

## Backend Contract

OpenGL materializes declared resources through the existing concrete resource
factories and registry bindings.

Vulkan consumes the same generation-owned descriptors by building a pending
resource planner and allocator from the committed registry. The pending Vulkan
plan validates render-pass metadata against declared textures, buffers, FBOs,
and FBO attachment slots, then allocates the replacement physical images and
buffers before swapping it into the active renderer state. If pending physical
allocation fails, the active plan remains in use and the failure is logged.
After a successful swap, old Vulkan physical resources are retired through the
renderer's existing frame-slot destruction queues; any remaining conservative
idle bridge stays explicit and diagnosed.

## Diagnostics And Tests

Generation logs include the pipeline key, generation key, resource counts, build
duration, commit reason, failure reason, and active generation retained after a
failure.

Descriptor parity diagnostics run once per committed generation and compare the
declared descriptors with the registry descriptors for migrated resources. They
are migration diagnostics only and should become unnecessary as legacy
cache-command authoring is removed for migrated resources.

Focused tests live in
`XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs` and cover
layout ordering, missing attachment validation, default-pipeline resource
coverage, MSAA predicates, pending-registry materialization, generation state
transitions, FBO validation failures, texture-view validation failures, and
seeded history resources.

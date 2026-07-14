# Render Pipeline Resource Lifecycle

[<- Rendering Architecture index](../../../architecture/rendering/README.md)

Status: implemented architecture; retained historical analysis below explains
the migration from command-list cache commands to explicit resource
specification and generation-based resource swaps.

## Summary

Render pipelines should declare the textures, render buffers, framebuffers, data
buffers, and graph-owned transient resources they require before frame
execution. The pipeline instance should materialize those declarations into a
complete resource generation, validate that generation, then commit it
atomically. During viewport or internal-resolution resize, the old generation
should keep rendering until the new one is ready.

The current `VPRC_CacheOrCreateTexture`, `VPRC_CacheOrCreateFBO`,
`VPRC_CacheOrCreateBuffer`, and `VPRC_CacheOrCreateRenderBuffer` commands are a
useful migration bridge, but they should not remain the long-term source of
truth for core pipeline resources. They occupy the render command list, execute
every frame, and rebuild resources by mutating the live registry. That makes
interactive resize prone to missing-resource frames and makes Vulkan planning
depend on descriptors discovered during command execution.

The target design is:

1. `RenderPipeline` describes a resource layout.
2. `XRRenderPipelineInstance` owns active and pending resource generations.
3. Resize creates a pending generation at the new size.
4. Rendering continues from the active generation while pending resources build.
5. Once pending resources are complete, the instance swaps generations and
   retires the previous generation after GPU use has finished.

## Goals

- Remove core FBO/texture creation commands from steady-state frame execution.
- Make pipeline resource requirements visible before executing render passes.
- Make viewport and internal-resolution resize free of missing-resource and
  black frames. Building a generation can still cost frame time; see
  [Threading And Resize Cost](#threading-and-resize-cost).
- Keep old resources alive until replacement resources are complete.
- Provide a single resource plan usable by OpenGL, Vulkan, tooling, debugging,
  and future render-graph scheduling.
- Make resource failures visible through diagnostics instead of silently
  presenting black frames or falling back to unrelated paths.
- Preserve command ownership for execution helpers such as fullscreen quads,
  materials, cached renderers, and branch-local state.

## Non-Goals

- This is not a full frame-graph rewrite.
- V1 and V2 retain separate command-chain organization; declaration helpers may
  be deduplicated without making V2 inherit V1's execution structure.
- XR/stereo present integration (OpenXR/OpenVR swapchain images, per-eye
  targets) is out of scope and is deferred to a separate follow-up doc. The
  generation key reserves room for a stereo dimension, but this round implements
  the mono desktop path.
- This does not remove command containers, mesh render commands, pass metadata,
  or render-graph synchronization.
- This does not make CPU fallback acceptable for explicitly requested GPU paths.
- This does not require all dynamic feature resources to be known in the first
  migration step.

## Current Behavior

The current resize path has two destructive branches, both triggered from
`XRViewport.Resize(...)`:

```text
XRViewport.Resize(...)
  -> XRViewport.SetInternalResolution(...)            // only when internal size changes
  |    -> XRRenderPipelineInstance.InternalResolutionResized(...)
  |       -> XRRenderPipelineInstance.InvalidatePhysicalResources()
  |          -> wait for Vulkan idle when required
  |          -> RenderResourceRegistry.DestroyAllPhysicalResources(retainDescriptors: true)
  |          -> ResourceGeneration++
  -> XRViewport.ResizeRenderPipeline()                // display-region resize, on every resize
       -> XRRenderPipelineInstance.ViewportResized(Width, Height)
          -> DefaultRenderPipeline.HandleViewportResized(...)
             -> RenderPipelineAntiAliasingResources.InvalidateViewportResizeResources(...)
             -> XRWindow.RequestRenderStateRecheck(resetCircuitBreaker: true)
```

The command chain then lazily recreates resources on subsequent execution:

```text
VPRC_CacheOrCreateTexture.Execute()
VPRC_CacheOrCreateFBO.Execute()
VPRC_CacheOrCreateBuffer.Execute()
VPRC_CacheOrCreateRenderBuffer.Execute()
```

This has several practical issues:

- The live registry is intentionally emptied before replacement resources exist.
- Passes may observe missing textures or FBOs until recreation catches up.
- FBO factories frequently call `GetTexture(... )!`, so dependency order matters
  and partial rebuilds can throw.
- `ViewportRenderCommandContainer.Execute()` catches command exceptions and
  continues so the pipeline can recover on later frames, but that recovery window
  can present black or skipped frames.
- `VPRC_CacheOrCreateFBO` may destroy an FBO because attachment identity changed,
  then recreate it in the same live registry.
- `VPRC_CacheOrCreateTexture` may try in-place resize, then destroy and recreate
  if type, size, format, sample count, or view identity is still incompatible.
- Vulkan already wants descriptors before execution through
  `VulkanResourcePlanner`, but the descriptors are currently incidental metadata
  registered by cache commands.
- The display-region path (`ViewportResized(...) -> HandleViewportResized(...)`)
  is a second, independent source of resize churn. It evicts the
  AA/post-process/present source chain via `InvalidateViewportResizeResources`
  and forces a render-state recheck, separately from the internal-resolution
  destruction above. Any staged-resize design must cover both branches.

This behavior is correct enough for recovery, but it is a poor fit for smooth
interactive resizing and explicit APIs.

## Existing Building Blocks

The repo already has much of the structure needed for the target design:

- `RenderResourceDescriptor`
- `TextureResourceDescriptor`
- `FrameBufferResourceDescriptor`
- `RenderBufferResourceDescriptor`
- `BufferResourceDescriptor`
- `RenderResourceSizePolicy`
- `RenderResourceLifetime`
- `RenderResourceRegistry`
- `XRRenderPipelineInstance.ResourceGeneration`
- `VulkanResourcePlanner`
- `VulkanResourceAllocator`
- render-pass metadata and resource usage descriptions

The missing step is making resource descriptors authoritative and complete before
execution, rather than best-effort metadata registered while commands run. The
existing descriptor records are also thinner than the target specs below; see
[Resource Spec Fields](#resource-spec-fields).

## Proposed Architecture

### Pipeline Resource Layout

Add a resource-description hook to `RenderPipeline`:

```csharp
protected virtual void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
{
}
```

The builder produces an immutable `RenderPipelineResourceLayout`. A layout is a
set of named resource specs plus dependency metadata. Each spec describes what
the resource is and how it should be materialized for a viewport, camera, and
effective frame profile.

Core specs:

```text
TextureSpec
RenderBufferSpec
FrameBufferSpec
BufferSpec
TextureViewSpec
QuadMaterialSpec
```

`QuadMaterialSpec` is optional and may be deferred until later. It is useful for
FBO-like resources such as `PostProcessFBO`, `LightCombineFBO`, and fog/atmosphere
quad passes whose "FBO" object also owns a material with source texture bindings.

### Resource Spec Fields

Every resource spec should include:

| Field | Purpose |
|-------|---------|
| `Name` | Stable logical resource name. |
| `Kind` | Texture, texture view, renderbuffer, framebuffer, data buffer, or external. |
| `Lifetime` | Persistent, transient, or external (the existing enum). History and frame-local are not lifetimes; see below. |
| `SizePolicy` | Internal, window/full, half-internal, absolute, custom scale, or external. |
| `Format` | Sized/internal format, pixel format, pixel type, buffer storage, or data layout. |
| `Samples` | MSAA sample count policy. |
| `Layers` | Array layers. (Stereo/per-eye layers are deferred with XR.) |
| `MipPolicy` | Mip count, auto-generate behavior, immutable storage requirement. |
| `Usage` | Color attachment, depth/stencil attachment, sampled texture, storage image, transfer source/destination, uniform/storage buffer, etc. |
| `Dependencies` | Logical resource names that must exist before this resource is materialized. |
| `Predicate` | Feature/runtime condition that determines whether the resource is required for this profile. |
| `HistoryPolicy` | How to handle resize, camera cut, AA mode change, and scene transition. |
| `DebugLabel` | Human-readable label for logs, GPU debug markers, and inspection tools. |

Several of these fields exceed what the current descriptor records carry. Today
`TextureResourceDescriptor` only has `FormatLabel` (a string), `StereoCompatible`,
`ArrayLayers`, `SupportsAliasing`, and `RequiresStorageUsage`, and
`RenderResourceLifetime` is only `{ Persistent, Transient, External }`.

Decision: introduce new spec types (`TextureSpec`, `RenderBufferSpec`,
`FrameBufferSpec`, `BufferSpec`, `TextureViewSpec`, and optional
`QuadMaterialSpec`) rather than widening the existing descriptor records. The
specs are the authoring and authoritative surface consumed by the layout builder
and resource manager; the existing `*ResourceDescriptor` records remain the
lower-level registry and Vulkan-planner currency, produced by lowering a spec.
This keeps registry and `VulkanResourcePlanner` inputs stable while the richer
authoring model evolves, and avoids churning a type those systems already switch
on.

Decision: `RenderResourceLifetime` stays at `{ Persistent, Transient, External }`.
History and frame-local are not added as lifetime values. Lifetime is a single
axis answering "how long must the allocation live, and can it alias?" Aliasing is
currently derived directly from it: `RenderResourceDescriptorFactory` sets
`SupportsAliasing: lifetime == Transient`. Adding `History` (which must not
alias, because it is sampled next frame) or `FrameLocal` (which is just
`Transient`) would force every lifetime switch and that aliasing derivation to be
revisited. Instead:

- frame-local resources use `Transient`.
- history resources use `Persistent` allocation plus a `HistoryPolicy` field on
  the spec (and, where they read previous and write current, a ping-pong pair;
  see [History Resources](#history-resources)).

The typed `Format`/`Samples`/`MipPolicy`/`Usage` fields, `Dependencies`,
`Predicate`, `HistoryPolicy`, and `DebugLabel` are new and live on the spec types.

The spec should be rich enough that the backend can plan physical resources and
the engine can report missing or incompatible resources without executing a
draw command.

### Resource Profiles

Some resources depend on effective frame state:

- output HDR or SDR
- AA mode
- MSAA sample count
- TSR/DLSS/XeSS internal render scale
- Vulkan-safe feature profile
- post-process feature enablement
- debug visualization mode

Those settings should be resolved into a compact `RenderPipelineResourceProfile`
before preparing resources. The profile becomes part of the generation key:

```text
ResourceGenerationKey =
  pipeline type/version
  viewport display size
  viewport internal size
  output HDR mode
  AA mode
  MSAA samples
  enabled resource feature set
  // stereo mode: reserved for the XR follow-up; mono only this round
```

The profile should use latched frame values when available. It must not observe
different AA/HDR settings halfway through a generation. Stereo/per-eye keying is
deferred to the XR follow-up; this round keys the mono desktop path only.

### Pipeline Instance Resource Generations

`XRRenderPipelineInstance` should own generation objects instead of a single
mutable registry. To avoid colliding with the existing `int ResourceGeneration`
counter on `XRRenderPipelineInstance` (a monotonic invalidation stamp read by
commands such as `VPRC_LightCombinePass`), the new generation type is named
`RenderResourceGeneration`. The existing `int` stamp remains during migration;
each committed `RenderResourceGeneration` bumps it so cache commands keyed on the
stamp still observe a change.

```text
ActiveGeneration    : RenderResourceGeneration
PendingGeneration   : RenderResourceGeneration?
RetiredGenerations  : RenderResourceGeneration awaiting GPU completion
```

Each generation contains:

- immutable generation key
- resolved resource layout
- `RenderResourceRegistry`
- backend physical allocation handles or links
- validation status
- diagnostics
- a GPU fence or conservative retirement mode

Active generation is used for frame execution. A pending generation is prepared
in two stages: descriptor and profile resolution can run off the render thread,
but physical GPU object creation (GL/Vulkan handles and FBO completeness checks)
must run on the render-thread context, consistent with the existing
`EnqueueResourceMutationIfOffRenderThread` guard. Retired generations are
destroyed only when no in-flight frame can reference them. See
[Threading And Resize Cost](#threading-and-resize-cost) for the resize-cost
caveat.

### Scoped Resource Build Context

Current resource factories use static helpers such as:

- `InternalWidth`
- `InternalHeight`
- `FullWidth`
- `FullHeight`
- `GetTexture<T>(...)`
- `GetFBO<T>(...)`
- `SetTexture(...)`
- `SetFBO(...)`

For migration, add a scoped resource build context so existing factories can be
reused while building a pending generation:

```text
using instance.PushResourceBuildContext(pendingGeneration)
{
    materialize specs in dependency order
}
```

Inside the scope:

- size helpers return the pending generation's target size
- `GetTexture` and `GetFBO` resolve from the pending registry first
- `SetTexture`, `SetFBO`, `SetBuffer`, and `SetRenderBuffer` bind into the pending
  registry
- active rendering still reads from the active generation

This avoids a broad first-pass rewrite of every factory while still enabling
transactional generation.

## Resize Lifecycle

### Desired Behavior

When a viewport or internal resolution changes, continue presenting the previous
render size until a replacement generation is fully ready.

The visible sequence should be:

```text
resize requested
  -> display region updates
  -> active generation keeps rendering at old internal size
  -> pending generation builds resources for new internal size
  -> pending generation validates complete
  -> commit pending as active
  -> render at new size
  -> retire old generation after GPU completion
```

This is the same UX principle already used by `XRWindowScenePanelAdapter`, which
keeps using the existing scene-panel FBO during resize debounce and lets ImGui
scale it until the new FBO is applied.

### API Sketch

Both resize entry points funnel into one generation request. The generation key
includes both display and internal size (see [Resource Profiles](#resource-profiles)),
so internal-resolution resize and display-region resize each prepare a pending
generation through the same path:

```csharp
public void RequestInternalResolutionResize(int width, int height)
    => RequestResourceGeneration(BuildGenerationKey(internalSize: (width, height)));

// Display-region resize must use the same mechanism. Window/full-resolution
// resources rebuild here, while internal-resolution resources rebuild above.
// Today this path is DefaultRenderPipeline.HandleViewportResized, which
// destructively evicts the AA/post-process/present chain; it should become a
// generation request instead.
public void RequestDisplayResize(int width, int height)
    => RequestResourceGeneration(BuildGenerationKey(displaySize: (width, height)));

private void RequestResourceGeneration(ResourceGenerationKey key)
{
    if (ActiveGeneration.Key == key || PendingGeneration?.Key == key)
        return;

    // A newer request supersedes any in-flight pending generation. Cancel and
    // dispose it so abandoned partial builds do not leak GPU objects. See
    // "Resize During Interactive Drag" for debounce/coalescing during drag.
    PendingGeneration?.CancelAndDispose();
    PendingGeneration = RenderResourceGeneration.Prepare(key);
    QueuePreparePendingGeneration();
}
```

Commit:

```csharp
private void TryCommitPendingGeneration()
{
    if (PendingGeneration is null || !PendingGeneration.IsReady)
        return;

    RenderResourceGeneration old = ActiveGeneration;
    ActiveGeneration = PendingGeneration;
    PendingGeneration = null;

    ResourceGeneration++; // bump the existing int stamp for cache-command compatibility
    ReleaseCommandContainerResourcesForNewGeneration();
    Retire(old);
    NotifyRenderResourcesChanged();
}
```

Failure:

```text
pending generation failed
  -> keep active generation
  -> record diagnostics
  -> retry when inputs change or after a bounded delay
```

Do not destroy active resources as part of a failed resize.

### Active Size vs Requested Size

The viewport should distinguish:

- display region size: where the image is presented
- requested internal size: desired render-resource size
- active internal size: size of the currently committed generation

During pending generation, render passes use active internal size. Presentation
uses display region size. Once pending commits, active internal size becomes the
requested internal size.

### Threading And Resize Cost

Generation preparation has two distinct cost centers:

- Descriptor and profile resolution (CPU only) can run off the render thread.
- Physical GPU object creation must run on the render-thread context. OpenGL
  handle/wrapper creation and FBO completeness checks, and Vulkan image/buffer
  allocation, cannot be moved off that context.

Because the active generation keeps presenting while the pending one builds,
resize never shows missing-resource or black frames. It can still cost frame
time: a full deferred generation is dozens of textures and FBOs (the example
diagnostic later shows ~54 textures and 32 FBOs at `BuildMs=3.42`), a meaningful
fraction of a frame at high refresh rates. To avoid a single multi-millisecond
build hitch on commit, physical creation is time-sliced across frames: a pending
generation prepares incrementally (a bounded number of object creations and FBO
completeness checks per frame) and only becomes committable once all physical
objects exist and validate. The active generation keeps presenting throughout, so
time-slicing trades a slightly longer time-to-sharp for a smoother frame rate
during resize.

### Resize During Interactive Drag

Interactive resize (dragging a window edge or scene-panel splitter) fires many
resize events per second. Preparing and discarding a time-sliced generation per
event would waste GPU work and could leak partially built objects. The target
behavior during an active drag is:

- Do not prepare a new generation on every resize event. Keep presenting the
  active generation, scaled to the current display region. This reuses the
  existing `XRWindowScenePanelAdapter` behavior, which already keeps the prior
  scene-panel FBO and lets ImGui scale it during resize debounce. Aspect ratio is
  preserved by the present blit, so the image is briefly soft but never
  distorted, missing, or black.
- Coalesce the requested size. While events keep arriving, restart a short
  debounce timer (target ~100-150 ms of no change) and also cap the maximum
  debounce interval so a slow continuous drag still commits periodically.
- When the debounce settles (or on an interval cap), call
  `RequestResourceGeneration(...)` with the latest size. Building is time-sliced
  across frames, so a mid-drag commit is allowed and simply sharpens the image
  once ready, and the final mouse-release size commits the same way.
- Each new request supersedes the in-flight pending generation via
  `CancelAndDispose`, so at most one pending generation builds at a time and
  superseded partial builds release their objects.
- Cap retired generations awaiting GPU completion (e.g. 2-3). If the cap is
  exceeded during a fast drag, force a GPU sync before starting another build so
  retired resources cannot accumulate unbounded.

Presenting the active image scaled is the v1 approach because it reuses the
scene-panel scaling path and needs no per-pass changes. Rendering into a sub-rect
of an oversized target is an alternative that avoids scaling softness and handles
the grow case directly, but it requires every pass to honor a render sub-rect
(viewport, scissor, full-screen UV math, mip generation). That is left as an
optional later enhancement.

## Command List Role After Migration

Render commands should execute rendering, state binding, dispatch, blit, and
presentation. They should not be the primary place where graph resources are
created every frame.

Keep command-owned resources for:

- fullscreen quad renderers
- command-local materials
- cached mesh renderers
- temporary CPU-side state
- feature controller state
- branch-local helpers that are not graph resources

Move pipeline-owned graph resources into the layout:

- GBuffer textures
- depth/stencil textures and views
- HDR scene textures
- lighting textures
- post-process output textures
- AA/upscale textures
- temporal history textures
- bloom/fog/atmosphere textures
- PPLL/depth-peeling graph buffers
- FBOs and attachment relationships
- renderbuffers

In the migration window, cache commands remain valid for unmigrated resources.
They should gradually become compatibility commands rather than standard
pipeline authoring tools.

## Required vs Optional Resources

The layout should support both required and conditional resources.

Required resources are always part of the generation. If one fails, the
generation cannot commit.

Conditional resources are included when their predicate is true for the resource
profile. Examples:

- MSAA GBuffer resources when MSAA deferred is enabled.
- FXAA/SMAA/TSR resources depending on effective AA mode.
- atmosphere chain resources only when the camera requires aerial perspective.
- volumetric fog chain resources only when active fog volumes exist.
- debug visualization targets only when the debug mode is enabled.

Predicates should be evaluated against stable profile inputs when possible.
Per-frame scene-content predicates can remain command-gated at first if the
resource would be expensive or impossible to predict. The layout can later add
"optional warm" resources for features that benefit from preallocation.

## Texture Views

Texture views should be explicit resources. A view spec should name its source
texture and the view parameters:

- source resource name
- mip range
- layer range
- sized internal format
- depth/stencil aspect
- array or multisample target interpretation

When a source texture is replaced in a pending generation, the corresponding
view should be rebuilt against the pending source. Avoid retargeting an active
view in place during resize.

## FBOs And Attachments

FBO specs should describe attachments by logical resource name:

```text
DeferredGBufferFBO
  color0 -> AlbedoOpacityTexture
  color1 -> NormalTexture
  color2 -> RMSETexture
  color3 -> TransformIdTexture
  depthStencil -> DepthStencilTexture
```

The resource manager should validate:

- all attachment resources exist in the same generation
- attachment dimensions agree
- sample counts agree
- attachment formats are compatible
- texture identity matches the generation
- backend completeness succeeds

This replaces the need for repeated size and texture-identity predicates in
hot commands.

## History Resources

History textures should have an explicit history policy. Examples:

| Event | Typical Policy |
|-------|----------------|
| First generation | clear or seed from current frame |
| Resize | reset or copy compatible region |
| Camera cut | reset |
| AA mode change | reset |
| Output HDR change | reset/recreate |
| Scene transition | reset |

History resources should not block the resize commit if they can safely start
empty. When a history resource is incompatible, the generation should commit with
history marked invalid and the relevant history pass should seed it.

History resources that read previous-frame data while writing current-frame data
(temporal AA, temporal accumulation) need two physical buffers that swap each
frame. This intra-generation ping-pong is separate from the inter-generation swap
used for resize: a generation owns its ping-pong pair, and on commit the new
generation seeds or invalidates that pair per the table above rather than reusing
the retired generation's history.

## Vulkan Considerations

Vulkan benefits most from declared resources:

- `VulkanResourcePlanner` can sync from a complete layout before execution.
- `VulkanResourceAllocator` can build an allocation plan from the full resource
  set rather than a partial registry.
- Render-pass metadata can be validated against declared resources before command
  buffers are recorded.
- Physical image aliasing can be planned from resource lifetimes and usage.
- Dynamic rendering migration can consume attachment specs directly.

For resize free of missing-resource frames, Vulkan also needs a prepare/swap
allocator path. `VulkanResourceAllocator.RebuildPhysicalPlan(...)` currently
destroys physical images and buffers before rebuilding. The target form should prepare a new
physical plan, allocate it, validate it, then atomically publish it and retire
the old plan after fences signal.

An initial conservative implementation may still use `DeviceWaitIdle()` on
commit or retirement. That is acceptable as a correctness bridge, but the API
shape should not require idle waits long term.

## OpenGL Considerations

OpenGL object creation is simpler but still benefits from transactional
generation:

- Do not delete active GL textures/FBOs before replacement objects exist.
- Generate and completeness-check pending FBOs before commit.
- Keep old GL objects alive for at least the current frame.
- Avoid in-place resize for active resources during interactive resize.
- Preserve current diagnostics for incomplete FBOs and attachment mismatches.

OpenGL can use immediate backend generation for pending resources on the render
thread, then swap registries once all handles are generated and FBO completeness
checks pass.

## Resource Layout Builder Example

Example shape for `DefaultRenderPipeline`:

```csharp
protected override void DescribeResources(RenderPipelineResourceLayoutBuilder b)
{
    b.Texture(DepthStencilTextureName)
        .Size(RenderResourceSizePolicy.Internal())
        .Format(EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248)
        .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
        .Attachment(EFrameBufferAttachment.DepthStencilAttachment)
        .Lifetime(RenderResourceLifetime.Persistent);

    b.TextureView(DepthViewTextureName)
        .Source(DepthStencilTextureName)
        .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
        .DepthStencilAspect(EDepthStencilFmt.Depth)
        .Lifetime(RenderResourceLifetime.Persistent);

    b.Texture(AlbedoOpacityTextureName)
        .Size(RenderResourceSizePolicy.Internal())
        .Format(EPixelInternalFormat.Srgb8Alpha8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
        .SizedFormat(ESizedInternalFormat.Srgb8Alpha8)
        .Attachment(EFrameBufferAttachment.ColorAttachment0)
        .Lifetime(RenderResourceLifetime.Persistent);

    b.FrameBuffer(DeferredGBufferFBOName)
        .Color(0, AlbedoOpacityTextureName)
        .Color(1, NormalTextureName)
        .Color(2, RMSETextureName)
        .Color(3, TransformIdTextureName)
        .DepthStencil(DepthStencilTextureName)
        .Size(RenderResourceSizePolicy.Internal())
        .Lifetime(RenderResourceLifetime.Transient);
}
```

This example is illustrative. The final builder should minimize repeated format
boilerplate through local helpers and named templates.

## Migration Plan

### Phase 0: Documentation And Guardrails

- Add this design document.
- Add a short note to `default-render-pipeline-notes.md` once implementation
  begins.
- Keep existing cache commands unchanged.
- Add diagnostics that distinguish active, pending, and retired resource
  generations once the generation model exists.

### Phase 1: Declared Descriptors Without Behavior Change

- Add `DescribeResources(...)` and `RenderPipelineResourceLayoutBuilder`.
- Teach `DefaultRenderPipeline` to declare core textures and FBO descriptors.
  (`DefaultRenderPipeline2` is out of scope this round.)
- Keep cache commands as the actual materialization path.
- Validate that descriptors from layout and descriptors registered by cache
  commands agree.
- Emit warnings for missing layout entries, format mismatch, size-policy mismatch,
  and attachment mismatch.

This phase makes the layout authoritative enough for tooling without changing
runtime behavior.

### Phase 2: Resource Manager Materialization

- Add `RenderPipelineResourceManager`.
- Materialize declared textures, texture views, renderbuffers, buffers, and FBOs
  before command execution.
- Let cache commands no-op when a declared resource already exists and matches
  the active generation.
- Keep unmigrated command-created resources functional.

### Phase 3: Staged Resize

- Add requested vs active internal size to `XRViewport` or the pipeline instance.
- Route both resize branches through `RequestResourceGeneration(...)`: the
  internal path (`InternalResolutionResized(...) -> InvalidatePhysicalResources()`)
  and the display path (`ViewportResized(...) -> HandleViewportResized(...)`,
  which today evicts the AA/post-process/present chain via
  `InvalidateViewportResizeResources`). Both should prepare a pending generation
  instead of destroying live resources.
- Build pending generations at the requested size.
- Keep rendering active generation until pending is complete.
- Commit pending generation atomically.
- Retire old generation after GPU completion.

### Phase 4: Remove Core Cache Commands

- Remove `CacheTextures(c)` calls for migrated core resources.
- Remove FBO cache commands for migrated core FBOs.
- Keep cache commands for truly dynamic, branch-local, or experimental resources
  until they are declared.
- Ensure command chains become primarily execution order, not resource lifecycle.

### Phase 5: Vulkan Prepare/Swap Physical Plan

- Add Vulkan pending physical resource plan support.
- Allocate pending images/buffers/FBOs without destroying the active plan.
- Commit the pending physical plan with the logical generation.
- Retire old physical resources via fences instead of unconditional idle waits.

## Validation

Target validation after implementation:

- Resize the editor scene panel continuously; no black frames.
- Resize the main window continuously; no black frames.
- Toggle output HDR while rendering; resources regenerate without stale FBO
  attachments.
- Toggle AA modes and MSAA sample counts; active generation remains valid until
  replacement generation commits.
- Run `DefaultRenderPipeline` resource-layout descriptor parity tests (layout
  specs vs cache-command-produced descriptors).
- Run a targeted editor build:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
```

- For Vulkan, validate Sponza/deferred path after resize and confirm no
  missing-resource warnings from light combine or post-process present.
- For OpenGL, validate FBO completeness diagnostics remain quiet after repeated
  resize.

## Diagnostics

Add diagnostics for:

- active generation key
- pending generation key and status
- generation build duration
- resource count by kind
- missing required resources
- failed backend generation
- incomplete FBO name and attachment summary
- commit reason
- retirement reason
- old generation lifetime after commit

Example log shape:

```text
[RenderResources] Pending generation ready. Pipeline=DefaultRenderPipeline#3
Old=1920x1080 New=1457x820 Textures=54 FBOs=32 Buffers=6 BuildMs=3.42
```

Failure example:

```text
[RenderResources] Pending generation failed. Pipeline=DefaultRenderPipeline#3
Target=1457x820 Resource=DeferredGBufferFBO Reason=Missing attachment NormalTexture
Active generation remains 1920x1080.
```

## Risks

- Factory migration may reveal hidden ordering dependencies that cache commands
  currently tolerate through frame-to-frame recovery.
- Resource predicates based on scene content can make the required resource set
  unstable. Start with always-needed and settings-driven resources.
- Vulkan physical plan double-buffering must account for descriptor references,
  image layouts, framebuffers, and command buffers that may still reference old
  resources.
- OpenGL wrapper creation currently happens when `GenericRenderObject` instances
  are constructed. Pending generation must run on the render thread or suppress
  wrapper creation until the correct backend context is active.
- History resources need explicit reset behavior so old history is not mistaken
  for valid current-size history after a generation swap.
- Command-owned materials that capture source texture references must be rebuilt
  or rebound when the generation changes.

## Resolved Decisions

- Scope: this round targets `DefaultRenderPipeline` only. `DefaultRenderPipeline2`
  and XR/stereo present integration are deferred (see Non-Goals).
- Authoring model: introduce new spec types (`TextureSpec`, `RenderBufferSpec`,
  `FrameBufferSpec`, `BufferSpec`, `TextureViewSpec`, optional `QuadMaterialSpec`)
  that lower to the existing `*ResourceDescriptor` records, rather than widening
  those records.
- Lifetime stays `{ Persistent, Transient, External }`. Frame-local maps to
  `Transient`; history maps to `Persistent` plus a `HistoryPolicy` (and a
  ping-pong pair where needed). Aliasing remains derived from lifetime.
- Resize cost: physical creation is time-sliced across frames; the active
  generation keeps presenting until the pending generation fully validates.
- Interactive drag: present the active generation scaled during drag, debounce
  the requested size (~100-150 ms, with a max-interval cap), and supersede any
  in-flight pending generation. See
  [Resize During Interactive Drag](#resize-during-interactive-drag).
- Scaled resources reuse the existing `RenderResourceSizePolicy` factories:
  `Internal(scale)` (e.g. `Internal(0.5f)` for half resolution), `Window(scale)`,
  `Custom(scaleX, scaleY)`, and `Absolute(width, height)`. No new per-scale
  helper methods are needed.

## Open Questions

- Should resource predicates be evaluated per camera, per viewport, or per
  pipeline instance?
- Should optional feature resources be prewarmed when a feature is disabled but
  likely to be enabled soon, such as debug visualizations?
- Should transient resources be aliasable in OpenGL, or should aliasing remain a
  Vulkan-only physical allocation optimization?
- Should fullscreen quad materials become declared resource specs or remain
  command-owned indefinitely?
- Should descriptor parity tests compare exact descriptors or tolerate equivalent
  legacy factory output during migration?

## Target Invariants

- A committed generation is complete enough to execute all required passes for
  its profile.
- Active generation resources are never destroyed while that generation may be
  presented or sampled by in-flight GPU work.
- Resize never empties the active registry.
- Pending generation failure does not destroy active resources.
- FBO attachments always reference resources from the same generation.
- Texture views always reference source textures from the same generation.
- Command containers allocate execution helpers against the committed generation.
- Core pipeline resources are visible before frame execution begins.
- Vulkan planning consumes the declared resource layout, not command side effects.
- Cache commands are transitional compatibility tools, not the final resource
  architecture.

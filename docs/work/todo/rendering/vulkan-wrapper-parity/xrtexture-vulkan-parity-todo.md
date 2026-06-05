# XRTexture Vulkan Parity TODO

Last Updated: 2026-06-05
Status: Active.

## Goal

Make Vulkan texture wrappers expose the same engine-facing behavior as OpenGL
texture wrappers for every `XRTexture` subtype: events, property changes,
sampler state, upload, mipmaps, framebuffer attachment, bind/clear operations,
views, resize handling, diagnostics, and streaming features.

## Source Inventory

OpenGL:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture1D.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture1DArray.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D*.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2DArray.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture3D.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureCube.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureCubeArray.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureRectangle.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureView.cs`

Vulkan:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTexture.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkImageBackedTexture.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTexture1D.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTexture1DArray.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTexture2D.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTexture2DArray.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTexture3D.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTextureCube.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTextureCubeArray.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTextureRectangle.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTextureBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkTextureView.cs`

## Current Parity Already Present

- Vulkan registers wrappers for all OpenGL texture wrapper categories.
- Image-backed Vulkan textures support descriptor views, samplers, layout
  transitions, staging uploads, attachment views, and generic blit mipmap
  generation.
- `VkTextureBuffer` supports Vulkan texel-buffer descriptors.
- `VkTextureView` can create image views and texel-buffer view passthroughs.

## Generation Contract

`IsGenerated` should mean the texture API object exists, not that its pixel data
is current. In OpenGL that is a texture object ID. In Vulkan, image-backed
textures should report generated when their Vulkan image/view/sampler resources
or other concrete backend handles exist; texture-buffer and texture-view
wrappers should key off their buffer/image view handles. Upload validity,
layout readiness, descriptor freshness, and mip/data residency need separate
state.

## Common Missing Parity TODO

1. Align texture generation and readiness state.
   - [ ] Fix image-backed Vulkan texture `IsGenerated` semantics so it reports
         handle existence, not data upload validity and not a permanent false
         default.
   - [ ] Keep upload validity, descriptor freshness, layout state, and mip
         residency separate from `IsGenerated`.
   - [ ] Add source/unit tests for generate, push, resize/recreate, and destroy
         state transitions for image-backed textures, texture buffers, and
         texture views.

2. Match base texture event wiring.
   - [ ] Subscribe/unsubscribe Vulkan wrappers to `AttachToFBORequested`,
         `DetachFromFBORequested`, `BindRequested`, `UnbindRequested`,
         `ClearRequested`, `PropertyChanged`, and `PropertyChanging` where the
         generic texture contract exposes those events.
   - [ ] Keep native Vulkan framebuffer ownership where appropriate, but make
         engine event behavior equivalent.
   - [ ] Add source tests for event subscription symmetry.

3. Match property invalidation and sampler updates.
   - [ ] Recreate or update Vulkan samplers when filter, wrap, LOD, anisotropy,
         depth-stencil mode, or compare settings change.
   - [ ] Apply `MinLOD`, `MaxLOD`, `LargestMipmapLevel`, and
         `SmallestAllowedMipmapLevel` to Vulkan sampler `MinLod` and `MaxLod`
         or document the native equivalent.
   - [ ] Fix `XRTexture2DArray.LodBias` omission in Vulkan sampler settings.
   - [ ] Mark descriptors dirty when sampler/image-view-affecting properties
         change.

4. Match upload hooks and auto-push behavior.
   - [ ] Implement equivalents for OpenGL `OnPrePushData` and
         `OnPostPushData`.
   - [ ] Ensure invalidated texture data is pushed before first descriptor use,
         not only when `PushDataRequested` is explicitly raised.
   - [ ] Keep upload-side diagnostics for missing data, invalid dimensions,
         unsupported format, and allocation failure.

5. Match clear/bind/unbind semantics.
   - [ ] Implement `ClearRequested` through Vulkan clear commands or an
         explicit unsupported diagnostic when no command context exists.
   - [ ] Decide what `BindRequested` and `UnbindRequested` mean for Vulkan:
         descriptor registration, current-program sampler binding, or no-op
         with diagnostics.
   - [ ] Ensure existing engine calls to `Bind()` or `Unbind()` do not silently
         skip required descriptor/image initialization.

6. Match framebuffer attachment behavior.
   - [ ] Verify all FBO attachment events have a Vulkan path through
         `VkFrameBuffer` or texture attachment-view creation.
   - [ ] Support per-layer, per-face, and OVR multiview attachment requests
         where the engine exposes them.
   - [ ] Add diagnostics when a requested attachment view cannot be created.

7. Match mipmap behavior.
   - [ ] Add a Vulkan equivalent to OpenGL's detail-preserving 2D compute
         mipmap path or document why generic blit is the accepted Vulkan v1
         behavior.
   - [ ] Validate non-filterable formats and fallback behavior.
   - [ ] Ensure mip-level counts and visible mip ranges match OpenGL for
         progressive and sparse streaming cases.

8. Match memory and diagnostics policy.
   - [ ] Add VRAM budget checks and allocation accounting comparable to OpenGL
         texture storage paths.
   - [ ] Rate-limit repeated Vulkan warnings.
   - [ ] Include texture name, type, dimensions, mip count, layer count, format,
         usage flags, and descriptor view type in failures.

## Type-Specific TODO

### `XRTexture1D`

- [ ] Track mipmap property changes and resized-data state like `GLTexture1D`.
- [ ] Recreate Vulkan image and descriptors when width, format, mip count, or
      sampler properties change.
- [ ] Validate upload of empty/missing mip levels against OpenGL behavior.

### `XRTexture1DArray`

- [ ] Track child texture and mipmap property changes.
- [ ] Recreate when layer count, layer dimensions, format, or mip count changes.
- [ ] Validate array-layer upload ordering and per-layer missing-data handling.

### `XRTexture2D`

- [ ] Port or explicitly replace OpenGL sparse texture streaming behavior.
- [ ] Port or replace progressive mip upload behavior.
- [ ] Add detail-preserving compute mipmap parity or document a Vulkan-native
      alternative.
- [ ] Validate video-frame upload behavior against OpenGL import/update paths.
- [ ] Align storage, external/imported texture, runtime-managed progressive
      range, and diagnostics behavior.

### `XRTexture2DArray`

- [ ] Add OVR multiview attach/detach event parity.
- [ ] Add per-layer attach/detach event parity.
- [ ] Track child `XRTexture2D` layer property changes and resize events.
- [ ] Recreate when layer dimensions diverge, layer count changes, or mip range
      changes.
- [ ] Fix sampler LOD bias.

### `XRTextureRectangle`

- [ ] Add resize subscription in `VkImageBackedTexture.SubscribeResizeEvents`
      and `UnsubscribeResizeEvents`.
- [ ] Confirm rectangle-specific sampler constraints map to Vulkan's 2D image
      view behavior.
- [ ] Validate no mipmap behavior where rectangle textures should not mipmap.

### `XRTexture3D`

- [ ] Track mipmap property changes and volume dimension changes.
- [ ] Validate 3D image upload row/slice layout against OpenGL.
- [ ] Expand alternate descriptor view support if shader contracts require
      2D/2D-array slices over 3D images.

### `XRTextureCube`

- [ ] Add face attach/detach event parity.
- [ ] Track face mipmap property changes and resized-data state.
- [ ] Validate face ordering and layer indices match OpenGL cubemap face
      behavior.
- [ ] Ensure depth-only and per-face attachment views work for point shadows.

### `XRTextureCubeArray`

- [ ] Track child cube, face, and mipmap property changes.
- [ ] Recreate when cube count, face dimensions, format, or mip count changes.
- [ ] Validate descriptor view compatibility for cube-array, cube, 2D-array,
      and 2D view expectations.

### `XRTextureBuffer`

- [ ] Mirror `GLTextureBuffer.PushData` by ensuring the underlying
      `XRDataBuffer` is uploaded before creating/binding the Vulkan buffer view.
- [ ] Recreate buffer views when source buffer, format, or texel count changes.
- [ ] Validate uniform texel buffer and storage texel buffer descriptors.
- [ ] Add diagnostics when the source buffer lacks required Vulkan usage flags.

### `XRTextureView`

- [ ] Subscribe to viewed texture resize/data/property changes when they affect
      view validity.
- [ ] Match OpenGL compatibility checks for cube-to-2D, cube-array-to-2D,
      cube-array-to-2D-array, and 2D-array-to-2D views.
- [ ] Support depth/stencil view mode parity where Vulkan aspect masks differ
      from OpenGL `DepthStencilTextureMode`.
- [ ] Expand `IVkImageDescriptorSource.GetDescriptorView` alternate view support
      for cube, cube array, and 3D where needed.
- [ ] Add diagnostics for incompatible target, format, mip, and layer ranges.

## Validation

- [ ] Source test: every texture wrapper subscribes/unsubscribes all required
      generic events.
- [ ] Source test: sampler-affecting property changes dirty/recreate Vulkan
      samplers and descriptors.
- [ ] Source test: `XRTextureRectangle` resize recreates Vulkan image resources.
- [ ] Unit/source test: texture view compatibility matrix matches OpenGL.
- [ ] Hardware: compare default pipeline FBO/post textures, point shadows,
      cascaded shadows, cube captures, 2D array captures, UI textures, texture
      buffers, and texture views against OpenGL.

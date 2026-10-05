# Editor Background Preparation

The ImGui editor separates passive drawing from measured cold preparation.
CPU preparation can run on a worker. Renderer resources still publish through
their owning thread and generation.

## Toolbar Icons

The toolbar's 12 SVG icons use one cancellable sequential CPU worker. The
worker reads, parses and rasterizes source images. The render owner publishes
bounded texture/preview work in a separate profiler scope. `DrawToolbar`
performs ready-handle lookup.

Vulkan honors `UploadIfNeeded`. Published-descriptor lookup does not call
`PushData`. Prepared pixels enter the generation-owned
`VulkanTextureUploadService`; the caller receives pending until publication
finishes. A renderer restart reuploads the prepared pixels without repeating
SVG rasterization.

Keep pixel ownership, cancellation, retry and retirement explicit. One icon
per frame is not a time bound if one upload can block. Missing or malformed
inputs must produce a useful failure state.

## Camera Settings Discovery

`CameraComponentEditor` starts creatable-type discovery when the user opens
Create/Replace. Passive settings drawing does not run synchronous discovery.
The serialized worker publishes generation-owned results. Unloading-context
filters, bounded retries and owner-thread retirement protect the picker caches.

The game loader strongly owns a loaded assembly context until explicit unload.
Garbage collection must not hide current script types while that context remains
loaded. Explicit unload clears discovery entries. Reload updates an open picker
without confusing an old live instance with the current creatable type.

The picker preserves notifications, undo/redo, constructor-failure state,
discovery Retry, search results, enum labels and tooltips. General warmed
metadata caching was deferred because the measured path was below its entry
threshold. This contract does not imply that such a cache was added.

## Validation References

The [toolbar investigation](../../work/investigations/rendering/2026-09-21-s10-toolbar-icon-preparation.md)
records Vulkan/OpenGL output, upload and restart checks. The
[camera discovery investigation](../../work/investigations/rendering/2026-09-22-s11-camera-inspector-discovery.md)
records picker, mutation, reload and unload checks. The
[result record](../../work/progress/rendering/vulkan-stall-remediation-results.md)
keeps measurements and remaining validation limits separate from these contracts.

# Texture Streaming

This document describes the runtime texture streaming system for imported 2D textures. It covers the service split, the cooked-cache authority, the residency rules, and the Vulkan upload and publication contract.

Related:

- [Vulkan Resource Lifetime And Retirement](vulkan-resource-lifetime-and-retirement.md)
- [Vulkan Renderer](vulkan-renderer.md)
- [Cooked Texture Payloads](../assets/cooked-texture-payloads.md)
- [Texture Runtime, Streaming, And Virtual Texturing Design](../../work/design/texturing/texture-runtime-streaming-virtual-texturing-design.md) (future sparse page residency and virtual texturing)
- [Texture Validation](../../work/testing/texturing/texture-validation.md)

## Scope

The system streams whole mip levels. It is not virtual texturing. Cooked payloads are mip-addressable, not page-addressable. OpenGL partial sparse page residency exists in code but policy keeps it disabled. Vulkan uses dense residency only; Vulkan sparse images are not implemented.

## Service Split

The renderer-neutral kernel is in `XREngine.Runtime.Rendering/Objects/Textures/2D/` and `XREngine.Runtime.Rendering/Runtime/`. Backends plug in through interfaces. The kernel never references a concrete graphics API.
| Type | Responsibility |
|---|---|
| `ImportedTextureStreamingManager` | Frame-level coordinator. Collects usage, runs policy, admits transitions, applies per-frame limits, and publishes telemetry. |
| `TextureStreamingRegistry`, `TextureStreamingRegistryRecords` | Weak per-texture records, usage samples, material binding observations, snapshots, and periodic compaction. |
| `TextureResidencyPolicy` | Desired resident size, priority, role multipliers, fairness, cooldowns, pressure fitting, and promotion fade. |
| `TextureTransitionQueue` | Pending transitions. Replaces superseded requests, cancels stale work, repairs stuck entries, and tracks lifecycle state. |
| `TextureUploadScheduler` | Upload priority queue. Coalesces duplicates, cancels by generation, applies budget gates, and records upload telemetry. |
| `TextureResidencyState` | Mutable residency fields on `XRTexture2D`. Mutation uses `SetField(...)`. |
| `TextureStreamingPublicationAuthority` | Holds the record monitor across one backend publication so a cancel or supersede cannot pass a stale precheck. |
| `ITextureStreamingBackendProvider`, `ITextureResidencyBackend` | Backend contracts for residency and synchronized upload. |
| `TextureStreamingBackendRegistry` | Static, AOT-safe registry. Each renderer leaf module registers its provider in its module entry point. |
| `TextureRuntimeDiagnostics` | Shared log and timing surface for cache reads, uploads, transitions, and rejections. |

Backend implementations:
| Backend | Type | Location |
|---|---|---|
| OpenGL dense (tiered) | `GLTieredTextureResidencyBackend` | `XREngine.Runtime.Rendering.OpenGL/.../Textures/OpenGLTextureResidencyBackends.cs` |
| OpenGL sparse whole-mip | `GLSparseTextureResidencyBackend` | same file |
| Vulkan dense | `VulkanDenseTextureResidencyBackend` | `XREngine.Runtime.Rendering.Vulkan/.../BackendObjects/Textures/` |
| Vulkan provider | `VulkanTextureStreamingBackendProvider` | same folder |
| Vulkan uploads | `VulkanTextureUploadService` (partial class) | `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/` |

## Cooked-Cache Authority

`TextureStreamingSourceFactory` selects the source of resident data for a texture path:

- `ResolveTextureStreamingAuthorityPathInternal` maps a source path to its authority path. When the authority is a cooked texture asset, the factory creates `AssetTextureStreamingSource` with the original source as fallback. Otherwise it creates `ThirdPartyTextureStreamingSource`, which decodes the original file.
- `DeferredAuthorityTextureStreamingSource` resolves the authority only on the first residency request. Placeholder registration does not read or cook the cache.
- `AssetTextureStreamingSource` leases the cache bytes through a `CookedPayloadOwner` and copies out only the selected resident mips. It reads the `XRTS` payload (magic `0x58525453`) through `XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes`.
- The source falls back to the original file when the cache asset is missing or when its binary payload is incompatible with the current format. The fallback is logged; it is not silent.
- `TextureStreamingResidentDataReuseCache` keeps deep copies of recently decoded resident chains for a short time, so a canceled and requeued transition does not decode again.

The `XRTS` payload stores a preview mip and per-mip offsets. Streamability checks read only the header and manifest. They do not hydrate mip blobs. See [Cooked Texture Payloads](../assets/cooked-texture-payloads.md) for payload layout, cache-key rules, compression plans, and known limits.

## Residency Rules

- Each texture starts at its preview residency (`XRTexture2D.GetPreviewResidentSize`). Promotion raises the resident size to the policy target; demotion lowers it.
- Promotion candidates are ranked by `TextureUploadPriorityClass`: `VisibleNow`, `NearVisible`, `Background`, then `Demotion`.
- Per-frame limits in `ImportedTextureStreamingManager` cap resident transitions, promotions, sparse finalizations, and promotion bytes. Import scopes use a lower promotion byte limit.
- Cooldowns prevent churn: a short cooldown after promotion, a longer cooldown before demotion, a pin window that keeps newly promoted visible textures resident, and an increasing cooldown after a failed promotion.
- Textures bound to visible materials keep a minimum resident size even when projected coverage is small.
- Budget pressure fits the resident set to the texture budget. Pressure demotions log bytes reclaimed and the reason.
- A dense upload after sparse residency clears the sparse state first (`XRTexture2D.ApplyResidentData`), and OpenGL recreates storage when it leaves sparse storage. `Texture.SparseStateClearedForDenseUpload` marks this boundary in logs.
- OpenGL sparse residency applies only to eligible textures (page-aligned `Rgba8`). Promotions use a shared context with fence-gated exposure and storage-generation checks. Committed-byte telemetry is an estimate.

## Vulkan Upload And Publication Contract

Vulkan imported textures never use one-shot layout transitions or copies for live streaming. `VulkanTextureUploadService` owns the whole path.

1. **Admission.** The dense backend submits a `VulkanImportedTextureUploadRequest`. The service rejects stale generations before it allocates Vulkan resources. Each texture tracks resident, upload, published, and retirement generations (`VulkanTextureUploadGenerationLedger`).
2. **Preparation.** Preparation is worker-only. A worker creates the destination image, memory, view, optional sampler, and staging resources in resumable steps (`VulkanImportedTextureUploadPreparationStep`). Device and allocator access from workers is serialized by `VulkanResourceRuntime.TextureUploadContextSync`. Workers never touch frame command buffers, descriptors, materials, or scene objects.
3. **Staging.** Staging comes from `VulkanStagingManager` leases. Background chunks are bounded separately from a protected foreground reserve. A burst defers work; it does not allocate unbounded staging.
4. **Transfer.** Prepared chunks are recorded into native batches and submitted with fence or timeline completion. The copy path records `Undefined` to `TransferDstOptimal`, the buffer-to-image copies for the resident mips, then `TransferDstOptimal` to `ShaderReadOnlyOptimal`. Imported uploads stay on the graphics queue; the transfer-queue switch only logs a compatibility message until an explicit semaphore release and acquire chain exists.
5. **Completion.** The render owner polls completion without blocking. `VulkanTextureUploadCompletionBudget` reserves whole signaled batches, so no child of a batch publishes or retires ahead of its siblings.
6. **Publication.** Publication occurs on the render thread only after the GPU receipt completes and the generation is still current. It swaps the descriptor-visible image, view, and sampler, advances the descriptor generation, and dirties only dependent descriptor or command state with a recorded reason.
7. **Retirement.** Old images, views, samplers, memory, and staging enter the frame-slot retirement queues. They are freed only after every frame that could sample them completes. A failed or canceled demotion keeps the previously published image.

Foreground readiness: when an accepted frame references an exact texture generation, the upload becomes foreground-required. The service drains `VisibleNow` preparation without background caps, but completion still follows the normal fence or timeline path. It never forces a device idle.

`VulkanResidentRehydrationUploadScheduler` sends retained resident mips through the same service.

`VulkanTextureUploadFaultInjection` is a development-only diagnostic. When an editor MCP tool arms it, it fails or cancels a bounded number of upcoming upload schedules through the normal rejection and cancel paths. It is inert when not armed.

## Flags And Environment Variables

The constants are in `XREngineEnvironmentVariables`. Each flag has an editor preference mirror.
| Variable | Effect |
|---|---|
| `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1` | Emergency kill switch. Clamps Vulkan imported textures to preview residency. Telemetry reports `vulkanFrozen` and `freezeReason`. Off by default. |
| `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1` | Opt-in experimental per-mip render-thread upload for Vulkan. Off by default. Not routed through the synchronized upload service. |
| `XRE_VULKAN_TEXTURE_UPLOAD_PREP_BUDGET_MS` | Render-thread time budget, in milliseconds, for upload preparation work. |
| `XRE_VULKAN_TEXTURE_UPLOAD_TRACE=1` | Verbose upload lifecycle logs. |
| `XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE` | Requests a dedicated transfer queue. Currently logs a `[Vulkan Compat]` message; uploads stay on the graphics queue. |
| `XRE_VULKAN_ASYNC_TEXTURE_UPLOAD`, `XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER` | Disabling either is ignored for imported uploads and logs a `[Vulkan Compat]` message. Preparation stays worker-only. |

## Diagnostics

- Logs: `log_textures.*` with `Texture.*` events such as `Texture.CacheRead`, `Texture.CacheWrite`, `Texture.UploadSlow`, `Texture.UploadValidationFailed`, `Texture.TransitionCanceled`, `Texture.DelayedByShadow`, and `Texture.BindingRisk`.
- Telemetry: `ImportedTextureStreamingTelemetry` and `ImportedTextureStreamingTextureTelemetry`. Vulkan rows add resident, published, upload, and retirement generations.
- Editor: the ImGui texture streaming panel (`EditorImGuiUI.TextureStreamingPanel`).
- MCP: `get_texture_streaming_summary` and `list_texture_streaming_textures`.

## Known Limits

- Partial sparse page residency is disabled by policy on OpenGL. Vulkan sparse images are not implemented.
- Cooked payloads are not page-addressable. GPU-native compressed payloads and compressed uploads are not implemented.
- Vulkan per-mip progressive exposure is opt-in and uses the render-thread path, not the synchronized service.
- Vulkan imported uploads do not use a dedicated transfer queue.
- Color-space and texture-role metadata are incomplete.

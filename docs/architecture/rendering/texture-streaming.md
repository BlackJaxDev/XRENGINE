# Texture Streaming

This document describes the runtime texture streaming system for imported 2D textures. It covers the service split, the cooked-cache authority, the residency rules, and the Vulkan upload and publication contract.

Related:

- [Vulkan Resource Lifetime And Retirement](vulkan-resource-lifetime-and-retirement.md)
- [Vulkan Renderer](vulkan-renderer.md)
- [Cooked Texture Payloads](../assets/cooked-texture-payloads.md)

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
| `TextureStreamingCapacity`, `TextureStreamingCapacityModel` | Shared decode gate whose width follows the CPU capacity, and the per-frame work lanes. See [Capacity Model](#capacity-model). |
| `TextureStreamingWorkLane`, `TextureStreamingWorkController` | Per-frame upload and publication limits from measured cost and frame time. |

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
- Per-frame limits in `ImportedTextureStreamingManager` cap resident transitions, promotions, sparse finalizations, and promotion bytes. Import scopes use a lower promotion byte limit. These limits admit transitions; the decode width and the upload limits of the [capacity model](#capacity-model) set how fast the admitted transitions complete.
- Cooldowns prevent churn: a short cooldown after promotion, a longer cooldown before demotion, a pin window that keeps newly promoted visible textures resident, and an increasing cooldown after a failed promotion.
- Textures bound to visible materials keep a minimum resident size even when projected coverage is small.
- Budget pressure fits the resident set to the texture budget. Pressure demotions log bytes reclaimed and the reason.
- `IsUnderBudgetPressure` reports the pressure of the last evaluation: the budget fit gave at least one texture less than its desired size, the streamed textures used more than the available budget so promotions stopped, or the Vulkan allocator budget stopped promotions. The per-frame transition limits do not count. `BudgetPressureFrameId` gives the frame of the evaluation that wrote it; a value that does not advance means that no evaluation ran, so the flag is stale. Any thread can read both, and they allocate nothing. The cutscene residency session reads them through `TextureResidencyBackend` and then releases the request holds outside its window, at most one time for each new evaluation.
- A dense upload after sparse residency clears the sparse state first (`XRTexture2D.ApplyResidentData`), and OpenGL recreates storage when it leaves sparse storage. `Texture.SparseStateClearedForDenseUpload` marks this boundary in logs.
- OpenGL sparse residency applies only to eligible textures (page-aligned `Rgba8`). Promotions use a shared context with fence-gated exposure and storage-generation checks. Committed-byte telemetry is an estimate.

## Explicit Residency Requests

A caller can request a mip of a streamed texture before the texture is visible. The public seam is `ITextureResidencyRequests` (namespace `XREngine.Rendering.Residency`). `TextureResidencyRequests.Shared` returns the shared instance. `ImportedTextureStreamingManager` stays internal and implements the interface explicitly. Any thread can call each member.

- `RequestResidency` returns an owned `TextureResidencyHandle`, or null when streaming does not track the texture. Dispose the handle to cancel the request; `TextureResidencyHandle.State` gives the request state. A handle that is never disposed keeps the mip resident until the process ends or the texture is destroyed. Streaming does not keep the handle alive and the handle has no finalizer.
- Each evaluation drops the request of a texture that was collected, destroyed, or queued for destruction. The state of a dropped request is `None`, and one `Texture` log line records the drop.
- `AcquireBudgetProfile(profile)` returns a lease. The seam counts the leases; the last dispose selects `Default` again. `Default` itself takes no lease. The interface has no setter, so callers that do not know each other cannot release the profile of another caller.
- The interface also exposes `CurrentFrameId` (the streaming collect frame, not the engine frame), `IsUnderBudgetPressure`, `BudgetPressureFrameId`, `AreStreamedTexturesImporting`, and `TryGetSourceMaxDimension`.

The internal manager members below implement the seam. The cutscene residency planner uses this seam through `TextureResidencyBackend` (see [Cutscene System](../animation/cutscene-system.md#residency)).

- `ImportedTextureStreamingManager.RequestResidency(texture, targetMip, deadlineFrame, priority, generation)` returns a `TextureResidencyTicket`. Mip 0 is the full source size; each step halves it. A large mip asks for the preview only. `CancelResidency(ticket)` ends the request. `GetResidencyRequestState(ticket)` returns `Pending`, `Resident`, or `None`. The event `ResidencyRequestCompleted` occurs one time for each request, after the state becomes `Resident`. A cancel can occur between the state change and the event, so a handler must ignore a ticket that it already cancelled. The `generation` argument appears in the rejection and cancel logs; the manager does not compare it.
- The texture must be tracked by streaming. A request registers a native imported texture the same way a first material use does. Otherwise the manager rejects the request, logs a warning, and returns `TextureResidencyTicket.None`.
- A request is a demand signal separate from visibility. Each evaluation writes the largest requested size, the highest priority, and the earliest deadline to the record. The policy target becomes the larger of the visibility target and the requested size. A request does not change `LastVisibleFrameId`. A promotion is request-driven only when the texture is not visible or recently bound in that frame and the request raised the assigned size above the visibility target. A request-driven promotion does not set the visible pin (`PinUntilFrameId`), and it does not use the mip fade: the new mips show in the first frame after the completion. The completion checks visibility again under the record lock: a texture that became visible or bound while the promotion was in flight (in the frame of the completion or the frame before it) gets the fade and the pin. A visible texture that a request raises keeps its pin and its fade.
- Requests use the normal transition path. The per-frame limits, the texture quality cap, the failed-promotion cooldown, the Vulkan preview freeze, and the VRAM budget fit apply to them. The publish rule does not change: publication occurs on the render thread only, after the GPU receipt and a generation check.
- Order: a requested texture that is not visible ranks after visible and recently bound textures, and before other textures. Among requests, the earlier deadline goes first, then the higher priority. A request raises the job priority of its promotion to its own priority, and to `Highest` at or after its deadline frame.
- While model imports are active, the policy queues no promotion of a texture that is not visible. A request for such a texture waits until the imports end. The manager writes one `Texture` log line for each import period when this occurs.
- Cancel: if the request raised a request-driven promotion that is still pending, and no other request needs that size, the cancel clears the pending transition and cancels its token. The generation is no longer current, so the result never publishes. A cancel can also land after the evaluation took its snapshot and before it queues the promotion. Admission (`TextureTransitionQueue.TryBeginTransition`) checks the live request fields under the record lock and rejects a request-driven promotion that no live request needs, so that case also queues nothing. The cancel of a resident request only releases the hold; the texture returns to visibility-driven residency, and the normal demotion rules apply.
- A request is `Resident` when the preview is ready and the resident size covers the requested size after the texture quality cap. If a budget demotion removes the mip, the state goes back to `Pending`; the event does not occur again. A budget that is too small keeps the request `Pending`.

## Budget Profiles

A budget profile lease (`ITextureResidencyRequests.AcquireBudgetProfile`) selects the per-frame limits (`TextureStreamingBudgetLimits`). The internal `ImportedTextureStreamingManager.SetBudgetProfile` does not count holders; only the lease code and tests call it. The limits fade from one profile to the other over `TextureStreamingBudgetProfileFadeFrames` evaluations (default 30; 0 changes them in one frame). The profile is global to the manager. The seam counts the leases of all holders, so the profile goes back to `Default` only when the last lease is disposed. Each cutscene `TextureResidencyBackend` holds one lease until it is disposed.

| Limit | `Default` | `Cutscene` setting (provisional default) |
|---|---|---|
| Transitions in one frame | 2 | `TextureStreamingCutsceneMaxTransitionsPerFrame` (6) |
| Promotions in one frame | 1 | `TextureStreamingCutsceneMaxPromotionsPerFrame` (4) |
| Promotion bytes in one frame | 32 MB | `TextureStreamingCutsceneMaxPromotionMegabytesPerFrame` (96) |
| Promotion cooldown | 6 frames | `TextureStreamingCutscenePromotionCooldownFrames` (2) |
| Mip fade after a promotion | 90 frames | `TextureStreamingCutscenePromotionFadeFrames` (12) |

The settings are on `RuntimeEngine.Rendering.EngineSettings`. The `Cutscene` values stay provisional. Cold jumps in Sponza (37 textures, 1024 and 2048 texels) gave almost the same refine time with each profile on OpenGL and on Vulkan, because these admission limits are not the bottleneck: the decode width and the upload and publication limits of the [capacity model](#capacity-model) set the refine time. No measurement shows that other values are better. Profile a cutscene before you change them. The short mip fade prevents a texture that loaded for a cutscene jump from looking late. Neither profile changes the VRAM budget fit, the import-era promotion limit, the demotion cooldown, or the visible pin.

The `Cutscene` profile also changes the capacity model: while it is active and at least one residency request is not resident (for example, at a cut hold), the upload lanes can use all of the measured idle frame headroom instead of half of it. The frame-time rule stays the same. With vsync, the measured render-thread time includes the present wait, so the headroom is 0 and this share has no effect.

## Capacity Model

Texture streaming throughput follows the measured capacity of the machine, not fixed limits. `TextureStreamingCapacity` owns the shared decode gate and the per-frame work lanes. `TextureStreamingCapacityModel` and `TextureStreamingWorkController` are pure and deterministic; unit tests drive them with fake clocks and fake costs (`TextureStreamingCapacityModelTests`, `TextureStreamingWorkControllerTests`).

### Decode Concurrency

The OpenGL tiered, OpenGL sparse, and Vulkan dense backends share one `PriorityAsyncSemaphore` decode gate. Only one backend streams at a time.

Base width: `clamp(processors - foreground - dedicated, 2, 8)`, from `TextureStreamingCapacityModel.ResolveBaseDecodeWidth`.

- `processors` is the number of logical processors in the live process affinity. The desktop platform installs `RuntimeProcessorServices.EffectiveProcessorCountReader` (Windows affinity mask). Without a reader, the value is `Environment.ProcessorCount`, which .NET reads one time at startup.
- `foreground` and `dedicated` are the foreground and dedicated background threads that `EngineExecutionTopology` reserves (render, update, fixed update, collect/swap; Vulkan command-chain, pipeline, and OpenXR lanes, job lanes). The execution settings and environment variables therefore control the decode base too. Without a topology, `foreground` is 4 and `dedicated` is 0.
- The lower bound 2 is the old fixed width. The upper bound 8 limits transient memory: one decode of a 2048-texel RGBA source holds the decoded image and its mip chain (about 16 MB and 22 MB), so 8 decodes hold about 300 MB.

| Processors (foreground, dedicated) | 4 (3, 2) | 6 (4, 2) | 8 (4, 2) | 12 (4, 2) | 16 (4, 2) | 16 (4, 12, desktop Vulkan and OpenXR lanes) |
|---|---|---|---|---|---|---|
| Base width | 2 | 2 | 2 | 6 | 8 | 2 |

`TextureStreamingDecodeWidthController` then gives the width frame-time feedback. The streaming evaluation gives it one sample for each render frame (frame time, permits in use, waiting decodes):

- Rise: while decodes wait and all permits are in use, the width grows by half (at least 1), but only after 8 frames with decodes at the current width (and at least 50 ms), and only when those frames do not go over the rule more often than frames without decodes. The bound is `clamp(processors - foreground, 2, 8)` (`ResolveMaxDecodeWidth`): decodes never take the processors of the foreground threads. On 4 logical processors the bound is 2, the old fixed width.
- Back-off: an over-budget frame with active decodes halves the width and holds it 500 ms, but only on evidence: after at least 8 frames with and 8 frames without decodes, frames with decodes go over the rule more often by 0.15. The width that gave the evidence, less 1, is a ceiling for 5 s.
- Floor: 2 without evidence, 1 for 5 s after evidence.
- A scene discontinuity restarts the statistics and relearns the normal frame time for 250 ms. A scrub that signals a discontinuity in each frame keeps the width controller in a relearn, so the width does not back off during the scrub.
- The budget uses the same bound as the work lanes (see below): at most the frame interval plus the tolerance.

The base width refreshes at most one time each second. A larger width grants waiting decodes at once. A smaller width takes effect as held permits are released. The Vulkan upload preparation workers use the same width, not more than the 8 background staging buffers, because each worker holds one buffer until its chunk completes.

### Upload And Publication Limits

Two work lanes limit render-thread streaming work in each render frame:

| Lane | Items | Measured cost | Start value |
|---|---|---|---|
| `OpenGL.ResidentApply` | Resident data applies (tiered) and sparse transitions on the OpenGL render thread | Render-thread CPU time of each apply without GC pauses, and GPU timestamps around each apply (`glQueryCounter` through the backend timestamp capability, read without a wait from a ring of 8 pairs) | 1 (the old fixed limit) |
| `Vulkan.Completion` | Transfer chunks that complete, retire, and publish | Render-thread CPU time of the batch completion without GC pauses, and the GPU timestamp of the transfer batch for each chunk. A PresentNow foreground barrier is not a lane sample. | 4 (the old fixed limit) |

A batch is indivisible: the first batch of a frame always completes, also when it has more chunks than the limit. A Vulkan preparation drain hands all completed worker results to the transfer queue (at most one for each worker), and it goes on to other jobs while a worker runs; the transfer drain completes all signaled batches that the completion lane admits in one tick.

Each lane finds the frame boundary itself (`TextureStreamingFrameSampler`, from `LastRenderTimestampTicks`) and reads the frame time from the render timestamps and the render-thread busy time from the frame lifecycle totals. The `TextureStreamingWorkController` then applies these rules. Smoothing uses time constants (`alpha = 1 - exp(-dt / tau)`), so machines with different frame rates behave the same in time.

| Rule | Value | Derivation |
|---|---|---|
| Frame-time rule | frame time <= budget + tolerance | Design target: no frame with streaming work more than 2 ms above the frame budget. `TextureStreamingFrameToleranceMilliseconds` (default 2). |
| Frame budget | max(target, min(idle mean + 2.5 x idle mean deviation, interval + tolerance)) | Learned only from frames without lane work (idle frames, tau 330 ms). A frame with work never raises it. 2.5 mean deviations are about 2 standard deviations, so scene jitter alone rarely breaks the rule. |
| Frame interval | max(target period, idle mean) | The target period of the engine (the focused or unfocused target FPS). When the scene is slower than the target, or when there is no target (VSync or no limit), the idle mean is the present interval. |
| Idle frame | counts as at most interval + tolerance | One stall frame moves the mean only a little. Two idle frames in a row above that limit are a scene step: the mean becomes the lower of the two. |
| Step down | mean becomes the higher of 2 frames | 2 frames in a row (with or without work) whose time without the measured work is below the mean by more than the jitter allowance plus 2 x tolerance, for example when a slow period of another process ends. It can only lower the mean. |
| Probe | 2 frames without work; the mean becomes the lowest of them | At the first frames, after a scene discontinuity (a cutscene cut or jump calls `ITextureResidencyRequests.NotifySceneDiscontinuity`), and after a frame with one item that still goes over the rule. At most one probe starts in each second; a discontinuity inside that second waits for its end, so a scrub that signals each frame stops streaming for at most 2 frames each second. A probe ends after 4 frames when it has no 2 known frames. A discontinuity also restarts the mean GPU cost. |
| Effective work | max(CPU work, items x mean GPU cost) | GPU timestamps make driver and copy cost visible. |
| Headroom | budget - idle busy time | Only when the idle render-thread time is below 90 % of the budget; otherwise the frame is full or the busy time includes a present wait, and the headroom is 0. With vsync, the busy time includes the present wait, so the headroom is 0 and the `Cutscene` share has no effect. |
| Allowed cost | tolerance + share x headroom | Share 0.5 normally (half of the idle time stays free for heavier gameplay frames), 1.0 while a cutscene waits for residency. |
| Cost per item | third-highest of the last 16 item costs | Samples come from the CPU time without GC pauses, from GPU timestamps, and from each back-off frame: max(effective work, frame - normal frame) / items, so cost that no timer sees still counts. The robust value ignores up to two hitches. |
| Target | allowed cost / cost per item, at most 16 | 16 is a hard bound for a wrong measurement. |
| Back-off | limit x 0.5, hold 250 ms | Every frame with lane work that goes over the rule, whether or not a timer saw the cost. |
| Floor | 1 item per frame | The old fixed limit. After a second back-off within 1 s, the floor is tolerance / cost (at least 1/32) for 1 s, so an item that is too large for one frame runs only as often as the tolerance pays for it on average. |
| Ceiling | N - 1 items for 2 s | After a second back-off within 1 s, with N items. |
| Rise | at most +25 % in one frame, never above the target | Only while work was deferred and after the hold time. |
| Fall without overrun | to the target | Only when the target is more than 15 % below the limit, which is wider than the usual cost jitter. |

A steady cost and a steady frame time give a constant limit. Cost that no timer sees cannot raise the budget, because only idle frames teach it, and the budget is never more than the frame interval plus the tolerance. Such cost shows as frames over the rule, which back off. A fixed cost of each work frame that is larger than the tolerance breaks the rule in each work frame, so the work frames become fewer. The lanes allocate nothing in a frame. The OpenGL apply lane reads its completed GPU timestamps one time before each lane frame closes.

### Settings

| Setting | Default | Effect |
|---|---|---|
| `TextureStreamingDecodeConcurrency` | null (automatic) | Pins the decode width, 1 to 32, and turns off its feedback. |
| `TextureStreamingUploadsPerFrame` | null (adaptive) | Pins the per-frame limit of each lane, 1 to 64. The OpenGL lane counts whole applies; the Vulkan lane counts transfer chunks. Tests and users can pin the old fixed values. |
| `TextureStreamingMaxUploadsPerFrame` | null (built-in bound 16) | Caps the adaptive limit, 1 to 16. |
| `TextureStreamingFrameToleranceMilliseconds` | 2 | The frame-time rule tolerance. |

### Diagnostics

The profiler summary (`RenderWorkBudgetCoordinator.AppendProfilerSummary`) writes `TextureStreamingDecode:` (width, base, permits in use, waiting decodes, back-offs, processors, cutscene wait, OpenGL GPU pairs read) and one line for each lane: limit, rate, target, cost per item, GPU cost per item, learned budget, headroom, ceiling, overruns, back-offs, discontinuities, probes, items, and frames with deferred work. `PriorityAsyncSemaphore.ExcessReleaseCount` counts a release without a held permit.

### Measurements

Release builds, Sponza cold jump to one shot (37 textures), RTX 3090, SATA SSD, 16-core X3D CPU, 60 Hz frame target, no audio. The 16-processor rows run on 16 logical processors (affinity after startup). The 4-processor rows set the affinity at launch, so `Environment.ProcessorCount`, the execution topology, and the thread pool see 4 processors (`XRE_ALLOW_CPU_OVERSUBSCRIPTION=1` is needed, because the default topology with 12 dedicated lanes does not start on 4 processors). "Before" is the base commit before the capacity model. "Over" counts frames from the jump to the last T2 texture that were more than 2 ms longer than the settled frame time of the shot. Each row has three runs.

Main configuration: Vulkan with the `AdvancedRenderPipeline`.

| Processors | Before T1 / T2 | After T1 / T2 | Before frames: mean, p95, max, over | After frames: mean, p95, max, over |
|---|---|---|---|---|
| 16 | 2.88-3.88 s / 3.77-5.02 s | 2.05-2.29 s / 2.72-2.95 s | 16.7 ms, 18.2-18.6 ms, 24-43 ms, 10-15 (4-5 %) | 16.7 ms, 19.0-19.8 ms, 26-31 ms, 10-11 (6-7 %) |
| 4 | 3.97-4.07 s / 5.12-5.20 s | 2.93-3.12 s / 3.62-3.82 s | 16.7 ms, 18.1-18.7 ms, 27-35 ms, 7-17 (2-6 %) | 16.7 ms, 18.6-19.3 ms, 26-37 ms, 8-26 (4-11 %) |

Secondary (measured at an earlier commit of the same change, before the decode bound, which does not change 16 processors): OpenGL with the `DefaultRenderPipeline`, 16 processors: before T1 2.05-2.10 s, T2 2.47-2.58 s, 29-32 frames over (19-20 %); after T1 0.73-0.79 s, T2 0.87-0.90 s, 19-21 frames over (39-43 %), max 37-40 ms. On OpenGL the uploads have driver cost that neither the CPU timer nor the GPU timestamps see in full, so more applies in a frame give more over-budget frames.

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

- An OpenGL resident apply has cost that the CPU timer does not see (driver work at the next present, about 5 to 9 ms for a 1024-texel chain). The apply lane cannot split an apply, so a frame with one large apply can still break the frame-time rule.
- A Vulkan imported upload stages one chunk for each mip level (or each 4 MB of rows), and the chunks of one texture complete in sequence. A 1024-texel chain needs 11 round trips through preparation, transfer, and completion, so the Vulkan refine is bound by this latency more than by the lane limits.
- The decode width feedback measures frame-time contention, not the disk (an HDD can prefer fewer parallel reads). Use `TextureStreamingDecodeConcurrency` to pin it.
- The decode width controller reads the gate one time in each frame, so a decode that starts and ends between two frames does not count as active, and a scrub that signals a discontinuity in each frame keeps it in a relearn without back-offs.
- An unsignaled scene step to a heavier scene makes every work frame go over the rule until a probe or an idle frame measures the new scene, so the lane backs off for a short time. A camera cut must call `NotifySceneDiscontinuity` (the cutscene player does).
- On a scene with jitter larger than the tolerance, frames with work go over the rule a little more often than frames without work, and each such frame backs off. This lowers throughput on jittery machines, but it keeps the frame-time rule.

- Partial sparse page residency is disabled by policy on OpenGL. Vulkan sparse images are not implemented.
- Cooked payloads are not page-addressable. GPU-native compressed payloads and compressed uploads are not implemented.
- Vulkan per-mip progressive exposure is opt-in and uses the render-thread path, not the synchronized service.
- Vulkan imported uploads do not use a dedicated transfer queue.
- Color-space and texture-role metadata are incomplete.

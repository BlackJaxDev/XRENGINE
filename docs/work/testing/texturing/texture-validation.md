# Texture Validation

Architecture: [Texture Streaming](../../../architecture/rendering/texture-streaming.md), [Cooked Texture Payloads](../../../architecture/assets/cooked-texture-payloads.md), [Vulkan Resource Lifetime And Retirement](../../../architecture/rendering/vulkan-resource-lifetime-and-retirement.md)  Code todos: [Texture Runtime, Streaming, And Virtual Texturing TODO](../../todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md), [Texture Compression And Cooked Texture Cache TODO](../../todo/texturing/texture-compression-and-cooked-cache-todo.md)

Evidence notes:

- [Texture Management Runtime Baseline - 2026-05-01](texture-management-runtime-baseline-2026-05-01.md)
- [Texture Streaming Run Analysis - 2026-05-01 18:06](../../investigations/texturing/texture-streaming-run-analysis-2026-05-01-180642.md)

## Setup

Use the ImGui editor unless a check names another host.

Tasks from `.vscode/tasks.json`:

- `Build-Editor`, `Build-Editor-Fast`, and `Build-Editor-Release` build the editor.
- `Generate-UnitTestingWorldSettings` updates Unit Testing World settings.
- `Cook-CommonAssets-Archive (Manual Slow)` cooks the common asset archive after an editor build.
- `Report-NewAllocations` runs the allocation scan for hot paths.

Launch profiles from `.vscode/launch.json`:

- `Editor (Unit Testing World)` starts the Unit Testing World with `XRE_WORLD_MODE=UnitTesting`.
- `Editor (Unit Testing World, Validation Layers)` starts the Unit Testing World with `XRE_VULKAN_VALIDATION=1` and `XRE_GL_DEBUG=1`.
- `Editor (Renderer Development)` starts the renderer development profile.

Useful commands:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
dotnet build .\XREngine.Runtime.Rendering.OpenGL\XREngine.Runtime.Rendering.OpenGL.csproj --no-restore
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "GLTexture2DContractTests|ImportedTextureStreamingContractTests|ImportedTextureStreamingPhaseTests|RuntimeRenderingHostServicesTests|XRTextureVulkanParityContractTests" --no-restore
```

Settings and environment variables:

- Set `XRE_WORLD_MODE=UnitTesting` for Unit Testing World scene runs.
- Set `XRE_FORCE_MESH_SUBMISSION_STRATEGY=CpuDirect` when a Vulkan texture smoke run must avoid other submission changes.
- Set `XRE_PROFILER_ENABLED=1` when the check needs FPS-drop and render-stall logs.
- Set `XRE_GL_DEBUG=1` when an OpenGL upload check needs debug output.
- Set `XRE_VULKAN_VALIDATION=1` when a Vulkan run needs validation layers.
- Set `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1` to hold Vulkan imported textures at preview residency.
- Set `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1` to test the experimental per-mip Vulkan render-thread upload path.
- Set `XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE=1` to request the transfer queue compatibility path.
- Set `XRE_VULKAN_TEXTURE_UPLOAD_PREP_BUDGET_MS=<float>` to change the upload preparation budget.
- Set `XRE_VULKAN_TEXTURE_UPLOAD_TRACE=1` to enable verbose Vulkan imported-texture upload logs.
- `XRE_VULKAN_ASYNC_TEXTURE_UPLOAD=0` and `XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER=0` are legacy toggles. Imported texture uploads ignore them and log a compatibility message.

Use `log_textures.*`, `log_opengl.*`, `log_vulkan.*`, `log_rendering.*`, `log_general.*`, profiler FPS-drop logs, profiler render-stall logs, `get_texture_streaming_summary`, `list_texture_streaming_textures`, `capture_render_pipeline_texture`, and the ImGui texture streaming panel.

## Checks

### Runtime mip streaming

Architecture: [Texture Streaming](../../../architecture/rendering/texture-streaming.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Runtime and editor builds | Run the build commands from Setup. | Runtime, OpenGL, Vulkan, and editor projects build. | Open | none |
| Texture contract tests | Run the targeted `dotnet test` command from Setup. | Texture contract tests pass or unrelated failures are filed. | Open | none |
| Cold-cache Sponza startup | Start `Editor (Unit Testing World)` with a cold texture cache. Record session root, branch, commit, GPU, driver, CPU, memory, raw source decode count, cache miss count, cache write count, first visible preview time, all visible previews resident time, pending visible transition drain time, upload validation failure count, and final promoted and preview counts. | First import writes streamable cache data. Visible textures get previews quickly. The run does not stabilize with many visible textures and few ready previews. | Open | none |
| Warm-cache Sponza startup | Start `Editor (Unit Testing World)` with the same scene and a warm cache. Record cache hits, slow cache reads, worst `cacheReadMs`, worst `cacheParseMs`, promotion queue wait, upload timings, preview times, drain time, CPU prep events by phase, cancellations, and final promoted and preview counts. | Warm-cache streaming uses `AssetTextureStreamingSource`. It avoids `MagickImage` source decode except for missing, stale, unreadable, or non-streamable fallback paths. | Open | none |
| Texture logs | Run a Debug file-logging editor session. Inspect the session logs. | `log_textures.txt` is present. The schema is stable or a breaking change is documented. | Open | none |
| OpenGL upload validity | Run cold-cache and warm-cache Sponza on OpenGL. Inspect `log_opengl.*` and `log_textures.*`. | No texture upload emits `GL_INVALID_VALUE`. `Texture.UploadValidationFailed` stays at zero in normal scene runs. | Open | 2026-05-01 |
| Promotion after demotion | Reproduce a texture demotion and promotion. Inspect `log_textures.*`. | The texture does not expose black or invalid mips. `Texture.SparseStateClearedForDenseUpload` appears only for valid sparse-to-dense handoffs. | Open | 2026-05-01 |
| Stale generation cancellation | Force texture resize or recreate while a residency transition is pending. | Stale work cancels before publication. Logs name the cancellation point. | Open | none |
| Upload budget | Set a low texture upload budget and move the camera near large textures. | No upload work item exceeds the budget by more than one permitted chunk. Large promotions advance over multiple frames with no black mips or invalid sampling. | Open | none |
| Pending upload telemetry | Open the texture streaming panel during active streaming. | Pending upload count, bytes, and oldest wait are visible. | Open | none |
| Render-thread slice removal | Inspect profiler logs during active streaming. | Progressive texture jobs no longer take 30-100 ms render-thread slices. | Open | none |
| Policy stability | Keep the camera stable during a scene run. | Duplicate `ApplyResidentData` calls disappear. Newly promoted visible textures do not demote during cooldown. Quality is monotonic. | Open | none |
| Pressure demotion | Run with a small texture budget. | Pressure demotions log bytes reclaimed and reasons. Visible maps keep a preview floor unless explicit pressure requires a 1 px target. | Open | none |
| Cancellation reuse | Reproduce cancellation-heavy bump maps. | Compatible superseded transitions reuse resident data and do not repeatedly decode or prepare identical data. | Open | 2026-05-01 |
| Binding-risk diagnosis | Reproduce a black surface with no upload validation failure. | `Texture.BindingRisk` entries explain the binding path, or the failure is filed against material, shader, lighting, or non-streaming binding code. | Open | 2026-05-01 |

### Cooked texture payloads and compression

Architecture: [Cooked Texture Payloads](../../../architecture/assets/cooked-texture-payloads.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Baseline imports | Import a third-party PNG and a normal-map sample before compressed payload work. Record generated `.asset` path, cooked cache path, `XRTS` cache logs, preview behavior, material preview, and warm-cache load timing. | Existing v1 `XRTS` behavior is documented before a format change. | Open | none |
| Metadata-only asset round trip | Load and save a generated texture asset that points to a cooked payload. | Metadata survives the round trip. The payload remains the byte authority. | Open | none |
| Missing or stale payload fallback | Remove or stale the cooked payload for a generated texture in the editor. | The editor falls back to source import with diagnostics. Runtime fails unless a fallback is configured. | Open | none |
| `XRTS` descriptor validity | Use fixture textures with NPOT dimensions and a final 1x1 mip. | Mip descriptors have valid dimensions, offsets, lengths, row metadata, and checksums. | Open | none |
| Corrupt payload rejection | Test bad magic, unsupported version, invalid offset, invalid length, truncated payload, and unsupported storage format. | The reader rejects the payload with one primary diagnostic. | Open | none |
| Cache key freshness | Change role, color space, compression profile, backend profile, encoder id, mip policy, normal convention, and alpha mode. | Each relevant setting changes the variant key. Unchanged settings keep a stable key. | Open | none |
| BC cook output validation | Cook fixture albedo, normal, mask, and HDR textures when the encoder path exists. | Output has correct block dimensions, mip count, data length, selected format, and source alpha behavior. | Open | none |
| OpenGL dense compressed upload | Validate BC7 color, BC5 normal, BC4 scalar mask, and BC6H HDR textures when samples exist. | Textures render in the editor preview. No upload validation failure, `GL_INVALID_ENUM`, or `GL_INVALID_VALUE` appears. | Open | none |
| Material sampling comparison | Render compressed and uncompressed validation materials. Capture screenshots or rendered comparisons. | Role-specific sampling is correct. Normal Z reconstruction and sRGB/linear handling match the material contract. | Open | none |
| Quality mip metrics | Cook fixture mips for color, alpha, normals, masks, and HDR textures. | Metrics report color, normal, scalar, and alpha error where supported. | Open | none |
| Vulkan compressed texture support | Run a BC7 color texture on Vulkan after compressed upload lands. | Vulkan uses a supported compressed `VkFormat`, rejects unsupported formats before queue submission, and logs selected format and upload bytes. | Open | none |
| Tiered compressed residency | Run a compressed dense texture through tiered residency. | Budget telemetry separates logical decoded bytes, cooked stored bytes, upload bytes, and committed GPU bytes. Existing `Rgba8` sparse streaming does not regress. | Open | none |
| Editor smoke after compression | Import PNG albedo, PNG normal, and EXR/HDR if available. Warm-cache reload. Preview the generated `.asset`. Render a material that uses compressed textures. | Import, cache reload, preview, and rendering all work with clear diagnostics. | Open | none |

### Vulkan dense texture streaming

Architecture: [Texture Streaming](../../../architecture/rendering/texture-streaming.md#vulkan-upload-and-publication-contract)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Vulkan hitch scenario | Run a reproducible Vulkan texture-streaming hitch scenario in Unit Testing World Sponza. Capture profiler logs during camera motion that triggers promotions and demotions. | Frame-time, upload preparation, upload command recording, descriptor publication, and command-buffer dirty or rerecord p50/p95/p99 are recorded. | Open | none |
| Promotion and demotion cause | Compare profiler markers during Vulkan texture promotion and stream-out. | Frame drops identify promotion, demotion, or another subsystem. | Open | none |
| Large visible promotion | Move the camera close to a large visible texture. | No noticeable frame drop occurs in the Vulkan editor. | Open | none |
| Texture demotion | Move away from high-detail textures under budget pressure. | Stream-out and demotion cause no noticeable frame drop. | Open | none |
| Render-thread work per transition | Inspect Vulkan upload telemetry. | Render-thread work includes only queue polling, publication checks, descriptor swap, command-buffer dirty marking, and retirement enqueue. | Open | none |
| Upload preparation budget | Inspect Vulkan upload timing during steady movement and active bursts. | Render-thread upload preparation p95 is below 0.5 ms during steady movement and below 1.0 ms during bursts. | Open | none |
| Upload transfer telemetry | Run with `XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE` enabled and disabled. | Transfer work appears in telemetry. The compatibility log is explicit. No upload failure is hidden. | Open | none |
| Synchronized publication correctness | Run Vulkan Unit Testing World Sponza with streaming enabled. | No `ErrorDeviceLost`, upload VUID, stale descriptor sample, or black texture frame appears. Descriptor publication is generation-gated and frame-safe. | Open | none |
| OpenGL parity | Repeat the same texture policy scenario on OpenGL. | Shared policy behavior is unchanged. | Open | none |
| Device shutdown with queued uploads | Shut down the Vulkan device while uploads are queued. | Teardown is clean. There is no leak or device loss. | Open | none |
| Reload during upload | Reload or import a texture while uploads are queued. | Stale work cancels. The new generation publishes. | Open | none |
| Preview freeze | Run with `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1`. | `previewReady` equals `tracked`, `promoted=0`, no device-loss logs appear, and Sponza albedo and final output are visible and nonblack. | Open | none |
| Low VRAM budget | Run with a small texture budget and fast camera motion. | Queued demotions appear. Old generations retire after frame completion. Pending demotions cancel when textures become visible. Sampling remains valid. | Open | none |
| Startup cache states | Run cold-cache and warm-cache Vulkan startup. | Preparation, transfer, completion, publication, and retirement cost stays bounded. | Open | none |
| Camera sweep soak | Run 120 automated close and far camera moves. | Stale generations cancel at every lifecycle state. No invalid sampling occurs. | Open | none |
| Active panel | Open the texture streaming panel while Vulkan promotions are active. | The panel causes no hitch or invalid state. | Open | none |
| Render target captures | Capture `AlbedoOpacity`, `Normal`, `RMSE`, `AmbientOcclusionTexture`, `LightingAccumTexture`, `HDRSceneTex`, and final post-process output after promotion. | The outputs are textured and nonblack. | Open | none |
| Progressive per-mip path | After per-mip Vulkan progressive upload lands, run with `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1` for close stationary view, rapid motion, cancellation during movement, and budget pressure. | Each mip becomes visible only after its upload completes. | Open | none |
| Validation-layer-clean run | Start `Editor (Unit Testing World, Validation Layers)` and run a streaming scenario. | Vulkan validation logs are clean for dense texture streaming. | Open | none |

### Sparse residency and virtual texturing

Architecture: [Texture Streaming](../../../architecture/rendering/texture-streaming.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| OpenGL partial sparse gate | Enable the future partial sparse path only after code work lands. Validate material UV transforms, wrapping, oblique anisotropic surfaces, high-speed motion, stereo divergence, and near-full requests. | Partial sparse residency is disabled by default. It enables only behind an explicit renderer setting after cross-vendor validation. | Open | none |
| Page-aligned math | Run tests for page-aligned region math, sparse-texture2 edge regions, mip-tail behavior, near-full coverage, repeat, mirror, clamp, out-of-range UVs, and generated-UV fallback. | Page selections are valid and never sample uncommitted regions. | Open | none |
| Large virtual textures | Validate with 16k and larger virtual textures, camera sweeps, high-speed motion, and teleports after SVT lands. | Coarse fallback remains valid. Cache requests stay bounded. | Open | none |
| SVT filtering | Validate repeat, mirror, clamp borders, bilinear and trilinear seams, compressed block alignment, and oblique anisotropic surfaces. | Sampling has no visible seams and respects the wrap mode. | Open | none |
| Cache exhaustion | Force physical cache exhaustion and retire old page-table versions. | Page-table mappings revoke before slot reuse. Old versions do not reference retired slots. | Open | none |
| VR multi-view | Validate stereo divergence, page request union, and foveated priority before SVT is enabled for VR content. | Both eyes share physical pages. The finest important request wins. | Open | none |
| Vulkan sparse hardware | On supported hardware, run validation-layer-clean Vulkan sparse image residency. | Bind, copy, publish, unbind, and memory reuse are ordered and device-loss-safe. | Open | none |
| Bindless deferred texturing | Validate non-stereo opaque deferred first. Then validate MSAA, stereo, transparent, and forward-only follow-ups. | Material records never reference invalid dense, sparse, or virtual data. | Open | none |
| Neural texture compression | Compare material captures and channel metrics after neural cook or decode lands. | The conventional fallback and any shader decode path match the accepted quality targets. | Open | none |
| Runtime virtual textures | Validate terrain-object blending, large decal cases, dirty regions, and fallback when page generation misses a frame. | RVT pages reuse SVT cache rules and never stall the frame. | Open | none |

### Diagnostics, profiling, and allocation

Architecture: [Texture Streaming](../../../architecture/rendering/texture-streaming.md#diagnostics)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Shadow contention | Run Sponza startup while shadow atlas updates are active. | Shadow atlas bursts no longer coincide with unbounded texture upload waits. `Texture.DelayedByShadow` identifies frames where shadow work consumes the shared budget first. | Open | 2026-05-01 |
| Sparse finalization wait | Inspect `log_general.*` and `log_textures.*` during startup. | `TextureStreaming.FinalizeSparseTransitions` does not wait for 15-17 second spans. | Open | 2026-05-01 |
| Slow-frame attribution | Inspect FPS-drop and render-stall logs. | Slow-frame logs identify which subsystem consumed the render-work budget. | Open | none |
| Diagnostics panel scale | Open the texture streaming panel with hundreds of tracked textures. | It identifies top VRAM textures and oldest pending uploads. It remains usable and low-overhead by default. | Open | none |
| Logging allocation safety | Inspect allocation reports for summary and slow texture logging. | Logging introduces no avoidable per-frame hot-path allocations. | Open | none |
| Allocation audit | Run `Report-NewAllocations` and inspect registry snapshots, usage recording, policy scoring, transition queueing, scheduler submit/execute, OpenGL upload chunks, Vulkan preparation/publication, and diagnostics panel open and closed. | New LINQ, captured lambdas, string formatting, boxing, transient lists, and avoidable heap allocations are flagged. | Open | none |
| Hardware profile | Record GPU, driver, CPU, memory, renderer, cache state, and validation switches for each run. | Each validation result can be compared across hardware. | Open | none |

## Hardware Matrix

| Renderer | Hardware | Driver | Scenario | Status | Last evidence |
|---|---|---|---|---|---|
| OpenGL | Windows desktop GPU with sparse texture support | TBD | Cold and warm Sponza, sparse-to-dense handoff, allocation audit | Open | 2026-05-01 |
| OpenGL | Windows desktop GPU without sparse texture support | TBD | Dense fallback and cache authority | Open | none |
| Vulkan | Windows desktop GPU with validation layers | TBD | Dense imported streaming and publication | Open | none |
| Vulkan | Windows desktop GPU without a dedicated transfer queue | TBD | Graphics-queue compatibility path | Open | none |
| Vulkan | Low-memory or constrained budget profile | TBD | Pressure demotion and cancellation | Open | none |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| May 1 runtime baseline | Imported-scene texture promotion stayed delayed. OpenGL emitted invalid upload rectangles. Shadow atlas work and texture uploads contended. | [Texture Management Runtime Baseline - 2026-05-01](texture-management-runtime-baseline-2026-05-01.md) |
| May 1 streaming analysis | Visible previews took about 37 seconds to become ready. The run logged many slow upload and transition cancellation rows. | [Texture Streaming Run Analysis - 2026-05-01 18:06](../../investigations/texturing/texture-streaming-run-analysis-2026-05-01-180642.md) |
| Sparse-to-dense promotion after demotion | Six uploads tried to write `512x512` data to a `256x256` allocated mip. | [Texture Streaming Run Analysis - 2026-05-01 18:06](../../investigations/texturing/texture-streaming-run-analysis-2026-05-01-180642.md) |
| Raw source decode remains in hot path | Preview starvation and repeated source decode can keep visible surfaces low resolution or black during startup. | [Texture Runtime, Streaming, And Virtual Texturing TODO](../../todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md#open-code-items) |
| GPU-native compressed payloads are missing | Cooked payloads stay uncompressed and use more VRAM and upload bandwidth than needed. | [Texture Compression And Cooked Texture Cache TODO](../../todo/texturing/texture-compression-and-cooked-cache-todo.md#open-code-items) |

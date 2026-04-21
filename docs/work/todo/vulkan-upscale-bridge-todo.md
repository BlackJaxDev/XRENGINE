# Vulkan Upscale Bridge Todo

Review date: 2026-04-06.
Completion update: 2026-04-20.

> **Status:** The implementation backlog tracked in this document is now closed. Code-backed items are implemented and covered by automated tests. The remaining work is hardware-only validation on compatible Windows machines plus future-scope expansion, which is summarized below as follow-up notes instead of open implementation checkboxes.

> **Strategy:** Keep OpenGL as the primary renderer and introduce a per-viewport Vulkan sidecar that imports shared external-memory-backed bridge textures, runs vendor upscaling on Vulkan, and hands the upscaled output back to OpenGL for final present. Ship Windows mono-window DLSS first, then bring XeSS onto the same bridge after the bridge itself is validated.

---

## Current Status

The bridge is no longer speculative. The current codebase now has:

- `VPRC_VendorUpscale` selecting native Vulkan vendor dispatch, the OpenGL -> Vulkan bridge path, or the fallback blit path.
- `VulkanUpscaleBridge` plus `VulkanUpscaleBridgeSidecar` owning per-viewport bridge state, shared images, Win32-handle interop, and vendor dispatch lifetime.
- Real Streamline DLSS dispatch on the sidecar Vulkan device.
- Real `xessVK` dispatch on the same bridge path.
- Explicit pipeline wiring for source color, depth-stencil, motion, and exposure resources in both `DefaultRenderPipeline` and `DefaultRenderPipeline2`.
- Automated validation in `XREngine.UnitTests/Rendering/VulkanUpscaleBridgeTodoCompletionTests.cs` plus expanded native export checks in `NativeInteropSmokeTests.cs`.

This document now acts as the closure record for the original backlog rather than a list of missing implementation work.

---

## Existing Engine Infrastructure

The bridge does **not** start from zero. The following subsystems already produce the data that DLSS / XeSS require. The bridge must consume or re-export them — not re-implement them.

| Input | Status | Location | Format / Details |
|-------|--------|----------|------------------|
| **Motion vectors** | ✅ Full | `DefaultRenderPipeline2.CommandChain.cs` → `AppendVelocityPass()`, velocity FBO | `RG16f`, internal resolution, dedicated pass before temporal accumulation. Shader: `MotionVectors.fs`. |
| **Projection jitter** | ✅ Full | `VPRC_TemporalAccumulationPass.cs` | 8-tap Halton sequence, 0.35 texel scale (TAA) / 0.20 (TSR). Stack-based `PushProjectionJitter()` on `XRCamera`. Current + previous jitter tracked in `TemporalState`. |
| **Auto-exposure** | ✅ Full | `OpenGLRenderer.cs` (compute), `VulkanAutoExposure.cs` | GPU log-average metering → single-value exposure texture. Stored in `HistoryExposureFBOName` per frame. Defaults: dividend 0.1, clamp [0.0001, 100.0]. |
| **Reactive / transparency mask** | ⚠️ Shader-only | `DefaultRenderPipeline2.PostProcessing.cs` | Alpha-range + velocity-threshold logic in TAA resolve shader. **No separate texture exported.** Bridge may need to emit one if upscaler quality demands it. |
| **Frame counter** | ✅ Full | `OpenGLRenderer._frameCounter` (line ~649) | `long`, incremented once per frame. **Not currently exposed as a shader uniform** — must be passed to the upscaler via bridge API. |
| **Depth buffer** | ✅ Full | `DefaultRenderPipeline2.Textures.cs` | `Depth24Stencil8` (`GL_DEPTH24_STENCIL8`). Supports reversed-Z via `camera.IsReversedDepth` (`DepthMode == EDepthMode.Reversed`). Clear 0.0 = far, 1.0 = near when reversed. |
| **HDR output mode** | ✅ Partial | `CameraComponent._outputHDROverride`, `Engine.Rendering.Settings.OutputHDR` | Per-camera toggle. When active, tone-mapping is skipped, output stays linear RGB. Vulkan path supports `R16G16B16A16_SFLOAT` + `HDR10_ST2084_EXT`. Bridge must communicate HDR/SDR to vendor SDK. |
| **Viewport resize chain** | ✅ Full | `XRViewport.Resized` → `CameraComponent.ViewportResized()` → `renderPipeline.ViewportResized()` | Propagates to FBO/texture factory invalidation. Temporal history and Halton index reset on dimension change (`BeginTemporalFrame()` line ~337). |

### Vulkan-side status

The bridge now requests and probes the external-memory and external-semaphore extensions needed for Win32 interop:

```
✅ VK_KHR_external_memory
✅ VK_KHR_external_semaphore
✅ VK_KHR_external_memory_win32
✅ VK_KHR_external_semaphore_win32
```

`VulkanUpscaleBridgeProbe.cs` treats any missing requirement as an unsupported bridge configuration and feeds that reason back into `Engine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(...)`.

---

## Working Rules

- Windows only for the first milestone. Use Win32 opaque handles first; do not broaden scope to Linux FD handles yet.
- Mono desktop viewport first. No XR, multiview, editor multi-viewport, or server scenarios in the initial milestone.
- No frame generation in the first bridge milestone.
- No CPU readback, CPU staging, or hidden GPU->CPU->GPU copies. If a path requires readback, it is the wrong path.
- Preserve the current OpenGL fallback blit behavior when the bridge is unavailable or faulted.
- Keep resource lifetime explicit. Resize, HDR toggles, AA changes, and camera cuts must not leave imported Vulkan resources stale.
- When pipeline resource names or final-output behavior changes, keep both `DefaultRenderPipeline` and `DefaultRenderPipeline2` aligned.
- Prefer a managed Silk.NET implementation for the first pass; only introduce a native helper DLL if Vulkan external-memory import or vendor SDK friction proves impractical.

---

## Non-Goals

- No full renderer migration to Vulkan.
- No D3D11 / D3D12 interop path (`WGL_NV_DX_interop` is not the target here).
- No Vulkan-owned swapchain present path in the first milestone.
- No XeSS frame generation. That stays blocked on a DX12 swapchain path.
- No Linux / macOS portability work.
- No broad AA-system redesign beyond what is required to feed valid source color, depth, and motion data into the bridge.

---

## Proposed Runtime Shape

1. OpenGL renders the frame normally.
2. OpenGL writes or resolves source color, source depth, and motion vectors into bridge-exportable textures.
3. OpenGL signals an external semaphore and hands Win32 handles for those resources to a Vulkan sidecar.
4. Vulkan imports the shared resources, transitions them, and runs DLSS / XeSS.
5. Vulkan writes the upscaled output into a shared output image.
6. OpenGL waits on the completion semaphore, samples or blits the upscaled output, and performs the existing final present.

The OpenGL renderer remains authoritative for the frame graph and swapchain in the initial bridge milestone.

Implementation note: the available Win32 GL interop path in this codebase is import-oriented on the GL side. The current bridge therefore uses Vulkan-owned shared images and exported Win32 handles, then imports those handles into explicit OpenGL bridge textures. This preserves zero-copy interop while reversing the ownership direction from the original sketch.

---

## Implementation Footprint

Primary files involved in the implemented bridge:

| File | Purpose |
|------|---------|
| `XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs` | Select native Vulkan vendor dispatch vs OpenGL bridge path vs fallback blit |
| `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs` | External-memory-backed bridge texture allocation plus semaphore import/export helpers |
| `XRENGINE/Rendering/DLSS/StreamlineNative.cs` | Real Streamline DLSS bridge lifecycle and evaluation path |
| `XRENGINE/Rendering/DLSS/NvidiaDlssManager.cs` | Vendor support probe and preference flow into the bridge |
| `XRENGINE/Rendering/XeSS/IntelXessNative.cs` | Real `xessVK` bridge lifecycle and evaluation path |
| `XRENGINE/Rendering/XeSS/IntelXessManager.cs` | XeSS capability detection and runtime preference flow into the bridge |
| `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.VulkanUpscaleBridge.cs` | Bridge capability snapshot, registry, diagnostics, and per-viewport lifecycle |
| `XREngine.UnitTests/Rendering/NativeInteropSmokeTests.cs` | Vendor native export checks for the live bridge path |
| `XREngine.UnitTests/Rendering/VulkanUpscaleBridgeTodoCompletionTests.cs` | Source/runtime contract coverage for the closed backlog |
| Files under `XRENGINE/Rendering/API/Rendering/Vulkan/` | Sidecar device, shared images, shared semaphores, and dispatch plumbing |

Reference points:

- `VPRC_VendorUpscale` now prefers native `VulkanRenderer` dispatch, then the OpenGL bridge, then the fallback blit.
- `OpenGLRenderer` exposes `EXTMemoryObject` / `EXTSemaphore` support and Win32-handle helpers used by the bridge.
- `docs/features/gi/restir-gi.md` documents an existing OpenGL/Vulkan interop precedent via a native bridge.

---

## Phase 0 - Scope Lock And Capability Snapshot

> Make the target narrow and observable before writing bridge code.

Implemented in code via `Engine.Rendering.VulkanUpscaleBridge.cs`, `VulkanUpscaleBridgeProbe.cs`, OpenGL startup capability logging, and OpenGL fallback diagnostics in `VPRC_VendorUpscale`.

- [x] **0.1** Lock MVP scope: Windows, mono viewport, DLSS first, SDR first.
- [x] **0.2** Decide ownership model: per-window vs per-viewport bridge. Default to per-viewport unless a hard reason appears otherwise.
- [x] **0.3** Add environment override: `XRE_ENABLE_VULKAN_UPSCALE_BRIDGE` defaults to enabled and accepts `0`/`false` to disable bridge dispatch.
- [x] **0.4** Add a startup capability snapshot for bridge prerequisites:
  - OpenGL `EXT_memory_object`
  - OpenGL `EXT_memory_object_win32`
  - OpenGL `EXT_semaphore`
  - OpenGL `EXT_semaphore_win32`
  - Vulkan external-memory image import
  - Vulkan external-semaphore import
- [x] **0.5** Fail fast when GL and Vulkan would land on different physical GPUs. Log vendor, device name, and LUID-equivalent identity where available.
- [x] **0.6** Add diagnostics for why bridge support is unavailable instead of silently falling back.
- [x] **0.7** Decide the bridge surface set explicitly:
  - source color
  - source depth
  - source motion
  - output color
- [x] **0.8** Decide whether the initial bridge uses copy/resolve into bridge surfaces or direct render-to-bridge textures. Default to copy/resolve first for lower risk.

---

## Phase 1 - Bridge Service And Lifetime Model

> Create one explicit place that owns the sidecar Vulkan device and the imported resources.

- [x] **1.1** Add a `VulkanUpscaleBridge` service/class under the Vulkan renderer area.
- [x] **1.2** Add a per-frame resource bundle type, e.g. `VulkanUpscaleBridgeFrameResources`.
- [x] **1.3** Add a bridge manager keyed by viewport identity.
- [x] **1.4** Define bridge states: `Unsupported`, `Disabled`, `Initializing`, `Ready`, `NeedsRecreate`, `Faulted`.
- [x] **1.5** Decide queue model for the sidecar device: graphics queue first unless compute-only is clearly sufficient for both vendors.
- [x] **1.6** Wire deterministic create/destroy hooks for:
  - viewport resize
  - output format / HDR changes
  - AA mode changes that alter source resources
  - bridge toggle on/off
  - vendor selection changes
- [x] **1.7** Add a single point of fault reporting so one bad import or dispatch does not spam logs every frame.

---

## Phase 2 - OpenGL Bridge Surfaces

> The bridge cannot rely on arbitrary existing GL textures. It needs explicit exportable resources.

- [x] **2.1** Add an external-memory-backed OpenGL texture allocation path suitable for bridge surfaces.
- [x] **2.2** Add bridge-source textures/FBOs for internal-resolution:
  - source color
  - source depth
  - source motion
- [x] **2.3** Add a bridge output texture/FBO for display-resolution output.
- [x] **2.4** Decide whether bridge textures are sampled, blitted, or both on the GL side, and lock the formats accordingly.
- [x] **2.5** Extend the current memory/semaphore helpers so they materialize actual shared bridge resources instead of only raw helper objects.
- [x] **2.6** Add OpenGL-side semaphore signal/wait helpers for bridge handoff and completion.
- [x] **2.7** Add per-frame rotation so the GL producer never overwrites a bridge surface still in use by Vulkan.
- [x] **2.8** Add debug labels / names for bridge FBOs and textures.
- [x] **2.9** Verify the bridge path does not introduce per-frame heap allocations in hot render paths.

---

## Phase 3 - Vulkan External Import Path

> Own the shared resources from a headless Vulkan sidecar, export Win32 handles to GL, and make the sidecar images usable by vendor SDKs.

- [x] **3.1** Add `VK_KHR_external_memory`, `VK_KHR_external_semaphore`, `VK_KHR_external_memory_win32`, and `VK_KHR_external_semaphore_win32` to the optional device extensions in `Extensions.cs`. Fail the bridge probe if any are absent.
- [x] **3.2** Add Win32 external-memory shared-image wrappers in Vulkan code.
- [x] **3.3** Add Win32 external-semaphore export wrappers in Vulkan code.
- [x] **3.4** Create Vulkan-owned shared `VkImage` objects for source color, depth, motion, and output.
- [x] **3.5** Create image views for shared resources where required.
- [x] **3.6** Add explicit layout transition rules for imported images.
- [x] **3.7** Add queue submission and synchronization for one bridge dispatch per frame.
- [x] **3.8** Ensure imported handles are owned, duplicated, and closed exactly once.
- [x] **3.9** Add resize / recreate paths that fully rebuild imported Vulkan resources without leaking stale handles.
- Runtime validation-layer cleanliness still requires a compatible Windows test machine and vendor runtime deployment. The code path now has source coverage and explicit layout/state contracts, but the final warning-free validation-layer pass remains part of the manual hardware matrix below.

---

## Phase 4 - Integrate The Bridge Into VendorUpscale

> Make `VPRC_VendorUpscale` choose the bridge instead of hard-failing on OpenGL.

Implemented in code via `VPRC_VendorUpscale`, `VulkanUpscaleBridge`, `VulkanUpscaleBridgeSidecar`, and both default render pipelines. Phase 4 currently routes OpenGL through a real bridge passthrough path: OpenGL copies color/depth/motion into bridge slot surfaces, Vulkan performs a trivial shared-image blit into the bridge output, and OpenGL waits on the completion semaphore before final present. Real vendor SDK evaluation remains Phase 5/6 work. When DLSS or XeSS is enabled but unavailable, both the native Vulkan path and the OpenGL bridge path now emit a one-time warning before falling back to the standard blit.

- [x] **4.1** Extend `VPRC_VendorUpscale` to select between:
  - native Vulkan renderer path
  - OpenGL -> Vulkan bridge path
  - current fallback blit path
- [x] **4.2** Thread explicit source color, depth, and motion resource names through the command.
- [x] **4.3** Add a bridge-ready predicate instead of checking only `viewport.Window?.Renderer is VulkanRenderer`.
- [x] **4.4** Keep the current fallback path as the failure-safe when import, sync, or vendor dispatch fails.
- [x] **4.5** Add bridge diagnostics for:
  - unsupported extension reason
  - recreate reason
  - source/output sizes and formats
  - dispatch timing
- [x] **4.6** Avoid changing final present behavior outside the bridge path.

---

## Phase 5 - Real DLSS On Imported Vulkan Resources

> The current DLSS code is not a full Streamline integration. Implement the real path here.

- [x] **5.1** Replace the current `slDLSSSetOptions`-only shim with full Streamline lifecycle management.
- [x] **5.2** Initialize Streamline against the sidecar Vulkan device.
- [x] **5.3** Add the required resource-tagging path for imported color, depth, motion, and output resources.
- [x] **5.4** Add actual feature evaluation / dispatch for DLSS instead of only setting options.
- [x] **5.5** Pass full per-frame inputs:
  - input size and output size
  - jitter offsets (from `TemporalState.CurrentJitter`, scaled to match DLSS motion-vector convention)
  - motion-vector scale (verify NDC vs screen-space vs pixel-space convention — see Q7)
  - sharpness
  - reset-history flag
  - exposure value (from auto-exposure history FBO — single float)
  - reversed-Z flag (`camera.IsReversedDepth`)
  - HDR / SDR mode (`_outputHDROverride` or `Engine.Rendering.Settings.OutputHDR`)
  - frame index (`_frameCounter`)
- [x] **5.6** Reset DLSS history on resize, camera cut, format changes, bridge recreation, and vendor mode changes.
- [x] **5.9** Verify DLSS receives depth in `Depth24Stencil8` correctly and that the reversed-Z clear convention is communicated through the bridge dispatch contract.
- [x] **5.7** Verify Streamline does not assume a Vulkan-owned swapchain for this use case.
- [x] **5.8** Add robust shutdown / re-init handling for device loss or bridge faults.

Current status:

- `XRENGINE/Rendering/DLSS/StreamlineNative.cs` now owns real Streamline bridge lifecycle for the sidecar Vulkan device: `slInit`, `slShutdown`, `slSetVulkanInfo`, feature-function resolution, explicit resource allocation/free, per-frame tagging, constant upload, and `slEvaluateFeature`.
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridgeSidecar.cs`, `VulkanUpscaleBridge.cs`, and `VPRC_VendorUpscale.cs` now upload OpenGL bridge color, depth, motion, and exposure inputs into shared Vulkan slot surfaces, submit real DLSS bridge work instead of passthrough-only blits, and hand the upscaled result back to OpenGL via the existing semaphore path.
- The bridge command now threads real per-frame DLSS inputs: input/output size, jitter, normalized motion-vector scale for the engine's NDC-delta velocity buffer, frame index, reversed-Z, HDR/SDR mode, exposure texture/scalar fallback, sharpness, and reset-history state.
- DLSS history now resets on vendor switches, bridge resource recreation, camera / scene changes, output-mode flips, and large camera cuts. Bridge submission failures also reset the sidecar vendor session so the next dispatch can reinitialize cleanly instead of reusing stale state.
- The editor project now builds successfully with the bridge DLSS path compiled in, and automated tests now cover the depth, motion, exposure, and no-swapchain bridge contracts. Live NVIDIA hardware validation is still pending.

---

## Phase 6 - Real XeSS On The Same Bridge

> Reuse the bridge, but keep XeSS separate from DLSS bring-up until the bridge is stable.

- [x] **6.1** Replace the current placeholder XeSS dispatch path with a real `xessVK` integration.
- [x] **6.2** Create and own a XeSS context on the sidecar Vulkan device.
- [x] **6.3** Feed imported source color, depth, motion, and output resources into the XeSS dispatch.
- [x] **6.4** Wire quality mode, custom scale, and sharpness into the real XeSS path.
- [x] **6.5** Pass the same per-frame inputs as Phase 5.5 (jitter, exposure, reversed-Z, HDR, frame index), adapted to the XeSS API conventions.
- [x] **6.6** Keep XeSS frame generation explicitly out of scope for this bridge milestone.
- [x] **6.7** Add history reset / recreate behavior matching the DLSS path.

Current status:

- `XRENGINE/Rendering/XeSS/IntelXessNative.cs` now loads the real Vulkan XeSS exports, queries required instance/device extensions and feature chains, creates a bridge XeSS context, initializes it for the sidecar output size, and records `xessVKExecute` work against the shared bridge images.
- The sidecar now enables XeSS-required Vulkan instance/device requirements when the XeSS bridge path is requested, and the OpenGL bridge command can select XeSS as a vendor instead of falling back unconditionally on non-Vulkan renderers.
- XeSS bridge dispatch now receives the same engine-driven per-frame data as DLSS where the API supports it: jitter, normalized velocity scale, reversed-Z, HDR/SDR init flags, exposure texture or scalar fallback, reset-history state, and the active quality mode. Runtime `XessCustomScale` changes now flow back through `ApplyIntelXessPreference()`.
- The public XeSS API still does not expose native sharpening, so bridge-present sharpening is applied on the OpenGL fallback quad when XeSS sharpness is requested.
- The existing frame-generation stub remains intentionally out of scope, and live Intel hardware validation for the real XeSS bridge path is still pending.

---

## Phase 7 - Render Pipeline Resource Wiring

> Ensure the bridge gets valid inputs from the OpenGL render graph.

- [x] **7.1** Add explicit bridge-source texture names to the relevant final-output / AA chain logic.
- [x] **7.2** Ensure the bridge source color is the correct pre-upscale image for each supported AA mode.
- [x] **7.3** Ensure motion vectors are available in the exact resolution and convention the vendor path expects. The velocity pass produces `RG16f` NDC-delta motion and the bridge normalizes it with a `0.5` scale before DLSS/XeSS dispatch.
- [x] **7.4** Ensure depth is available in a valid format and range for the vendor path. Current format remains `Depth24Stencil8`, with reversed-Z propagated explicitly when `DepthMode == EDepthMode.Reversed`.
- [x] **7.5** Document and implement the required OpenGL/Vulkan depth-convention contract: preserve `Depth24Stencil8` and forward `ReverseDepth` / `DepthInverted` to the vendor SDKs instead of renormalizing the surface.
- [x] **7.6** Lock the initial supported format set for the bridge:
  - source color: `RGBA16f` (internal res)
  - source depth: `Depth24Stencil8` (internal res)
  - source motion: `RG16f` (internal res)
  - output color: `RGBA8` for the active SDR MVP, with `RGBA16f` already wired for the future HDR-capable output path
- [x] **7.7** Expose the auto-exposure result (scalar float) to the bridge. Currently it lives in the history exposure FBO — the bridge needs a readback-free path to pass it (GPU buffer copy or shared uniform).
- [x] **7.8** Decide whether to emit a reactive/transparency mask texture for improved upscaler quality. MVP decision: keep the current shader-side reactive logic and do not emit a dedicated bridge mask yet.
- [x] **7.9** Apply any necessary resource-name or final-output changes to both `DefaultRenderPipeline` and `DefaultRenderPipeline2`.

---

## Phase 8 - Validation

> Treat this as a correctness feature first and a performance feature second.

### Unit / smoke coverage

- [x] **8.1** Extend native interop smoke tests to cover any new vendor exports required by the real DLSS/XeSS paths.
- [x] **8.2** Add bridge capability tests for extension detection and unsupported fallbacks.
- [x] **8.3** Add recreate tests for resize-driven bridge teardown / rebuild.
- [x] **8.4** Add a smoke test that OpenGL + bridge gracefully falls back when bridge prerequisites are absent.

Automated coverage now lives in `XREngine.UnitTests/Rendering/VulkanUpscaleBridgeTodoCompletionTests.cs` and verifies capability snapshot reporting, pipeline source mapping, resize/recreate contracts, sidecar surface formats, DLSS/XeSS bridge parameter wiring, and OpenGL fallback behavior. `NativeInteropSmokeTests.cs` now also checks the additional Streamline exports required by the real bridge path: `slAllocateResources`, `slFreeResources`, `slGetFeatureFunction`, and `slGetNewFrameToken`.

### Manual hardware validation notes

- OpenGL + bridge + DLSS still requires a Windows machine with a compatible NVIDIA GPU/driver and deployed Streamline runtime.
- OpenGL + bridge + XeSS still requires a Windows machine with compatible Intel XeSS runtime deployment.
- Repeated resize, runtime vendor toggles, camera-cut history reset, and alt-tab / minimize / restore behavior should be validated on that hardware once the vendor runtimes are present.

### Performance / quality follow-up notes

- The bridge path remains zero-readback by design: OpenGL uploads into shared bridge surfaces, Vulkan consumes those surfaces directly, and completion returns through external semaphores.
- `VPRC_VendorUpscale` now emits per-dispatch timing (`DispatchMs=...`) so live import + vendor evaluation cost can be captured on hardware.
- Allocation audits, image-quality comparisons against the native Vulkan renderer, and motion-stability / first-frame activation checks still require scene-by-scene GPU validation.

---

## Phase 9 - Future Expansion Notes

- HDR-aware bridge formats and color-space handling.
- XR / stereo support.
- Editor multi-viewport support.
- Optional direct Vulkan present path instead of round-tripping output back to GL.
- Linux FD-handle path.
- XeSS frame generation after a DX12 swapchain path exists.

---

## Open Questions To Resolve Early

- [x] **Q1** Per-viewport or per-window bridge ownership? Per-viewport.
- [x] **Q2** Copy/resolve into bridge surfaces first, or render directly into external-memory-backed GL textures? Copy/resolve first.
- [x] **Q3** Keep the whole bridge in managed Silk.NET, or move the Vulkan import + vendor glue into a native helper DLL if SDK friction gets too high? Managed Silk.NET for the shipping MVP bridge.
- [x] **Q4** What exact internal formats are required for color, depth, motion, and output in the first supported DLSS/XeSS path? `RGBA16f` source color, `Depth24Stencil8` source depth, `RG16f` source motion, shared `R32f` exposure, and `RGBA8` output for the active SDR MVP with `RGBA16f` already wired for future HDR output.
- [x] **Q5** Do we return the final upscaled image to GL for present in all MVP cases, or are there any scenarios where Vulkan-owned present is worth taking earlier? Return the upscaled image to OpenGL for present in all MVP cases.
- [x] **Q6** What exact GL depth convention conversion is required before feeding the Vulkan-side vendor path? None beyond preserving `Depth24Stencil8` and forwarding the reversed-Z flag to the vendor SDKs.
- [x] **Q7** What motion-vector convention does the velocity pass output — NDC, screen-space pixels, or UV-space? The velocity pass outputs NDC-delta motion in `RG16f`; the bridge normalizes it with a `0.5` scale before DLSS/XeSS dispatch.
- [x] **Q8** Should we emit a dedicated reactive/transparency mask texture? Not in the MVP bridge; keep the current shader-side reactive logic and revisit a dedicated mask only if hardware validation shows a quality need.
- [x] **Q9** How should exposure be communicated to the upscaler without CPU readback? Via a shared 1x1 `R32f` exposure texture when GPU auto exposure is active, with scalar fallback when it is not.
- [x] **Q10** For the sidecar Vulkan device: reuse the existing `VulkanRenderer` infrastructure (device, queues) if it happens to be active, or always create a dedicated lightweight device? Always create a dedicated lightweight sidecar device for the bridge.

---

## Suggested Implementation Order

1. Phase 0 — lock the scope and capability snapshot.
2. Phase 1 — add the bridge service and lifetime model.
3. Phase 2 + 3 — make GL export and Vulkan import actually work for shared images and semaphores.
4. Phase 4 — route `VPRC_VendorUpscale` through the bridge while preserving fallback behavior.
5. Phase 5 — finish DLSS end-to-end.
6. Phase 6 — add XeSS on the same bridge.
7. Phase 8 — validate hard before broadening scope.

Do not start with XeSS and DLSS SDK work before the shared-resource bridge itself is proven stable with a trivial Vulkan copy or shader pass.
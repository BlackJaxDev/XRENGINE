# Vulkan Upscale Bridge Todo

Review date: 2026-04-06.

> **Strategy:** Keep OpenGL as the primary renderer and introduce a per-viewport Vulkan sidecar that imports shared external-memory-backed bridge textures, runs vendor upscaling on Vulkan, and hands the upscaled output back to OpenGL for final present. Ship Windows mono-window DLSS first, then bring XeSS onto the same bridge after the bridge itself is validated.

---

## Why This Exists

The current code already contains several pieces of the problem, but not the actual bridge:

- `VPRC_VendorUpscale` only runs DLSS / XeSS when the active renderer is Vulkan and otherwise falls back to a passthrough blit.
- The OpenGL renderer already probes `EXT_memory_object`, `EXT_semaphore`, and Win32 handle variants, and exposes raw memory/semaphore handle helpers.
- There is no Vulkan-side external-memory import path for those OpenGL resources yet.
- The DLSS integration is only a partial Streamline binding today.
- The XeSS integration is still placeholder code that always falls back.
- There is repository precedent for solving OpenGL/Vulkan interop through a dedicated bridge layer: `RestirGI.Native.dll`.

This todo tracks the work required to make `vendorupscale` actually bridge an OpenGL-produced frame into Vulkan for DLSS / XeSS without switching the whole renderer to Vulkan.

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

### Vulkan-side gaps

The sidecar Vulkan device **does not** currently request the extensions needed for external-memory import:

```
❌ VK_KHR_external_memory          — not in Extensions.cs
❌ VK_KHR_external_semaphore       — not in Extensions.cs
❌ VK_KHR_external_memory_win32    — not in Extensions.cs
❌ VK_KHR_external_semaphore_win32 — not in Extensions.cs
```

These must be added to the optional device extensions array in `XRENGINE/Rendering/API/Rendering/Vulkan/Extensions.cs` before any import code can run.

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

## File Targets

Likely files to touch during implementation:

| File | Purpose |
|------|---------|
| `XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs` | Select Vulkan-native path vs OpenGL bridge path vs fallback blit |
| `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs` | External-memory-backed bridge texture allocation, semaphore export/import helpers |
| `XRENGINE/Rendering/DLSS/StreamlineNative.cs` | Replace partial Streamline shim with real DLSS evaluation path |
| `XRENGINE/Rendering/DLSS/NvidiaDlssManager.cs` | Separate vendor support probe from renderer-mode restrictions where needed |
| `XRENGINE/Rendering/XeSS/IntelXessNative.cs` | Replace placeholder XeSS path with real `xessVK` dispatch |
| `XRENGINE/Rendering/XeSS/IntelXessManager.cs` | Separate capability detection from actual bridge availability |
| `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.State.cs` | Bridge capability flags / diagnostics |
| `XREngine.UnitTests/Rendering/NativeInteropSmokeTests.cs` | Vendor native export checks |
| New files under `XRENGINE/Rendering/API/Rendering/Vulkan/` | Sidecar device, imported image, imported semaphore, and dispatch plumbing |

Reference points:

- `VPRC_VendorUpscale` currently hard-requires `VulkanRenderer`.
- `OpenGLRenderer` already exposes `EXTMemoryObject` / `EXTSemaphore` support and Win32-handle helpers.
- `docs/features/gi/restir-gi.md` documents an existing OpenGL/Vulkan interop precedent via a native bridge.

---

## Phase 0 - Scope Lock And Capability Snapshot

> Make the target narrow and observable before writing bridge code.

Implemented in code via `Engine.Rendering.VulkanUpscaleBridge.cs`, `VulkanUpscaleBridgeProbe.cs`, OpenGL startup capability logging, and OpenGL fallback diagnostics in `VPRC_VendorUpscale`.

- [x] **0.1** Lock MVP scope: Windows, mono viewport, DLSS first, SDR first.
- [x] **0.2** Decide ownership model: per-window vs per-viewport bridge. Default to per-viewport unless a hard reason appears otherwise.
- [x] **0.3** Add experimental toggle: `XRE_ENABLE_VULKAN_UPSCALE_BRIDGE=1` and/or equivalent engine setting.
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
- [ ] **3.10** Run with Vulkan validation enabled and keep the import path warning-free.

---

## Phase 4 - Integrate The Bridge Into VendorUpscale

> Make `VPRC_VendorUpscale` choose the bridge instead of hard-failing on OpenGL.

Implemented in code via `VPRC_VendorUpscale`, `VulkanUpscaleBridge`, `VulkanUpscaleBridgeSidecar`, and both default render pipelines. Phase 4 currently routes OpenGL through a real bridge passthrough path: OpenGL copies color/depth/motion into bridge slot surfaces, Vulkan performs a trivial shared-image blit into the bridge output, and OpenGL waits on the completion semaphore before final present. Real vendor SDK evaluation remains Phase 5/6 work.

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
- [ ] **5.5** Pass full per-frame inputs:
  - input size and output size
  - jitter offsets (from `TemporalState.CurrentJitter`, scaled to match DLSS motion-vector convention)
  - motion-vector scale (verify NDC vs screen-space vs pixel-space convention — see Q7)
  - sharpness
  - reset-history flag
  - exposure value (from auto-exposure history FBO — single float)
  - reversed-Z flag (`camera.IsReversedDepth`)
  - HDR / SDR mode (`_outputHDROverride` or `Engine.Rendering.Settings.OutputHDR`)
  - frame index (`_frameCounter`)
- [ ] **5.6** Reset DLSS history on resize, camera cut, format changes, bridge recreation, and vendor mode changes.
- [ ] **5.9** Verify DLSS receives depth in Depth24Stencil8 correctly and that the reversed-Z clear convention (0.0 = far) is communicated.
- [ ] **5.7** Verify Streamline does not assume a Vulkan-owned swapchain for this use case.
- [ ] **5.8** Add robust shutdown / re-init handling for device loss or bridge faults.

Current status:

- `XRENGINE/Rendering/DLSS/StreamlineNative.cs` now owns real Streamline bridge lifecycle for the sidecar Vulkan device: `slInit`, `slShutdown`, `slSetVulkanInfo`, feature-function resolution, explicit resource allocation/free, per-frame tagging, constant upload, and `slEvaluateFeature`.
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridgeSidecar.cs`, `VulkanUpscaleBridge.cs`, and `VPRC_VendorUpscale.cs` now upload OpenGL bridge inputs into shared Vulkan slot surfaces, submit real DLSS bridge work instead of passthrough-only blits, and hand the upscaled result back to OpenGL via the existing semaphore path.
- Temporal invalidation on resize / AA changes already clears shared history state, and the bridge command now also forces a reset when the selected bridge vendor changes; the remaining 5.6 work is narrower runtime validation around camera cuts, format changes, and bridge recreation edge cases.
- Current bridge DLSS assumptions are still the MVP SDR path: the sidecar passes LDR / non-HDR options and now opts into Streamline auto-exposure with neutral pre-/exposure scale defaults; the dedicated engine-driven exposure bridge resource work remains open under 5.5 and Phase 7.
- The editor project now builds successfully with the bridge DLSS path compiled in, but runtime validation on actual vendor hardware is still pending.

---

## Phase 6 - Real XeSS On The Same Bridge

> Reuse the bridge, but keep XeSS separate from DLSS bring-up until the bridge is stable.

- [x] **6.1** Replace the current placeholder XeSS dispatch path with a real `xessVK` integration.
- [x] **6.2** Create and own a XeSS context on the sidecar Vulkan device.
- [x] **6.3** Feed imported source color, depth, motion, and output resources into the XeSS dispatch.
- [ ] **6.4** Wire quality mode, custom scale, and sharpness into the real XeSS path.
- [ ] **6.5** Pass the same per-frame inputs as Phase 5.5 (jitter, exposure, reversed-Z, HDR, frame index), adapted to the XeSS API conventions.
- [x] **6.6** Keep XeSS frame generation explicitly out of scope for this bridge milestone.
- [x] **6.7** Add history reset / recreate behavior matching the DLSS path.

Current status:

- `XRENGINE/Rendering/XeSS/IntelXessNative.cs` now loads the real Vulkan XeSS exports, queries required instance/device extensions and feature chains, creates a bridge XeSS context, initializes it for the sidecar output size, and records `xessVKExecute` work against the shared bridge images.
- The sidecar now enables XeSS-required Vulkan instance/device requirements when the XeSS bridge path is requested, and the OpenGL bridge command can select XeSS as a vendor instead of falling back unconditionally on non-Vulkan renderers.
- The bridge command now applies the same history-reset policy to XeSS vendor switches that it applies to DLSS, while resize / AA invalidation still flows through the shared temporal reset path.
- The current XeSS bridge path also stays in the MVP SDR configuration and now explicitly enables XeSS auto-exposure without a dedicated engine exposure texture; the existing frame-generation stub remains intentionally out of scope.

---

## Phase 7 - Render Pipeline Resource Wiring

> Ensure the bridge gets valid inputs from the OpenGL render graph.

- [ ] **7.1** Add explicit bridge-source texture names to the relevant final-output / AA chain logic.
- [ ] **7.2** Ensure the bridge source color is the correct pre-upscale image for each supported AA mode.
- [ ] **7.3** Ensure motion vectors are available in the exact resolution and convention the vendor path expects. Current format is `RG16f` from the velocity pass — verify the scale/sign convention matches what DLSS/XeSS expect (see Q7).
- [ ] **7.4** Ensure depth is available in a valid format and range for the vendor path. Current format: `Depth24Stencil8`, reversed-Z when `DepthMode == EDepthMode.Reversed`.
- [ ] **7.5** Document and implement any OpenGL/Vulkan depth-convention normalization required by the bridge.
- [ ] **7.6** Lock the initial supported format set for the bridge:
  - source color: `RGBA16f` (internal res)
  - source depth: `Depth24Stencil8` (internal res)
  - source motion: `RG16f` (internal res)
  - output color: `RGBA16f` or `RGBA8` (display res) — decide in Phase 0
- [ ] **7.7** Expose the auto-exposure result (scalar float) to the bridge. Currently it lives in the history exposure FBO — the bridge needs a readback-free path to pass it (GPU buffer copy or shared uniform).
- [ ] **7.8** Decide whether to emit a reactive/transparency mask texture for improved upscaler quality. The engine currently computes reactive logic shader-side in the TAA resolve — a texture-based mask is optional but recommended for DLSS quality.
- [ ] **7.9** Apply any necessary resource-name or final-output changes to both `DefaultRenderPipeline` and `DefaultRenderPipeline2`.

---

## Phase 8 - Validation

> Treat this as a correctness feature first and a performance feature second.

### Unit / smoke coverage

- [x] **8.1** Extend native interop smoke tests to cover any new vendor exports required by the real DLSS/XeSS paths.
- [ ] **8.2** Add bridge capability tests for extension detection and unsupported fallbacks.
- [ ] **8.3** Add recreate tests for resize-driven bridge teardown / rebuild.
- [ ] **8.4** Add a smoke test that OpenGL + bridge gracefully falls back when bridge prerequisites are absent.

### Manual validation

- [ ] **8.5** Validate OpenGL + bridge + DLSS on a mono editor viewport.
- [ ] **8.6** Validate OpenGL + bridge + XeSS on a mono editor viewport after DLSS is stable.
- [ ] **8.7** Resize the editor window repeatedly and verify no black frames, stale output, or crashes.
- [ ] **8.8** Toggle vendor upscaling on/off at runtime.
- [ ] **8.9** Validate camera cuts and scene loads reset temporal history correctly.
- [ ] **8.10** Alt-tab / minimize / restore and verify bridge recovery.

### Performance / quality validation

- [ ] **8.11** Confirm there are no CPU readbacks in the bridge path.
- [ ] **8.12** Confirm no per-frame bridge allocations show up in hot paths.
- [ ] **8.13** Compare output quality against the native Vulkan renderer path on the same scene.
- [ ] **8.14** Capture dispatch timing for bridge import + vendor evaluation.
- [ ] **8.15** Validate motion stability, resize behavior, and first-frame activation quality.

---

## Phase 9 - Post-MVP Follow-Ups

- [ ] **9.1** HDR-aware bridge formats and color-space handling.
- [ ] **9.2** XR / stereo support.
- [ ] **9.3** Editor multi-viewport support.
- [ ] **9.4** Optional direct Vulkan present path instead of round-tripping output back to GL.
- [ ] **9.5** Linux FD-handle path.
- [ ] **9.6** XeSS frame generation after a DX12 swapchain path exists.

---

## Open Questions To Resolve Early

- [ ] **Q1** Per-viewport or per-window bridge ownership?
- [ ] **Q2** Copy/resolve into bridge surfaces first, or render directly into external-memory-backed GL textures?
- [ ] **Q3** Keep the whole bridge in managed Silk.NET, or move the Vulkan import + vendor glue into a native helper DLL if SDK friction gets too high?
- [ ] **Q4** What exact internal formats are required for color, depth, motion, and output in the first supported DLSS/XeSS path?
- [ ] **Q5** Do we return the final upscaled image to GL for present in all MVP cases, or are there any scenarios where Vulkan-owned present is worth taking earlier?
- [ ] **Q6** What exact GL depth convention conversion is required before feeding the Vulkan-side vendor path?
- [ ] **Q7** What motion-vector convention does the velocity pass output — NDC, screen-space pixels, or UV-space? DLSS expects pixel-space jittered motion vectors; XeSS expects NDC by default. Verify and add a conversion step if needed.
- [ ] **Q8** Should we emit a dedicated reactive/transparency mask texture? The engine currently does reactive logic in the TAA resolve shader (alpha-range + velocity-threshold). Emitting a mask texture improves DLSS/XeSS quality for transparencies but adds a pass.
- [ ] **Q9** How should exposure be communicated to the upscaler without CPU readback? Options: (a) shared GPU buffer copy from auto-exposure output, (b) Vulkan-imported texture that the compute exposure writes into directly, (c) keep a one-frame-lagged CPU scalar from the previous frame's readback (least desirable).
- [ ] **Q10** For the sidecar Vulkan device: reuse the existing `VulkanRenderer` infrastructure (device, queues) if it happens to be active, or always create a dedicated lightweight device? Reusing avoids duplicate driver state but couples the bridge to the full Vulkan renderer lifecycle.

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
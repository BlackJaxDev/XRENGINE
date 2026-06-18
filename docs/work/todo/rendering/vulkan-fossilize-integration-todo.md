# Vulkan Fossilize Integration TODO

Last Updated: 2026-06-18
Owner: Rendering
Status: Planning
Target Branch: create in Phase 0

Design sources:

- [ValveSoftware/Fossilize](https://github.com/ValveSoftware/Fossilize)
- [Fossilize README](https://github.com/ValveSoftware/Fossilize#readme)
- [Fossilize CLI target list](https://github.com/ValveSoftware/Fossilize/blob/master/cli/CMakeLists.txt)
- [Fossilize license](https://github.com/ValveSoftware/Fossilize/blob/master/LICENSE)
- [Vulkan Frame Loop Performance TODO](vulkan-frame-loop-performance-todo.md)
- [Vulkan ReSTIR Radiance Cache GI TODO](vulkan-restir-radiance-cache-gi-todo.md)
- [Vulkan Pipeline Cache](../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanPipelineCache.cs)
- [Vulkan Pipeline Prewarm Database](../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanPipelinePrewarmDatabase.cs)
- [Vulkan Shader Artifact Cache](../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanShaderArtifactCache.cs)

## Goal

Integrate Valve's Fossilize tooling into XREngine's Vulkan workflow so the
engine can capture, inspect, replay, and ship persistent Vulkan object state in
a controlled Windows-first pipeline.

Primary outcomes:

- Use Fossilize's Vulkan layer to capture `.foz` repro archives when pipeline
  creation, shader module creation, descriptor layout creation, render pass
  creation, or driver behavior fails before a RenderDoc capture is practical.
- Keep XREngine's Vulkan cache stack as the runtime source of truth and use
  Fossilize as an additive capture, replay, inspection, and warmup tool.
- Add an optional engine-owned Fossilize capture/export path for persistent
  Vulkan objects only after the external layer workflow proves useful.
- Replay captured archives on one or more devices to warm device-specific
  `VkPipelineCache` blobs without launching the full editor or world.
- Build maintenance tools around `.foz` archives: list, merge, prune, rehash,
  disassemble, optimize, synthesize, benchmark, and package them.
- Keep startup hitches, VR first-frame stalls, and shader/pipeline variant
  regressions visible through logs, profiler counters, and reproducible replay
  artifacts.

## Upstream Fossilize Facts

Fossilize is a library plus Vulkan layer that serializes persistent Vulkan
objects and their `CreateInfo` structures for replay. The README lists support
for:

- `VkSampler` for immutable samplers in descriptor set layouts
- `VkDescriptorSetLayout`
- `VkPipelineLayout`
- `VkRenderPass`
- `VkShaderModule`
- `VkPipeline` for compute and graphics

The upstream README names three main use cases:

- Internal engine use: extend the idea of `VkPipelineCache` to include the
  persistent object graph so objects can be created at load time and, if
  useful, shipped with the application.
- Vulkan layer capture: capture state for repro cases when failures happen too
  early for a conventional capture.
- Replay-on-N-devices: serialize state once, then replay it on many machines to
  generate device-specific `VkPipelineCache` objects without running the
  application.

Current upstream CLI targets include:

- `fossilize-replay`
- `fossilize-bench`
- `fossilize-convert-db`
- `fossilize-merge-db`
- `fossilize-disasm`
- `fossilize-prune`
- `fossilize-list`
- `fossilize-rehash`
- `fossilize-opt`
- `fossilize-synth`

Layer capture facts to preserve in XREngine docs and scripts:

- Layer name: `VK_LAYER_fossilize`.
- The implicit layer can be enabled with `FOSSILIZE=1` when the layer JSON is
  discoverable by the Vulkan loader.
- Default desktop capture writes `fossilize.$hash.$index.foz` in the working
  directory on `vkDestroyDevice`.
- `FOSSILIZE_DUMP_PATH` changes the capture prefix.
- `FOSSILIZE_DUMP_SIGSEGV=1` captures on access violations or segmentation
  faults; on Windows it uses a global SEH handler and terminates after the dump.
- `FOSSILIZE_DUMP_SYNC=1` records synchronously before driver calls; it is more
  robust for crash repros but expected to be slow.

## Current Local Facts

- `VulkanPipelineCache.cs` already creates a persistent `VkPipelineCache` and
  stores cache bytes under `%LOCALAPPDATA%\XREngine\Vulkan\PipelineCache`,
  keyed by vendor id, device id, driver version, and API version.
- `VulkanPipelinePrewarmDatabase.cs` already records graphics and compute
  pipeline cache misses in a JSON database when
  `XRE_VK_PIPELINE_PREWARM_CAPTURE=1` is set.
- `VulkanShaderArtifactCache.cs` already persists SPIR-V artifacts and metadata
  for resolved Vulkan shaders.
- `VulkanPipelineCompileQueue.cs` can compile graphics pipelines in background
  workers, but those workers intentionally do not use the shared
  `VkPipelineCache` because parallel `vkCreateGraphicsPipelines` calls would
  need cache synchronization.
- `VulkanGraphicsPipelineLibraryCache.cs` keeps graphics pipeline library
  subsets only in memory today.
- Dynamic rendering paths identify pipelines with format signatures and
  `VkPipelineRenderingCreateInfo`; Fossilize replay compatibility with dynamic
  rendering and graphics pipeline libraries must be validated before relying on
  `.foz` archives for those paths.
- The Vulkan ReSTIR/ray tracing roadmap will eventually add ray query and ray
  tracing pipelines. Current Fossilize source appears to include ray tracing
  replay plumbing, but the public README's object list only promises compute
  and graphics pipelines. Treat ray tracing pipeline replay as "verify before
  depending on it."

## Non-Goals

- Do not replace XREngine's shader source resolution, SPIR-V artifact cache, or
  `VkPipelineCache` persistence with Fossilize.
- Do not expose Fossilize as a competing replacement backend through a setting
  such as `VulkanCacheBackend = XREngine | Fossilize`. The cache boundary is
  layered, not either/or.
- Do not enable the Fossilize layer silently in ordinary editor, server, or VR
  client runs.
- Do not make OpenGL, DirectX, or non-Vulkan tests depend on Fossilize.
- Do not ship one machine's raw `VkPipelineCache` blob as a universal cache.
  Driver cache bytes are device, driver, and pipeline-cache-UUID specific.
- Do not add a submodule, binary dependency, or package feed without owner
  approval and the repository dependency-license workflow.
- Do not assume Fossilize archives are safe to publish publicly. They contain
  shader modules and pipeline state, and may expose project-specific shader
  names, pass names, material permutations, or experimental techniques.
- Do not hide failed Fossilize capture/replay behind CPU or OpenGL fallback
  behavior. Vulkan diagnostic paths should fail visibly with actionable logs.

## Integration Shape

Use XREngine caches as the runtime authority:

- `VulkanShaderArtifactCache` owns shader source rewrite, reflection metadata,
  SPIR-V bytes, and shader invalidation.
- `VulkanPipelinePrewarmDatabase` owns engine-readable pipeline identity:
  render pass, material, mesh, program, dynamic rendering signature, feature
  profile, and cache-miss diagnostics.
- `VulkanPipelineCache` owns runtime `VkPipelineCache` creation, loading,
  saving, device/driver keying, and startup diagnostics.

Use Fossilize as an additive workflow beside that authority:

- Fossilize layer/native capture records persistent Vulkan object `CreateInfo`
  state into `.foz` archives.
- Fossilize replay compiles those archived objects offline and can produce
  device-specific `VkPipelineCache` bytes.
- XREngine consumes replay-generated cache bytes through the existing
  `VulkanPipelineCache` load path after validation and installation.
- XREngine metadata provides the map from Fossilize objects back to engine
  passes, materials, shaders, feature profiles, and profiler counters.

Use three implementation lanes, in this order:

- External tool lane: build or install Fossilize CLI/layer, add scripts and VS
  Code tasks, capture `.foz` archives with `VK_LAYER_fossilize`, and replay
  archives into device-local `VkPipelineCache` files.
- Engine-assisted lane: use XREngine pipeline/prewarm metadata to name, group,
  validate, and package Fossilize captures; correlate replay results with
  profiler counters and runtime pipeline cache misses.
- Native recorder lane: add an optional native bridge around Fossilize
  `StateRecorder` only after the layer/tool workflow proves value. This bridge
  should export the renderer's persistent Vulkan object graph directly instead
  of relying on implicit layer interception.

## Settings Contract

Do not add a single "choose Valve or custom cache" setting. Add independent
settings for the runtime driver cache and for Fossilize participation:

- `VulkanPipelineCacheMode` / `XRE_VK_PIPELINE_CACHE_MODE`:
  `Auto`, `Disabled`, `Required`.
- `VulkanFossilizeMode` / `XRE_VK_FOSSILIZE_MODE`:
  `Disabled`,
  `LayerCapture`,
  `LayerCaptureSync`,
  `CrashCapture`,
  `NativeRecord`.
- `VulkanFossilizeReplayCacheMode` /
  `XRE_VK_FOSSILIZE_REPLAY_CACHE_MODE`:
  `Disabled`,
  `UseIfPresent`,
  `Required`.

Defaults:

- `VulkanPipelineCacheMode = Auto`; current behavior stays on when supported.
- `VulkanFossilizeMode = Disabled`; no layer or native recorder is active in
  normal editor, server, client, or test runs.
- `VulkanFossilizeReplayCacheMode = UseIfPresent`; curated replay-generated
  cache bytes may be loaded only after they pass the same device/driver/API
  identity checks as ordinary runtime cache files.

Mode semantics:

- `Auto` pipeline cache creates and persists the ordinary runtime
  `VkPipelineCache`, with visible warnings if creation or save fails.
- `Required` pipeline cache fails the requested Vulkan diagnostic/profile run
  when `VkPipelineCache` creation, loading, or required replay-cache use fails.
- Fossilize `LayerCapture` sets process-local Vulkan loader/layer environment
  through launch tooling only.
- Fossilize `LayerCaptureSync` also enables synchronous recording for robust
  repros and must warn about expected performance cost.
- Fossilize `CrashCapture` enables the crash-dump mode only for explicitly
  requested repro runs.
- Fossilize `NativeRecord` uses the future engine bridge and must remain
  diagnostic until layer and replay parity are proven.
- Replay cache `Required` is for QA/release-warmup validation only; it should
  fail visibly if no validated replay cache matches the active device profile.

## Artifact Layout

Proposed local layout:

- `Build/Fossilize/Tools/` - locally built Fossilize CLI/layer binaries.
- `Build/Fossilize/Captures/` - raw `.foz` captures from editor and VR runs.
- `Build/Fossilize/Replays/` - replay logs, stats, validation output, and
  generated device-specific `VkPipelineCache` blobs.
- `Build/Fossilize/Packages/` - curated archives intended for CI, QA, or
  release packaging.
- `%LOCALAPPDATA%\XREngine\Vulkan\Fossilize/` - optional per-user working cache
  if replay artifacts must persist outside the repo checkout.

Do not commit generated `.foz` archives or device cache blobs unless a later
policy explicitly defines a small, sanitized fixture format for tests.

## Phase 0 - Branch, Dependency Decision, And Policy

- [ ] Create a dedicated branch, for example
      `feature/vulkan-fossilize-integration`.
- [ ] Decide whether Fossilize is:
      an optional external tool download, a submodule under `Build/Submodules`,
      a vendored source dependency, or a developer-provided tool path.
- [ ] If adding or pinning Fossilize as a repository-managed dependency, get
      owner approval before the dependency change.
- [ ] Audit Fossilize and its required CLI submodules for open-source and
      commercial-use compatibility.
- [ ] Run the dependency workflow after any dependency addition:
      `pwsh Tools/Generate-Dependencies.ps1`.
- [ ] Update `docs/DEPENDENCIES.md` and `docs/licenses/` if a dependency is
      added or vendored.
- [ ] Define the initial support policy:
      Windows desktop first, Vulkan only, editor/unit-testing world first, VR
      client later.
- [ ] Define artifact privacy policy for `.foz`, replay logs, disassembled
      SPIR-V, and generated device cache blobs.
- [ ] Define cache ownership in code/docs:
      XREngine owns shader artifacts, prewarm identities, runtime
      `VkPipelineCache` loading/saving, and diagnostics; Fossilize owns
      optional `.foz` object-state capture/replay tooling.
- [ ] Reject a binary cache-backend selector design. Do not implement
      `XREngine` versus `Fossilize` as mutually exclusive cache choices.

Acceptance criteria:

- [ ] The team knows whether Fossilize is an optional tool, submodule, or
      vendored dependency before implementation starts.
- [ ] License and dependency documentation requirements are clear.
- [ ] No dependency or submodule change lands without explicit approval.
- [ ] The cache boundary is documented before any settings or scripts are added.

## Phase 1 - Build And Locate Fossilize Tools

- [ ] Add a setup script such as `Tools/Setup-Fossilize.ps1`.
- [ ] Support a user-provided `FOSSILIZE_ROOT` or `XRE_FOSSILIZE_ROOT`.
- [ ] Support a local build output under `Build/Fossilize/Tools/`.
- [ ] Build or locate:
      `fossilize-replay`,
      `fossilize-list`,
      `fossilize-merge-db`,
      `fossilize-prune`,
      `fossilize-rehash`,
      `fossilize-disasm`,
      `fossilize-opt`,
      `fossilize-synth`,
      `fossilize-bench`,
      and the `VK_LAYER_fossilize` library plus JSON manifest.
- [ ] Verify `fossilize-replay --help` and record the version or Git SHA.
- [ ] Add a script or helper that prints the Vulkan loader environment needed
      for the built layer, including `VK_LAYER_PATH`, `FOSSILIZE=1`, and
      `FOSSILIZE_DUMP_PATH`.
- [ ] Add an ExecTool entry for setup and tool discovery.
- [ ] Keep the setup script idempotent and non-destructive.

Acceptance criteria:

- [ ] A clean Windows developer machine can discover or build Fossilize tools
      through one documented command.
- [ ] Tool discovery failure reports the missing executable, DLL, or layer JSON.
- [ ] Normal XREngine build/test commands do not require Fossilize.

## Phase 2 - Layer Capture Workflow

- [ ] Add `VulkanFossilizeMode` and `XRE_VK_FOSSILIZE_MODE` with modes:
      `Disabled`,
      `LayerCapture`,
      `LayerCaptureSync`,
      `CrashCapture`,
      and reserved `NativeRecord`.
- [ ] Ensure the default is `Disabled`.
- [ ] Ensure ordinary editor/server/client launches do not activate Fossilize
      unless an explicit mode or capture script requests it.
- [ ] Add `Tools/Capture-FossilizeEditor.ps1` for editor captures.
- [ ] Add `Tools/Capture-FossilizeVRClient.ps1` after the editor path works.
- [ ] Launch the editor from the repo root so assets, settings, logs, and
      capture outputs resolve predictably.
- [ ] Default output prefix to
      `Build/Fossilize/Captures/editor-$timestamp/xrengine`.
- [ ] Set process-scoped environment only:
      `VK_LAYER_PATH`,
      `FOSSILIZE=1`,
      `FOSSILIZE_DUMP_PATH`,
      and optionally `FOSSILIZE_DUMP_SIGSEGV` or `FOSSILIZE_DUMP_SYNC`.
- [ ] Add flags for:
      unit-testing world,
      MCP,
      Vulkan feature profile,
      render target mode,
      GPU submission strategy,
      bindless material mode,
      and pipeline prewarm capture.
- [ ] Copy or link the relevant XREngine logs beside the `.foz` output:
      `log_vulkan.log`, `log_rendering.log`, profiler frame logs, GPU pipeline
      dumps, and current settings.
- [ ] Add a capture manifest with:
      command line,
      env vars,
      Git commit,
      dirty status summary,
      GPU name,
      vendor/device/driver/API ids,
      Vulkan feature profile,
      capture reason,
      and generated `.foz` files.
- [ ] Add a "sync crash repro" mode that enables `FOSSILIZE_DUMP_SYNC=1` and
      warns about performance cost.
- [ ] Add a "driver crash repro" mode that enables `FOSSILIZE_DUMP_SIGSEGV=1`
      only when explicitly requested.

Acceptance criteria:

- [ ] A Vulkan Unit Testing World editor run produces a `.foz` archive under
      `Build/Fossilize/Captures/`.
- [ ] The capture can be correlated with the exact XREngine log session and
      renderer settings.
- [ ] Capture scripts leave global Vulkan loader and user environment state
      unchanged.
- [ ] Runtime defaults remain unchanged when Fossilize mode is `Disabled`.

## Phase 3 - Replay And Device Cache Warmup

- [ ] Add `VulkanPipelineCacheMode` and `XRE_VK_PIPELINE_CACHE_MODE` with modes:
      `Auto`,
      `Disabled`,
      and `Required`.
- [ ] Keep `Auto` behavior equivalent to the current runtime `VkPipelineCache`
      path unless a validated replay cache is available.
- [ ] Add `VulkanFossilizeReplayCacheMode` and
      `XRE_VK_FOSSILIZE_REPLAY_CACHE_MODE` with modes:
      `Disabled`,
      `UseIfPresent`,
      and `Required`.
- [ ] Add `Tools/Replay-FossilizeArchive.ps1`.
- [ ] Support replaying one archive, a directory of archives, or a merged
      archive.
- [ ] Default replay output to `Build/Fossilize/Replays/$timestamp/`.
- [ ] Support `fossilize-replay` options for:
      validation,
      SPIR-V validation,
      pipeline stats,
      thread count,
      timeout seconds,
      shader cache size,
      pipeline hash,
      graphics/compute/raytracing pipeline ranges,
      on-disk pipeline cache,
      and replayer cache.
- [ ] Generate XREngine-compatible cache names from the same device profile
      fields used by `VulkanPipelineCache.cs`.
- [ ] Write replay-generated `VkPipelineCache` blobs to a staging path by
      default, not directly into `%LOCALAPPDATA%`.
- [ ] Add an explicit install command that copies a validated staged replay
      cache into the XREngine `VulkanPipelineCache` path.
- [ ] Make installed replay caches pass the same vendor/device/driver/API and
      pipeline-cache-UUID checks as ordinary runtime cache files.
- [ ] Make replay-generated cache bytes an input to `VulkanPipelineCache.cs`;
      do not add a parallel runtime Fossilize cache loader.
- [ ] Add replay summaries:
      parsed pipelines,
      compiled pipelines,
      skipped pipelines,
      failed pipelines,
      cached pipelines,
      pipeline creation duration,
      validation failures,
      crashes/hangs/timeouts,
      and output cache bytes.
- [ ] Add a cache-install script that backs up any existing local
      `pcache_*.bin` before replacing it with replay output.
- [ ] Teach the replay script to fail visibly when a capture needs unsupported
      device features or extensions.

Acceptance criteria:

- [ ] A `.foz` captured from the editor can be replayed without launching the
      editor.
- [ ] Replay can produce a device-specific pipeline cache blob in a staging
      directory.
- [ ] Installing the replayed cache reduces runtime Vulkan pipeline cache misses
      or pipeline compile stalls in a measured editor run.
- [ ] `XRE_VK_FOSSILIZE_REPLAY_CACHE_MODE=Required` fails visibly when no
      validated replay cache matches the active device profile.

## Phase 4 - Engine Metadata And Capture Correlation

- [ ] Extend the existing prewarm database schema or add a companion manifest so
      Fossilize archives can be matched to XREngine pipeline identities.
- [ ] Include these identities where available:
      pass index/name,
      render pipeline name,
      material/effect,
      mesh,
      program,
      shader artifact identity,
      dynamic rendering signature,
      graphics pipeline library subset,
      descriptor layout fingerprint,
      pipeline layout fingerprint,
      and feature profile.
- [ ] Add profiler counters for Fossilize capture/replay workflow results:
      capture enabled,
      capture files written,
      archive bytes,
      replay cache hits,
      replay failures,
      and runtime cache misses after replay.
- [ ] Add a small viewer/report script that joins:
      `.foz` list output,
      prewarm JSON,
      runtime profiler dumps,
      and Vulkan logs.
- [ ] Preserve allocation-free render hot paths. Manifest/correlation work must
      happen at capture boundaries, pipeline creation boundaries, or tooling
      time, not inside per-frame draw submission loops.

Acceptance criteria:

- [ ] A runtime pipeline cache miss can be traced to a Fossilize archive entry
      or reported as missing from all known archives.
- [ ] Replay summaries name XREngine passes/materials/programs when metadata is
      available.
- [ ] Capture metadata does not add per-frame render submission allocations.
- [ ] Engine metadata remains authoritative even when a `.foz` archive can
      replay the Vulkan objects successfully.

## Phase 5 - Native Fossilize Recorder Bridge

- [ ] Decide whether native recording is worth implementing after Phases 1-4.
- [ ] If yes, add a minimal native wrapper project such as
      `XREngine.Native.Fossilize` with a narrow C ABI.
- [ ] Wrap Fossilize `StateRecorder` recording calls for:
      samplers,
      descriptor set layouts,
      pipeline layouts,
      render passes,
      shader modules,
      graphics pipelines,
      compute pipelines,
      and ray tracing pipelines only if verified.
- [ ] Add safe managed handles and lifetime rules for recorder/database objects.
- [ ] Record exact `CreateInfo` data at Vulkan object creation time, not by
      reconstructing state from higher-level engine objects after the fact.
- [ ] Ensure immutable sampler references, descriptor layout dependencies,
      render pass dependencies, and pipeline layout dependencies resolve to the
      hashes Fossilize expects.
- [ ] Verify support for dynamic rendering pipelines whose render pass handle is
      null and whose formats arrive through `VkPipelineRenderingCreateInfo`.
- [ ] Verify support for graphics pipeline libraries and linked library
      pipelines.
- [ ] Keep native recording optional and diagnostic until replay proves parity.
- [ ] Ensure native recording does not bypass `VulkanShaderArtifactCache`,
      `VulkanPipelinePrewarmDatabase`, or `VulkanPipelineCache`; it should feed
      Fossilize archives while XREngine caches continue to drive runtime
      behavior.
- [ ] Add source-contract tests that every Vulkan persistent object creation
      path either records to Fossilize or intentionally opts out with a reason.

Acceptance criteria:

- [ ] XREngine can export a `.foz` archive without enabling the Vulkan layer.
- [ ] Layer-captured and native-recorded archives for the same smoke scene
      replay the same required object set.
- [ ] Native recording failures report missing dependency hashes or unsupported
      `pNext` structures instead of silently dropping pipelines.
- [ ] Turning native recording off leaves ordinary Vulkan cache behavior
      unchanged.

## Phase 6 - Archive Maintenance And Packaging

- [ ] Add `Tools/Merge-FossilizeArchives.ps1` around `fossilize-merge-db`.
- [ ] Add `Tools/List-FossilizeArchive.ps1` around `fossilize-list`.
- [ ] Add `Tools/Prune-FossilizeArchive.ps1` around `fossilize-prune`.
- [ ] Add `Tools/Rehash-FossilizeArchive.ps1` around `fossilize-rehash`.
- [ ] Add `Tools/Disassemble-FossilizeArchive.ps1` around `fossilize-disasm`.
- [ ] Add optional `fossilize-opt` and `fossilize-synth` workflows only after
      their impact on XREngine shader identity and debugging is understood.
- [ ] Define archive classes:
      developer repro,
      CI smoke,
      release warmup,
      per-scene sample,
      and private user report.
- [ ] Add pruning rules so release warmup archives do not grow without bound.
- [ ] Add a manifest schema for curated archives:
      engine commit,
      shader artifact fingerprint,
      feature profile,
      scene/settings source,
      object counts,
      archive hash,
      source captures,
      and intended use.
- [ ] Add archive integrity checks before replay or packaging.

Acceptance criteria:

- [ ] Multiple captures can be merged into a curated archive with stable
      metadata.
- [ ] Stale or duplicate archive content can be identified and pruned.
- [ ] Curated archives are reproducible from documented capture inputs.

## Phase 7 - CI, QA, And Device Matrix

- [ ] Add a CI or local QA lane that replays a small Fossilize archive with
      Vulkan validation enabled.
- [ ] Add per-device replay jobs for available GPU families:
      NVIDIA, AMD, Intel, and software/validation adapters where useful.
- [ ] Record device-specific output cache blobs separately by
      vendor/device/driver/API/pipeline-cache UUID.
- [ ] Add a regression gate for:
      replay parse failures,
      validation failures,
      pipeline compile required when it should be cached,
      driver crashes,
      and large compile-time regressions.
- [ ] Compare replay results before and after Vulkan render-path changes such
      as dynamic rendering, bindless materials, meshlet dispatch, and ReSTIR.
- [ ] Do not make this a required PR gate until the archive set is small,
      deterministic, and well understood.

Acceptance criteria:

- [ ] A small Fossilize smoke archive protects core Vulkan pipeline creation
      behavior.
- [ ] Device-specific failures produce a replay bundle that can be attached to
      a driver bug or XREngine issue.
- [ ] Runtime startup and first-camera-move hitches have before/after evidence
      when replay caches are installed.

## Phase 8 - Editor And Developer UX

- [ ] Add ImGui diagnostic controls for:
      pipeline cache mode,
      Fossilize tool discovery,
      Fossilize mode,
      replay cache mode,
      capture enabled state,
      current dump path,
      latest `.foz` files,
      and replay/cache-install status.
- [ ] Add MCP actions only after the scripts are stable:
      start Fossilize capture,
      stop editor after capture,
      list latest archives,
      replay latest archive,
      and dump capture manifest.
- [ ] Add VS Code tasks:
      `Setup-Fossilize`,
      `Capture-Fossilize-Editor`,
      `Replay-Fossilize-Archive`,
      and `Merge-Fossilize-Archives`.
- [ ] Add ExecTool entries under the Build, Rendering, or Reports category.
- [ ] Add clear warnings when capture modes can slow or terminate the process.
- [ ] Add links from Vulkan renderer diagnostics docs once workflow stabilizes.

Acceptance criteria:

- [ ] A developer can capture and replay a Vulkan pipeline repro without
      memorizing Fossilize environment variables.
- [ ] The editor reports whether Fossilize is active, where captures are being
      written, and why capture is unavailable.
- [ ] The editor distinguishes ordinary runtime cache status from Fossilize
      capture/replay status.
- [ ] MCP automation can include Fossilize captures in evidence-based rendering
      investigations.

## Phase 9 - Release Warmup And Distribution

- [ ] Decide which scenes/settings create release warmup archives:
      empty editor,
      Unit Testing World,
      default demo scene,
      VR launch scene,
      material-diverse scene,
      and rendering feature smoke scenes.
- [ ] Decide whether release builds ship:
      no Fossilize data,
      `.foz` archives only,
      device-specific cache blobs for known QA devices,
      or a first-run background replay job.
- [ ] Prefer `.foz` archives plus first-run/user-controlled replay over shipping
      raw `VkPipelineCache` blobs for unknown hardware.
- [ ] Add user-visible controls for expensive background replay if this reaches
      shipped builds.
- [ ] Keep the shipped runtime loader pointed at `VulkanPipelineCache`; release
      Fossilize warmup artifacts can pre-populate that cache but should not
      introduce a second cache authority.
- [ ] Add telemetry/logging for warmup duration and skipped pipeline counts.
- [ ] Make cache invalidation respect:
      engine version,
      shader artifact fingerprint,
      Vulkan feature profile,
      GPU vendor/device,
      driver version,
      API version,
      pipeline cache UUID,
      dynamic rendering mode,
      bindless mode,
      and graphics pipeline library mode.

Acceptance criteria:

- [ ] Release warmup policy is explicit and documented.
- [ ] A driver update invalidates incompatible caches without corrupting normal
      startup.
- [ ] First-run warmup improves user experience without hiding failures or
      blocking ordinary launches indefinitely.
- [ ] A release build can run with all Fossilize modes disabled and still use
      the normal XREngine Vulkan cache path.

## Phase 10 - Validation

- [ ] Validate the layer capture path in Unit Testing World.
- [ ] Validate replay on the same machine and compare runtime cache misses
      before/after installing replay output.
- [ ] Validate runtime behavior with:
      `XRE_VK_PIPELINE_CACHE_MODE=Auto`,
      `XRE_VK_PIPELINE_CACHE_MODE=Disabled`,
      and `XRE_VK_PIPELINE_CACHE_MODE=Required`.
- [ ] Validate Fossilize remains inactive with
      `XRE_VK_FOSSILIZE_MODE=Disabled`.
- [ ] Validate replay-cache policy with:
      `XRE_VK_FOSSILIZE_REPLAY_CACHE_MODE=Disabled`,
      `UseIfPresent`,
      and `Required`.
- [ ] Validate replay on at least one different Vulkan-capable machine.
- [ ] Validate dynamic rendering captures and legacy render-pass captures.
- [ ] Validate traditional CPU-driven draws and GPU-driven/material-table draws.
- [ ] Validate bindless material table pipelines once Vulkan bindless is enabled
      in runtime smoke.
- [ ] Validate captures during known problematic cases:
      pipeline compile saturation,
      black-frame investigations,
      camera-motion resource churn,
      graphics pipeline library mode,
      and shader artifact cache invalidation.
- [ ] Validate crash capture with `FOSSILIZE_DUMP_SIGSEGV=1` only in a controlled
      repro run.
- [ ] Validate synchronous capture with `FOSSILIZE_DUMP_SYNC=1` and document the
      performance impact.
- [ ] Validate that all scripts clean up background processes and do not leave
      editor or replay sessions running.

Acceptance criteria:

- [ ] Captures replay cleanly or fail with actionable logs.
- [ ] Runtime cache miss counts decrease after a successful replay/cache-install
      pass.
- [ ] No validation run requires choosing between "Valve cache" and "XREngine
      cache"; tests exercise layered participation modes instead.
- [ ] Validation results are recorded in this TODO or a linked test report.

## Suggested Additional Uses

- Driver repro packets: attach a tiny `.foz` plus replay log to GPU vendor bugs
  when a full project or RenderDoc capture is too large.
- Shader variant inventory: use `fossilize-list` and XREngine metadata to find
  unused, duplicated, or unexpectedly exploding pipeline variants.
- Shader bloat triage: use `fossilize-disasm` and `fossilize-opt` outputs to
  compare generated SPIR-V across material systems and backend defines.
- Graphics pipeline library validation: replay captured subset/link pipelines
  to check whether library-mode benefits survive driver updates.
- Dynamic rendering migration safety net: keep paired legacy-render-pass and
  dynamic-rendering archives for the same scene until the migration is fully
  validated.
- VR demo warmup: prebuild pipeline caches for the VR launch scene and common
  avatar/material variants so headset startup avoids first-frame stutter.
- ReSTIR/ray tracing bring-up: once Vulkan RT lands, test whether Fossilize can
  capture/replay ray tracing pipelines well enough to reproduce shader binding
  table or pipeline-creation issues.
- Content-build regression signal: compare curated archive object counts and
  replay time across builds to detect accidental shader permutation explosions.
- QA minimization: prune a failing archive down to the smallest set of pipeline
  objects that still reproduces a crash, hang, or validation error.
- Offline performance experiments: replay the same archive with different
  worker counts, shader cache sizes, validation settings, and driver versions
  without repeatedly launching the full editor.

## Open Questions

- Should XREngine ship Fossilize source/tooling, require a developer-provided
  install, or download/build it as an optional setup step?
- Can current Fossilize master replay XREngine dynamic rendering pipelines and
  graphics pipeline library paths without patches?
- Should native recording live in a new native bridge, in a tooling executable,
  or remain layer-only until v1?
- Should replay-generated pipeline cache blobs ever install directly into
  `%LOCALAPPDATA%`, or should every install flow pass through a staged
  validation/backup command first?
- How should archives be sanitized before sharing outside the project?
- Should release builds ever run `fossilize-replay` in the background, or should
  this remain a developer/QA/build-farm workflow?

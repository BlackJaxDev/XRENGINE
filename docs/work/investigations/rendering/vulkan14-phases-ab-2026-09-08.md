# Vulkan 1.4 baseline and descriptor heap contract

Opened: 2026-09-08. Resumed and completed: 2026-09-09. Status: phases A/B complete.

Scope: phases A and B of the Vulkan 1.4 performance and shader modernization
todo. No performance improvement is claimed. No tests are added or modified.
User acceptance has not yet been reported.

## Final result

All A/B acceptance checkboxes are complete. The final fixes preserve the
128-byte common shader-constant prefix in ordinary heap program mappings and
publish indirect per-draw constants before descriptors. This corrects the
reproduced heap indirect device loss. OpenXR version bounds are compared in
their original 64-bit representation, allowing Monado's broad maximum while
still requiring Vulkan 1.4.

Final build 52 passed with zero warnings/errors. Heap desktop controls 51/52
and Monado smoke 53 have no Vulkan VUID/SYNC/ERROR matches through shutdown.
The desktop captures were viewed, including changed materials and camera views.
The source contains no temporary draw suppression, constant-color shader bypass
or hard-coded indirect arguments. No tests were added/modified, no dependencies
changed, and no commit was made during this resumed work. The named editor
session is stopped. Earlier failures below are retained as investigation history.

## Resumed acceptance work (2026-09-09)

The user resumed A/B completion and selected the existing Monado path for XR.
Previous implementation is now committed in `08283cda1`; the original stop note
below remains historical evidence.

- Control 44 (PID 30244, build 44: zero warnings/errors) disabled only
  `Rendering.OpenGL.AllowProgramPipelines`. Bootstrap confirmed
  `AllowShaderPipelines=False`. Vulkan 1.4, descriptor heaps and GPU indirect
  submission remained enabled. Cube insertion still caused device loss, so GPL
  linking is not the sole cause. This run's checkpoint tokens were null; it does
  not replace the attributed draw bracket from controls 40/42. A timeline VUID
  reported a current value of `UINT64_MAX` immediately before device loss;
  earlier steady-state validation was clean.
- Monado control 45 (PID 30128) reached the staged runtime but exposed a version
  conversion bug: its XrVersion maximum `1023.1023.4095` was rejected for exceeding
  Vulkan's packed major field. Negotiation now compares major/minor in the
  original 64-bit OpenXR representation and ignores patch as required by the
  [OpenXR graphics-requirements contract](https://registry.khronos.org/OpenXR/specs/1.1/man/html/XrGraphicsRequirementsVulkanKHR.html).
  This retains the mandatory 1.4 requirement. Build 46 passed with zero warnings
  and errors; Monado heap control 46 (PID 8788) then initialized Vulkan 1.4.341.
- Monado heap control 46 captured both actual OpenXR preview eyes at 896x1007
  with distinct stereo views. A blue material edit and world-space movement
  changed the viewed left-eye output. Conventional descriptor-indexing control
  47 (PID 30016, same build/settings) reproduced the bright cube and noisy RVC
  background, so those visual limitations are not heap-specific evidence.
  Both runs had zero Vulkan VUID/SYNC/ERROR matches through shutdown. Early
  strict stereo frames reported pipeline-readiness retries; later eye captures
  and desktop frame outcomes succeeded. Both named editor runs are stopped;
  the task's Monado service ownership marker was cleaned up.
- Bounded Monado heap smoke 48 (PID 13092, build 46) passed with exit code 0:
  30 warmup + 120 retained frames, 139 successful true single-pass stereo
  submissions, 11 startup no-layer frames, zero `xrEndFrame` failures and zero
  sequential fallback attempts. Both eyes published 139 times; all 148 acquired
  images per eye were released. Standard and synchronization validation were
  effective, the summary has no failures, and teardown completed. Summary:
  `Build/_AgentValidation/20260908-184611-vulkan14-ab/reports/monado48-heap-smoke.json`.
- A bounded independent Sol review (`014db92e0feb43a1b2e11f48fa14d038`, requested
  and actual `gpt-5.6-sol`) found no proven mapping ABI defect in the supplied
  native mapping/root/indirect source. Actual GPU arguments and metadata remain
  the next discriminator; unavailable counter readback is not evidence of zero.
- Input capture 49 (PID 31240, build 49: zero warnings/errors) suppressed planned
  indirect draws and copied their GPU-generated inputs in the same primary
  command buffer, with consumption only after its completion. At frame 1110,
  pass 1, the count word was 1. Active command 0 contained `indexCount=36`,
  `instanceCount=1`, `firstIndex=15`, `vertexOffset=11`, `firstInstance=3`.
  The canonical scene mapping independently identified the cube's legacy command
  index as 3. Unconsumed command-array entries are not active draws. Frames
  completed with the diagnostic enabled; this does not validate indirect raster
  output. Exact shader-bound metadata and transform contents remain to be checked.
- Fragment control 50 (PID 32336, build 50: zero warnings/errors) retained the
  original fragment resources but added a temporary `gl_FragCoord.x >= 0` early
  branch producing fixed G-buffer outputs. Disassembly confirms bindings 11/31
  and the branch preceding resource loads. Device loss still occurred at frame
  5432. This narrows executed fragment reads as a cause, but does not rule out
  driver descriptor setup or prove the vertex/index path correct. Its native
  checkpoint tokens were unresolved; earlier controls 40/42 remain the evidence
  attributing the failure to indirect graphics. The temporary shader control is
  diagnostic only and must be removed from the final implementation.
- The local capture review found completion-ownership ordering and unused-tail
  decoding issues; both were corrected before further validation. An optional
  additional broker review failed because API credits were exhausted. Native
  review and runtime investigation continued; no broker result was fabricated.
- The decisive defect was in `VkMeshRenderer.RecordIndirectDrawState`: unlike
  the other mesh paths, it published heap descriptor indices and then wrote
  `MeshDrawPushConstants` at byte zero. Ordinary heap layout creation had also
  omitted the common shader-constant prefix, so those writes aliased descriptor
  indices. Record constants before descriptors, and reserve the existing
  `VulkanPipelineManager.CommonPushConstantByteSize` (128 bytes) in ordinary heap
  program layouts. ImGui already uses a separate explicit 16-byte prefix.
- Control 51 (PID 33720, build 51: zero warnings/errors) changed only the indirect
  ordering in the active rendering path. Full bindless material shaders and
  native GPU-generated indirect-count draws then rendered successfully. Viewed
  captures show the lit cube and grid, followed by a blue material edit, object
  movement and a different camera view. Frame 5324 completed; the scene recorded
  26 compute operations and one indirect draw. Full Vulkan logs contain zero
  VUID/SYNC/ERROR matches through shutdown. The optional capture extension and
  temporary fragment/direct controls were removed from production source;
  control 49's command evidence remains in this note. No draw suppression or
  shader bypass is part of the fix.
- Final control 52 (PID 23952, build 52: zero warnings/errors) additionally
  reserves the 128-byte prefix and removes the temporary diagnostics. A cube
  and sphere render through the full heap GPU indirect path. A green cube
  material edit, moved sphere and second camera view were captured and viewed.
  Frame 2836 completed with 27 compute operations and one indirect draw;
  full Vulkan validation remained clean through shutdown.
- Final Monado smoke 53 (PID 21788, build 52) passed with exit code 0, 30 warmup
  and 120 retained frames. It made 136 successful true single-pass stereo
  submissions, with 14 startup no-layer frames, zero end-frame failures and zero
  sequential fallback attempts. Both eyes acquired/released 148 images.
  Validation and synchronization validation were effective, the failure list
  is empty, and teardown completed. The full Vulkan log is clean. Summary:
  `Build/_AgentValidation/20260908-184611-vulkan14-ab/reports/monado53-heap-smoke.json`.
- Final native Sol review confirmed the overwrite and the common-prefix fix.
  The observed largest descriptor tail is 104 bytes, totaling 232 with the
  prefix, below this device's 256-byte push-data limit. Oversized layouts still
  fail visibly. ImGui retains its separate 16-byte projection prefix.

## Earlier closeout at user request

A1–A4 are complete. The native heap implementation has substantial pipeline,
root, storage, lifetime and state corrections, but A5/B acceptance remains open.
The latest source build (40) passed with zero warnings and errors; all 102 modules
in the retained validation set passed `spirv-val --target-env vulkan1.4`.
Desktop material edits, compute, UI, resize, final grid ordering and both emulated
XR eye targets were validated. One CPU shutdown run leaked a pipeline layout;
emulated XR startup, rendering and shutdown were clean. Physical SteamVR OpenXR
advertises Vulkan 1.0–1.2, and OpenVR lacks the requested compositor interface.

The remaining heap GPU failure is inside an indirect graphics draw, bracketed by
native checkpoints. Control 42 (PID 48504) selected `MaterialTable` instead of
`BindlessMaterialTable` with all other diagnostic settings unchanged and reproduced
the same bracket at operation 67. This excludes the global sampler-array branch
for this reproduction. Control 43 (PID 50108) added existing counter diagnostics
to the bindless path; it still failed. Those logs report unavailable native counts
(`not-vulkan` / `gpu-readback-unavailable`), not zero generated commands. CPU-side
metadata reports one matching visible cube among 17 scene rows.

No concrete graphics pipeline/root defect was found in the last source audit.
The next discriminators are a separately selected monolithic-pipeline control via
`RuntimeEngine.Rendering.Settings.AllowShaderPipelines`, and validated native
indirect arguments / attachment state. Those follow-ups were **not run**. No shader
or accelerated path was disabled as a final workaround. The named isolated editor
is stopped, RenderDoc replay is closed, and changes are left uncommitted.

## Route inventory (A1)

| Route / source symbol | Before | Required and implemented target |
| --- | --- | --- |
| Desktop `VulkanDeviceContext.ResolveRequestedApiVersion` | Vulkan 1.3 | Vulkan 1.4; reject an older loader |
| OpenXR `GetRequestedVulkanRuntimeRequirements` and instance negotiation | Clamp to XR maximum | Require the XR range to contain 1.4; reject incompatible min/max |
| `TrySelectPhysicalDevice` | No mandatory 1.4 gate | Reject physical devices below 1.4 |
| `VulkanFeatureProfile.RequestedCapabilityTier` | `Vulkan13Production` | `Vulkan14Production`; old settings cannot lower the API requirement |
| `CreateConfiguredLogicalDevice` | Individual local-read/maintenance5 nodes | One Vulkan 1.4 aggregate enabling local-read, maintenance5 and maintenance6; preserve existing required 1.2/1.3 functionality |
| Ordinary/generated/native GLSL `VkShader.BuildCpuArtifact -> VulkanShaderCompiler.Prepare/CompilePrepared` | Implicit Shaderc target except task/mesh | Explicit Vulkan 1.4 / SPIR-V 1.6 |
| Task/mesh compiler route | Explicit Vulkan 1.3 / SPIR-V 1.6 | Vulkan 1.4 / SPIR-V 1.6, retain mesh/task capabilities |
| Prewarm/background/native visibility/auto-exposure compilation | Same compiler entry point | Same explicit target and artifact identity |
| ImGui direct `VulkanShaderCompiler.Compile` | Implicit target | Same explicit target |
| Editor/backend-neutral `VulkanShaderCrossCompiler` GLSL/HLSL | Implicit target | Shared explicit Vulkan target configuration |
| Disk shader artifacts | Cache schema 4 | Schema 5, API/SPIR-V/ABI and managed/native compiler identities |
| Explicit OpenGL GLSL | Driver compile/link, OpenGL 4.6 | Preserved driver compile/link route |

Descriptor indexing remains the default binding backend. Heap use requires
`VK_EXT_descriptor_heap`, enabled `VK_KHR_shader_untyped_pointers` and its feature,
native entry points and usable storage. Explicit unsupported heap requests fail.
Shader objects, mesh shaders and other accelerated extensions remain independently
gated. Vulkan 1.4 local-read depth/stencil and multisample properties are reported
separately from the core feature. Host image copy is not inferred from API alone.

## Findings and changes

- Heap pipelines require a null native layout; retain the logical pipeline layout
  for engine identity and snapshot heap mode independently of resource count.
- Root ABI preserves the ordinary 128-byte PushConstant prefix first, followed by descriptor
  index words. All heap constant/index updates use `vkCmdPushDataEXT`; the
  descriptor tail does not overwrite shader constants. Check `maxPushDataSize`.
- Heap state changes invalidate cached conventional descriptor bindings.
- The native descriptor mapping union was 48 instead of 56 bytes on Windows x64,
  corrupting mappings after the first array entry. Reserve the largest SDK union arm.
- Host descriptor array writes incorrectly supplied one destination range for an
  entire array. Supply a destination for each descriptor.
- Use the portable descriptor property sizes consistently, including texel buffers;
  reject invalid sizes, alignment and capacity instead of silently clamping.
- Preserve reserved heap prefixes and never publish a partially written binding.
- Heap storage explicitly requires coherent, host-visible, device-local memory.
  The old staging allocation had no upload consumer; it could publish descriptors
  that never reached the GPU. Unsupported storage now fails visibly.
- Published bindings are immutable and reused only for identical descriptor
  contents and resource generations. Changed contents receive fresh slots. Capture
  exact image/view/buffer/sampler generations in recorded commands, including the
  image behind an image view. Slots remain reserved until device teardown; arena
  exhaustion fails visibly. Reclamation/compaction is future phase E work.
- Heap graphics uniforms retain ordinary buffer descriptors with their concrete
  frame-slot offsets; dynamic descriptor-set offsets are not a heap mechanism.
- Frequency-owned uniform allocation also applies to heap programs. After the
  frame-owned range is published, refresh its native heap descriptor and captured
  lifetime closure. Otherwise a successful uniform write can leave the draw
  referencing an old range.
- Buffer descriptor publication checks the registered logical allocation size,
  address usage, range, overflow and `WholeSize` expansion. Mapped frame arenas
  carry device-address buffer usage and memory allocation flags in heap mode.
- Graphics pipeline libraries support heap flags and per-library stage mappings;
  library and final-link layouts are null. Cache keys include the binding mode.
- Secondary command buffers inherit the exact sampler/resource heap ranges and
  publish root data without rebinding those inherited heaps. The tracked execute
  wrapper binds matching heaps on the primary first, including dynamic UI, then
  invalidates primary cached state after execution. This resolves the successive
  11231/11238, 11308 and 11473/11474 validation findings without disabling
  secondary recording.
- Managed data buffers choose device-address allocation from the requested heap
  backend at creation time. Waiting for heap storage to become active omitted
  usage flags from early GPU material-table buffers and prevented indirect draws.
- Default pipeline metadata now explicitly orders the post-temporal forward,
  exposure, postprocess, final, AA/upscaling and presentation chain. Missing
  dependencies previously let the grid execute after the final image copy.
  The authored command-chain order itself is unchanged.
- Resize successor creation no longer waits for a current-frame scene that resize
  preflight prevents from recording. Successor completion still requires authored
  scene/UI/ImGui output; recovery presentations cannot release held ownership,
  and captured screen-space UI identity/readiness is checked before release.

## Shader compiler failure and resolution

Enabling SPIR-V 1.6 for ordinary stages exposed a native optimizer crash in
`VulkanAdvancedBuildDepthPyramid.comp`. Both the bundled Shaderc and SDK `glslc
-O` crashed on the retained source; unoptimized compilation and validation
succeeded. `spirv-opt -O --print-all` localized the invalid ID to simplification
of an `OpCopyLogical` of `XRAdvancedViewRecord`, followed by field extraction.
The record contains row-major matrix members.

`XR_ADV_LoadView` now loads its fields explicitly instead of copying the whole
storage-buffer struct. This preserves shader semantics and optimization and
avoids the defective optimizer path. The exact failing source, optimizer output,
and passing fieldwise-source experiments are retained under `compiler/` in the
evidence root. Both SDK and bundled optimized compilation then passed
`spirv-val --target-env vulkan1.4`; the live editor compiled the affected pass.
No compiler dependency or target was downgraded.

SPIR-V 1.6 compilation also emitted `DemoteToHelperInvocation`. The initial live
run correctly exposed its missing device enable through
`VUID-VkShaderModuleCreateInfo-pCode-08740`. Demote and terminate invocation are
now queried and required for this compiler profile, enabled once through either
the Vulkan 1.3 aggregate or its individual promoted nodes. Vulkan 1.4's uint8
index support similarly lives only in the 1.4 aggregate.

## Validation ledger

Evidence root: `Build/_AgentValidation/20260908-184611-vulkan14-ab/`.

- Loader 1.4.350; NVIDIA RTX 3090 API 1.4.341, driver 610.88. AMD integrated
  device reports 1.3.262 and is incompatible with the new Vulkan baseline.
- SDK headers: `K:/VulkanSDK/1.4.350.0/Include/vulkan/vulkan_core.h`.
  NVIDIA reports descriptorHeap and shaderUntypedPointers, plus maintenance5,
  maintenance6, local-read, depth/stencil local-read and multisample local-read.
- Bundled Silk.NET.Shaderc 2.23.0 native compiler compiled vertex, fragment,
  geometry, compute, task and mesh GLSL. All six headers are SPIR-V 1.6 and
  pass `spirv-val --target-env vulkan1.4`. Missing validator fails visibly.
- Direct reflection smoke against the built engine's `VulkanShaderCrossCompiler`
  compiled both GLSL and HLSL inputs to SPIR-V 1.6. Independent validation passed
  for both outputs and all 70 retained live SPIR-V modules. Scheduler labels
  (prewarm/background versus ordinary compilation) share the compiler entry
  point; the retained artifacts do not separately attribute every scheduler.
- `XRE_VULKAN_VALIDATE_SPIRV=1` opts into validation of generated and cached
  artifacts. Native compiler binary hash participates in disk cache identity.
- Initial integrated Vulkan project build: zero warnings/errors after fixing two
  native pipeline request constructor call sites.
- Isolated editor build after heap lifetime changes: zero warnings/errors.
- First explicit heap run: RTX 3090 successfully allocated coherent device-local
  sampler/resource heaps (131072 / 621056 bytes); queried maxPushDataSize=256.
  It exposed the dynamic-UBO promotion issue described above before successful
  scene submission; it is not accepted as an output validation run.
- The configured Advanced pipeline explicitly rejects its unimplemented advanced
  scene heap realization. This is an existing unsupported combination, not an
  automatic switch to descriptor indexing. Heap acceptance uses an explicitly
  selected `DefaultRenderPipeline` and the same scene for the indexing comparison.
- The original settings file contains the retired `UseAdvancedRenderPipeline`
  field. Controlled settings must specify `Rendering.RenderPipeline` explicitly;
  otherwise the current default is `AdvancedRenderPipeline`.
- Negotiation reflection smoke against the built assembly passed eight cases:
  desktop and XR 1.1–1.4 request exactly 1.4.0; XR max 1.3, XR min 1.5,
  incomplete/malformed XR constraints and Streamline min 1.5 reject clearly.
- OpenGL control run (PID 33556): OpenGL 4.6.0 / GLSL 4.60, NVIDIA 610.88.
  Viewed `mcp-captures/opengl/RenderPipeline_FinalPostProcessOutputTexture_20260908_192931.png`:
  lit orange cube and grid visible; all 6,220,800 RGB samples finite. The scene
  uses no imports/sky/probes, the Default pipeline, a size-2 cube at the origin
  (`#C84020`), and camera (3,2,5) looking at (0,0,0). Root user settings were
  left untouched. The capture is a reference, not a performance measurement.
- The next heap run exposed ten missing buffer-device-address-usage messages
  and two each of secondary heap inheritance VUIDs 11231/11238. Mapped frame
  arenas now include address usage/allocation flags for heap descriptors.
  Secondaries bind their heaps explicitly and therefore do not also inherit
  non-null heap bind infos. The next run eliminated those specific messages.
- Heap PID 20436 completed frame 16948, including 32 mesh draws, one compute
  dispatch, two swapchain writes and ImGui recording. A live BaseColor edit to
  `(0.08, 0.5, 0.9)` was read back and visibly changed the cube to blue;
  `194336.png` and `194438.png` show two different camera positions. However,
  the complete log contained ten `vkCmdDrawIndexed-None-11308` messages before
  the validation layer's duplicate limit suppressed further reporting. A later
  profiler counter of zero therefore did **not** establish a clean run.
- The grid was absent on both Vulkan indexing and heap, while visible on OpenGL.
  Its typed publisher declared View frequency but shader lowering generated a
  Material block. Correcting that declaration eliminated the observed 144-byte
  uniform schema mismatch in PID 15196; the grid remained absent, so this alone
  is not accepted as a complete visual fix.
- RenderDoc 1.44 captured indexing frame 4442. The saved EID 345 snapshot has
  no material marker, descriptor/binding dump, or target-resource identity, so
  it does not prove that EID 345 is the InfiniteGrid draw. Earlier claims that
  its target contains the grid or that it establishes an output-loss ordering
  are withdrawn. Live command inspection instead identifies InfiniteGrid as a
  TransparentForward draw; its actual bindings and target must be traced from
  that identified draw. `renderdoc/indexing-grid.rdc` and
  `indexing-grid-e345.png` remain diagnostic artifacts only. The
  RenderDoc-instrumented run also reports image-layout VUID 09600 and reduces
  multiview+mesh support in the layer; it is not a production
  capability/performance baseline.
- Live resize from 1920x1080 to 1751x1080 reproduced a circular wait in the
  predecessor recreation guard. Independent exact-model Sol review accepted
  moving the gate and identified the recovery/UI-identity guards now included.
- Fresh `GpuIndirectInstrumented` selection exposed a material-table buffer
  created without device-address usage. The new range/usage check failed visibly
  before recording the invalid descriptor. The common allocation policy now
  covers that startup ordering; runtime revalidation follows.
- GPU dispatch selection must exist when the Default pipeline is constructed.
  Changing only `GameStartupSettings.GPURenderDispatch` in an existing process
  does not rebuild its recorded command-chain choices. Controlled indirect runs
  use `GPURenderDispatch=true` plus
  `XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectInstrumented` at startup.
- Heap CPU control PID 43184 (build 11) completed frame 16,437 with 32 mesh draws,
  one compute dispatch and two swapchain writes. The isolated window was visibly
  inspected with scene and ImGui intact at 1920x1080, maximized to 2560x1369,
  then restored to 1920x1080. Both resize transitions completed. A BaseColor edit
  to `(0.08, 0.5, 0.9)` was read back and visibly changed the cube to blue.
  Captures `heap-control11/202512.png` and `202659.png` (full filenames retain
  the `RenderPipeline_FinalPostProcessOutputTexture_20260908_` prefix) show the
  change and new extent. Full startup/steady/shutdown logs contain no VUID,
  ERROR or device-loss matches. This validates those heap cases, but the grid
  remains absent from the actual desktop viewport and is still being traced.
- GPU indirect PID 33128 (build 11) passed the corrected raw buffer address-usage
  check, then reached `vkCmdDrawIndexedIndirectCount` with heap state invalidated
  by a later classic global material descriptor-set bind. VUID 11308 preceded
  device loss; buffer/memory teardown diagnostics after device loss are separate.
  Recording now avoids the classic global bind in heap mode and publishes the
  global texture array into the program's heap root, preserving stable material
  indices and exact resource generations. Runtime revalidation follows.
- Equivalent grid output, indirect submission and XR acceptance remain under
  investigation. No performance claim is made.

### XR availability

The active Windows OpenXR manifest is SteamVR's `steamxr_win64.json`. No Monado
runtime is installed in the repository's supported discovery locations. A
manifest does not expose the runtime's Vulkan version range. Actual headset
output is therefore not established by the negotiation smoke or the desktop run.
`VR.Mode=Emulated` can exercise engine stereo cameras and preview textures but
does not exercise OpenXR API calls, swapchains, compositor or API negotiation;
any emulated result must be labeled separately.

An actual OpenXR launch was attempted with descriptor indexing (PID 38584).
The loader reports 1.4.350; SteamVR's `xrGetSystem` returned
`ErrorFormFactorUnavailable` while selecting the Vulkan physical device. Engine
initialization failed visibly rather than selecting another API/renderer. A
connected headset/runtime system is required to finish physical XR acceptance.

With a connected SteamVR runtime, the actual OpenXR Vulkan requirements report
a maximum of Vulkan 1.2. The Vulkan 1.4 policy rejects that range visibly; it
does not clamp the device request or select another renderer. An OpenVR scene
attempt (PID 43656) obtained an OpenVR system interface but not
`IVRCompositor_027`; `UpdateDraw` previously dereferenced that missing compositor
in `WaitGetPoses`. OpenVR scene admission now requires both the system and
compositor interfaces before it activates render callbacks, so this runtime
state fails at startup with a compositor-specific diagnostic.

The emulated sequential-view run (PID 2796) completed frame 4143 with 87 mesh
draws, three compute dispatches and three view contexts, without VUIDs in its
log. Eye capture exposed a diagnostic lookup bug: the legacy two-pass renderer
uses `VRState.TwoPassLeftPipeline/RightPipeline`, while MCP inspected the empty
default instance on each eye viewport. Resource listing/capture now selects the
actual eye pipeline and enters a readback scope for that same instance. This
preserves resource-planner provenance; it does not make emulation an OpenXR test.

### Validation-switch correction

The todo originally named `XRE_VULKAN_SYNCHRONIZATION_VALIDATION`, which the
engine does not read. The actual switch is `XRE_VULKAN_SYNC_VALIDATION`.
Runs through build 16 had standard validation enabled, but did **not** enable
synchronization validation. Their zero-VUID results establish only that narrower
check. The todo and controlled environment files are corrected; synchronization
validation acceptance requires the subsequent runs. All 102 retained live SPIR-V
modules independently pass `spirv-val --target-env vulkan1.4`.

Build 15 completed with zero warnings/errors. Its GPU indirect run (PID 43104)
no longer emitted heap-state VUID 11308, but lost the device during submission
at frame 2083. Build 16 also compiled cleanly. GPU-assisted run PID 24216 reported
compute shared-memory races, then an internal GPU-AV submission error and native
process failure; it is not accepted as a successful validation run. The exact
shader and relationship to the non-instrumented device loss remain under review.

The following run, PID 33108, explicitly logged both standard and synchronization
validation plus EXT device-fault capture enabled. It still lost the device at
frame 1600 after the cube entered GPU indirect work, with no preceding VUID or
SYNC-HAZARD report. The profiler confirms fault capture is active; extracting the
native fault information and comparing descriptor indexing remain outstanding.
The GPU-assisted shared scan report alone does not justify a shader rewrite:
the candidate material-scatter shader has uniform scan/reduction barriers, and
GPU-AV's conflicting accesses name two different shader modules before its own
internal failure. This is inconclusive instrumentation evidence.

The global heap review identified and corrected another cache transition:
flushing dirty global texture descriptors must invalidate the cached heap image
prefix when those descriptors become sealed. Otherwise a later valid material
row could still see a cached placeholder. Backing image/buffer generations are
already retained transitively by the heap generation capture helper. This fix
is included in build 16, which completed with zero warnings/errors.

Build 19 compiled with zero warnings/errors. Its descriptor-indexing GPU control
(PID 39996) completed frame 1036 with one indirect draw, 26 compute dispatches,
31 mesh draws and two swapchain writes. The final capture was viewed and contains
the lit cube; standard and synchronization validation reported no errors. This
narrows the remaining indirect device loss to the heap path.

Heap PID 43872 then reproduced the loss with the newly connected first-loss
fault-capture hook. EXT counts and details both succeeded: an invalid GPU write
was reported at `0x26234000` with `0x1000` precision, plus 27 instruction-pointer
records and an 80,224-byte vendor binary. These diagnostics are retained under
`logs/vulkan-device-fault-report.log` and its companion binary. The faulting
buffer and descriptor/root cause are not yet identified.

The same build's retained frame-op trace is conclusive about the grid omission:
frame 1935 ends with post-process at index 119, final post-process at 120, FXAA
at 121, window output at 122, then `InfiniteGrid.FullscreenTriangle` at index
123 targeting ForwardPassFBO and labeled TransparentForward. The grid is
appended after presentation-source rendering despite its earlier pass label.
This is current command-recording evidence, not an inference from helper-method
source ordering. Missing metadata dependencies across framebuffer attachment and
sampled-texture identities left the terminal chain eligible ahead of transparent
forward work. Build 23's initial grid-to-exposure/postprocess edges moved the grid
earlier, but its trace still put final/FXAA/presentation before that partial chain.
The subsequent source correction explicitly connects the complete terminal chain,
including direct, FXAA, SMAA and TSR output branches; runtime verification is pending.

Build 23 succeeded with zero warnings/errors. CPU heap PID 32576 completed frame
18360 after maximize, and restored to 1920×1080. Viewed captures show the cube and
grid, and an orange-to-blue material edit is visible. Standard and synchronization
validation were both enabled. No steady-state validation errors occurred; shutdown
reported duplicate destruction of the two heap buffers and their shared allocator
memory, plus remaining device children. Investigation traced the duplicate release
to the generic allocation sweep preceding the heap owner's teardown. This is
recorded separately from successful steady-state resize/material behavior.

Heap GPU PID 37108 reproduced device loss at frame 1315 with expanded NVIDIA
diagnostics. Its report contains an instruction-pointer fault at `0x2000AC5D0`
and an unclassified address, rather than the previous invalid-write address.
The address-binding event list was empty because both debug messenger registrations
omitted the address-binding message-type bit and informational severity; both are
corrected for the next capture. The callback processes address events before normal
message filtering, as required by the
[address-binding callback contract](https://docs.vulkan.org/refpages/latest/refpages/source/VkDeviceAddressBindingCallbackDataEXT.html).
Cache-miss descriptor buffer-address publications are retained in
`logs/heap24-vulkan-descriptor-heap-bda.log`. This run does not identify the offending
storage binding or satisfy indirect-rendering acceptance.

Emulated sequential XR PID 28056 completed frame 2147 with 67 mesh draws and three
view contexts. The updated eye readback scope returned successful captures, but
both viewed images are black. Native source identity still needs verification:
the generic screenshot path can fall back to a presentation target, and the
reported format differs from the authored external eye texture. Left-eye preview also
reports a reflected auto-uniform block without an allocated buffer. The earlier
black internal pipeline textures were not valid eye-output evidence: this mode
renders into external eye targets. Actual output and the uniform failure remain
under investigation; successful readback alone is not rendering acceptance.

Build 27 passed with zero warnings/errors after correcting the diagnostic request
field used by the debug messenger. Its initial frame trace now orders grid,
exposure, postprocess, final postprocess, FXAA and window output correctly.
GPU PID 7572 nevertheless lost the device with an invalid write at `0x70D7FF000`
(`0x1000` precision). The descriptor BDA publication history has no overlapping
range; the 128-event allocation ring overflowed. Build 29 (zero warnings/errors)
expanded this diagnostic ring to 1024 and retained capture serials. PID 656
reproduced the same address with all 759 events retained and zero overflow.
The complete evidence is in `logs/heap29-*`; root-cause correlation is pending.

Emulated PID 39288 still reports the eye-preview uniform failure after the first
allocation-order change. The reusable refresh path bypassed that caller; uniform
publication now ensures missing reflected views at the shared write entry point.
Its correctness under native command reuse is being audited. Current native
recording logs contain cube and grid work for both distinct external eye targets,
so a smaller stale trace that omitted the cube does not establish a culling bug.
No XR culling or provenance relaxation was made from that observation.

RenderDoc cannot capture this driver's heap path: its layer hides
`VK_EXT_descriptor_heap`, and PID 44976 correctly failed explicit heap admission.
The separately selected indexing/emulated control, PID 44024, produced a retained
frame capture with visible cubes in both external eye attachments (EIDs 31 and
160). This proved that the earlier black MCP images were not eye-output evidence.
Two-pass XR calls the pipeline directly and never assigns the viewport's
`LastRenderedTargetFBO`. MCP now explicitly selects the matching runtime left/right
eye target under the existing accepted planner scope.

Heap emulated PID 10348 then validated that correction without RenderDoc: both
viewed PNGs report `R8G8B8A8Unorm` and show the requested orange cube with horizontal
eye separation. Moving the cube to `(1, 0.5, -5)` visibly moves it in a new left-eye
capture. Frame 5086 completed with 67 mesh draws, one compute dispatch and three
contexts; descriptor failure summary is empty. Full startup, steady-state and
shutdown logs contain zero VUID/SYNC-HAZARD/ERROR lines. The preview auto-uniform
and heap double-destruction failures are resolved in this run.

Build 32 passed with zero warnings/errors. CPU heap PID 34352 completed frame
1074 and its viewed final postprocess texture contains the lit cube and grid.
Its trace ends in exposure, postprocess, final postprocess, FXAA and window output
in the intended order. The grid overlay crossing the cube also occurs in the
OpenGL control and is not newly introduced by this change.
The complete CPU34 log is clean during startup and rendering, but shutdown reports
one leaked `VkPipelineLayout` (`0x5080000000508`, VUID 05137). Screenshot readback
does not create pipeline layouts and its cleanup path is complete; this is not
attributed to screenshot resources without owner evidence. It is recorded
separately from the steady-state heap acceptance results.

The heap indirect failure remains unresolved. The independent Sol review's shared
compute scratch hypothesis was tested by holding one per-program transaction
across build and consumption in direct and reusable recording, with opt-in
contention tracing. PID 24856 still lost the device at frame 1813; no contention
events occurred. This hypothesis is falsified for the reproduction, although the
shared scratch now has coherent ownership. The new invalid write at `0x2B1E9000`
also has no overlap in the complete 757-event allocation history. Lack of overlap
does not rule out a large shader-derived out-of-bounds offset. Native checkpoint
insertion/retrieval is being added behind the existing diagnostic flag to identify
the failing operation; previously that feature was only enabled and reported.
Build 35 caught a missing `System.Runtime.InteropServices` import in the new
checkpoint diagnostics (two errors, zero warnings). The initial bounded token
table also needed stable operation identities so startup recordings cannot exhaust
all markers before the reproduction. No runtime result is claimed for that build.
Build 36 passed with zero warnings/errors. PID 27360 reproduced an invalid write
at `0x42184000` with checkpoint support enabled, but the queue returned no records.
The explicit diagnostic-config variant, PID 38404, returned two TOP/BOTTOM records
with null tokens; these cannot be attributed to an operation. Actual native marker
insertion is being checked before drawing conclusions from those records.

A separate compute-uniform audit found no address-offset loss. MaterialScatter's
64-byte block at binding 104 uses a dedicated buffer per image/dispatch, making
offset zero correct. Mesh arena descriptors later publish nonzero resolved offsets
(for example 64, 384, 448 and 512); the initial zero-offset entries are placeholder
views, not the final per-draw payload. No shader or uniform change was made from
that rejected hypothesis.

Build 39 passed with zero warnings/errors after fixing a diagnostic-only C#
stack-allocation declaration caught by build 38. Its loss report proved 1178
native checkpoint calls and 134 immutable identities, with no capacity exhaustion,
but still returned null driver tokens. Build 40 (zero warnings/errors) extended
the query to every distinct graphics, secondary graphics, compute and transfer
queue and retained the identity table once. PID 51080 then returned attributable
checkpoints: graphics TOP at **IndirectDraw after**, BOTTOM at **IndirectDraw
before**, both pass 1 / operation 81, first recorded in frame 1275. Other queues
returned zero records. The indirect draw targets `DeferredGBufferFBO`, uses count
buffer submission and allows 64 commands. This brackets the failure in that draw;
the preceding compute sequence completed. The investigation now targets its
graphics pipeline, heap state and material/vertex bindings rather than attributing
the fault to the later steady-state compute trace. Shader opcode inventory found
two valid `OpCopyLogical` instructions in BVH construction but none in Scatter;
no shader change was made merely because those opcodes were present.

## Observed configuration matrix

The source-level before/after targets are in A1 above. These observations use
the uninstrumented NVIDIA device with validation enabled; RenderDoc's altered
capability exposure is excluded. Feature enablement alone is not output proof.

| Explicit configuration | Negotiation / enabled contract | Observed output / limitation |
| --- | --- | --- |
| OpenGL / Default / CPU direct | OpenGL 4.6, GLSL 4.60 | Lit cube and grid; driver compile/link retained |
| Vulkan / Default / indexing | Requested 1.4, device 1.4.341; SPIR-V 1.6 | Lit cube and UI; GPU indirect control 19 passes; shared grid ordering corrected during this investigation |
| Vulkan / Default / heap / CPU direct | Same API/target; native heap and untyped pointers enabled | Material edits, compute, UI and resize pass; final grid ordering/output validated in PID 34352 |
| Vulkan / Default / heap / GPU indirect | Same API/target; indirect-count capability enabled | Controls 51/52 render cube/sphere, material edits and movement; full validation clean |
| Vulkan / Advanced / heap | Same API/target | Advanced scene heap realization explicitly unsupported; no backend substitution |
| Vulkan / heap / emulated sequential XR | Same API/target; multiview remains enabled | Both actual eye targets viewed; cube movement verified; PID 10348 full logs clean |
| Vulkan / heap / Monado OpenXR | Runtime broad XrVersion range contains 1.4; true single-pass stereo | Both actual eye previews viewed; material/movement changes verified; final smoke 53 passes with clean validation and teardown |
| Vulkan / physical OpenXR / SteamVR | Runtime advertises 1.0–1.2 | Rejected because range excludes 1.4; headset output unavailable on this runtime |
| Vulkan / physical OpenVR / SteamVR | System initializes; compositor interface absent | Admission guard rejects missing compositor before render callbacks |

Dynamic rendering, synchronization2, timeline semaphores, buffer device address,
indirect count, graphics pipeline libraries and multiview remain enabled in the
heap desktop run. Maintenance5/6 and dynamic-rendering local-read are required
through the 1.4 aggregate. Depth/stencil and multisample local-read properties are
reported true on this device. Mesh/task support remains enabled with native
commands loaded, but the control scene does not exercise meshlet dispatch.
Shader objects remain enabled but unused because that backend is not implemented;
ray tracing and VRS are not exercised or newly disabled by these changes.

## Diagnostic workflow

Set `XRE_VULKAN_VALIDATE_SPIRV=1` to validate generated and cached Vulkan modules
with `spirv-val --target-env vulkan1.4`; `spirv-val` must be on PATH. Set
`XRE_VULKAN_SHADER_DIAGNOSTICS_DIR` to a directory under the task evidence root
to retain hash-named GLSL inputs before native compilation and SPIR-V modules
during validation. This is opt-in diagnostic I/O; it is not enabled in production.

Use a named isolated editor session with validation and synchronization validation
enabled. `capture_viewport_screenshot` captures the last framebuffer attachment
without HDR tonemapping; it does not capture the desktop window. Inspect named
pipeline textures and their pixel statistics before treating a white/black PNG
as evidence about presentation. Record terminal readiness errors from
`get_render_profiler_stats` as failures even if MCP remains responsive.

## Specification references

[Descriptor heaps](https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_descriptor_heap.html),
[heap properties](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceDescriptorHeapPropertiesEXT.html),
[push data](https://docs.vulkan.org/refpages/latest/refpages/source/vkCmdPushDataEXT.html).

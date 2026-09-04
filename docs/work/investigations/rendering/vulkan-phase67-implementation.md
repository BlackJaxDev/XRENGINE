# Vulkan Phases 6, 7, and 7R implementation

Last updated: 2026-09-04

## Objective and baseline

Implement the master TODO's asynchronous OpenXR ownership/lifecycle and Advanced rendering requirements, including the review remediation in Phase 7R. The user authorized implementation on 2026-09-04. Baseline revision: `55f46a4e335a03b923883b600d308313cd3efa81`.

The source review found incomplete ownership of accepted asynchronous submissions, unenforced in-flight and retired-generation limits, unsafe session teardown, uncompilable shading shaders, incompatible GPU records, unbounded classification/froxel consumers, and unexecuted shading/late/post stages. Contract declarations and managed builds do not establish runtime completion.

## Work and evidence

- In progress: receipt-based XR ownership and enforced admission, followed by session-safe replacement and teardown.
- In progress: executable classification/native shading and integration with existing Advanced late/post command primitives.
- Compiler checks passed for the current mono native implementation; live validation is in progress. GPU captures, allocation/lifetime evidence, stereo/offscreen/editor acceptance, and supported runtime/hardware acceptance remain open.
- New tests remain subject to the repository's runtime-first and explicit-clearance policy. Existing diagnostic tests may be used when necessary to reproduce an active defect.

Local evidence root: `Build/_AgentValidation/20260904-124955-vulkan-phase67-implementation/`. Required findings and commands will also be recorded here so this document does not depend on disposable evidence.

## Validation baseline

The preceding review passed `dotnet build XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -v minimal -p:XREngineUseExistingNativeBridges=true` with zero warnings/errors. The native bridge build failed on FileTracker access, and the test runner failed before discovery because its results directory was inaccessible. Neither is a feature acceptance result.

Shader compilation with the engine-generated Vulkan preamble passed reconstruction/classification/froxel compilation, but failed `ShadeNativeOpaque.comp` and `ShadeBackground.comp`. No live rendering or headset result has been established for this implementation yet.

## Implementation checkpoint: native compute and XR ownership

The Vulkan managed build now passes with zero warnings/errors using the command above. Native compute preparation seals the exact graph generation, image views, descriptor family, resident scene and pipeline generations. Recording now dispatches classification, per-kernel indirect argument construction, froxel construction, background initialization, native opaque shading, and a GPU overflow repair dispatch with explicit buffer/image dependencies. The late/post command chain now invokes transparency, temporal accumulation, motion blur, DoF, bloom, atmosphere/fog, final composition, and AA/upscaling commands; their visual output still needs validation.

The material shader contract is generated from the actual CPU layout offsets. Visibility identities resolve through draw/material/kernel generation handles, classification supports all 128 admitted kernel slots with bounded independent memberships, and froxel storage derives from extent and view count. Froxel index exhaustion marks affected cells for a conservative GPU light-list repair. Masked coverage now reads the actual alpha cutoff and base alpha rather than unrelated constant words. Real shadow/AO/GI/decal consumption and layered view execution are not yet complete.

`glslc --target-env=vulkan1.3` passed all 14 permutations: mono and array variants of `ClassifyTiles.comp`, `BuildClassificationIndirect.comp`, `BuildFroxels.comp`, `ShadeNativeOpaque.comp`, `ShadeBackground.comp`, and `VisibilityRasterMasked.frag`, plus `ReconstructionReference.comp` as the control. Sources used the freshly generated `AdvancedShaderAccessLibrary.BuildPreamble` with set 3 descriptor indexing, canonical include resolution, and the engine layout defines. Array compilation establishes syntax/layout viability only; the Vulkan runtime still rejects unimplemented layered output.

XR ordinary/paired/parallel-eye submissions reserve bounded tracker capacity before recording, prepare ownership before native submit, and commit the exact accepted semaphore/value in the common submit authority. Prepared inputs and uploads transfer to tracker retirement even when a later outward operation fails. Temporary mirror commands now have a distinct retirement payload; remaining mirror/SPS callers and parent/child teardown are under implementation. These source changes have not yet passed the runtime lifecycle matrix.

## Live validation setup

Named isolated session: `phase67-native`, created under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260904-132443-phase67-native/`. Process overrides select Vulkan, Advanced Required, Desktop, and Vulkan synchronization validation. `XRE_UNIT_TEST_USE_ADVANCED_RENDER_PIPELINE=1` was added so this selection does not require modifying saved world settings.

The first isolated build failed before launch: both configured NuGet audit endpoints were unavailable, and `OscCore` treats NU1900 as an error. The same isolated artifacts directory is being rebuilt with `-p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true -p:XREngineUseExistingNativeBridges=true`; this invocation-only adjustment does not change package versions or repository audit policy. No live image has been accepted at this checkpoint.

## Live validation findings and corrections

The isolated editor build subsequently passed with zero warnings/errors. Incremental validation uses `dotnet build XREngine.Editor/XREngine.Editor.csproj --configuration Debug --no-restore --artifacts-path Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260904-132443-phase67-native/artifacts -p:Platform=AnyCPU -p:XREngineUseExistingNativeBridges=true -p:UseSharedCompilation=false /nodeReuse:false`. The sandboxed launch could not create its HTTP listener; launching the same named session through the approved elevated execution path established MCP readiness. Only this named session is stopped/restarted.

Successive Vulkan runs exposed these concrete blockers before native scene submission:

1. Managed resource realization rejected `BloomBlurTexture`: five declared mips but one produced mip. Late/post factories now derive dimensions, layers and mip ranges from their immutable resource profile; the auto-exposure and depth-history formats were also aligned with their declarations.
2. Depth/stencil view aspect declarations disagreed with the actual views. The declarations now specify depth or stencil explicitly, and depth-peel depth images no longer request unsupported depth storage-image usage.
3. Advanced stage ordinals collided with the classic mesh-pass collection indices. The graph merged attribute reconstruction with late raster depth writes and correctly rejected the feedback hazard. Advanced graph nodes now use synthetic graph identities with named dependencies, independent of mesh collection numbers.
4. The graph then reached exact scene-descriptor publication, which rejected canonical texture `1:1` indefinitely. Cold image wrappers were inspected without triggering their existing readiness/upload path. Publication now prepares the exact image before rechecking readiness and reports the source name, dimension, generations and wrapper readiness if it remains unavailable.

The graph-fix and texture-fix editor builds both passed with zero warnings/errors. The next runtime attempt is in progress; recovery-background presentation is not accepted as a rendered scene. No screenshot or GPU frame has yet passed visual acceptance.

XR source integration now covers immutable generation-bearing admission tickets, full ordinary/paired/parallel/SPS/mirror ownership, real frame/predicted-display metadata, and profiler completion at proven timeline retirement. In-session resize retains the parent session; terminal teardown returns a failure/defer result while child GPU ownership remains. RuntimeRecommended dimensions retain the safe Monado refresh/reprobe policy. These changes pass narrow rendering/Vulkan builds and are undergoing independent lifetime review; Monado/hardware acceptance remains open.

Typed canonical texture descriptors now include 2D arrays and cubes, with independently validated default sampler handles. Shadow publication and native sampling are being integrated; the shadow ABI now carries explicit depth conventions, moment-filter parameters and rendered depth/cascade ranges rather than relying on live camera state.

## Native dispatch and ownership checkpoint

The original saved skinned world reached stable-bin sealing and failed because the prepared payload referenced GPU-deformed vertex offsets while the canonical geometry described immutable bind-pose vertices. The implementation must retain a frame-local deformation overlay and exact GPU output ranges; changing the canonical geometry or binding static vertices would be incorrect. This repair is in progress.

A controlled three-mesh static OBJ fixture, held only in the isolated evidence directory, reached actual native compute dispatch with Vulkan synchronization validation. That run reported VUID-VkComputePipelineCreateInfo-layout-10069 and VUID-vkCmdPushConstants-offset-01795 because the common pipeline layout exposed 16 push-constant bytes while native compute uses 64. It also reported VUID-vkCmdDispatchIndirect-buffer-02709 because the classification dispatch buffer lacked indirect-buffer usage. The common compatible push range is now 128 bytes and classification arguments use DispatchIndirectBuffer. These changes require a fresh GPU run; the static fixture is an isolation aid, not a production fallback.

Independent XR ownership review identified six further defects: accepted-submit publication occurred too late; rejected preregistered uploads lacked a single settlement authority; retirement could repeat already-released components; partial swapchain creation was published before complete enumeration; direct batch calls bypassed checked acquire/release accounting; and teardown discarded drain failures. The corresponding corrections now pass the narrow Vulkan build with zero warnings/errors. A NUL-filled worktree copy of OpenXRAPI.RuntimeStateMachine.cs was restored from its intact staged copy and the scoped teardown corrections were reapplied. No Monado or hardware runtime acceptance is inferred from these source/build results.

Native shadow records now have a canonical 272-byte ABI, including rendered depth/cascade ranges, moment parameters, depth conventions, and the rendered point-light origin/far distance. Contiguous shadow groups cannot be relocated by generic compaction. Point/spot atlas sampling uses completion-stamped snapshots matched against allocation identity, content generation, and rendered frame; missing or legacy-only sources remain explicitly nonresident. Native shading consumes the exact texture/default-sampler generations and supports atlas PCF, VSM, EVSM2/4, cascade selection/blending, and radial point depth. All 14 mono/array shader compiler permutations pass after these changes; visual correctness remains unverified.

A layered R32ui AdvancedShading.ShadingDiagnostics output is wired at native set 1 binding 18. Bits 0–7 encode EAdvancedShadowFallbackReason, bits 8–15 encode shadow visibility from 0 to 255, and bits 16/17/18 identify invalid reconstruction, invalid material layout, and required classification overflow. The immutable request captures the selected shading debug view. This replaces inference from a magenta image with a captureable reason, while retaining visible failure output.
## Steady-frame capture and sampler admission correction

The static fixture was captured from two camera positions; both saved images were solid magenta. HDRScene was zero and the shading-diagnostics image had no native writes. A RenderDoc 1.41 frame (19408) contained Bloom, exposure, post composition, TSR and editor overlay work, but no native visibility/classification/opaque-shading dispatch. This is a failed frame, not acceptance.

The native stages were rejected in log_general.log, rather than log_rendering.log: canonical sampler 2:1 had no material binding that could revalidate its source. The validator already checked each texture's strong source, content generation and default-sampler state, but discarded that ownership evidence and required every sampler to appear in a material. Global shadow textures legitimately retain samplers without a material. Validation now records each successfully revalidated texture's generation-resolved default sampler before adding material witnesses; unowned or changed sources still fail.

The sampler, native-deformation binding, temporal scheduling and diagnostic-readback changes pass the isolated editor build with zero warnings/errors (99.10 seconds). The next static GPU run is PID 10912, started 2026-09-04 16:26:52 PDT. This build result does not close visual acceptance.

Diagnostic image readback also produced VUID-VkImageMemoryBarrier-oldLayout-01208: a planner-owned storage image lost its usage metadata, so an undefined prior layout restored as ColorAttachmentOptimal without attachment usage. BlitImageInfo now carries the exact physical image usage, resolves its submitted layout, and chooses a legal restore layout. GPU revalidation is pending. RenderDoc additionally reported host-access stage VUIDs during capture; application versus capture-layer ownership still needs isolation. The Vulkan host pseudo-stage is distinct from all queue commands ([Vulkan pipeline-stage specification](https://docs.vulkan.org/refpages/latest/refpages/source/VkPipelineStageFlagBits.html)); these errors are not dismissed as harmless.

RenderDoc was loaded only into the named session with ENABLE_VULKAN_RENDERDOC_CAPTURE=1 and DISABLE_VULKAN_RENDERDOC_CAPTURE_1_44=1, avoiding the two installed capture layers. The target-control helper verified the owned process ID and retained the connection until capture completion. Capture, pass inventory and validation messages are saved under the current evidence root. The replay session and editor were closed before rebuilding.

Further XR review caught an accepted receipt capturing timeline value zero before reservation. The common native-submit gateway now marks acceptance immediately after successful queue submission and commits its patched exact timeline value before telemetry. Remaining findings involve duplicate cleanup after registration, failed child destruction/rollback retention, and teardown result propagation; those are still being corrected. A clean build is not evidence that these ownership paths or headset runtime scenarios pass.

## User-reported outcomes

No implementation attempt has yet been reported working or failing by the user.

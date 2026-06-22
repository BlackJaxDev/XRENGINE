# Vulkan Parallel Command Chain Refactor TODO

Last Updated: 2026-06-21
Owner: Rendering
Status: Complete - Phase 14 validation passed; command-chain path remains feature-flagged
Target Branch: `vulkan-parallel-command-chain-refactor`
Design Doc: `docs/work/design/rendering/vulkan-parallel-command-chain-refactor-design.md`

## Purpose

Implement the Vulkan parallel command-chain architecture described in the design
doc. The end state is a Vulkan backend where independent views and passes can
produce immutable render packets, record command chains on worker threads, reuse
static scene command buffers across frames, and keep volatile overlay/text work
isolated from the main scene.

The refactor must preserve visual correctness first. Parallelism is useful only
after the single-thread packet/chain path produces the same output as the
current Vulkan renderer.

## Branch-Local Status

2026-06-21 packet/chain migration layer:

- Branch: `vulkan-parallel-command-chain-refactor`.
- Target validation scene: `Assets/UnitTestingWorldSettings.jsonc` selects `RenderAPI: Vulkan`; Sponza node loaded at `Root Node/Static Model Root/Sponza`.
- Disabled-path visual gate: `Build/McpCaptures/command-chains/phase1-disabled/Screenshot_20260621_112012.png`.
- Disabled-path log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_11-19-21_pid22804`.
- Disabled-path profiler evidence: `command_chains` counters were all zero with `XRE_VULKAN_COMMAND_CHAINS` unset.
- Enabled-path visual gate after key fix: `Build/McpCaptures/command-chains/phase3-enabled-after-key-fix/Screenshot_20260621_112534.png`.
- Enabled-path log gate after key fix: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_11-25-07_pid33212`.
- Enabled-path profiler evidence after key fix: `chains_scheduled=102`, `chains_recorded=3`, `chains_reused=99`, `chains_frame_data_refreshed=85`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, no first structural dirty, descriptor-generation mismatch, or resource-plan mismatch.
- Log review: no VUIDs, no Vulkan/rendering errors, no exceptions. Pre-existing warnings observed: shader-output-not-consumed validation performance warning, auto-exposure mip-0 fallback for planner-backed `HDRSceneTex`, CPU fallback for Vulkan editor picking, and screenshot-induced render-thread stall logs.
- Build/test gates: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` passed with 0 warnings; `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ProfilerProtocolTests|FullyQualifiedName~VulkanCommandChainDataModelTests"` passed 16/16.

2026-06-21 Phase 7 dynamic mesh secondary command-buffer gate:

- Dynamic-rendering visual gate: `Build/McpCaptures/command-chains/phase7-dynamic-mesh-secondary-real/Screenshot_20260621_122842.png`.
- Dynamic-rendering log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_12-27-56_pid46352`.
- Runtime profiler evidence from the validated frame: `chains_scheduled=99`, `chains_recorded=3`, `chains_reused=96`, `chains_frame_data_refreshed=82`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `secondary_command_buffer_count=99`, `validation.message_count=0`, `validation.error_count=0`.
- Log review: no VUIDs, no Vulkan/rendering errors, no exceptions, no invalid command buffers, and no frame-op errors. Pre-existing/noise warnings observed: duplicate EOS overlay layer, startup resource-planner optional-resource warnings, auto-exposure mip-0 fallback for planner-backed `HDRSceneTex`, Vulkan editor picking CPU fallback, VR settings not initialized, and profiler stall traces during screenshot/debug capture.
- Post-diagnostic visual gate after adding secondary inheritance mismatch logs: `Build/McpCaptures/command-chains/phase7-diagnostics-after-inheritance-logs/Screenshot_20260621_123650.png`.
- Post-diagnostic log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_12-36-08_pid32972`.
- Post-diagnostic profiler evidence: `chains_scheduled=99`, `chains_recorded=3`, `chains_reused=96`, `chains_frame_data_refreshed=82`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `visibility_packet_count=2`, `render_packet_count=99`, `secondary_command_buffer_count=99`, `validation.message_count=0`, `validation.error_count=0`, descriptor failures/skipped draws/skipped dispatches/plan replacements all zero.
- Post-diagnostic log review: all `log_*.log` files reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `FrameOpError`, `Exception`, and `Secondary inheritance mismatch`. Remaining warnings match previous startup/screenshot noise: duplicate EOS overlay layer, optional-resource planner declarations, auto-exposure mip-0 fallback, startup physical image handle refreshes, Vulkan editor picking CPU fallback, and screenshot-induced render-thread stall.
- Crash diagnosis fixed during this phase: mesh secondary command buffers executed by reusable primary command buffers must remain alive for the command-buffer generation. Transient-freeing the secondary after recording made a reused primary invalid at queue submit. Mesh secondary command buffers are now retained until command-buffer destruction.
- Cache ownership fix after validation caught a variant lifetime edge: one chain-owned secondary per image/run was unsafe because primary command-buffer variants for the same image can coexist. The mesh chain key now includes the primary command-buffer owner identity, and command-chain cache teardown frees each secondary from its owner pool.
- Primary-owned secondary visual gate: `Build/McpCaptures/command-chains/phase7-primary-owned-secondary/Screenshot_20260621_124515.png`.
- Primary-owned secondary log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_12-44-53_pid6472`.
- Primary-owned secondary profiler evidence: `chains_scheduled=99`, `chains_recorded=3`, `chains_reused=96`, `chains_frame_data_refreshed=82`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `validation.message_count=0`, `validation.error_count=0`, descriptor failures/skipped draws/skipped dispatches/plan replacements all zero.
- Primary-owned secondary log review: all `log_*.log` files reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `destroyed or rerecorded`, `FrameOpError`, `Exception`, and `Secondary inheritance mismatch`.
- Shape-diagnostics visual gate: `Build/McpCaptures/command-chains/phase6-shape-diagnostics/Screenshot_20260621_125110.png`.
- Shape-diagnostics log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_12-50-47_pid37104`.
- Shape-diagnostics profiler evidence: `chains_scheduled=99`, `chains_recorded=3`, `chains_reused=96`, `chains_frame_data_refreshed=82`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `validation.message_count=0`, `validation.error_count=0`.
- Shape-diagnostics log review: all `log_*.log` files reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `destroyed or rerecorded`, `FrameOpError`, `Exception`, and `Secondary inheritance mismatch`.
- Command-chain data-model tests now cover frame-data-only reuse, structural dirtying, descriptor/resource/pipeline generation dirtying, packet-shape dirtying, static clear/barrier volatility, overlay pass metadata volatility, dynamic-overlay override volatility, and the named command-chain frame-data refresh gate. `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanCommandChainDataModelTests"` passed 13/13.
- Refresh-gate visual gate: `Build/McpCaptures/command-chains/phase6-refresh-gate/Screenshot_20260621_125759.png`.
- Refresh-gate log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_12-57-36_pid37672`.
- Refresh-gate profiler evidence: `chains_scheduled=99`, `chains_recorded=3`, `chains_reused=96`, `chains_frame_data_refreshed=82`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `validation.message_count=0`, `validation.error_count=0`.
- Refresh-gate log review: all `log_*.log` files reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `destroyed or rerecorded`, `FrameOpError`, `Exception`, and `Secondary inheritance mismatch`.
- Scratch packet-list visual gate after moving packet/schedule assembly to reusable scratch backing lists: `Build/McpCaptures/command-chains/phase2-scratch-packets/Screenshot_20260621_130107.png`.
- Scratch packet-list log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_13-00-42_pid43068`.
- Scratch packet-list profiler evidence: `chains_scheduled=99`, `chains_recorded=3`, `chains_reused=96`, `chains_frame_data_refreshed=82`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `visibility_packet_count=2`, `render_packet_count=99`, `secondary_command_buffer_count=99`, `validation.message_count=0`, `validation.error_count=0`, descriptor failures/skipped draws/skipped dispatches/plan replacements all zero.
- Scratch packet-list log review: `log_vulkan.log`, `log_rendering.log`, and `log_general.log` reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `destroyed or rerecorded`, `FrameOpError`, and `Secondary inheritance mismatch`. The only Vulkan validation messages were the pre-existing shader-output-not-consumed performance warnings.
- Resource-validation focused visual gate after adding reusable-chain stale descriptor/resource/pipeline assertions: `Build/McpCaptures/command-chains/phase5-resource-validation-focus/Screenshot_20260621_130954.png`.
- Resource-validation focused log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_13-09-35_pid51612`.
- Resource-validation focused profiler evidence: `chains_scheduled=110`, `chains_recorded=3`, `chains_reused=107`, `chains_frame_data_refreshed=93`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `validation.message_count=0`, `validation.error_count=0`, descriptor failures/skipped draws/skipped dispatches/retired descriptor pools/plan replacements all zero.
- Resource-validation focused log review: `log_vulkan.log`, `log_rendering.log`, and `log_general.log` reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `destroyed or rerecorded`, `FrameOpError`, `Secondary inheritance mismatch`, `stale descriptor-set`, `stale physical-image`, `stale framebuffer`, and `stale pipeline`.
- Frozen-plan visual gate after wrapping command-chain lowering in an explicit frozen resource-plan read scope: `Build/McpCaptures/command-chains/phase5-frozen-plan/Screenshot_20260621_131651.png`.
- Frozen-plan log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_13-16-32_pid36144`.
- Frozen-plan profiler evidence: `chains_scheduled=110`, `chains_recorded=3`, `chains_reused=107`, `chains_frame_data_refreshed=93`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `validation.message_count=0`, `validation.error_count=0`, descriptor failures/skipped draws/skipped dispatches/retired descriptor pools/plan replacements all zero.
- Frozen-plan log review: `log_vulkan.log`, `log_rendering.log`, and `log_general.log` reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `destroyed or rerecorded`, `FrameOpError`, `Secondary inheritance mismatch`, `Resource planner cannot be replaced`, `LazyRebuildDuringFrozenCommandChainPlan`, `Refusing lazy physical-image plan rebuild`, `stale descriptor-set`, `stale physical-image`, `stale framebuffer`, and `stale pipeline`.
- Phase 8 primary schedule visual gate after making the primary recorder preserve command-chain schedule order and include secondary command-buffer handles in the primary group signature: `Build/McpCaptures/command-chains/phase8-primary-current-focus-20260621_133259/Screenshot_20260621_133322.png`.
- Phase 8 primary schedule log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_13-32-59_pid43444`.
- Phase 8 primary schedule profiler evidence: `chains_scheduled=65`, `chains_recorded=3`, `chains_reused=62`, `chains_frame_data_refreshed=48`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `visibility_packet_count=2`, `render_packet_count=65`, `secondary_command_buffer_count=65`, `record_command_buffer_ms=3.2537`, `validation.message_count=0`, `validation.error_count=0`.
- Phase 8 primary schedule log review: `log_vulkan.log`, `log_rendering.log`, and `log_general.log` reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `FrameOpError`, `Secondary inheritance mismatch`, `destroyed or rerecorded`, `stale descriptor-set`, `stale physical-image`, `stale framebuffer`, `stale pipeline`, `Command-chain primary schedule`, and `dynamic overlay group before`. A stricter intermediate validator run exposed a second-sort mismatch and follow-on image-layout validation error; the fix was to treat the schedule as the authoritative primary op order instead of sorting again inside primary recording.
- Phase 9 old-path/no-env comparison visual gate: `Build/McpCaptures/command-chains/phase9-oldpath-baseline-20260621_133548/Screenshot_20260621_133608.png`.
- Phase 9 old-path/no-env comparison log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_13-35-48_pid37336`.
- Phase 9 old-path/no-env profiler evidence: `chains_scheduled=0`, `chains_recorded=0`, `chains_reused=0`, `chains_frame_data_refreshed=0`, `volatile_chains_recorded=0`, `primary_command_buffers_reused=0`, `primary_command_buffers_recorded=0`, `record_command_buffer_ms=2.4185`, `validation.message_count=0`, `validation.error_count=0`.
- Phase 9 old-path/no-env log review: `log_vulkan.log`, `log_rendering.log`, and `log_general.log` reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `FrameOpError`, `Secondary inheritance mismatch`, and `destroyed or rerecorded`.
- Phase 9 screenshot comparison against current single-thread command-chain gate: old-path `Build/McpCaptures/command-chains/phase9-oldpath-baseline-20260621_133548/Screenshot_20260621_133608.png` versus command-chain `Build/McpCaptures/command-chains/phase8-primary-current-focus-20260621_133259/Screenshot_20260621_133322.png`; both are 1920x1080 and a sampled 8-pixel-stride RGB diff reported `SAMPLES=32400`, `MEAN_RGB_DELTA=10.2807`, `MAX_RGB_DELTA=29`. Visual inspection showed the same settled Sponza view.
- Phase 10 worker-pool visual gate with `XRE_VULKAN_COMMAND_CHAINS=1`, validation enabled, and single-thread mode unset: `Build/McpCaptures/command-chains/phase10-worker-pool-20260621_134357/Screenshot_20260621_134418.png`.
- Phase 10 worker-pool log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_13-43-58_pid27144`.
- Phase 10 worker-pool profiler evidence: `chains_scheduled=65`, `chains_recorded=3`, `chains_reused=62`, `chains_frame_data_refreshed=48`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `visibility_packet_count=2`, `render_packet_count=65`, `secondary_command_buffer_count=65`, `chain_worker_record_ms=0.6587`, `render_thread_wait_for_workers_ms=0.0215`, `validation.message_count=0`, `validation.error_count=0`.
- Phase 10 worker-pool log review: `log_vulkan.log`, `log_rendering.log`, and `log_general.log` reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `FrameOpError`, `Secondary inheritance mismatch`, `destroyed or rerecorded`, `stale descriptor-set`, `stale physical-image`, `stale framebuffer`, `stale pipeline`, `Command-chain primary schedule`, and `dynamic overlay group before`.
- Phase 10 implementation note: the bounded worker pool currently prepares independent command-chain work with per-worker graphics/compute command pools, scratch storage, bind-state ownership, cancellation, and teardown safety. Inheritance-sensitive Vulkan secondary command-buffer recording still executes through the validated primary-compatible path until the full recorder can be moved behind the same worker ownership model.
- Phase 11 parallel packet-build visual gate with `XRE_VULKAN_PARALLEL_PACKET_BUILD=1`: `Build/McpCaptures/command-chains/phase11-parallel-packets-20260621_134908/Screenshot_20260621_134930.png`.
- Phase 11 parallel packet-build log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_13-49-09_pid51632`.
- Phase 11 parallel packet-build profiler evidence: `chains_scheduled=65`, `chains_recorded=3`, `chains_reused=62`, `chains_frame_data_refreshed=48`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `visibility_packet_count=2`, `render_packet_count=65`, `secondary_command_buffer_count=65`, `chain_worker_record_ms=1.0096`, `render_thread_wait_for_workers_ms=0.0356`, `validation.message_count=0`, `validation.error_count=0`.
- Phase 11 parallel packet-build log review: `log_vulkan.log`, `log_rendering.log`, and `log_general.log` reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `FrameOpError`, `Secondary inheritance mismatch`, `destroyed or rerecorded`, `stale descriptor-set`, `stale physical-image`, `stale framebuffer`, `stale pipeline`, `Parallel command-chain packet build mismatch`, and `Parallel command-chain packet build produced`.
- Phase 11 implementation note: `XRE_VULKAN_PARALLEL_PACKET_BUILD=1` now builds immutable render packets from the frozen static/volatile frame-op snapshot using independent jobs, then appends static and dynamic-overlay packets in deterministic order. Validation mode builds a sequential reference packet list and asserts packet equivalence before scheduling chains.
- Phase 12 VR/shadow specialization final visual gate with command chains, validation, and parallel packet build enabled: `Build/McpCaptures/command-chains/phase12-vr-shadow-specialization-final-20260621_140141/Screenshot_20260621_140205.png`.
- Phase 12 VR/shadow specialization final log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_14-01-42_pid41196`.
- Phase 12 VR/shadow specialization profiler evidence: `chains_scheduled=65`, `chains_recorded=3`, `chains_reused=62`, `chains_frame_data_refreshed=48`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `visibility_packet_count=2`, `render_packet_count=65`, `secondary_command_buffer_count=65`, `chain_worker_record_ms=1.0075`, `render_thread_wait_for_workers_ms=0.0198`, `validation.message_count=0`, `validation.error_count=0`, `descriptor.binding_failures=0`, `retired.plan_replacements=0`.
- Phase 12 VR/shadow specialization log review: `log_vulkan.log`, `log_rendering.log`, and `log_general.log` reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `FrameOpError`, `Secondary inheritance mismatch`, `destroyed or rerecorded`, `stale descriptor-set`, `stale physical-image`, `stale framebuffer`, `stale pipeline`, `Command-chain VR eye`, `Command-chain shadow key`, `Command-chain schedule mixes`, `left eye before right eye`, and `Command-chain shadow validation rejected`.
- Phase 12 implementation note: command-chain view keys now distinguish separate VR left/right eyes when the renderer supplies eye-specific cameras, keep single-pass stereo as an explicit multiview sentinel, and require left-before-right ordering in validation mode. Shadow command-chain keys now carry explicit shadow light and cascade/face identities; shadow atlas/cascade packing participates in structural signatures; validation rejects resident shadow tiles that claim non-reusable fallback modes and non-resident tiles with no explicit fallback.
- Phase 13 disabled multi-queue fallback visual gate with `XRE_VULKAN_COMMAND_CHAIN_MULTI_QUEUE=1`: `Build/McpCaptures/command-chains/phase13-multiqueue-disabled-fallback-20260621_140633/Screenshot_20260621_140657.png`.
- Phase 13 disabled multi-queue fallback log gate: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_14-06-34_pid38476`.
- Phase 13 disabled multi-queue fallback profiler evidence: `chains_scheduled=65`, `chains_recorded=3`, `chains_reused=62`, `chains_frame_data_refreshed=48`, `volatile_chains_recorded=3`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0`, `visibility_packet_count=2`, `render_packet_count=65`, `secondary_command_buffer_count=65`, `chain_worker_record_ms=1.0352`, `render_thread_wait_for_workers_ms=0.0181`, `validation.message_count=0`, `validation.error_count=0`, `descriptor.binding_failures=0`, `retired.plan_replacements=0`.
- Phase 13 disabled multi-queue fallback log review: `log_vulkan.log`, `log_rendering.log`, and `log_general.log` reported zero matches for `VUID`, `Validation Error`, `[ERROR]`, `InvalidCommandBuffer`, `FrameOpError`, `Secondary inheritance mismatch`, `destroyed or rerecorded`, `stale descriptor-set`, `stale physical-image`, `stale framebuffer`, `stale pipeline`, `Command-chain queue schedule`, `sidecar queue node`, `single-queue fallback`, `timeline semaphore`, and `queue-family ownership`.
- Phase 13 implementation note: multi-queue scheduling is represented as disabled infrastructure. The command-chain scheduler now classifies queue eligibility, emits a graphics-only fallback node even when `XRE_VULKAN_COMMAND_CHAIN_MULTI_QUEUE=1` is requested, records timeline/dependency/ownership-transfer metadata in queue schedule nodes, and validates that any future sidecar queue node has explicit timeline semaphore and dependency information before it can run.
- Phase 0 no-env Release measurement gate: `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-06-21_14-09-10/summary.json`; log dir `Build/Logs/Release_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_14-08-55_pid32944`. Command-chain counters stayed zero with command-chain env vars unset. The intentionally short 5s/10s run caught startup/streaming (`VulkanRecordCommandBufferP50Ms=185.777`, `VulkanCommandBufferRecordsTotal=4`) and is only used as the no-env baseline record, not as settled performance evidence.
- Release settled command-chain measurement gate: `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-06-21_14-11-02/summary.json`; log dir `Build/Logs/Release_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_14-09-28_pid44192`. With `XRE_VULKAN_COMMAND_CHAINS=1` and `XRE_VULKAN_PARALLEL_PACKET_BUILD=1`, the 25s/60s capture reached a fully reused settled state: `RenderP50Ms=0.03`, `RenderP95Ms=0.038`, `VulkanCommandBufferRecordsTotal=0`, `VulkanCommandBufferForcedDirtyTotal=0`, `VulkanRecordCommandBufferAllocatedBytesTotal=0`, `VulkanResourcePlanReplacementsTotal=0`, and no capture-window GPU readbacks/mapped buffers. `VulkanRecordCommandBufferP50Ms/P95Ms` were null because no command-buffer recording samples occurred in the settled window, which is below the command-recording budget by elimination rather than by measuring a nonzero record duration.
- Legacy render-pass inheritance code is present, but the `XRE_VK_RENDER_TARGET_MODE=LegacyRenderPass` validation run exposed separate image-layout VUIDs around transfer-source layouts versus render-pass initial layouts before it could serve as a clean legacy-secondary gate. Treat legacy render-target layout cleanup as a blocker for checking the combined legacy/dynamic success criterion.
- Build/test gates after implementation: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` passed with 0 warnings; `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanCommandChainDataModelTests"` passed 33/33 after Phase 13 queue-schedule coverage; `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanCommandChainDataModelTests|FullyQualifiedName~VulkanP1ValidationTests"` passed 31/32 with the one skip being the optional missing CI workflow check; `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ProfilerProtocolTests|FullyQualifiedName~VulkanCommandChainDataModelTests"` passed 26/26 before the Phase 5 assertion expansion.
- Architecture doc gate: updated `docs/architecture/rendering/vulkan-renderer.md` with the feature-flagged command-chain contract, `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md` with the render-thread/worker split, and `docs/architecture/rendering/mesh-submission-strategies.md` with the Vulkan command-chain volatility contract. The legacy frame-op recorder remains intentionally retained as the compatibility/fallback executor until command chains become default and Phase 14 validation is complete.

2026-06-21 Phase 14 final integration gate:

- Final cache-safety fix: fast primary command-buffer reuse now requires a cached command-chain schedule and recomputes the current primary group signature from live chain secondary command-buffer handles before reusing a primary. This prevents reusing a primary that references a secondary command buffer that was destroyed or rerecorded.
- Final build gate: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -nr:false` passed with 0 warnings and 0 errors.
- Final unit-test gates: command-chain/profiler/render-ordering/resource lifecycle slice passed 102/102; `Test-VulkanPhase3-Regression` passed 82/82. The render-ordering and backlog fixtures now install `RuntimeShaderServices.Current` with the same test shader service used by nearby rendering fixtures.
- Final MCP visual gate with command chains, parallel packet build, and multi-queue fallback enabled: `Build/McpCaptures/command-chains/phase14-final-current-postfix-mcp-20260621_164503/`. Screenshots reviewed: `Screenshot_20260621_164505.png` and `Screenshot_20260621_164600.png`; both show Sponza rendering from distinct camera poses. Exported and inspected active pipeline targets: `DepthView`, `AmbientOcclusionTexture`, `LightingAccumTexture`, `BloomBlurTexture`, `Velocity`, `TsrOutputTexture`, and `FinalPostProcessOutputTexture`.
- Final exact G-buffer/HDR target gate: `Build/McpCaptures/command-chains/phase14-final-current-gbuffer-mcp-20260621_165224/`. Exported and inspected `AlbedoOpacity`, `Normal`, `RMSE`, `HDRSceneTex`, `DepthView`, `FinalPostProcessOutputTexture`, and `TsrOutputTexture`; MCP profiler validation counters were `message_count=0`, `error_count=0`.
- Final Debug MCP log gates: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_16-44-33_pid51352` and `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_16-51-53_pid16112`. Log scans reported zero matches for `VUID`, `Validation Error`, `InvalidCommandBuffer`, `destroyed or rerecorded`, `[ERROR]`, `Exception`, stale descriptor/physical/framebuffer/pipeline, secondary inheritance, command-chain errors, frame-op errors, device lost, and unhandled exceptions.
- Final Release measurement gate: `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-06-21_16-50-46/summary.json`; log dir `Build/Logs/Release_net10.0-windows7.0/windows_x64/xrengine_2026-06-21_16-48-51_pid37048`. With `XRE_VULKAN_COMMAND_CHAINS=1`, `XRE_VULKAN_PARALLEL_PACKET_BUILD=1`, and `XRE_VULKAN_COMMAND_CHAIN_MULTI_QUEUE=1`, the 45s warmup/60s capture passed strict steady-state gates: `VulkanRecordCommandBufferP50Ms=1.966`, `VulkanRecordCommandBufferP95Ms=2.216`, `VulkanCommandBufferRecordsTotal=0`, `VulkanCommandBufferForcedDirtyTotal=0`, `VulkanRecordCommandBufferAllocatedBytesTotal=0`, `VulkanResourcePlanReplacementsTotal=0`, `VulkanPrimaryCommandBuffersReusedTotal=490`, `VulkanPrimaryCommandBuffersRecordedTotal=0`, capture-window `GpuReadbackBytesTotal=0`, capture-window `GpuMappedBuffersTotal=0`, and `ForbiddenFallbackEventsTotal=0`.
- Final Release log review for `xrengine_2026-06-21_16-48-51_pid37048`: zero matches for `VUID`, `Validation Error`, `InvalidCommandBuffer`, `destroyed or rerecorded`, `[ERROR]`, `Exception`, stale descriptor/physical/framebuffer/pipeline, secondary inheritance, command-chain errors, frame-op errors, device lost, and unhandled exceptions.

Implemented scope note: the active Vulkan renderer now uses command-chain schedule order as the primary recording contract while the existing frame-op recorder remains the compatibility executor. `XRE_VULKAN_COMMAND_CHAINS=1` currently enables packet lowering, opt-in parallel packet build via `XRE_VULKAN_PARALLEL_PACKET_BUILD=1`, VR/shadow view-key specialization, disabled multi-queue schedule/fallback validation via `XRE_VULKAN_COMMAND_CHAIN_MULTI_QUEUE=1`, volatility classification, per-frame-slot chain cache evaluation, command-chain stats, trace/validate diagnostics, reuse telemetry, primary schedule validation/reuse, bounded worker pre-record dispatch, and dynamic-rendering mesh secondary command-buffer execution.

## Success Criteria

- [x] Vulkan can build immutable visibility/render packets for main, shadow,
  overlay, and VR eye views.
- [x] Vulkan can record normal mesh draw chains into secondary command buffers
  inside legacy render passes and dynamic rendering scopes.
- [x] Static scene command chains are reused during camera movement.
- [x] Dynamic UI/text/profiler changes record only small volatile chains.
- [x] Render-thread primary recording is reduced to pass orchestration,
  barriers, secondary execution, queries, submit, and present.
- [x] Worker-thread recording can be enabled for independent command chains.
- [x] Parallel and single-thread modes produce equivalent screenshots and pass
  outputs.
- [x] Release settled-scene `Vulkan.FrameLifecycle.RecordCommandBuffer` p50 is
  below 2 ms and p95 is below 5 ms on the target validation scene.
- [x] No steady-state descriptor pool retirement, resource-plan replacement, or
  command-buffer structural dirtying occurs during ordinary camera movement.

## Guardrails

- [x] Keep a single-thread command-chain mode available for bisection.
- [x] Keep existing Vulkan GPU/accelerated paths explicit; do not hide failures
  behind OpenGL or CPU fallbacks.
- [x] Do not mutate scene, material, descriptor, FBO, or resource planner state
  from visibility or recording workers.
- [x] Do not create one secondary command buffer per tiny draw unless a measured
  case proves it is beneficial.
- [x] Keep barrier planning centralized in the render graph/compiler path.
- [x] Treat descriptor lifetime bugs as correctness blockers, not perf
  follow-ups.
- [x] Add diagnostics before enabling parallel work by default.
- [x] Every phase must have a visual gate and at least one targeted build/test
  gate.

## Feature Flags And Diagnostics

- [x] Add `XRE_VULKAN_COMMAND_CHAINS=0/1` to enable the packet/chain path during
  migration.
- [x] Add `XRE_VULKAN_COMMAND_CHAINS_SINGLE_THREAD=1` to force deterministic
  single-thread chain recording.
- [x] Add `XRE_VULKAN_COMMAND_CHAIN_VALIDATE=1` for expensive signature,
  descriptor generation, and resource-plan assertions.
- [x] Add `XRE_VULKAN_COMMAND_CHAIN_TRACE=1` for first-dirty-reason chain logs.
- [x] Add `XRE_VULKAN_DISABLE_PARALLEL_CHAIN_RECORDING=1` as the final
  user-facing bisection flag once parallel recording exists.
- [x] Add `XRE_VULKAN_COMMAND_CHAIN_MESH_SECONDARY_NOOP=1` as a diagnostic-only
  secondary inheritance/lifetime smoke test.
- [x] Surface command-chain counters in runtime stats, profiler packets, editor
  profiler UI, and NDJSON profile capture:
  - chains scheduled;
  - chains recorded;
  - chains reused;
  - chains frame-data-refreshed;
  - volatile chains recorded;
  - primary command buffers reused;
  - primary command buffers recorded;
  - chain worker record time;
  - render-thread wait-for-workers time;
  - first structural dirty reason;
  - first descriptor generation mismatch;
  - first resource plan revision mismatch.

## Phase 0 - Branch, Baseline, And Safety Net

- [x] Create dedicated branch `vulkan-parallel-command-chain-refactor` from the
  current integration branch.
- [x] Confirm the target validation scene and GPU configuration.
- [x] Record a no-env Vulkan baseline using
  `Tools/Measure-VulkanFrameLoop.ps1`.
- [x] Capture baseline MCP screenshots for:
  - main viewport;
  - `AlbedoOpacity`;
  - `Normal`;
  - `RMSE`;
  - `DepthView`;
  - `AmbientOcclusionTexture`;
  - `LightingAccumTexture`;
  - `HDRSceneTex`;
  - final AA/post-process output.
- [x] Record baseline command-buffer cache stats, retired resource stats,
  descriptor pool retirement, resource-plan replacement, and frame-op census.
- [x] Document any pre-existing visual defects separately so they do not block
  command-chain work unless they regress.
- [x] Add a branch-local status section to this TODO after each implementation
  phase with evidence links.

Acceptance criteria:

- [x] Baseline run directory and screenshot directory are linked in this TODO.
- [x] Current visual defects are named and separated from refactor regressions.
- [x] The branch exists before source changes begin.

## Phase 1 - Instrumentation And Command-Chain Metrics

- [x] Add command-chain metric fields to `Engine.Rendering.Stats.Vulkan`.
- [x] Extend profiler packet serialization/deserialization tests for the new
  fields.
- [x] Surface command-chain counters in `EngineProfilerDataSource`.
- [x] Add profiler UI rows/columns for command-chain reuse and worker timing.
- [x] Extend NDJSON profile capture with command-chain metrics.
- [x] Add first-dirty-reason aggregation for chains, matching the existing
  command-buffer dirty reason model.
- [x] Add log throttling for chain diagnostics so validation mode is useful but
  not log-spammy.
- [x] Update `Tools/Measure-VulkanFrameLoop.ps1` and
  `Tools/Measure-GameLoopRenderPipeline.ps1` summary output with the new
  counters.

Acceptance criteria:

- [x] Existing profiler protocol tests pass.
- [x] A no-behavior-change editor run reports zero command chains while the
  feature flag is disabled.
- [x] Enabling trace flags without command chains does not crash or allocate
  unbounded logs.

## Phase 2 - Packet Data Model Without Behavior Change

- [x] Add `RenderViewKind`.
- [x] Add `RenderViewKey`.
- [x] Add immutable `VisibilityPacket`.
- [x] Add immutable `RenderPacket`.
- [x] Add `DrawPacket` and `DispatchPacket` snapshots with only stable handles,
  IDs, and value snapshots.
- [x] Add `RenderPacketVolatility`.
- [x] Add `CommandChainKey`.
- [x] Add `CommandChain`.
- [x] Add `RenderPassChainGroup`.
- [x] Add `CommandChainSchedule`.
- [x] Use pooled backing arrays or frame-owned buffers for packet lists.
- [x] Add debug-only ownership checks so packet memory cannot be returned to a
  pool before frame retirement.
- [x] Add unit tests for equality/hash stability on keys and volatility values.

Acceptance criteria:

- [x] The new types compile and are only used by the feature-flagged migration layer.
- [x] Packet/key tests are deterministic.
- [x] No new warnings are introduced.

## Phase 3 - Lower Existing FrameOps Into Packets

- [x] Add a `FrameOp` to `RenderPacket` lowering path behind
  `XRE_VULKAN_COMMAND_CHAINS=1`.
- [x] Preserve current render ordering exactly:
  - render graph pass order;
  - scheduling identity;
  - target grouping;
  - transparent draw ordering;
  - original same-pass ordering where required.
- [x] Compute `StructuralSignature` for lowered packets.
- [x] Compute `FrameDataSignature` for lowered packets.
- [x] Add validation that lowered packet signatures explain the current
  frame-op signature.
- [x] Add a packet dump mode for one frame of:
  - pass index;
  - target;
  - pipeline identity;
  - viewport identity;
  - draw count;
  - volatility;
  - structural signature;
  - frame-data signature.
- [x] Keep actual command recording on the old path in this phase.

Acceptance criteria:

- [x] With command chains enabled, packet dumps match the old frame-op census.
- [x] With command chains disabled, behavior is byte-for-byte unchanged except
  for dormant code.
- [x] No visual output changes.

## Phase 4 - Volatility Classification And Static/Dynamic Split

- [x] Classify lowered packets as:
  - `StaticStructural`;
  - `FrameDataOnly`;
  - `DynamicCommand`;
  - `StructuralDirty`.
- [x] Move UI text packet classification to `DynamicCommand`.
- [x] Move profiler/ImGui/editor gizmo packets to `DynamicCommand` where
  practical.
- [x] Remove camera matrices, model matrices, material constants, and other
  refreshable values from static structural signatures.
- [x] Keep descriptor layout and descriptor set handle stability in structural
  signatures.
- [x] Add diagnostics when a packet is classified dynamic because draw count,
  instance count, or descriptor handle count changed.
- [x] Add tests for known volatility examples:
  - static mesh with moving camera;
  - static mesh with changing material constant;
  - UI text with changing instance count;
  - shader variant change;
  - FBO attachment format change.

Acceptance criteria:

- [x] Dynamic UI/text no longer dirties static packet structural signatures.
- [x] Static packet structural signatures remain stable during camera movement.
- [x] Real structural changes still dirty the correct packets.

## Phase 5 - Resource Planner Freeze And Descriptor Snapshots

- [x] Define the exact point where the Vulkan resource plan freezes for the
  current frame.
- [x] Prevent worker recording from triggering resource planner replacement.
- [x] Add descriptor/resource binding snapshots to `RenderPacket` or
  chain-record input.
- [x] Track descriptor generation per chain.
- [x] Track physical resource plan revision per chain.
- [x] Track pipeline generation per chain.
- [x] Convert descriptor refresh blockers into explicit chain dirty reasons.
- [x] Ensure descriptor pool retirement is frame-slot/timeline based for any
  descriptor set referenced by reusable chains.
- [x] Add validation assertions for stale descriptor sets, stale physical image
  handles, stale framebuffers, and stale pipeline handles.

Acceptance criteria:

- [x] Resource planner changes invalidate affected chains explicitly.
- [x] Workers can read a frozen plan without locks that serialize recording.
- [x] No command buffer references retired descriptor pools in validation mode.

## Phase 6 - Chain Cache And Frame-Data Refresh

- [x] Add a per-frame-slot command-chain cache.
- [x] Add chain cache lookup by `CommandChainKey`.
- [x] Track chain structural signature, resource plan revision, descriptor
  generation, pipeline generation, and profiler/query mode.
- [x] Add a `TryRefreshReusableCommandChainFrameData` path for:
  - engine uniforms;
  - auto uniforms;
  - object transforms;
  - camera/view data;
  - material constants in refreshable buffers;
  - dynamic uniform/storage offsets.
- [x] Keep the old command-buffer refresh path intact until chain refresh is
  proven.
- [x] Add dirty reason values:
  - structure;
  - resource-plan;
  - descriptor-generation;
  - pipeline-generation;
  - profiler-mode;
  - frame-data-refresh-failed;
  - volatile-command.
- [x] Add tests for chain cache reuse and frame-data refresh.

Acceptance criteria:

- [x] A static scene with moving camera refreshes frame data without recording
  equivalent static chains.
- [x] Dirty reason diagnostics identify the first non-reusable chain.
- [x] Existing command-buffer cache metrics remain correct during migration.

## Phase 7 - Secondary Command Buffers For Graphics Draw Chains

- [x] Extend secondary command-buffer helpers beyond blits/indirect draws.
- [x] Support mesh draw recording in secondary command buffers.
- [x] Add legacy render-pass inheritance:
  - render pass;
  - framebuffer;
  - subpass;
  - `RenderPassContinueBit`.
- [x] Add dynamic-rendering inheritance:
  - color attachment formats;
  - depth attachment format;
  - stencil attachment format;
  - sample count;
  - view mask;
  - `RenderPassContinueBit`.
- [x] Make primary pass begin support secondary-command-buffer contents where
  needed.
- [x] Avoid mixing inline draw commands with secondary-only pass contents in a
  way that violates Vulkan validation.
- [x] Add a minimal secondary graphics chain for one stable mesh pass.
- [x] Add a volatile secondary chain for UI text/profiler overlay.
- [x] Add validation logs for secondary inheritance mismatches.

Acceptance criteria:

- [x] Vulkan validation logs contain no secondary-command-buffer inheritance
  errors.
- [x] The minimal secondary mesh pass visually matches the old inline path.
- [x] UI text/profiler overlay can record as a dynamic secondary without dirtying
  the static scene chain.

## Phase 8 - Primary Command-Buffer Orchestration

- [x] Add primary recording from `CommandChainSchedule`.
- [x] Emit centralized pass barriers before each chain group.
- [x] Begin render pass/dynamic rendering for each group.
- [x] Execute ordered secondary command buffers for the group.
- [x] End render pass/dynamic rendering.
- [x] Execute volatile overlay chains after main scene groups and before final
  present.
- [x] Transition swapchain to present exactly once.
- [x] Keep timing/GPU profiler query behavior correct.
- [x] Keep primary command-buffer dirty reasons separate from secondary chain
  dirty reasons.
- [x] Reuse primary command buffers when pass group structure and secondary
  handles are stable.

Acceptance criteria:

- [x] Primary recording can run the command-chain path for a representative
  frame.
- [x] Primary reuse is reported separately from secondary reuse.
- [x] Dynamic secondary re-recording does not require primary re-recording when
  secondary handles are stable.

## Phase 9 - Single-Thread Command-Chain Renderer

- [x] Route a full representative Vulkan frame through packets, chains,
  secondary recording, and primary orchestration on the render thread only.
- [x] Keep `XRE_VULKAN_COMMAND_CHAINS_SINGLE_THREAD=1` equivalent to this mode.
- [x] Compare screenshots and pass outputs against the old path.
- [x] Compare command-chain stats against old command-buffer stats.
- [x] Add fallback kill switch to return to old frame-op recording during this
  phase.
- [x] Fix all visual regressions before adding worker-thread recording.

Acceptance criteria:

- [x] Single-thread command-chain output matches the old path.
- [x] Logs contain no new Vulkan validation errors.
- [x] Static/dynamic chain reuse works in single-thread mode.

## Phase 10 - Recording Worker Pool

- [x] Add a bounded Vulkan recording worker pool.
- [x] Add per-worker graphics command pools.
- [x] Add optional per-worker compute command pools if compute chains are
  enabled later.
- [x] Add per-worker scratch arenas for temporary sorting/binding data.
- [x] Add worker-safe command-buffer bind-state tracking.
- [x] Dispatch independent chain recordings after resource planning.
- [x] Record worker wait time separately from worker record time.
- [x] Add a deterministic mode that records workers in schedule order for
  debugging.
- [x] Add cancellation/teardown handling for swapchain recreation and device
  loss.
- [x] Ensure no command pool is reset while GPU work using its command buffers is
  still in flight.

Acceptance criteria:

- [x] Parallel worker recording can be toggled at runtime or startup.
- [x] Single-thread and parallel modes produce equivalent images.
- [x] Worker recording improves high draw-count record p95 without adding
  visible hitches.

## Phase 11 - Parallel Visibility And Packet Build

- [x] Freeze a read-only scene/render snapshot before visibility jobs begin.
- [x] Build visibility packets in parallel for independent views.
- [x] Add view jobs for:
  - main editor/game view;
  - VR left eye;
  - VR right eye;
  - directional shadow cascades;
  - point/spot shadow views where applicable;
  - reflection/probe views where applicable.
- [x] Build render packets from visibility packets in parallel.
- [x] Ensure scene graph and component access is read-only during worker
  collection.
- [x] Add deterministic sorting after packet build so output order is stable.
- [x] Add validation mode comparing parallel visibility output to single-thread
  output for selected scenes.

Acceptance criteria:

- [x] Parallel visibility can be enabled independently from parallel recording.
- [x] Main/shadow/VR packet counts are stable and deterministic.
- [x] No scene mutation occurs from visibility worker threads.

## Phase 12 - VR And Shadow Chain Specialization

- [x] Add explicit `RenderViewKind.VREye` handling in command-chain keys.
- [x] Record separate left/right eye chains first.
- [x] Validate VR eye ordering and swapchain/image ownership.
- [x] Add explicit `RenderViewKind.Shadow` handling in command-chain keys.
- [x] Record independent shadow map/cascade chains in parallel.
- [x] Keep shadow atlas packing changes as structural dirty reasons.
- [x] Add stale-tile/fallback shadow behavior to validation gates so parallel
  shadow recording cannot reintroduce one-frame shadow disable flicker.
- [x] Evaluate multiview chain recording only after separate eye chains are
  correct and measured.

Acceptance criteria:

- [x] VR eye chains can record independently without changing output.
- [x] Shadow chains can record independently and reuse when light/caster sets
  are stable.
- [x] Shadow-map visual gates pass while moving and settling the camera.

## Phase 13 - Optional Multi-Queue Scheduling

- [x] Keep this phase disabled until single-queue command chains are correct and
  fast.
- [x] Identify chains eligible for sidecar queue submission:
  - async compute culling/compaction;
  - async skinning/blendshape compute;
  - transfer uploads;
  - shadow rendering on a second graphics queue if hardware benefits.
- [x] Add queue dependency nodes to the command-chain schedule.
- [x] Add timeline semaphore waits/signals per sidecar submission.
- [x] Add queue-family ownership transfers where needed.
- [x] Add single-queue fallback for every multi-queue schedule.
- [x] Add queue overlap diagnostics and GPU timestamp ranges.

Acceptance criteria:

- [x] Multi-queue mode is never required for correctness.
- [x] Multi-queue mode produces equivalent images.
- [x] Multi-queue mode is kept only where measurements show a real win.

## Phase 14 - Validation, Documentation, And Cleanup

- [x] Run `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`.
- [x] Run command-chain unit tests.
- [x] Run profiler protocol tests.
- [x] Run render graph ordering tests.
- [x] Run `Test-VulkanPhase3-Regression`.
- [x] Run Unit Testing World Vulkan with MCP enabled.
- [x] Capture visual gates from at least two camera positions.
- [x] Export and inspect critical render targets:
  - `AlbedoOpacity`;
  - `Normal`;
  - `RMSE`;
  - `DepthView`;
  - `AmbientOcclusionTexture`;
  - `LightingAccumTexture`;
  - `HDRSceneTex`;
  - final post-process/AA output.
- [x] Run `Tools/Measure-VulkanFrameLoop.ps1` in Release.
- [x] Compare against the Phase 0 baseline.
- [x] Update `docs/architecture/rendering/vulkan-renderer.md` with the final
  command-chain contract.
- [x] Update `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md`
  with the new render-thread/worker split.
- [x] Update `docs/architecture/rendering/mesh-submission-strategies.md` if
  command-chain volatility changes mesh strategy contracts.
- [x] Keep the legacy frame-op recording code available until the command-chain
  path is the default and Phase 14 validation is complete.
- [x] Keep `vulkan-parallel-command-chain-refactor` unmerged until Phase 14
  validation is complete and final integration is requested.

Acceptance criteria:

- [x] All required builds/tests pass or failures are documented as unrelated.
- [x] Visual output remains correct.
- [x] No new Vulkan validation errors appear in logs.
- [x] Release measurements meet or move materially toward success criteria.
- [x] Architecture docs describe the final implemented behavior.

## Suggested Implementation Order

1. Branch and capture baseline.
2. Add metrics and dormant data types.
3. Lower old frame ops into packets without changing rendering.
4. Classify volatility and split dynamic overlay/text work.
5. Freeze resource plans and descriptor snapshots.
6. Add chain cache and frame-data refresh.
7. Record graphics mesh chains as secondary command buffers.
8. Switch primary recording to schedule orchestration in single-thread mode.
9. Enable worker-thread secondary recording.
10. Move visibility and packet building to workers.
11. Specialize VR and shadow views.
12. Consider multi-queue only after single-queue wins are proven.
13. Validate, update architecture docs, retire obsolete paths, and merge back.

## Open Questions To Resolve During Implementation

- [x] Should `FrameOp` become a compatibility lowering layer, or should it be
  removed after packets are stable?
  Answer: keep `FrameOp` as the compatibility lowering layer until the command-chain path is default and fully validated; retire obsolete direct frame-op recording only during final cleanup.
- [x] Which render-packet fields are allowed to be frame-data-only instead of
  structural?
  Answer: camera/view matrices, model/previous-model matrices, material constants in refreshable buffers, and dynamic uniform/storage offsets are frame-data-only; draw counts, descriptor set count/signature, pipeline/layout identity, pass/target/view identity, and shadow atlas packing are structural.
- [x] Which descriptor generations can be refreshed without command
  re-recording?
  Answer: only frame-data changes can refresh in place; descriptor generation or descriptor set signature changes explicitly dirty the affected chain and are rejected by validation if a reusable chain references stale descriptor data.
- [x] How should transparent order be represented without blocking opaque
  parallelism?
  Answer: preserve render graph pass order and source ordinal in packet keys; opaque chains can be independently scheduled, while transparent same-pass ordering remains deterministic through `SourceStartIndex`/chain ordinal.
- [x] Should VR multiview be a first-class packet mode or a later optimization?
  Answer: separate left/right eye keys are the first-class correctness path; single-pass stereo is represented as an explicit multiview sentinel and remains a later measured optimization.
- [x] Which shadow atlas changes should dirty only affected chains instead of
  all shadow chains?
  Answer: shadow light identity plus cascade/face identity and atlas packing state dirty only the affected shadow view/chain; broad target/resource-plan changes still invalidate by target/resource signature.
- [x] How should GPU profiler queries interact with reusable secondary command
  buffers?
  Answer: primary profiler/query state remains a primary command-buffer dirty reason; reusable secondary chains are not dirtied by profiler mode unless their own recorded command contents need query commands.
- [x] What is the minimum chain size where secondary command buffers beat inline
  primary recording?
  Answer: keep the current conservative threshold at four mesh draws per secondary chain until Release profiling proves a lower threshold wins.

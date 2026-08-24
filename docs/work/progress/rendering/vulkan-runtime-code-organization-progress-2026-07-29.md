# Vulkan Runtime Code Organization Progress

Date: 2026-07-29
Branch: `master`
Implementation base: `404df57741745359ea7e7dcaa0f3a67f667c1051`
Final commit: not created; this note describes the shared dirty integration tree

## Outcome

The implementation pass described by the
[Vulkan Runtime Code Organization TODO](../../todo/rendering/vulkan-runtime-code-organization-todo.md)
is complete. Vulkan remains in the leaf
`XREngine.Runtime.Rendering.Vulkan` assembly and now has explicit authorities
for device state, backend-object identity, command scheduling/recording,
render-graph planning, resource lifetime, descriptors, pipelines, desktop
frames, OpenXR graphics binding, and ImGui.

The implementation and measurable desktop acceptance gates are complete. Two
exceptions remain visible in the TODO:

- the compatibility facade still has a one-way legacy budget of 72 stateful
  partials and 548 fields, so the literal small-facade endpoint is not claimed;
- SteamVR could not expose an OpenXR system because no HMD/form factor was
  available.

The earlier compact-submission and allocation exceptions are resolved. The
material table now waits for descriptor publication, `OnTopForward` is an
explicit CPU-direct overlay pass, material preparation reuses scratch state,
and indirect draw operations are frame-pooled.

The work began in a heavily modified shared tree. The initial
`git status --short` contained 167 entries from several concurrent workstreams.
Those changes were preserved and are not all claimed by this effort.

## Ownership And Organization Delivered

| Responsibility | Owner or contract | Result |
|---|---|---|
| Backend object identity | `VulkanBackendObjectRegistry`, `VulkanBackendObjectBucket<T>`, `VulkanBackendObjectContext` | Wrapper identity, binding slots, and publication are scoped to one renderer/device. |
| Device creation and capabilities | `VulkanDeviceContext`, immutable `VulkanDeviceCapabilities`, query/builder/reporter contracts | `CreateLogicalDevice` is a short query/create/publish/report coordinator. |
| Command scheduling | `VulkanFrameOperationScheduler`, `VulkanCommandChainState` | Frame-operation ordering has one scheduler; cache recency generation remains with the command-chain artifacts it orders. |
| Command recording | `VulkanCommandRecorder`, `VulkanCommandRecordingContext`, render-scope owner, per-domain recording methods | Recording inputs are captured explicitly; begin/end, scopes, barriers, transfers, uploads, and readback are separated from scheduling policy. |
| Render graph | `VulkanRenderGraphRuntime`, immutable `VulkanRenderGraphPlan`, immutable `VulkanBarrierPlan` | Compiler/planner/allocator state has one authority and recording consumes versioned plan data. |
| Binding grammar | `VulkanResourceBindingKey`, `EVulkanResourceBindingKind` | `tex::`, `fbo::`, and `buf::` parsing is centralized; malformed and duplicate prefixes are guarded by tests. |
| Resource lifetime | `VulkanResourceLifetimeTracker`, `VulkanResourceRetirementQueue` | Use publication, deferred retirement, completed-frame observation, and destruction follow one traceable path. |
| Descriptors | `VulkanDescriptorManager` | Frame-slot state, pools, layouts, immutable samplers, allocation publication, and descriptor generations have device lifetime. |
| Pipelines | `VulkanPipelineManager` | Program linking, pipeline caches, prewarm state, and device-lifetime disposal are consolidated. |
| Desktop frames | `VulkanDesktopFrameCoordinator`, `VulkanFrameAttempt` | Acquire, phase results, submit, present, abort/recovery, completion, and slot advance use typed state and exactly-once paths. |
| OpenXR graphics | `VulkanOpenXrBackend`, `VulkanOpenXrFrameContext` | Vulkan eye, mirror, preview, external-image, and diagnostics state is separate from generic OpenXR session/pacing policy. |
| ImGui | `VulkanImGuiBackend` | Input, GPU resources, texture registry, immutable snapshots, submission, and retirement share Vulkan authorities. |

Mechanical and domain cleanup also completed the frame-op, buffer-allocation,
readback/pixel-decoding, transform-feedback, domain-folder, one-type-per-file,
shader uniform, descriptor, program, resource-planner, barrier, and wrapper
splits listed in the TODO. Implementation types are internal by default and
subsystem namespace contracts are imported through leaf/test global usings
without leaking concrete Vulkan types into the stable kernel.

All Vulkan `[ThreadStatic]` state was removed. Reusable per-thread scratch is
owned by explicit `ThreadLocal<T>` workspaces with release paths. Persistent
compute uniforms and reusable descriptor resources are now prepared before the
command recorder enters its recording scope; command emission does not create
persistent resources.

## Guardrails

`VulkanSourceArchitectureGuardrailTests` and the focused ownership suites now
enforce:

- recursive Vulkan source discovery rather than exact implementation paths;
- one top-level type per file and no syntax-based dumping-ground folders;
- per-renderer registry/cache isolation and explicit device lifetime;
- binding grammar centralization and immutable render-graph plan validity;
- no Vulkan `[ThreadStatic]` production fields;
- command-buffer and logical-device coordinator size limits;
- compute persistent-resource preparation before recording;
- allocation-free microguards for the primary reuse and frame-coordinator
  paths;
- a one-way ceiling of 72 stateful `VulkanRenderer` partials and 548 fields.

The final item prevents silent monolith regrowth, but it is intentionally a debt
ceiling rather than proof that the facade has reached its ideal final size.
Detailed inventory is in
[`stateful-partial-fields-inventory.txt`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/reports/stateful-partial-fields-inventory.txt).

## Validation Ledger

| Gate | Result | Evidence |
|---|---|---|
| Focused ownership, architecture, lifecycle, binding, and allocation guards | Passed 84/84 | [`vulkan-organization-focused-final-clean.trx`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/reports/vulkan-organization-focused-final-clean.trx) |
| Canonical Vulkan Phase 3 regression task | Passed 110/110 | [`vulkan-phase3-regression-post-runtime-fix.trx`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/reports/vulkan-phase3-regression-post-runtime-fix.trx) |
| Vulkan leaf build | Passed, 0 errors; 140 existing `NU1901`/`NU1902` advisories | [`vulkan-runtime-final-build.log`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/logs/vulkan-runtime-final-build.log) |
| Runtime-rendering kernel build | Passed, 0 errors; 112 existing advisories | [`runtime-rendering-final-build.log`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/logs/runtime-rendering-final-build.log) |
| Editor build | Passed, 0 errors; 560 existing advisories | [`editor-post-runtime-fix-build.log`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/logs/editor-post-runtime-fix-build.log) |
| Fresh isolated Vulkan Unit Testing World/MCP startup | Passed: 0 compute-record failures, allocation-guard mentions, VUIDs, device-loss events, or fatal errors | [`runtime-after-fix-summary.txt`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/reports/runtime-after-fix-summary.txt) |
| SteamVR/OpenXR smoke | Executed but externally blocked before system selection by `ErrorFormFactorUnavailable` | [`openxr-steamvr-smoke-summary.json`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/reports/openxr-steamvr-smoke-summary.json) |
| Canonical Vulkan `Quick` performance lane | Superseded by the strict authored-material acceptance lane below | [`summary.json`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/perf-final-fixed/reports/summary.json) |
| Focused GPU material and bloom contracts | Passed 7/7 | Focused `GpuMaterialReadinessContractTests` Release run |
| Flying-camera Vulkan material/bloom capture | Passed: bloom and final targets finite, varied, and non-magenta | [`investigation`](../../investigations/rendering/archive/vulkan-material-readiness-and-magenta-bloom-2026-07-30.md) |
| Strict authored-material Vulkan lane | Passed: readiness, fallback, reuse, allocation, compact-pass, submission, and VUID gates | [`summary.json`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/perf-material-final-short/reports/summary.json) |

No new compiler warnings were introduced; the build warnings above are the
existing Magick.NET advisory set.

## Runtime And OpenXR Details

The post-fix desktop run used the named isolated session
`codex-vulkan-org-final-fix`. Its Vulkan log contained no compute dispatch
recording failures, allocation guard violations, validation VUIDs, or device
loss/fatal diagnostics.

The canonical SteamVR runner was attempted with the installed SteamVR OpenXR
manifest. The native loader preflight terminated its PowerShell host, so the
lane was rerun with only `-SkipLoaderPreflight`; the editor and the remainder of
the canonical smoke checks stayed enabled. Engine initialization then failed in
`VulkanRenderer.PickPhysicalDevice` because `xrGetSystem` returned
`ErrorFormFactorUnavailable`. No OpenXR instance/system/session/swapchain was
available to validate, so this is a hardware/runtime availability block rather
than an engine smoke pass.

The exact startup failure is under:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-29_20-56-25_pid11200/startup-failure.log`

## Material And Performance Acceptance

The authored-material cohort uses
`XREngine.UnitTests/TestData/Gltf/large-production-scene.gltf`, including its
checked-in `checker.png` base-color texture. Temporal AA is disabled so the
capture displays authored material output directly. `Locomotion` is disabled in
all large-scene Vulkan cohorts, selecting `EditorFlyingCameraPawnComponent`
instead of the cursor-capturing character pawn.

The first magenta stage was `BloomBlurTexture`, not the glTF material. The bloom
copy material declared zero textures and supplied `SourceTexture` only through a
callback, so Vulkan published a fallback descriptor. Bloom now resolves and
owns a stable source texture slot when its declared framebuffer material is
created. The intermediate draw-time texture-list update was rejected because it
fixed color while invalidating primary reuse. Full findings are in
[Vulkan Material Readiness And Magenta Bloom](../../investigations/rendering/archive/vulkan-material-readiness-and-magenta-bloom-2026-07-30.md).

The final strict material capture produced 628 steady-state samples and reports:

- required/ready material rows: `12 / 12`;
- non-ready texture references / invalid IDs / fallback rows: `0 / 0 / 0`;
- eligible primary reuse ratio: `1.0` (`628 / 628`);
- primary command encoding p50/p95: `0 / 0 ms`;
- eligible/raw primary-recording allocation bytes: `0 / 0`;
- unsupported compact passes: `0`;
- submission rejections / validation VUIDs: `0 / 0`.

Evidence: [`summary.json`](../../../../Build/_AgentValidation/20260729-vulkan-runtime-organization/perf-material-final-short/reports/summary.json).
The flying-camera Vulkan capture also confirms that `BloomBlurTexture` and the
final post-process target are finite, varied images rather than solid magenta.

## Remaining Acceptance Work

No implementation or measurable desktop-performance checkbox remains open. Two
acceptance statements stay unchecked:

1. a later migration that reduces the 72-partial/548-field compatibility facade
   below the recorded one-way debt ceiling;
2. a connected SteamVR/OpenXR HMD so system, session, eye-swapchain, submit, and
   teardown paths can execute.

No commit was created.

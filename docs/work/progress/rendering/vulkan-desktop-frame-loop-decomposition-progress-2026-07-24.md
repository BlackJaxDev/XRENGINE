# Vulkan Desktop Frame Loop Decomposition Progress

Date: 2026-07-24
Code completion update: 2026-07-29
Branch: `codex/runtime-modularization-phase4`
Integration HEAD at implementation start: `e5e04190`
Final commit: not created; validation describes the current dirty working tree

## Scope

This note records P4.8b implementation of the
[Vulkan Desktop Frame Loop Decomposition TODO](../../todo/rendering/vulkan-desktop-frame-loop-decomposition-todo.md)
inside `XREngine.Runtime.Rendering.Vulkan`. It does not claim P4.8c collectible
module loading or unrelated renderer behavior.

The working tree already contained the broader Runtime Modularization Phase 4
changes, a modified `Build/Submodules/OscCore-NET9` worktree, and untracked
`Build/Dependencies/vcpkg/`. Those items are not P4.8b outputs.

## Final Structure

`Frame/VulkanRenderer.FrameLoop.cs` is an 89-line coordinator. It captures an
immutable desktop frame identity, creates a stack-only `DesktopFrameAttempt`,
and calls:

1. preflight;
2. captured desktop slot preparation;
3. swapchain acquire;
4. acquired-image preparation;
5. scene/overlay recording;
6. tracked submission;
7. tracked presentation;
8. telemetry and activity release in the outer `finally`.

The exact owner map is maintained in
`XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/README.md`.
Generic frame-op/render-state APIs and render-object creation moved to
`Commands/VulkanRenderer.FrameOpApi.cs`,
`Commands/VulkanRenderer.RenderStateApi.cs`, and
`BackendObjects/VulkanRenderer.RenderObjectFactory.cs`.

## Decisions

- `DesktopFrameAttempt` is a `ref struct`; frame identity and post-acquire
  ownership travel by `ref` through phase methods without a per-frame heap
  context.
- Desktop activity is published atomically with immutable frame number and
  desktop slot. External diagnostics and OpenXR consume coherent snapshots.
- Desktop attempt entry/exit and OpenXR's complete retirement check-and-drain
  interval hold `_desktopFrameRetirementGate`. This provides a real
  cross-thread lease: a desktop attempt cannot enter after OpenXR classifies a
  slot as drainable but before retired-resource destruction completes.
- Desktop in-flight slots and OpenXR eye frame-data slots remain distinct index
  domains.
- `SuboptimalKhr` is an acquiring result. Its binary semaphore/image ownership
  must still resolve exactly once.
- `ErrorSurfaceLostKhr` is a visible renderer failure/restart condition. The
  engine does not call a swapchain-only recreate a surface rebuild.
- Acquire and upload ownership use typed transition states. Final telemetry
  rejects unresolved ownership without masking an existing primary exception.
- Successful queue submit publishes acquire consumption, global/slot/image
  timeline values, and upload ownership before fallible PCL marker, staging
  trim, or diagnostic work.
- Recovery cannot issue new queue work after device loss. Lost-device timeline
  zeroing is not treated as normal completion.
- Collect-visible release occurs before potentially blocking desktop present.
- Native and Streamline acquire/present share typed result policy and the
  tracked queue gateway. Streamline PCL lifecycle helpers live in
  `Features/Upscaling/VulkanRenderer.StreamlineFrameLifecycle.cs`.
- Queue-operation provenance uses explicit stable labels where extraction would
  otherwise change `[CallerMemberName]` history.
- Consecutive non-interactive `NotReady`/`Timeout` results are owned by
  `VulkanDesktopAcquireAvailabilityTracker`. Successful acquisition resets the
  sequence, interactive resize does not grow it, and the third consecutive
  unavailable result requests swapchain recovery.
- Renderer-local deterministic fault injection covers acquire, image
  preparation, scene/overlay recording, submit, post-submit auxiliary work,
  present, and post-present auxiliary work. It uses one packed atomic request,
  not delegates, closures, or external callbacks.
- A healthy queue-submit rejection settles upload and acquired-image ownership
  before propagating a result-specific failure; successful recovery no longer
  hides the rejected submit.
- The former ambiguous completion timestamp is named
  `_lastDesktopFrameTickObservedTimestamp`; it means callback completion/skip
  observation, not GPU completion or presentation.

## Automated Evidence

Commands use the repository-managed existing native bridge:

```powershell
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -v:q -p:XREngineUseExistingNativeBridges=true
```

Result during implementation: zero warnings, zero errors.

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -v:q `
  --filter "FullyQualifiedName~VulkanDesktopFrameLoopPolicyTests|FullyQualifiedName~VulkanP1ValidationTests|FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCoreHardeningPhase51Tests|FullyQualifiedName~VulkanDesktopPlanStabilityTests" `
  -p:XREngineUseExistingNativeBridges=true
```

The final focused gate additionally covered activity-state contention, legal
phase transitions, OpenXR retirement exclusion, source ownership, stable queue
labels, and post-submit settlement:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -v:q `
  --filter "FullyQualifiedName~VulkanDesktopFrameLoopPolicyTests|FullyQualifiedName~VulkanDesktopFrameStateTests|FullyQualifiedName~VulkanP1ValidationTests|FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~WindowOwnershipContractTests|FullyQualifiedName~VulkanCoreHardeningPhase21Tests|FullyQualifiedName~OpenXrStereoTemporalIsolationCompletionTests" `
  -p:XREngineUseExistingNativeBridges=true
```

Result: 206 passed, zero failed, one intentional skip.

The focused source-reader migrations were then validated directly:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -v:q `
  --filter "Name=Vulkan_ImGuiOverlay_UsesExplicitSwapchainLayoutHandoff|Name=VulkanImGuiOverlay_RecordsOutsideReusableScenePrimary|Name=MeshCacheTeardown_RetiresSharedPipelinesAndUniformBuffers|Name=SceneRecordingTiming_IsCapturedBeforeOverlayTimestampReuse|Name=VulkanDeviceLossDiagnostics_TagSwapchainAndOpenXrSubmissions|Name=PipelineLayoutsUseExactTicketDeferredRetirementInsteadOfShutdownRetention|Name=ExactRetirementInvalidationResetsCompletedRecordingsBeforeResourceDrain|Name=SwapchainAttachmentRetirement_AbortsFrameWithoutImmediateRecordingRetry|Name=VulkanBackend_UsesTransactionalGroupEnqueueAndFailsAbortedMarkers" `
  -p:XREngineUseExistingNativeBridges=true
```

Result: 9 passed, zero failed. A broader 21-fixture Vulkan regression run
reached 408 tests: 360 passed and 48 failed on unrelated pre-existing
P4.8a-era source paths/contracts. The assertions changed for P4.8b were
isolated and passed; the remaining failures must not be reported as P4.8b
regressions without separate triage.

The final integrated builds were:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:q -p:XREngineUseExistingNativeBridges=true
dotnet build .\XRENGINE.slnx --no-restore -v:q -p:XREngineUseExistingNativeBridges=true
git diff --check
```

Both builds completed with zero warnings and zero errors. `git diff --check`
reported no whitespace errors; Git emitted only the repository's expected
LF-to-CRLF working-copy notices.

The complete unit-test project was also started with a five-minute bound. It
did not finish within that bound and produced no complete TRX summary, so no
full-project pass/fail claim is made.

Callable policy tests cover acquire/present classification, legal acquire and
upload ownership transitions, unresolved/double-consume rejection, device-loss
recovery policy, and zero-allocation classification. Activity-state tests cover
coherent publication and contention.

## Runtime And Hardware Matrix

The following paths have not yet been run for this working-tree revision and
remain explicitly unvalidated:

| Path | Status |
|---|---|
| Normal Vulkan editor startup | Unvalidated |
| Vulkan Unit Testing World, two inspected camera captures | Unvalidated |
| ImGui and dynamic-text visual comparison | Unvalidated |
| Drag resize, maximize/restore, minimize/restore, DPI/display change | Unvalidated |
| Texture upload and dirty-recording stress | Unvalidated |
| Vulkan standard and synchronization validation layers | Unvalidated |
| OpenXR Vulkan with desktop mirror and retirement coexistence | Unvalidated |
| OpenVR smoke | Unvalidated |
| Native Streamline/DLSS-G swapchain on supported NVIDIA hardware | Unvalidated |
| Fault-injected out-of-date/suboptimal/surface-lost/device-loss matrix | Partially covered by pure policy tests; runtime injection unvalidated |
| `Tools/Measure-VulkanFrameLoop.ps1` p50/p95/p99/allocation comparison | Unvalidated |

No screenshots, RenderDoc captures, GPU/driver comparison, validation-layer log
summary, or hardware performance report is claimed by this note. RenderDoc is
not required unless later visual/log evidence is inconclusive.

Two bounded attempts to start the required isolated editor session were made:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Manage-McpEditorSession.ps1 Start -Name p48b-frame-loop -StartupTimeoutSeconds 180 -AsJson
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Manage-McpEditorSession.ps1 Start -Name p48b-frame-loop -StartupTimeoutSeconds 600 -AsJson
```

In both attempts the private session remained in `Building` with an empty
`build.log` and produced no editor artifact. The named session was stopped with
the session manager after each bound. This is recorded as validation
infrastructure failure, not as a successful or failed Vulkan runtime result.

## Remaining Gates

- No production-code item remains open for P4.8b.
- Complete the full unit-test project beyond the bounded run recorded above.
- Complete the runtime/visual/resize/validation/OpenXR matrix above.
- Record a same-scene frame-loop performance and managed-allocation comparison.
- Validate Streamline/DLSS-G on supported hardware or retain it as explicitly
  unvalidated.
- Record final commit identity and post-integration evidence before promotion.

The allocation/performance comparison and remaining runtime matrix were not
executed during the 2026-07-29 code-completion pass. No performance, hardware,
or new validation claim is inferred from code review.

## Residual Risks

- The deterministic failure seams have not yet been exercised across the full
  runtime fault matrix.
- OpenXR dirty-quiet and pending-timeline startup bypass behavior still requires
  its dedicated concurrency/policy validation.
- Streamline/DLSS-G behavior remains dependent on supported NVIDIA hardware.
- Integration or later Vulkan lifecycle changes require the deferred focused,
  validation-layer, XR, and performance gates before final promotion.

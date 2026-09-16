# Vulkan Default world output and resize pause — 2026-09-14

## Request and status

The user asked why the last default-world run rendered incorrectly under Vulkan, then quickly froze/stopped rendering after a resize. The user subsequently authorized implementation of the rendering correction. Explicit frame-slot ownership, rejection cleanup, cache identity, safe retirement, and paused-frame diagnostics are being corrected and validated in an isolated Vulkan/Advanced session. Saved world settings remain unchanged.

The actual run is `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-09-14_12-09-19_pid51948/`. The incorrect output and terminal pause are explained below. A frame-slot versus swapchain-image ownership mismatch is the strongest underlying cause, with high-confidence source/log correlation; the exact failed allocation-cursor predicate was not logged. No GPU-device hang is demonstrated.

## Actual configuration and failed scene output

`Assets/UnitTestingWorldSettings.jsonc:8` selects the Default world. Lines 46–52 separately select Vulkan, RequireRequested startup policy, and AdvancedRenderPipeline. Default is the scene choice, not the pipeline choice. Runtime `log_general.log:35` loads Default Empty World; `log_rendering.log:74,105` requests and commits AdvancedRenderPipeline#11.

Correction after the user's challenge: the initial explanation incorrectly relied on a stale August 29 architecture paragraph (commit 799f04bbc). Advanced shaded output is implemented. The current Vulkan path loads and dispatches `ShadeNativeOpaque.comp`, writes HDRSceneColor, and runs the Advanced late/post/final-output chain. September 4–8 acceptance records document bounded static-opaque, AO/IBL, temporal/post-processing and OpenGL stereo output. Broader backend/material/profile certification remains separate; it is not evidence of a missing shading producer. The architecture paragraph has been corrected.

The actual failure evidence in this run is rejected Advanced visibility/scene publication: `log_general.log:54` and `log_vulkan.log:353` (12:09:42.799), before any resize. The latter reports retained publication uses from an incomplete generation. Rejected frames present recovery clears, and later frames enter the terminal pause described below. The bad output should be investigated as a failure in an implemented path, with the ownership mismatch as the strongest underlying cause, rather than expected unfinished-shading output.

## Resize-to-pause timeline

| Time / evidence | Observation |
| --- | --- |
| 12:10:41.555 onward; rendering lines 230, 234, 239 | Queue, build, and commit replacement resources at 2560 by 1494. |
| Vulkan lines 623–647 | Resize scaling validates; swapchain recreation completes in 255.868 ms. |
| Vulkan lines 680–682 | Frame 3635 rejects recording because it produced no fresh swapchain terminal; an initialization clear is presented successfully. |
| 12:10:42.155; Vulkan line 688 | Physical-resource descriptor references are released after RetiredRenderResourceGenerationFenceFailed. |
| 12:10:42.385; Vulkan line 700 | Frame 3637 enters RendererPaused / RendererTerminal. Required Advanced scene publication fails because the advanced-scene allocation cursor is unavailable. Readiness elapsed time is 168 ms. |
| 12:10:42.552; Vulkan line 716 | The desktop failure record reports native=Success and no scene submission. |
| 12:10:47.553 onward; Vulkan line 729 and following | Frame-gap warnings start five seconds after the terminal pause and repeat rapidly, growing log_vulkan.log to about 63 MB. |

The resize itself converged. The visible freeze follows the engine's explicit terminal readiness policy, rather than a recorded blocked Vulkan call or a 30-second watchdog expiration. There are no VUIDs, VK_ERROR_DEVICE_LOST entries, or runtime device-loss records in the run. There is no graceful shutdown sequence; the tail remains the warning flood. The later gap warnings are consequences of the pause.

## Strongest underlying cause: two ownership index domains

The run has three swapchain images and two in-flight frame slots (`log_vulkan.log:77`). Those index spaces are not interchangeable.

- Desktop cleanup resets FrameDataArena and releases retained Advanced uses by logical `attempt.FrameSlot` in `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.FrameSlots.cs:58,77`.
- Submission prepares and marks the same logical slot in `.../Frame/Loop/VulkanRenderer.FrameLoop.Submission.cs:149,331`.
- Desktop primary-recording input does not set FrameDataImageIndexOverride in `.../Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs:2101`.
- Primary recording therefore defaults frame-data ownership to the acquired swapchain image in `.../Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs:72`. Advanced publication receives that image-derived slot at line 854.
- `.../Resources/Authority/VulkanAdvancedSceneResourceRuntime.cs:293` rejects a slot that still owns a use as an incomplete generation. Its cursor capture at line 787 also requires a writable arena slot; line 793 emits the observed allocation-cursor error.

The first failure matches this mismatch: Vulkan line 349 publishes Advanced storage in resource slot 0; line 350 completes logical slot 0; line 355 reacquires swapchain image 0 for frame 6 after the logical frame slot advances. Cleanup targets logical slot 1 while publication uses resource slot 0 again. Resize frame 3637 directly records logical slot 1 and acquired image 0 at lines 700 and 713.

This is a concrete source ownership inconsistency matching both observed error messages. However, TryCaptureReservedLaneCursor does not log which individual predicate failed, so the final cursor-state transition remains an inference rather than directly instrumented proof.

## Separate resource-retirement concern

`log_rendering.log:249–250` reports a failed retirement fence and still disposes the previous Advanced resource generation. Vulkan line 688 then releases associated descriptor references. This occurs during active rendering, not shutdown.

`XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs:2185` treats EGpuFenceStatus.Failed as permission to prepare destruction and dispose. A failed/unsubmitted marker does not itself prove that previous GPU ownership has completed. Audit this independently. The descriptor-cache release in `.../Resources/Authority/VulkanResourceRuntime.LifetimeLedger.cs:532` does not reset FrameDataArena, so it is not by itself the direct source of the missing cursor.

## Fix direction

1. Require immutable FrameDataSlotIndex and TimingQuerySlotIndex in prepared recording input. Desktop data uses the logical frame slot; presentation caches and desktop timing remain indexed by the acquired image.
2. Keep FrameDataArena, Advanced publication uses, storage authority, and retained-use cleanup on the same ownership index. Validate that invariant in the actual runtime path.
3. Report the failed cursor precondition and release Advanced uses retained by rejected pre-submit recordings.
4. Audit failed retirement markers; defer destruction or establish completion through a proven owner timeline.
5. Validate the repaired Advanced path directly using the previously accepted shaded-output cohorts plus startup and repeated resize. An explicit DefaultRenderPipeline comparison can isolate the failure, but replacing Advanced is not a fix for its ownership bug.

Implementation is in progress. Primary/schedule cache reuse must include the data slot because recorded descriptors and secondary handles are slot-specific.

## Evidence and validation limits

A native read-only reviewer correlated the exact run with source. `rdc doctor` passed. A RenderDoc capture was unnecessary for the confirmed terminal mechanism because the engine stops in CPU-side readiness before submitting the scene. No editor process was started, controlled, or stopped for this retained-run diagnosis.

Bounded evidence was copied to `Build/_AgentValidation/20260914-105054-inspector-layout/logs/vulkan-user-run-120919/`: the first 780 Vulkan log lines (original numbering preserved), its final eight lines, and complete rendering/general/bootstrap logs. Ignored evidence is disposable; this note records the durable findings. The implementation must pass live Vulkan startup and repeated-resize validation; current progress is recorded below.

## Implementation and live validation progress

- Replaced the nullable frame-data image override with required immutable frame-data and timing-query slot fields. Resource preparation/Advanced publications use the logical slot; swapchain ownership and desktop query completion keep the acquired image.
- Desktop compute readiness prepares only the sealed logical slot, rather than every swapchain image.
- Rejected primary recording releases unsubmitted retained publication uses before marker settlement.
- Primary and command-chain cache identities are data-slot-aware; the outer native command-buffer caches remain image-owned.
- Added failure-only allocator identity, generation, slot/reset epoch, lane/group/chunk state and host-access diagnostics. Paused callbacks advance their observed-tick baseline so a past gap is reported once.
- Missing retirement fences remain pending; failed fences are rearmed while retaining the generation. Disposal requires a Signaled fence. Requesting GPU progress is not permission to force disposal.
- First isolated session `vulkan-ownership-0914`, process 37584, built with 0 warnings/errors and submitted over 5,000 Vulkan/Advanced frames plus an actual window resize without the original incomplete-generation/cursor/RendererPaused failures. This intermediate build is not final acceptance; later cache/lifetime corrections require another build/run.
- The saved Default world disables sky and every direct light. Its initial dark output is therefore not a shaded-output completion test. A directional light was added only to the owned live scene, and the model was focused from two camera positions.
- Viewed composited captures and an HDRSceneTex capture. Model colors remain suspicious in the intermediate build, so visual correctness is not yet accepted. ShadingDiagnostics readback contained 65280 (unoccluded/no-rejection encoding), without non-finite samples. Complete the ownership/cache correction before further shading isolation.
- Initial evidence is under the existing task root: `mcp-captures/vulkan-*`, `mcp-output/vulkan-*`, and `logs/vulkan-ownership-initial/`. The original startup settings file was not edited.
### Final ownership audit additions

The initial correction exposed older image-based ownership in adjacent paths. The coherent model now also covers:

- Scheduled secondaries and dynamic UI have frame-data-slot-specific cache identity; immutable native command ownership remains per acquired image.
- The descriptor rewrite guard checks only the logical frame-slot timeline, rather than inferring an ownership domain from whether the number fits the swapchain image array.
- Mapped uniform arena reset moved to logical-slot completion maintenance, before readiness may write uniforms. Preparation, rollback and accepted-submission publication all use the same logical slot. Image acquisition still owns image-layout/timing completion.
- A second intermediate isolated build passed with zero warnings/errors, and the editor again reached Ready. The final mapped-arena correction and minimized-window guard were subsequently built and exercised as recorded below.

## Final runtime evidence (September 14)

The final logical-slot build ran in owned session `vulkan-ownership-0914` as PID 18120. Its preserved pre-shutdown log contains completed frame-tree receipts through frame 5808 and 13 successful swapchain recreations, with zero `RendererPaused`, missing-allocation-cursor, incomplete-generation, or Vulkan VUID messages. Camera changes, resizing and minimize/restore produced fresh captures. Native subagent review found no remaining correctness blocker in the desktop data-slot, cache-reuse, rejection-cleanup or signaled-only retirement changes. Reserved OpenXR slots were checked in source but were not runtime-validated.

A subsequent minimize experiment exposed another concrete issue: the window's 1x1 minimized extent requested a full Advanced generation, whose five-mip BloomBlurTexture contract cannot be realized at 1x1. This repeatedly rebuilt and failed the generation during minimization. `XRRenderPipelineInstance` now declines desktop generation preparation while the window snapshots report minimized, discards unsubmitted pending resources, and retains the complete active generation. External swapchain outputs are excluded from this desktop guard. Resize notifications that arrive just before the minimized snapshot may still queue a descriptor layout; the render-side guard discards it before materialization.

The final integrated build, including that guard, succeeded with **0 warnings and 0 errors** (90.62 seconds). PID 42692 then reached current Advanced publication/frame **2067**, with the expected Vulkan/Advanced/CpuDirect path and zero CPU fallback. Validation included:

- Startup at 1200x800, a 31-second minimize, restoration, and further surface changes to 1628x994 and 2153x1219. The requested Win32 sizes differed because the diagnostic helper was DPI-virtualized; these are the actual observed viewport extents.
- Seven successful swapchain recreations in that run, including lifecycle convergence, with zero original cursor/generation failures, renderer pauses, Vulkan VUID messages, retirement-fence failures or bloom mip-range failures in the preserved pre-shutdown logs.
- The active Advanced resource generation remained 2 through minimize/restore, then advanced to 3 and 4 for the real resizes. Advanced resource materialization reported 1191.63 ms and 1248.80 ms for those resizes. This is bounded functional recovery evidence, not a general frame-rate benchmark.
- The screenshot after the last resize was viewed, and the camera position and current publication were independently read back. The neutral opaque sphere showed normal unshadowed lighting.

An earlier stressed resize in PID 18120 took 39 seconds to materialize 198 resources and had a 90-second CPU frame. It did resume and did not re-enter the original terminal failure. The later 1.2-second builds do not establish a general fix for all CPU stalls. The final capture still reports roughly 6-8 render Hz for this animated scene; broad CPU performance work remains separate.

Final evidence is under `Build/_AgentValidation/20260914-105054-inspector-layout/`: `logs/vulkan-final-ownership/`, `logs/vulkan-acceptance/`, `mcp-output/vulkan-acceptance-*`, and `mcp-captures/vulkan-acceptance-*`. All owned editor sessions were stopped. The saved world settings and assets were not changed.

## Shaded output: missing source textures, not an absent producer

The red/blue/purple patches were traced to the actual imported material input:

- Mitsuki's `Body` material is named `Skin`, with BaseColor `(0.635206, 0.428728, 0.39646)`, Roughness 0.9, Specular 1, Metallic 0 and Emission 0. The active Advanced `ShadingDebugView` was `Disabled`.
- Its `Skin_BaseColor` texture is 8x8. `XRTexture2D.GetFillerBitmap()` creates exactly that red/blue 8x8 checkerboard for a missing texture.
- The live texture's actual FilePath is `<desktop>/misc/Assets/YAKUpandaupdate/Skin_BaseColor.png`. Both this file and its parent directory were verified absent. A bounded search under the known model directory found Mitsuki.fbx but no Skin_BaseColor texture. The configured `TextureLoadDirSearchPaths` is empty.
- Removing just this missing texture from the disposable live Skin material removed the checkerboard from the skin. No imported asset or saved material was changed. A neutral emissive sphere rendered correctly, and the final neutral sphere shaded correctly with the validation light's CastsShadows disabled.

Restoring the model's intended appearance requires the original texture folder or a configured search root containing those files. Replacing missing content with invented textures or changing the Vulkan shader would hide the actual source problem. A default newly-created shadow-casting validation light left the neutral sphere dark; disabling its shadows isolated working direct shading. That shadow configuration/path was not accepted as correct and remains outside this ownership repair.

A RenderDoc 1.44 capture was saved (`renderdoc/vulkan-shading_frame679.rdc`), but the installed rdc replay module is 1.41 and rejected Vulkan capture version 32 (supports 23). Selecting the installed 1.41 layer loaded its module, but queued captures produced no file. No dependencies or registration were modified, and no RenderDoc inspection success is claimed. The material/file readbacks, controlled mutations, native texture captures and viewed viewport captures establish the missing-texture finding independently.

## Validation scope

No test source was added or changed. The focused test command selected AdvancedFrameSlotUploadContractTests, VulkanDesktopFrameStateTests, VulkanCommandRecordingDependencyTests and VulkanArchitectureLifecycleGuardTests. Its build stopped with CS7036 at VulkanArchitectureLifecycleGuardTests.cs lines 126 and 149: the two existing recording-context constructor calls still use the former nullable slot argument and need explicit frame-data/timing slots. This is caused by the intentional API change, not an unrelated baseline failure. Per the repository testing policy, explicit clearance was requested before updating those calls; no tests executed. The complete output is logs/vulkan-focused-tests.log under the evidence root. This work does not certify every Advanced material, shadow, XR or backend profile. It fixes the demonstrated desktop ownership failure and minimized-generation churn, and identifies the missing content responsible for the character's checkerboard appearance.

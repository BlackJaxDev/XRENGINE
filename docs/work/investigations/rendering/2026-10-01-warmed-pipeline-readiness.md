# Warmed Advanced pipeline readiness allocations

Status: **Validated for the measured allocation-only scope**, October 1, 2026. This is the S13g increment of the [Vulkan stall remediation TODO](../../progress/rendering/vulkan-stall-remediation-results.md). Generation/transaction readiness reuse is **Deferred**. No readiness cache or state-machine change is retained; the original stalls, image-quality report and cumulative acceptance remain open.

## Mechanism and retained change

The selected path is `VulkanAdvancedVisibilityPipelineRuntime.GetReadiness` → `RequestPreparation` → preparation gate → generated-source revision refresh → `CapturePreparationIdentity` → `AreRequiredProgramsCurrent`. Family compute/native/raster getters call it; fresh raster-bin preparation adds calls. Stage enqueue can observe readiness three times through its explicit call and nested support/promotion predicates. All call sites and lock ownership remain unchanged.

`AddProgramIdentity` used `foreach` over `EventList<XRShader>`, whose `IEnumerator<XRShader>` return boxes its list enumerator. The fixture contains 22 non-null programs: **880 allocated bytes per warmed poll**, at approximately 431 polls per completed presentation. The retained two-line change reads the fixed shader count once and indexes the same ordered shader list. Every source revision is still read on every poll.

Source audit found that these privately retained program lists are populated by construction before publication. Generated-source refresh replaces `binding.Generated.Source`; it does not alter program shader membership. Linking, shader invalidation and program destruction do not structurally mutate these lists. Indexed iteration therefore hashes the same inputs in the same order without changing the audited concurrency contract. This reasoning is specific to these owned lists, not a general claim that an arbitrary mutable `EventList` can be snapshotted by its count.

Generated-source refresh, configuration revisions, program link/layout checks, optional-capability checks, async completion identity rejection, cancellation and resource lifetime remain intact. There is no memoized Ready, Pending or Failed result. No inspected existing single epoch covers every required invalidation producer, so broader reuse was not added merely to remove validation calls.

## Predeclared gate and measurement

Before baseline collection, the entry condition was warmed request body above 0.05 ms/completed present **or any recurring managed allocation**. Acceptance required zero measured identity/request-body allocation and either below 0.05 ms/present or at least **25 percent body reduction** when above that threshold, with accepted work preserved, zero foreground compilation joins and no new Vulkan validation errors. The final change passes the relative-reduction arm; remaining readiness work is still above 0.05 ms/present.

Temporary environment-gated probes separately measured preparation-gate wait, held body, generated-source refresh, identity traversal and required-program currentness. Thread-local allocation counters bracketed identity/currentness and the observed request body; Interlocked counters accumulated results. Diagnostic serialization/file IO occurred only during on-demand capture, outside those intervals. Both compared variants used identical probes. Nested segment times and observer overhead were not subtracted. These are warmed attribution measurements, not exclusive CPU cycles, whole-frame speedup, FPS or tail-percentile evidence. The outer resource-runtime getter lock was not separately instrumented.

The first indexed candidate still reread `EventList.Count` for every loop condition. Three stationary windows removed allocation but averaged 0.534594 ms/present, only **22.8 percent** below baseline. That missed the gate; the unfinished motion run was stopped and excluded. Reading the invariant list count once produced the final candidate below.

## Fixture and provenance

Evidence root: `Build/_AgentValidation/20261001-130000-readiness/`. Baseline HEAD: `f81f944967c8ef1e1df5e0c382bde2062e4da905`, with unrelated concurrent profiling changes in the workspace. Named isolated Release editor sessions were used. Full source/probe copies, raw replies, per-window samples, settings, build logs and images are disposable evidence; required conclusions are preserved here. Broker tools were unavailable; an independent native read-only audit supplied source review, not runtime proof.

Vulkan was required with no backend fallback. Fixture: Advanced pipeline, desktop Sponza, deferred materials, light probes off, 1920 × 1080 viewport, 396 GPU commands and 398 tracked renderables in every baseline/final sample. Each variant settled for 45 seconds after setup, then collected three stationary and three moving 60-second windows. Camera A was (-8, 2, 0), looking at (0, 2, 0); motion used x = -8 + 3 sin(t/7), z = 1.5 sin(t/5), with the same look-at. Captures and camera resets occurred between windows. Stationary operation counts matched at 29 headers, with about one family and seven live stages per completed present. Cold/retried admission can move normalized counts slightly away from integers.

The final measured candidate was rebuilt into the stopped owned session, copied into its editor output, hash-checked and launched with `-NoBuild`. The final source was then rebuilt **without probes** and separately passed the lifecycle run. No probe code, environment setting or telemetry schema is retained.

Vulkan assembly SHA-256 values:

- Observed baseline: `9BD269CCE3FF55D85A3BCA443D89953AF0B0B98414C1AC6983FE98906AA2C29C`
- Observed final candidate: `733FB1BA633943FED711DA04A6102DDAC41906C4F508EC8C43654EEE82112C5E`
- Final without probes: `AB5CFF7E4613A34E5B3CECA4AAE69A9DA2BD16C1F41F06F1FD110A2C188638D3`

## Results

The accepted comparison contains **12,609 baseline presents** and **14,184 final presents**. Baseline measured request-body allocation was **4,784,006,480 bytes**; final allocation was **zero**. All 84 diagnostic samples across those windows reported Ready and zero foreground joins. Every completed window had zero new validation errors, a Completed endpoint frame and zero endpoint retirement backlog.

| Variant / window | Presents | Polls / present | Gate wait ms / present | Body ms / present | Identity ms / present | Bytes / poll |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline stationary-1 | 2,636 | 432.134 | 0.011030 | 0.664014 | 0.330902 | 880 |
| baseline stationary-2 | 2,653 | 431.002 | 0.015365 | 0.647472 | 0.316857 | 880 |
| baseline stationary-3 | 2,560 | 431.002 | 0.012698 | 0.768676 | 0.427819 | 880 |
| baseline motion-1 | 1,190 | 429.361 | 0.015566 | 1.083750 | 0.660405 | 880 |
| baseline motion-2 | 1,811 | 430.985 | 0.013454 | 0.749662 | 0.417963 | 880 |
| baseline motion-3 | 1,759 | 431.493 | 0.018564 | 0.872276 | 0.511866 | 880 |
| final stationary-1 | 2,782 | 432.705 | 0.011573 | 0.510811 | 0.163568 | 0 |
| final stationary-2 | 2,767 | 431.168 | 0.013539 | 0.494930 | 0.156951 | 0 |
| final stationary-3 | 2,753 | 431.014 | 0.012107 | 0.491761 | 0.156178 | 0 |
| final motion-1 | 1,873 | 430.366 | 0.011947 | 0.513628 | 0.170350 | 0 |
| final motion-2 | 2,031 | 431.231 | 0.017123 | 0.509187 | 0.170895 | 0 |
| final motion-3 | 1,978 | 431.221 | 0.011917 | 0.503939 | 0.159533 | 0 |

Presentation-weighted cohort means:

| Cohort | Baseline body ms/present | Final body ms/present | Reduction |
| --- | ---: | ---: | ---: |
| Stationary | 0.692559 | 0.499201 | 27.9% |
| Moving | 0.878495 | 0.508836 | 42.1% |

Program-currentness checks allocated zero bytes in both variants and remain live. Baseline gate wait averaged 0.013039 ms/present stationary and 0.015870 moving, identifying held computation/allocation rather than major contention. Native-object endpoint changes totaled +5 for baseline and +1 for final; descriptor totals did not grow in any window. These whole-session counters do not attribute objects to readiness or prove general lifetime closure. No retained cache or resource owner was introduced.

## Lifecycle validation on final code

Live MCP edits changed only in-memory shader text. Original text was restored, and disk SHA-256 checks confirmed both source files unchanged. Existing `RendererReloadFailureInjection.DelayedCompletion` provided a two-second compile delay; the hook was reset after each use and in cleanup. No tests or injection mechanisms were added.

| Exercise | Observed result |
| --- | --- |
| Unchanged warmed family | Ready throughout all performance samples; zero foreground joins. |
| Unrelated `bvh_anyhit` text edit and restore | Remained Ready; attempt count 49 and preparation wall time 560.0489 ms unchanged. |
| Dependent `EarlyVisibility` text edit | Observable Pending with delayed compilation, then Ready; settled frame Completed and retirement backlog zero. |
| Invalid dependent source (`#error`) | Pending → Failed; Failed remained observable until source correction. No false Ready was reported for the settled failing revision. |
| Rapid successive dependent revisions | Second revision applied while Pending; recovered to Ready, with zero foreground joins and settled backlog zero. |
| Original source restoration | Returned to Ready; original disk files unchanged. |
| Full shader reload | Invalidated 122 shaders; Pending → Ready; settled frame Completed and backlog zero. |
| Renderer replacement initiated while Pending | Replacement succeeded without rollback; frame authority changed 1 → 2. New runtime reached Ready, then 216 completed presents with zero backlog and a recovered scene image. |

All final mutation snapshots reported zero Vulkan validation errors and zero foreground compilation joins. The static backend's reload metadata generation remained 0; the new frame authority, resource generation and re-created runtime provide the replacement evidence. The shader stale-result rejection counter remained zero. The run exercised overlapping revisions and pending replacement but **does not claim deterministic observation of a rejected late shader completion**; such a forced race remains required before introducing broader readiness reuse. Existing source/configuration/link and stale-completion guards were preserved.

An earlier ten-second post-replacement capture was black while exact texture admission retried `RequiredUploadCompletion` for a superseded upload generation. It subsequently resumed Completed frames. Rebuilding the original readiness loop and repeating the mutation/replacement sequence reproduced the same transient rejection, then recovered. The final uninstrumented candidate also recovered, and its settled image was inspected. This is cold upload/scene recovery under the existing owner, not evidence of a readiness-loop regression or a closed cold-admission performance issue. Waiting only for the first editor frame was insufficient to judge scene output.

## Validation tooling and checks

During validation, editor-session retention encountered RenderBench manifests in the shared registry and threw because they have no `editorPath`. A small guard now excludes non-editor manifests from editor retention and listing, leaving their lifecycle to the owning tool. Live Start and List succeeded with those manifests present; the RenderBench directories remained intact. Named Stop continued to operate only on the owned editor.

Release editor builds and the final Vulkan leaf build succeeded with zero warnings/errors. The runtime log scan found no Vulkan validation errors, device loss or unhandled exception matches; deliberate shader compile failures were expected injection evidence. Named sessions were stopped. Managed stop can force termination and is not by itself a clean-native-teardown certificate.

Existing focused NUnit selection: **15 passed, 5 failed, 20 total**. All 10 `ShaderSourceDependencyHotReloadTests` cases passed, along with five pipeline tests including the non-calling-thread async compile check. Five `VulkanPipelineCompilationP05Tests` source-text checks failed:

- `BackgroundGraphicsCompilation_UsesIsolatedPersistentCacheAndCompileRequiredProbe`: old cache-call spelling.
- `AsyncCompileQueue_IsBoundedAndPublishesCompletedPipelines`: expects `int capacity = workerCount;`, while current code uses a bounded clamp.
- `ShaderAndProgramMutation_DrainNativePipelineCompilesBeforeDestroyingDependencies`: old mutation-gate spelling.
- `SharedPipelineLibraries_ReserveBeforeEnteringTheDriver`: searches an owner file from which the creation path moved.
- `EveryMeshRecordingPath_PrewarmsBeforeBeginningCommandRecording`: searches methods moved from the old owner file.

`git diff --exit-code HEAD` returned zero for the test file and every source file at these failure points. These failures therefore predate this change; they do not read the changed identity loop. **The broader regression selection is not fully green.** Tests were not edited or bypassed. Its maintenance remains separate; live pending/failure/reload/replacement evidence above validates this increment. Results: `reports/tests/readiness-regressions.trx` under the evidence root.

## Limits and next decision

Baseline and final captures from two positions show responsive Sponza geometry; the final post-replacement capture shows scene recovery. Existing severe dark regions and editor highlighting preclude a pixel-parity or image-quality acceptance claim. Stereo, hardware XR, OpenGL, arbitrary shader-list structural mutation and deterministic late-completion rejection were not newly exercised. No RenderDoc diagnosis of the existing shading issues is claimed.

This completes the selected allocation-removal increment and its measured gate. Generation/transaction readiness caching remains deferred; reopen only with a remaining cost target and complete invalidation/lifetime proof. S13h is the next scheduled item. S13i cumulative timing, S15 temporal/image quality and the original reported stall remain open.

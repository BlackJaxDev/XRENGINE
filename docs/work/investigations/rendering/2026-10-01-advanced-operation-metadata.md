# Advanced operation metadata scan attribution

Status: **Deferred/Not Applicable**, October 1, 2026. The selected Advanced family discovery/filter scans are below the predeclared entry threshold and allocate nothing. No structural cache or runtime behavior change is retained. This is the S13f disposition for the [Vulkan stall remediation TODO](../../progress/rendering/vulkan-stall-remediation-results.md); it is not a Fixed or Validated optimization claim. Next permitted item: S13g, with a fresh comparable readiness baseline.

## Scope and source audit

The selected owner is `VulkanCommandRuntime.TryPrepareAdvancedVisibilityOperations` and its two family passes in `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs`. For N operation headers and F output families it visits N(1 + 2F) headers; only the matching stages execute the live preparation and association bodies.

`FramePlanBuilder.BuildAndSeal` acquires writable storage, resets and lowers each new accepted frame, then assigns a new generation and publishes. Pre-acquire readiness and recording share that sealed plan, but Advanced discovery runs during recording. An index published on each new plan would move one scan into lowering, not eliminate per-frame construction. Plan-lease reservation collection runs once when attaching ownership; it does not recur as a cache miss on an unchanged pinned plan.

The variant-manifest cache already retains built requirement metadata, and admitted frame-data scratch already retains its compatibility ledger. Manifest demand hashing still scans operations before lookup. Caching that by plan generation alone is unsafe: static, overlay and logical-eye streams have different ordinal namespaces; accepted target revalidation changes backing compatibility without rebuilding the logical plan; dynamic-rendering mode and submission strategy are call inputs; live wrapper binding IDs can change over their lifecycle. The hashed program-link generations are captured payload fields, not complete live shader-currentness validation. These other scans were audited, not declared cheap by the selected scan counters. Any measured demand-hashing bottleneck must reopen this item with the complete stream/target/mode/binding identity contract.

## Entry condition declared before measurement

- At least **0.05 ms mean structural discovery/filter time per completed presentation** in repeated warmed windows, or recurring heap allocation attributable to those scans.
- Three 60-second stationary and three 60-second moving-camera windows for desktop, repeated with emulated stereo. The threshold is an opportunity budget, not a post-change tolerance or a claimed FPS improvement.
- Count visits, discoveries, families, stages, elapsed ticks and thread-local allocated bytes. Exclude live scene preparation, resource realization, target validation, readiness and association work.
- If below threshold and allocation-free, defer. If actionable, declare matched performance tolerances from baseline variability before implementing an index and validate the full mutation/lifetime matrix.

## Measurement method and identity

Temporary opt-in counters extended the existing publication telemetry. Discovery timing ended before family preparation. The two filter loops were temporarily extracted into a source-order next-operation scanner timed only across header/reservation filtering. Each match returned to the unchanged live stage body. Independent review checked index advancement, early `continue`, source order, visit totals and snapshot field order. Telemetry publication was outside the measured interval; no per-operation strings, lists or arrays were added. The temporary patch is preserved as disposable `reports/observation.diff` and was removed after measurement.

Time per presentation = delta scan ticks / stopwatch frequency / delta completed desktop presents. The stopwatch frequency was 10,000,000 ticks/s. In stereo the numerator contains all three families and the denominator remains desktop presents. Independent snapshot boundaries cause small fractional deviations from exactly 1/3 families or 7/21 stages. Scan sampling itself contributes overhead, so comfortably below-budget scan readings are conservative for this owner. No whole-frame observer-overhead or performance-improvement claim is made; subsequent optimization work must establish its own comparable baseline.

Initial checkout: `f81f944967c8ef1e1df5e0c382bde2062e4da905`, plus preserved in-progress component-profiling changes. This is **not** the September 26 binary. Release editor build: **0 warnings, 0 errors**. Named session `plan-scan-baseline` used isolated settings, Vulkan Advanced rendering, standard validation enabled, and publication telemetry enabled. Desktop and stereo reused the exact same binaries through a managed `-NoBuild` restart. Synchronization validation was not enabled. SHA-256:

- Editor: `E1511BEA49F5AA99694A49C01A962901AA881CAC9A66A7AA9490EBAA741909A5`
- Runtime.Rendering: `C7A58157D94F60D9D41C09B7FA0CD8C758AC872226C06C8FA2C382B8B912E88B`
- Runtime.Rendering.Vulkan: `F31287860EBE010B543DF98D8119696A2520CBA96BED5744339C8D931B6299C2`

Fixture: Sponza at scale 0.01, origin translation, deferred material mode, light probes off, 1920 x 1080 desktop output. The desktop scene snapshot contained 396 GPU commands and 398 tracked renderables; this is not an exact recreation of the older 393-draw binary. Camera A was (-8, 2, 0), looking at (0, 2, 0). Motion used x = -8 + 3 sin(t/7), z = 1.5 sin(t/5), with the same look-at point. The stereo cohort added two emulated sequential eye outputs; the scripted motion moved the desktop camera, not a physical headset. Warmup was 90 seconds per launch, in addition to scene setup; the resumed stereo-motion cohort settled for 15 seconds on the already warm same process.

Evidence root: `Build/_AgentValidation/20261001-120000-s13f-plan-metadata/`. Raw replies, per-window samples, images, settings, hashes, the patch and logs are disposable; the numerical results and limitations below are durable. Broker tools were not exposed; a native read-only audit supplied independent source review, not runtime proof.

## Results

Twelve completed windows, **19,585 completed desktop presentations**. Every row has zero measured scan allocation, zero additional Vulkan validation errors, zero endpoint retirement backlog, and a Completed endpoint frame. Native/descriptor deltas below are total session resource counters, not allocations by the scan.

| Cohort / window | Presents | Scan ms / present | Header visits / present | Families / present | Native / descriptor delta |
| --- | ---: | ---: | ---: | ---: | ---: |
| desktop stationary-1 | 2,263 | 0.001574 | 87.0 | 1.000 | +0 / +0 |
| desktop stationary-2 | 2,738 | 0.001275 | 87.0 | 1.000 | +0 / +0 |
| desktop stationary-3 | 2,775 | 0.001210 | 87.0 | 1.000 | +0 / +0 |
| desktop motion-1 | 1,868 | 0.001772 | 231.4 | 0.999 | +97 / +80 |
| desktop motion-2 | 2,032 | 0.001613 | 218.7 | 1.000 | +0 / +0 |
| desktop motion-3 | 1,742 | 0.001866 | 241.9 | 1.000 | +2 / +0 |
| stereo stationary-1 | 1,271 | 0.006525 | 644.1 | 3.000 | +0 / +0 |
| stereo stationary-2 | 1,172 | 0.007432 | 686.3 | 3.000 | +0 / +0 |
| stereo stationary-3 | 1,162 | 0.008352 | 643.6 | 2.997 | +0 / +0 |
| stereo-motion motion-1 | 843 | 0.008705 | 1360.1 | 3.000 | +14 / +0 |
| stereo-motion motion-2 | 738 | 0.009269 | 1244.4 | 3.000 | +0 / +0 |
| stereo-motion motion-3 | 981 | 0.007311 | 1200.0 | 2.997 | +0 / +0 |

Desktop stationary uses 29 operation headers (87 total scan visits per presentation); moving views raise the operation count. Stereo measures approximately three families and 21 live stages per presentation. Even the largest window mean, **0.009269 ms**, is below the 0.05 ms entry threshold. No p95/p99 or frame-rate improvement is claimed.

The first desktop motion route added 97 native objects / 80 descriptor sets; the next repeat added neither, and the last added two native objects without descriptor growth. The stereo-motion deltas are retained above rather than treated as proof of complete lifetime closure. No new retained metadata was introduced, and these totals do not identify an owner or prove a leak.

The first stereo-motion attempt was interrupted by Python `getaddrinfo` error 11003 for `localhost`. The collector switched to numeric loopback while preserving the registered HTTP Host, confirmed the same named session, and reran all three motion windows. The interrupted window is excluded; its earlier raw replies remain evidence, not a pass.

## Visual and diagnostic limits

Desktop captures from different positions show the Sponza interior and respond to camera changes. Exposure and hover highlighting change, so captures are not pixel-identical parity evidence. Both emulated eyes contain scene geometry with parallax, but exhibit severe dark/high-contrast clipping and thin magenta lines. **Stereo image correctness is not validated by this work.** Preserve that as an image-quality reproduction for the temporal/original-report owner (S15), not as a passing cumulative gate or a diagnosis of its cause.

All measured windows reported no new validation errors; startup loader and unconsumed-vertex-attribute warnings remain recorded. Captured session logs contain no matching validation-error/device-loss/exception failures. Managed stop verifies ownership and may forcibly terminate the process; this work does not certify clean native teardown. The named session was stopped after collection.

Failed reservation/capacity discovery returns before publishing a partial discovery sample. The accepted warmed cohorts support the selected cost decision, not malformed-plan rejection coverage. No cache was implemented, so the cache-specific mutation/order/retirement gate is not applicable. Adjacent existing stage-order and stream-sorting tests do not exercise primary stale/duplicate rejection; no new or modified tests were needed for this documentation-only disposition.

## Reopening and next work

Reopen if repeated same-workload measurements exceed 0.05 ms per presentation, scan-attributable allocation appears, operation/family counts materially increase, or independent attribution identifies recurring manifest/other structural work as significant. If implemented later, retain only ordered indices grouped by exact reservation; use stream identity/mutation revision and owning sealed-plan generation, with separate logical-eye index namespaces. Keep request validity, mutable target size/backing, package freshness, reservation leases, stage coverage/order, producer readiness and native association checks live. Never freeze Pending or retryable failure behind a structural cache key.

The temporary telemetry/scanner source changes were reversed only from this item's saved patch, preserving unrelated work. The retained change is this evidence record plus the TODO and investigation index. S13g can now measure warmed pipeline-readiness cost; S13i/S15/S16 remain open.

# S12: Shared Advanced Preparation

Status: Active. S12 is a measurement-led disposition, not authorization to replace the shared extractor's lifetime model.

## Entry Evidence And Hypothesis

The Vulkan/Advanced Unit Testing World currently publishes 393 canonical draws and 393 indirect ranges for one desktop view. The first live Release sample showed 158 rebuilds, 790 same-frame cache hits, 131 successful family copies, no copy failures, and 108,468 bytes copied per family. After warm-up, an additional 1,144 rebuilds consumed approximately 0.449 ms each in `AdvancedPreparationExtractor.Build`, of which approximately 0.159 ms was command extraction. The original 153-165 ms stall has not been reproduced by this workload.

The first candidate for the residual cost is `AdvancedIndirectRangePlanner.Build`: it linearly searches existing ranges once per payload in each of two passes. With 393 unique range keys, that is 393 squared key comparisons per rebuild. This is a source-level hypothesis, not yet a measured attribution. The check that can reject it is a per-phase timer showing that range planning is small relative to the rebuild or that warmed build time is already below the entry threshold. Arena growth and shared-lock contention are separate candidates and must not be inferred from the rebuild duration.

## Ownership And Acceptance Set Before Behavioral Change

- `RenderWorldSnapshotPublication` is first-wins for a frame. Actual callers cannot alternate worlds within one published frame today. The singleton service holds one extractor under `_sync`; changing that cache key or introducing per-world storage would require a separate upstream world-publication and lifetime review.
- Extractor columns mutate on the next build and when views are added. OpenGL copies into renderer-owned arrays; Vulkan copies into an authoring lease under the service lock and copies once more into a deferred output-family plan. Keep those coherent copy boundaries and exact publication/view/generation checks. Do not pass mutable spans to deferred consumers or build outside `_sync` in S12.
- Gather three settled 60-second stationary windows before and after any behavioral change, with identical Release Vulkan/Advanced settings and a 393-draw/393-range Sponza scene. Record frame/rebuild/hit/copy counts, draw/range/job counts, stopwatch-frequency-normalized phase and lock timings, copied bytes, allocations, arena growth and retired generations. Sample diagnostics only at window boundaries. Also exercise camera motion and one scene/view change. If the workload or accepted frame identity changes, discard that comparison.
- Treat range planning as actionable only if its warmed mean is at least 0.15 ms per rebuild and at least 25% of total build mean. If changed, require at least a 20% reduction in warmed build mean across matched windows, with no more than 0.05 ms mean lock wait per acquisition and 0.10 ms mean family-copy time, zero copy/publication mismatches, no new per-build allocation, no unbounded arena or lease retention, and no correctness change in stationary/moving images or publication identities. A slower neighboring stage or a changed scene invalidates a claimed speedup.
- If those entry conditions fail, retain low-cost telemetry and explicitly defer the range/storage optimization. A live check of the ordinary Vulkan path is still required; a successful build is not the gate.

The named isolated editor session is `s12-sharedprep-0922`; task evidence is under `Build/_AgentValidation/20260922-164422-s12-shared-prep/`. No regression tests are added or run without the repository's post-validation test clearance.

## Evidence And Disposition

The instrumented `XREngine.Runtime.Rendering` build passed with zero warnings and errors. The same source was built into the named isolated Release editor with zero warnings and errors. A Vulkan viewport capture was saved and viewed; it showed the running scene with a highlighted edge, though the initial camera was too close to a surface to use as a broad visual-correctness comparison. Runtime introspection confirmed 393 draws and 393 ranges, one view, one rebuild with five same-frame hits per prepared frame, a valid canonical publication, zero deformation jobs, zero copy failures, and no per-frame managed allocation after startup.

Three consecutive, settled, approximately 60-second windows from that process:

| Window | Rebuilds / hits / copies | Build mean | Extraction mean | Family copy mean | Acquire lock wait mean | Copied bytes / family |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 750 / 3,750 / 750 | 0.439 ms | 0.156 ms | 0.0109 ms | 0.000049 ms | 108,468 |
| 2 | 798 / 3,990 / 798 | 0.423 ms | 0.139 ms | 0.0103 ms | 0.000041 ms | 108,468 |
| 3 | 817 / 4,085 / 817 | 0.422 ms | 0.150 ms | 0.0102 ms | 0.000040 ms | 108,468 |

The copy byte counter covers extractor-to-authoring retention only. Vulkan subsequently copies the same six columns from that lease into an output-family plan; its count/timing is not separately instrumented here. `BuildTicks` excludes cache-hit view additions, which could matter with new XR view sets. No lock-wait tail distribution or arena growth/retirement snapshot was captured. Thus these means disprove shared-lock contention as a *steady-state mean* issue in this one desktop scene, but they do not clear the S12 contention/lifetime gate or prove the range planner is the remaining owner. Deformation, changed geometry/materials, multiple views, actual alternating worlds, supersession, and teardown still need focused live checks. Upstream first-wins frame publication currently prevents same-frame alternating worlds; its exact-publication stability is an assumption to verify if that contract changes.

The last unvalidated phase-timing extension was removed before wrap-up. The retained source change is only the telemetry that produced the live numbers. No planner algorithm, cache key, storage ownership, or launch setting was changed. S12 remains **Active** and S13 must not start from this partial gate. The named validation editor was stopped; no regression tests were added or run pending explicit post-validation clearance.

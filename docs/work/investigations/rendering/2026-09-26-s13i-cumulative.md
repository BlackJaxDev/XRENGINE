# S13i: cumulative publication/recording reproduction gate

Status: Validated for reachable scope (September 27, 2026, desktop Vulkan).
Four matched Release binaries were measured in one interleaved matrix on the
S13a fixture. The frozen S13a baseline still reproduces the reported defect
(render waits on collect for 53.5 ms at p95 and presents a stationary frame
every 61.8 ms at p50); the cumulative S13 state presents a stationary frame
every 8.6 ms at p50 with the wait at 0.86 ms p95. Against the previous
validated increment the S13f-S13h changes remove 3.1 ms from the stationary
render p50 and 3.1 ms from the motion render p50, cut command recording
allocation from 568 KB to 210 KB per stationary frame, and leave workload
identity, feature state, GPU coverage, resource endpoints and images
unchanged. Two predeclared criteria are not met as literally written and are
dispositioned below as owned outside S13 (the stationary worst frame and the
native resource endpoint). The motion-window tail, the OpenGL path and the
original frame-rate report remain open and are handed to named owners; they
are not closed by this gate.

Gate record for
[S13i](../../progress/rendering/vulkan-stall-remediation-results.md)
under the todo document's one-by-one protocol. Evidence root:
`Build/_AgentValidation/20260926-205821-s13i-cumulative/` (ignored, disposable;
findings are copied here). Related gate records:
[S13a](2026-09-23-s13a-publication-attribution.md),
[S13b](2026-09-23-s13b-identity-feedback.md),
[S13c](2026-09-26-s13c-registration-retention.md),
[S13d](2026-09-26-s13d-auxiliary-state.md),
[S13e](2026-09-26-s13e-family-preparation.md),
[S13f](2026-09-26-s13f-plan-metadata.md),
[S13g](2026-09-26-s13g-pipeline-readiness.md),
[S13h](2026-09-26-s13h-critical-section.md).

## Matched binaries and measurement configuration

| Label | Source | Role | Editor DLL / Rendering.Vulkan DLL / Rendering DLL SHA256 (first 16) |
| --- | --- | --- | --- |
| D | `cee30cd57`, clean | S13a frozen pre-instrumentation baseline (the reported workload) | `BEC635AE6D4DE5BE` / `821A51050AB836CF` / `9E95D330EE1C8F74` |
| A | `7ab827983`, clean | Source before the September 26 S13b-final and S13c-S13e commit | `C29E6770085B549C` / `A925182644D56A1E` / `22E5B1326EE43C43` |
| B | `9fee4b983`, clean | Previous validated increment (S13e) | `00CD19A43FC3E06F` / `DB7B8118FF70C89B` / `1C62C695AFB3375B` |
| C | `9fee4b983` plus the S13f-S13h tracked diff (957-line patch and the two new files `VulkanPreparedStableBinSortKey.cs` and `S13aAdvancedFamilyStep.cs`) | Cumulative state; its eight changed files are byte-identical to the main checkout | `31DF2DDD3419CA4D` / `A643DA5A08FB1833` / `149C70DF68E6BA7A` |

All four are Release editors built with zero errors and zero warnings in
detached git worktrees placed beside the checkout
(`<repo-root-parent>/XRENGINE-s13i-A`, `-BC`, `-D`; B and C share one worktree
and differ only by the patch). Build command per worktree:

```text
dotnet build XREngine.Editor/XREngine.Editor.csproj --configuration Release
  --artifacts-path <worktree>/Build/_AgentValidation/<artifacts>
  -p:Platform=AnyCPU -p:RestoreIgnoreFailedSources=true
  -p:XREngineUseExistingNativeBridges=true -p:UseSharedCompilation=false /nodeReuse:false
```

Every binary ran wholly inside its own worktree, so the repository root,
working directory, shaders, engine assets and log folder belong to the
binary's own commit. The harness (`Tools/Measure-GameLoopRenderPipeline.ps1`,
SHA256 `1354FBFA01F11018...`, unchanged from the main checkout) and the fixture
were copied into each worktree, so measurement code and scene settings are the
same files for all four. Shader sources differ between the commits (five files
under `Build/CommonAssets`), which is why the binaries were not run against
the main checkout's assets.

Configuration: fixture `unit-world-s13i.jsonc` (SHA256
`4533B5F71F644DF075CC8665EB5FBBE8640FEDADBADF6BBF8A608DB49052FB17`, the
S13f-derived copy of the S13a fixture: Sponza2 OBJ import without
`OptimizeMeshes` or post-import merging), camera A `(-20, 2, 4)` looking at
`(-20, 2, -8)`, controlled motion to `(-15, 3, 2)` looking at `(-15, 3, -10)`,
CpuDirect, `DevelopmentProfile`, one-frame sampling, 120-second warmup,
60-second stationary window, 60-second motion window, `-NoStabilityGate`,
telemetry off, no debugger, Vulkan validation off, uncapped presentation.
Order: D, A, B, C, D, A, B, C, after one unmeasured priming run per binary to
warm each worktree's import and shader caches.

Effective state reported by every capture manifest: NVIDIA GeForce RTX 3090,
Vulkan, Advanced render pipeline, 1920x1080 display, 1286x723 internal, TSR at
0.67 render scale, MSAA sample count 4, motion vectors requested, ambient
occlusion, bloom, motion blur and auto exposure off, `DevParity` GPU-driven
profile. Host: AMD Ryzen 9 7950X3D, Windows driver `32.0.16.1714`, High
performance power scheme, mains power. This is a different machine from the
one in the S13a record (RTX 4070 Laptop GPU), so absolute numbers in the two
records are not comparable; the same-day baseline D is the reference here.

## Predeclared acceptance

Declared in the September 26 in-progress version of this record, before any
capture completed, and unchanged:

1. C must not regress A or B on stationary or motion render p50, p95, p99 or
   max beyond the S13a run-order allowance (mean within 0.5 ms; percentiles
   within the larger of 5% or the paired spread over median).
2. C keeps one workload identity per window, equal across the stationary and
   motion windows.
3. The admission screenshot is captured and accepted.
4. Coarse GPU coverage stays at the S13a level (99.9%).
5. C ends within one native live resource and zero descriptor sets of its
   start.
6. Images of the binaries are identical apart from known temporal noise.
7. No speedup may depend on missing draws, changed identity, reduced coverage
   or reduced quality.

Falsifier: any C regression beyond the allowance, any identity, coverage or
image difference between B and C, or a C-only retention change.

## Results: all-frame distributions

Columns "Other editors" count editor processes from another session at the
start and end of each run (a Debug VR editor from another MCP session started
and stopped on its own schedule; it was not stopped for these measurements).
"Environment" classifies which environment map the run drew (see the scene
content section). Render and wait columns are the harness statistics; present
interval columns come from every frame of the editor's per-frame stream inside
the same window.

### Stationary window, per run (ms)

| Run | Other editors start/end | Environment | Samples | Render avg | Render p50 | Render p95 | Render p99 | Render max | Render-collect wait p50 | Wait p95 | Wait p99 | Present interval p50 | Present p95 | Present p99 | Present max | GPU p50 | GPU coverage % |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| s13i-A-r1 | 0/0 | E1 | 4961 | 11.295 | 11.056 | 13.874 | 15.701 | 34.379 | 0.800 | 0.920 | 0.994 | 11.899 | 14.655 | 16.561 | 35.339 | 5.857 | 99.940 |
| s13i-A-r2 | 0/0 | E2 | 5015 | 11.256 | 10.919 | 13.537 | 15.397 | 34.067 | 0.706 | 0.817 | 0.880 | 11.683 | 14.255 | 16.149 | 34.759 | 5.054 | 99.940 |
| s13i-B-r1 | 0/1 | E2 | 4836 | 11.563 | 11.243 | 14.224 | 16.516 | 33.728 | 0.845 | 1.003 | 1.136 | 12.132 | 15.101 | 17.392 | 34.699 | 6.646 | 99.938 |
| s13i-B-r2 | 0/0 | E3 | 5214 | 10.801 | 10.414 | 12.921 | 14.544 | 34.690 | 0.696 | 0.819 | 0.890 | 11.198 | 13.649 | 15.280 | 35.429 | 5.077 | 99.942 |
| s13i-C-r1 | 1/0 | E2 | 6607 | 8.295 | 7.983 | 10.589 | 12.993 | 36.388 | 0.774 | 0.907 | 1.000 | 8.815 | 11.417 | 13.732 | 37.307 | 5.912 | 99.955 |
| s13i-C-r2 | 0/0 | E4 | 6962 | 7.912 | 7.503 | 10.202 | 12.314 | 38.212 | 0.688 | 0.817 | 0.876 | 8.286 | 10.932 | 13.026 | 38.877 | 5.424 | 99.957 |
| s13i-D-r1 | 0/0 | E2 | 1399 | 11.830 | 11.566 | 13.392 | 18.489 | 24.793 | 15.229 | 54.179 | 58.335 | 62.164 | 66.944 | 71.824 | 92.866 | 4.889 | 99.857 |
| s13i-D-r2 | 0/0 | E5 | 1420 | 11.669 | 11.432 | 12.806 | 19.094 | 25.425 | 18.954 | 52.823 | 57.551 | 61.343 | 65.667 | 70.945 | 80.927 | 4.252 | 99.859 |

### Stationary window, means of two runs per binary (ms)

| Binary | Role | Frames per second | Render avg | Render p50 | Render p95 | Render p99 | Render max | Wait p50 | Wait p95 | Present interval p50 | Present p95 | Present p99 | Present max | GPU p50 | Record p50 | Record p95 | Encoding p50 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| D | S13a frozen baseline | 23.406 | 11.750 | 11.499 | 13.099 | 18.791 | 25.109 | 17.091 | 53.501 | 61.754 | 66.305 | 71.385 | 86.897 | 4.571 | 7.732 | 8.357 | 2.155 |
| A | pre-S13c source | 82.840 | 11.276 | 10.988 | 13.706 | 15.549 | 34.223 | 0.753 | 0.869 | 11.791 | 14.455 | 16.355 | 35.049 | 5.456 | 7.402 | 8.039 | 2.170 |
| B | S13e increment | 83.445 | 11.182 | 10.829 | 13.572 | 15.530 | 34.209 | 0.770 | 0.911 | 11.665 | 14.375 | 16.336 | 35.064 | 5.861 | 7.251 | 7.879 | 2.229 |
| C | S13f-S13h cumulative | 112.668 | 8.104 | 7.743 | 10.396 | 12.654 | 37.300 | 0.731 | 0.862 | 8.550 | 11.175 | 13.379 | 38.092 | 5.668 | 4.258 | 4.752 | 2.152 |
| C minus B | | +29.223 | -3.079 | -3.085 | -3.177 | -2.876 | +3.091 | -0.039 | -0.049 | -3.114 | -3.200 | -2.957 | +3.028 | -0.193 | -2.992 | -3.126 | -0.076 |
| C minus A | | +29.828 | -3.172 | -3.245 | -3.310 | -2.895 | +3.077 | -0.022 | -0.007 | -3.241 | -3.280 | -2.976 | +3.043 | +0.212 | -3.143 | -3.287 | -0.018 |
| C minus D | | +89.261 | -3.646 | -3.756 | -2.704 | -6.138 | +12.191 | -16.360 | -52.639 | -53.203 | -55.131 | -58.006 | -48.805 | +1.098 | -3.474 | -3.604 | -0.003 |

### Controlled motion window, per run (ms)

| Run | Other editors start/end | Environment | Samples | Render avg | Render p50 | Render p95 | Render p99 | Render max | Render-collect wait p50 | Wait p95 | Wait p99 | Present interval p50 | Present p95 | Present p99 | Present max | GPU p50 | GPU coverage % |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| s13i-A-r1 | 0/0 | E1 | 1675 | 34.959 | 12.796 | 73.551 | 78.489 | 108.190 | 0.959 | 1.101 | 1.233 | 13.727 | 74.566 | 79.561 | 109.343 | 6.610 | 99.821 |
| s13i-A-r2 | 0/0 | E2 | 1705 | 34.392 | 12.506 | 72.434 | 75.817 | 104.094 | 0.891 | 1.015 | 1.139 | 13.411 | 73.300 | 76.831 | 105.058 | 5.793 | 99.824 |
| s13i-B-r1 | 0/1 | E2 | 1623 | 36.062 | 13.123 | 76.848 | 83.414 | 100.881 | 0.998 | 1.198 | 1.393 | 14.145 | 77.894 | 84.375 | 101.844 | 7.188 | 99.815 |
| s13i-B-r2 | 0/0 | E3 | 1717 | 34.140 | 12.121 | 72.031 | 75.652 | 97.333 | 0.890 | 1.013 | 1.163 | 12.991 | 72.927 | 76.729 | 98.048 | 5.825 | 99.825 |
| s13i-C-r1 | 1/0 | E2 | 1813 | 32.214 | 9.780 | 71.577 | 76.374 | 94.189 | 0.954 | 1.117 | 1.285 | 10.780 | 72.472 | 77.530 | 95.808 | 6.633 | 99.835 |
| s13i-C-r2 | 0/0 | E4 | 1917 | 30.484 | 9.194 | 67.653 | 70.085 | 93.401 | 0.883 | 1.028 | 1.180 | 10.050 | 68.563 | 70.976 | 94.396 | 5.971 | 99.844 |
| s13i-D-r1 | 0/0 | E2 | 913 | 34.483 | 13.105 | 72.616 | 78.110 | 89.425 | 50.468 | 53.886 | 61.683 | 65.170 | 121.608 | 130.148 | 138.561 | 5.388 | 99.781 |
| s13i-D-r2 | 0/0 | E5 | 926 | 34.105 | 13.005 | 71.843 | 77.429 | 90.070 | 17.162 | 52.973 | 60.563 | 64.334 | 120.123 | 128.814 | 135.577 | 4.962 | 99.784 |

### Controlled motion window, means of two runs per binary (ms)

| Binary | Role | Frames per second | Render avg | Render p50 | Render p95 | Render p99 | Render max | Wait p50 | Wait p95 | Present interval p50 | Present p95 | Present p99 | Present max | GPU p50 | Record p50 | Record p95 | Encoding p50 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| D | S13a frozen baseline | 15.274 | 34.294 | 13.055 | 72.230 | 77.769 | 89.748 | 33.815 | 53.430 | 64.752 | 120.865 | 129.481 | 137.069 | 5.175 | 8.532 | 47.794 | 2.889 |
| A | pre-S13c source | 28.067 | 34.675 | 12.651 | 72.993 | 77.153 | 106.142 | 0.925 | 1.058 | 13.569 | 73.933 | 78.196 | 107.201 | 6.202 | 8.309 | 48.998 | 2.970 |
| B | S13e increment | 27.741 | 35.101 | 12.622 | 74.440 | 79.533 | 99.107 | 0.944 | 1.105 | 13.568 | 75.411 | 80.552 | 99.946 | 6.506 | 8.232 | 52.003 | 3.056 |
| C | S13f-S13h cumulative | 30.980 | 31.349 | 9.487 | 69.615 | 73.230 | 93.795 | 0.918 | 1.073 | 10.415 | 70.517 | 74.253 | 95.102 | 6.302 | 5.212 | 45.766 | 2.987 |
| C minus B | | +3.239 | -3.752 | -3.135 | -4.825 | -6.303 | -5.312 | -0.025 | -0.033 | -3.153 | -4.893 | -6.299 | -4.844 | -0.205 | -3.021 | -6.237 | -0.070 |
| C minus A | | +2.912 | -3.326 | -3.164 | -3.377 | -3.923 | -12.347 | -0.007 | +0.015 | -3.154 | -3.415 | -3.943 | -12.099 | +0.100 | -3.098 | -3.233 | +0.017 |
| C minus D | | +15.706 | -2.945 | -3.568 | -2.614 | -4.540 | +4.047 | -32.896 | -52.357 | -54.337 | -50.348 | -55.228 | -41.967 | +1.127 | -3.320 | -2.028 | +0.098 |

Reading: the identity-feedback removal (D to A) is what takes the wait from
tens of milliseconds to under one; its benefit shows in successful-present
intervals, not in render dispatch time, because the baseline spent the time
waiting between dispatches. S13c-S13e (A to B) are neutral on these windows
within run-to-run spread (render p50 10.99 to 10.83 ms): the fixture is
stationary or moving only its camera, so the registration, auxiliary-state and
repeated-preparation work those children removed is mostly idle here, and
their own records hold their mutation evidence. S13f-S13h (B to C) account for
the 3.1 ms per frame, all of it in command recording (record p50 7.25 to
4.26 ms), which matches the family preparation reduction measured by the
children (4.4 ms per recording at the S13f entry to 1.1 ms here).

## Results: allocation, garbage collection and recording

### Stationary window: allocation and garbage collection, per run

| Run | Frames | Record alloc KB per frame | Render thread alloc KB per frame | Process alloc KB per frame | Process alloc MB per second | Gen0 | Gen1 | Gen2 | GC pause ms | GC pause ms per frame | Publication p95 ms | Buffer upload bytes per frame |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| s13i-A-r1 | 4961 | 605.9 | 1,186.9 | 2,951.0 | 237.4 | 283 | 11 | 0 | 892.1 | 0.180 | 0.004 | 20,144 |
| s13i-A-r2 | 5015 | 605.9 | 1,186.9 | 2,947.4 | 239.8 | 287 | 11 | 1 | 833.0 | 0.166 | 0.004 | 20,144 |
| s13i-B-r1 | 4836 | 567.6 | 1,148.7 | 2,913.2 | 228.4 | 273 | 10 | 1 | 863.0 | 0.178 | 0.004 | 20,144 |
| s13i-B-r2 | 5214 | 567.6 | 1,148.7 | 2,907.2 | 245.9 | 294 | 76 | 1 | 803.6 | 0.154 | 0.004 | 20,144 |
| s13i-C-r1 | 6607 | 210.3 | 762.9 | 2,520.4 | 270.0 | 319 | 77 | 1 | 996.9 | 0.151 | 0.004 | 20,144 |
| s13i-C-r2 | 6962 | 210.2 | 762.8 | 2,510.9 | 283.6 | 335 | 10 | 0 | 1,068.8 | 0.154 | 0.004 | 20,144 |
| s13i-D-r1 | 1399 | 605.2 | 1,179.6 | 3,448.3 | 78.2 | 94 | 18 | 1 | 381.3 | 0.273 | 0.005 | 20,144 |
| s13i-D-r2 | 1420 | 605.2 | 1,179.7 | 3,553.0 | 81.9 | 99 | 21 | 1 | 499.5 | 0.352 | 0.004 | 20,144 |

### Controlled motion window: allocation and garbage collection, per run

| Run | Frames | Record alloc KB per frame | Render thread alloc KB per frame | Process alloc KB per frame | Process alloc MB per second | Gen0 | Gen1 | Gen2 | GC pause ms | GC pause ms per frame | Publication p95 ms | Buffer upload bytes per frame |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| s13i-A-r1 | 1675 | 2,071.1 | 5,670.2 | 7,935.2 | 215.5 | 206 | 202 | 2 | 1,440.8 | 0.860 | 0.006 | 20,144 |
| s13i-A-r2 | 1705 | 2,070.9 | 5,668.3 | 7,922.3 | 219.1 | 209 | 208 | 3 | 1,452.9 | 0.852 | 0.005 | 20,144 |
| s13i-B-r1 | 1623 | 2,028.5 | 5,621.0 | 7,880.1 | 207.4 | 198 | 195 | 3 | 1,553.2 | 0.957 | 0.006 | 20,144 |
| s13i-B-r2 | 1717 | 2,048.5 | 5,682.7 | 7,940.8 | 221.2 | 212 | 210 | 4 | 1,416.5 | 0.825 | 0.005 | 20,144 |
| s13i-C-r1 | 1813 | 1,679.3 | 5,244.3 | 7,493.9 | 220.4 | 208 | 206 | 4 | 1,499.7 | 0.827 | 0.006 | 20,144 |
| s13i-C-r2 | 1917 | 1,681.4 | 5,258.2 | 7,503.1 | 233.4 | 217 | 216 | 2 | 1,472.4 | 0.768 | 0.005 | 20,144 |
| s13i-D-r1 | 913 | 2,079.0 | 5,757.0 | 8,192.7 | 121.3 | 115 | 113 | 1 | 1,011.6 | 1.108 | 0.006 | 20,144 |
| s13i-D-r2 | 926 | 2,077.1 | 5,750.2 | 8,165.8 | 122.7 | 116 | 113 | 1 | 1,022.7 | 1.104 | 0.005 | 20,144 |

Command recording allocates 210 KB per stationary frame in C against 568 KB
in B and 606 KB in A and D; the render thread total falls from 1,149 KB to
763 KB per frame, the same 386 KB. Garbage collection pause per frame is flat
to slightly lower (0.15 ms per frame in B and C). The number of Gen1
collections per window is either about 10 or about 77 in both B and C, so it
varies by run and not by binary. Process-wide allocation still runs at 2.5 MB
per frame; a large share belongs to the profiling observer, which serializes a
95 KB record for every frame in this profile mode, so the process figure is an
upper bound for the engine, not an engine measurement.

### Motion window split into ordinary and shadow-update frames, per run

The motion window is bimodal in every binary. Forty percent of its frames
render three additional full-scene shadow passes (the directional cascades are
refreshed while the camera moves: 407 draw calls and 945 thousand triangles
against 14 draw calls and 849 triangles, `directional_cascade_stale_sampled`
set, three shadow passes skipped by CPU query). No stationary frame is of that
kind.

| Run | Ordinary frames | Ordinary render p50 | Ordinary p95 | Ordinary p99 | Ordinary record p50 | Ordinary record alloc KB | Shadow-update frames | Share % | Shadow render p50 | Shadow p95 | Shadow p99 | Shadow record p50 | Shadow record p95 | Shadow record alloc KB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| s13i-A-r1 | 1002 | 11.709 | 18.553 | 20.243 | 7.826 | 621.3 | 673 | 40.18 | 67.495 | 76.050 | 81.934 | 47.101 | 53.724 | 4,229.6 |
| s13i-A-r2 | 1020 | 11.643 | 18.182 | 19.879 | 8.003 | 621.4 | 685 | 40.18 | 66.090 | 74.666 | 79.869 | 45.789 | 52.465 | 4,229.2 |
| s13i-B-r1 | 973 | 12.009 | 19.347 | 22.065 | 7.984 | 583.0 | 650 | 40.05 | 69.603 | 79.698 | 88.956 | 48.136 | 56.310 | 4,192.3 |
| s13i-B-r2 | 1021 | 11.144 | 17.584 | 19.987 | 7.519 | 583.6 | 696 | 40.54 | 65.680 | 73.686 | 77.987 | 45.877 | 53.223 | 4,197.4 |
| s13i-C-r1 | 1084 | 8.827 | 15.558 | 17.687 | 5.018 | 226.2 | 729 | 40.21 | 64.696 | 74.431 | 82.467 | 44.396 | 52.067 | 3,840.1 |
| s13i-C-r2 | 1143 | 8.279 | 15.208 | 16.735 | 4.669 | 226.2 | 774 | 40.38 | 61.233 | 69.057 | 72.616 | 41.989 | 48.529 | 3,830.5 |
| s13i-D-r1 | 545 | 12.381 | 13.899 | 20.126 | 7.957 | 620.8 | 368 | 40.31 | 65.282 | 76.289 | 82.223 | 45.200 | 53.803 | 4,238.5 |
| s13i-D-r2 | 553 | 12.197 | 13.596 | 20.446 | 7.885 | 621.0 | 373 | 40.28 | 64.489 | 76.202 | 78.452 | 44.632 | 52.727 | 4,235.8 |

The shadow-update frames own the motion tail: 42 to 48 ms of command
recording and 3.8 to 4.2 MB allocated per recording, through the ordinary
CPU-direct mesh draw path (per heavy frame: 1,697 fast-path draws, 820 draws
on the legacy automatic-uniform fallback, 25,601 reflected uniform name
lookups, 455 KB of legacy full-block uniform bytes; evidence
`reports/harness/heavy-frames-C-r1-motion.md`). S13 did not change that path:
its share is 40% in all four binaries and its cost differs between B and C
only by the 3 ms and 360 KB that every recording gained. This is the long
recording of the original report, reproduced and attributed, and it remains
open (see the handoffs).

## Results: counters with the S13a telemetry on

One telemetry-on run each of B and C on the same fixture and windows
(`reports/telemetry/`), with a read-only sidecar sampling
`get_s13a_publication_telemetry` every five seconds; deltas are taken between
the first and last sample inside each harness window. B predates the S13f-S13h
counters, so only C has the recording columns.

| Counter | B stationary | B motion | C stationary | C motion |
| --- | ---: | ---: | ---: | ---: |
| Sample span (s) | 53.7 | 53.5 | 59.3 | 53.5 |
| Identity dirty notifications | 0 | 0 | 0 | 0 |
| Other dirty notifications and mesh update calls | 0 | 0 | 0 | 0 |
| Scene swaps | 9,446 | 3,068 | 13,334 | 3,366 |
| Publications reused / resource mutation | 4,723 / 0 | 337 / 1,197 | 6,667 / 0 | 354 / 1,329 |
| Family preparations | 4,724 | 1,533 | 6,668 | 1,682 |
| Scene publication prepare calls per family | 1.00 | 1.00 | 1.00 | 1.00 |
| Scene publication reuses per family | 6.00 | 6.00 | 6.00 | 6.00 |
| Scene slot realizations per family | 1.00 | 1.00 | 1.00 | 1.00 |
| Scene publication prepare time per family (us) | 128.4 | 142.0 | 125.9 | 144.6 |
| Primary recordings | n/a | n/a | 6,668 | 1,683 |
| Plan discovery visits per recording | n/a | n/a | 29.0 | 188.1 |
| Storage gate wait per recording (us) | n/a | n/a | 0.06 | 0.07 |
| Plan discovery per recording (us) | n/a | n/a | 0.40 | 0.65 |
| Discovery plus family preparation per recording (us) | n/a | n/a | 1,122.6 | 1,129.8 |
| Allocated in discovery plus preparation per recording (bytes) | n/a | n/a | 38,652 | 38,782 |
| Readiness calls per recording | n/a | n/a | 40.0 | 40.0 |
| Stable-bin records ordered at freeze | n/a | n/a | 2,620,524 | 661,026 |
| Freeze order violations | n/a | n/a | 0 | 0 |
| Publication failures | 0 | 0 | 0 | 0 |

Per-step attribution in C (stationary; the motion window is within 10% on
every step): bin sealing 608 us (geometry stream 282 us, of which payload
loop 228 us and freeze ordering 50 us; manifests 69 us; submission plans
257 us), raster pipelines 137 us and 80 bytes, scene publication 126 us,
native compute 67 us and 17,908 bytes, readiness 58 us and 1,600 bytes over
40 calls, target closure 35 us and 800 bytes, raster realization 38 us.

The removed work stayed removed in still and moving views: no identity or
other dirty notification and no mesh update call in either window, one scene
preparation per family, six reuses, and 40 readiness calls per recording
instead of the 431 measured at the S13g entry. Genuine mutations (cube add,
motion and removal, shader reload, rapid double reload, TSR scale change and
restore, transactional renderer restart, emulated stereo) were exercised on
this same source in the S13g and S13h matrices and are not repeated here. The
storage gate hold implied by these numbers is 1.12 ms per recording, inside
the 1.4 ms budget that S13h missed at 1.56 ms under two concurrent Debug
editors.

Observer overhead: the telemetry-on run of C measured stationary render avg
8.162, p50 7.820, p95 10.153, p99 12.588 ms against 8.104, 7.743, 10.396,
12.654 ms for the telemetry-off mean; B measured 10.622 and 10.376 ms against
11.182 and 10.829 ms. Both are inside the run-to-run spread, so the counters
carry no measurable cost.

## Results: scene content, coverage, identity and images

### Identity, coverage and endpoints, per run

| Run | Stationary identity | Motion identity | Camera poses verified | Admission image | Scene commands p50 | Native live resources | Descriptor sets | Backlogs returned | Diagnostic loss passed | Fallback events | Rejected submissions | Failed frames |
| --- | --- | --- | --- | --- | ---: | --- | --- | --- | --- | ---: | ---: | ---: |
| s13i-A-r1 | 10991459253885323059 | 10991459253885323059 | True/True | True | 309 | 9782 to 10102 (+320) | 6081 to 6401 (+320) | True | True | 0 | 0 | 0 |
| s13i-A-r2 | 10991459253885323059 | 10991459253885323059 | True/True | True | 309 | 9782 to 10102 (+320) | 6081 to 6401 (+320) | True | True | 0 | 0 | 0 |
| s13i-B-r1 | 10991459253885323059 | 10991459253885323059 | True/True | True | 309 | 9782 to 10102 (+320) | 6081 to 6401 (+320) | True | True | 0 | 0 | 0 |
| s13i-B-r2 | 10991459253885323059 | 10991459253885323059 | True/True | True | 309 | 9772 to 10093 (+321) | 6071 to 6391 (+320) | True | True | 0 | 0 | 0 |
| s13i-C-r1 | 10991459253885323059 | 10991459253885323059 | True/True | True | 309 | 9772 to 10093 (+321) | 6071 to 6391 (+320) | True | True | 0 | 0 | 0 |
| s13i-C-r2 | 10991459253885323059 | 10991459253885323059 | True/True | True | 309 | 9782 to 10102 (+320) | 6081 to 6401 (+320) | True | True | 0 | 0 | 0 |
| s13i-D-r1 | 10991459253885323059 | 10991459253885323059 | True/True | True | 309 | 13567 to 13888 (+321) | 9831 to 10151 (+320) | True | True | 0 | 0 | 0 |
| s13i-D-r2 | 10991459253885323059 | 10991459253885323059 | True/True | True | 309 | 13567 to 13888 (+321) | 9831 to 10151 (+320) | True | True | 0 | 0 | 0 |

Every run has one workload identity, the same in both windows and the same as
the S13a record. Scene command counts, draw calls and triangle counts per
frame class are equal in all binaries (309 commands, 14 draws and 849
triangles in ordinary frames; 394 commands, 407 draws and 945 thousand
triangles in shadow-update frames), so no speedup comes from missing draws.
No fallback event, rejected submission or failed frame occurred. Coarse GPU
coverage is 99.94 to 99.96% stationary in A, B and C (99.86% in D) and 99.78
to 99.84% in the motion window for all four; C is the highest in both. GPU
time per frame moves between 4.3 and 6.6 ms p50 across runs without following
the binary, so the CPU and GPU results are independent: the CPU change is
3.1 ms per frame and the GPU shows no change attributable to S13. Coarse GPU
time stays below the CPU frame time in both windows (5.7 ms against 7.7 ms
stationary p50 in C), so the GPU is not the limiting stage of this workload on
this machine; dense pass timing was not needed and no GPU remediation item is
opened.

### Scene content is not deterministic: the environment map is random

The default unit-test world chooses one of five environment maps with an
unseeded random number on every launch (`BootstrapWorldFactory`, all four
source states). The eight matrix runs drew all five. The map is loaded
asynchronously, so a short warmup can also capture the fallback sky. This
changes the image and the lighting, not the draw workload, and it explains why
the S13a record described a saturated interior while these captures mostly
show an overcast sky. Timing runs kept the unchanged fixture to stay matched
with S13a. Images were compared two ways.

Runs that drew the same environment (`reports/harness/image-pairs.md`):

| Pair | Kind | Mean abs diff (0-255) | Max diff | Pixels over 8/255 (%) | Pixels over 32/255 (%) |
| --- | --- | ---: | ---: | ---: | ---: |
| C-r1 / C-r1 telemetry | same binary (noise floor) | 0.039 | 76 | 0.066 | 0.007 |
| B-r1 / C-r1 | B vs C | 0.085 | 75 | 0.009 | 0.002 |
| B-r1 / C-r1 telemetry | B vs C | 0.097 | 64 | 0.058 | 0.006 |
| A-r2 / C-r1 | A vs C | 0.049 | 78 | 0.050 | 0.043 |
| D-r1 / C-r1 | D vs C | 0.092 | 45 | 0.062 | 0.037 |
| C-r2 / B-r1 telemetry (second environment) | B vs C | 0.009 | 7 | 0.000 | 0.000 |

Cross-binary differences sit at the same-binary noise floor. Both fixture
views look at the building's unlit exterior wall from outside (the S13a camera
poses), where thin mortar lines shimmer under temporal upscaling; that is the
source of the isolated high single-pixel differences.

Runs on fixture variants with the deterministic procedural sky
(`reports/followups/`), at view A and at the second view, first with the
fixture's temporal upscaling and then with the camera anti-aliasing override
set to none (internal resolution 1920x1080 instead of 1286x723, workload
identity `15115958026561542460`, so no jitter and no history):

| View | Configuration | Pair | Mean abs diff (0-255) | Max diff | Pixels over 8/255 (%) | Pixels over 32/255 (%) |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| A | temporal upscaling | same binary, B and C repeats | 0.153 to 0.166 | 76 to 79 | 0.256 to 0.274 | 0.050 to 0.098 |
| A | temporal upscaling | B vs C, four pairs | 0.094 to 0.218 | 76 to 85 | 0.084 to 0.687 | 0.003 to 0.116 |
| A | temporal upscaling | A vs B (repeat) | 0.024 | 6 | 0.000 | 0.000 |
| Second | temporal upscaling | same binary, B and C repeats | 0.053 to 0.222 | 67 to 90 | 0.052 to 0.459 | 0.005 to 0.010 |
| Second | temporal upscaling | B vs C, four pairs | 0.247 to 0.287 | 38 to 116 | 0.748 to 0.917 | 0.001 to 0.010 |
| A | jitter-free | same binary, C repeat | 0.000 | 1 | 0.000 | 0.000 |
| A | jitter-free | B vs C, two pairs | 0.000 | 1 | 0.000 | 0.000 |
| Second | jitter-free | same binary, C repeat | 0.000 | 1 | 0.000 | 0.000 |
| Second | jitter-free | B vs C, two pairs | 0.000 | 1 | 0.000 | 0.000 |

Without temporal jitter the previous increment and the cumulative binary
produce the same image at both views, to within one step of 255 on every
pixel, which is also the difference between two runs of one binary. With
temporal upscaling the sky region is identical to within one step in every
pair and all larger differences lie on the sub-pixel mortar lines of the wall;
at the second view the B-versus-C pairs differ on up to twice as many of those
pixels as two captures of one binary (0.92% against 0.46%), with differences
above 32/255 on at most 0.01% of pixels. The jitter-free result shows that
geometry, draw order and shading are unchanged, so the remaining difference is
the temporal phase at the moment of capture, which depends on the frame count
and therefore on the frame rate. Whether the temporal history itself behaves
correctly is an S15 question and is not decided here.

Viewed images: the admission screenshot of every matrix run and of the
deterministic-sky runs at both views. Each shows the sky or environment on the
left and the building wall on the right with no missing geometry, no stale or
black output and no difference in framing between binaries. Disocclusion and
camera-cut sequences were not captured; they belong to the S15 temporal track.

### Native resource endpoints

Every binary, including the frozen baseline, ends the run 320 or 321 native
resources and 320 descriptor sets above its start, so the fifth criterion is
not met as written. A read-only sidecar sampled the counts every five seconds
during the telemetry-on run of C (`reports/telemetry/resource-series.json`):

| Time (UTC) | Phase | Native live | Descriptor sets | Mesh descriptor set high water |
| --- | --- | ---: | ---: | ---: |
| 10:24:26 | load complete | 9,765 | 6,081 | 2,225 |
| 10:26:14 | stationary window start, after the admission screenshot | 9,782 | 6,081 | 2,225 |
| 10:27:13 | first camera movement | 10,343 | 6,641 | 2,545 |
| 10:27:18 | five seconds later | 10,103 | 6,401 | 2,545 |
| 10:28:12 | end of 60 seconds of continuous motion | 10,103 | 6,401 | 2,545 |

The counts are flat through warmup and the stationary window, step once at
the first camera movement (560 sets created, 240 retired within five seconds,
320 kept), and stay flat for the rest of the motion. Runs with the views
swapped show the same step of 320 when the camera first leaves the second
view, so the step follows the first movement and not the content of a view. It
is a bounded one-time materialization of mesh descriptor sets, most likely for
the shadow passes that first re-render at that moment, it predates S13 (the
baseline has it), and it is identical in B and C. The criterion as written
assumed the S13a final-matrix behaviour on another machine and checkout; the
disposition is that S13 adds no retention, and the owner of the step is handed
off with the shadow-update item.

Relative to the baseline the later binaries hold 3,785 fewer native resources
and 3,750 fewer descriptor sets at steady state (13,567 and 9,831 in D against
9,782 and 6,081), the retained-variant correction recorded by S13a and S13b.

## Stationary worst frame

C's stationary maximum is 37.3 ms against 34.2 ms in A and B, which exceeds
the allowance for the maximum. The slow stationary frames are a periodic event
and not recording: their Vulkan frame time is the ordinary one (6.8 ms in C,
9.6 to 10.1 ms in A and B) and the extra time is in render dispatch outside
the Vulkan frame. The events are spaced at multiples of 1.36 seconds in A, B
and C.

| Run | Events with 8 ms or more outside the Vulkan frame | Mean outside Vulkan (ms) | Max outside Vulkan (ms) | Ordinary frame outside Vulkan, median (ms) | Median spacing (s) |
| --- | ---: | ---: | ---: | ---: | ---: |
| s13i-A-r1 | 21 | 16.7 | 24.2 | 1.28 | 2.05 |
| s13i-A-r2 | 20 | 16.6 | 24.3 | 1.14 | 1.36 |
| s13i-B-r1 | 29 | 16.2 | 23.6 | 1.31 | 1.37 |
| s13i-B-r2 | 19 | 16.5 | 25.1 | 1.12 | 2.73 |
| s13i-C-r1 | 39 | 19.3 | 29.6 | 1.26 | 1.36 |
| s13i-C-r2 | 43 | 19.7 | 31.2 | 1.12 | 1.36 |

The event is larger and more regular in C, in proportion to the frames
rendered per period (about 113 per 1.36 seconds in A and B, about 153 in C).
That is the signature of work proportional to the frames accumulated since the
last period. One run of C with the profiler sampling every eighth frame
instead of every frame tested the obvious candidate
(`reports/followups/sparse-sampling/`):

| Run of C | Frames per second | Render p50 | Render p95 | Render p99 | Sampled max | Engine running worst frame, median over the window |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Every frame sampled (matrix mean) | 112.7 | 7.743 | 10.396 | 12.654 | 37.300 | 32.0 (view B repeat run) |
| Every eighth frame sampled | 127.3 | 6.774 | 8.280 | 10.610 | 11.953 | 11.8 |

The engine's running worst-frame statistic covers every rendered frame, also
the unsampled ones, and its median over the window falls from 32.0 ms to
11.8 ms. The periodic slow frame therefore belongs to the per-frame sampling
observer of the `DevelopmentProfile` mode that S13a's matrix prescribes, not
to the engine and not to S13f-S13h. The same observer costs about 1 ms on
every frame. Both effects apply to all four binaries, so the comparisons
stand; absolute frame times without the observer are about 1 ms lower. The
first criterion's maximum is recorded as not met in the prescribed
configuration and attributed to the observer.

## Adjacent paths and coverage limits

### OpenGL: not measurable on this machine, unchanged by S13

The harness ran every binary on OpenGL with the unchanged fixture
(`reports/followups/opengl/`, `reports/followups/opengl-main-tree/`). Every
attempt was rejected at admission because the scene viewport is black:

| Binary | Repository root | Backend | Admission result |
| --- | --- | --- | --- |
| D (S13a frozen baseline) | its own worktree | OpenGL | Rejected: visually empty, 3 of 16 colour buckets |
| A | its own worktree | OpenGL | Rejected: visually empty, 4 of 16 |
| B | its own worktree | OpenGL | Rejected: visually empty, 4 of 16 |
| C | its own worktree | OpenGL | Rejected: visually empty, 4 of 16 |
| C (copy of the same build) | main checkout | OpenGL | Rejected: visually empty, 11 of 16 (editor panels visible, scene black) |
| C (copy of the same build) | main checkout | Vulkan | Accepted; stationary render p50 8.583, p95 10.976, p99 13.309 ms; identity `10991459253885323059` |

The viewed screenshots show the editor toolbar and the debug overlay
("OpenGL | AdvancedRenderPipeline | CpuDirect", 14 draw calls, 727 triangles)
over a black viewport with no sky. The baseline that predates every S13
change fails the same way, in an isolated worktree and in the main checkout,
so the black OpenGL scene is neither caused nor affected by S13; B and C
behave identically. The S13a record measured OpenGL on another machine. The
S13f-S13h source changes are confined to the Vulkan renderer plus default-off
counters in the shared rendering assembly, and the S13b-S13e OpenGL evidence
is in their own records. OpenGL timing on the cumulative state is therefore
not validated by this gate, and the black OpenGL scene on this machine is
handed off as its own defect.

### Emulated stereo (two views per frame)

One run each of B and C with the S13b emulated two-pass stereo environment,
120-second warmup and 30-second windows (`reports/followups/stereo/`). Both
report two 1920x1080 eye outputs rendered as sequential views and the same
stereo workload identity `7564941461329649519`, 395 scene commands, verified
camera poses and an accepted admission image.

| Run | Window | Samples | Render avg | Render p50 | Render p95 | Render p99 | Render max | Wait p50 | Wait p95 | Record p50 | Record p95 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| B | stationary | 1396 | 20.649 | 20.306 | 24.439 | 26.101 | 34.674 | 0.859 | 0.956 | 15.176 | 16.799 |
| C | stationary | 1760 | 16.217 | 15.860 | 19.114 | 20.662 | 33.064 | 0.859 | 0.962 | 9.177 | 10.426 |
| B | motion | 790 | 37.038 | 21.159 | 81.657 | 85.726 | 103.071 | 0.936 | 1.118 | 15.742 | 55.855 |
| C | motion | 905 | 32.286 | 16.241 | 77.730 | 81.411 | 119.736 | 0.918 | 1.103 | 9.485 | 50.232 |

With two families per recording the recording saves 6.0 ms at p50 and the
frame 4.4 ms. The motion maximum of C is higher in this single pair (119.7 ms
against 103.1 ms); it is one frame of one run and the p99 is lower, so it is
recorded without a conclusion. Physical XR hardware was not used.

Not exercised by this gate: physical XR hardware, resize, MSAA change, camera
cut and disocclusion sequences, multi-LOD and streaming content, skinning,
texture replacement, failed shader compilation, Vulkan validation layers, a
debugger-attached window, and cold canonical pipeline admission. No evidence
of cold pipeline admission or required texture finalization appeared in the
warmed windows (texture upload bytes are zero in the stationary window and
87 KB in total in the motion window, equal in all binaries), so no child was
opened for them.

## Child dispositions and retained diff

| Child | Disposition | Retained change |
| --- | --- | --- |
| S13a | Validated | Opt-in publication telemetry and trace, default off; attribution of the identity-feedback owner |
| S13b | Validated | Publication identity separated from command dirtiness; retained mesh descriptor variant corrections; two-pass stereo viewport boundary fix |
| S13c | Validated for reachable scope | Retained registration signature on the logical mesh state with an allocation-free hit path; shape-replacement command leak fix |
| S13d | Validated for reachable scope | Typed equality for bounds and draw metadata; rows, transparency stream and material-state class written only when changed |
| S13e | Validated for reachable scope | Scene publication prepared once per compatible family and reused by its stages |
| S13f | Deferred | Observation counters only; retention needs a publication-stable geometry identity first |
| S13g | Validated for reachable scope | Readiness, raster program and prepared pipeline resolved once per coverage, meshlet and cull combination; allocation-free identity hash |
| S13h | Validated for reachable scope | Stable-bin freeze orders compact keys and permutes records once in place; no synchronization change (no contention measured) |

S13a-S13e are in commit `9fee4b983`. The S13f-S13h diff is uncommitted in the
main checkout: six modified files and two new files under
`XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/` and
`XREngine.Runtime.Rendering.Vulkan/`, byte-identical to what binary C was
built from. All telemetry stays default off.

## Disposition

| Criterion | Result |
| --- | --- |
| 1. No regression on p50, p95, p99 (stationary and motion) | Met: C improves every one against A and B |
| 1. No regression on max | Motion met. Stationary not met as written (+3.1 ms); the slow frame is outside the Vulkan frame, belongs to the per-frame profiling observer and disappears with sparse sampling |
| 2. One identity per window, equal across windows | Met in all runs |
| 3. Admission screenshot | Met in all runs |
| 4. GPU coverage at 99.9% | Met in the stationary window; the motion window is 99.84% in C and 99.78 to 99.82% in the others |
| 5. Native resource endpoint | Not met as written in any binary including the baseline; one bounded step at the first camera movement, no growth afterwards, identical in B and C |
| 6. Images | Met: identical to within 1/255 without temporal jitter at both views; with temporal upscaling the differences are confined to sub-pixel wall lines |
| 7. No speedup from missing work | Met: equal draws, triangles, scene commands, identity and feature state |

The cumulative S13 change explains the collect-wait part of the original
report and is measured on the reported workload. It does not explain or
remove the long recordings during camera motion, and it does not by itself
establish the temporal behaviour of the original report.

Remaining owners, the largest first:

1. **Shadow-update recording during camera motion.** Forty percent of motion
   frames record three full-scene cascade passes through the CPU-direct mesh
   path at 42 to 48 ms and 3.8 to 4.2 MB per recording; motion present
   interval stays at 70.5 ms p95 and 74.3 ms p99 in C. Needs its own item
   with its own gate.
2. **First-movement descriptor step**: 320 mesh descriptor sets and native
   resources created once at the first camera movement, in every binary;
   most likely part of the same shadow-update path.
3. **Residual recording allocation.** 210 KB per stationary recording, of
   which family discovery and preparation are 38.7 KB (native compute
   17.9 KB, readiness 1.6 KB, target closure 0.8 KB, about 18 KB outside the
   probed steps); the rest is elsewhere in recording. Render thread total
   763 KB per frame. For the allocation audit.
4. **Per-payload geometry loop** in bin sealing (228 us per recording) and
   submission plans (257 us), blocked on the publication-stable geometry
   identity that S13f deferred.
5. **Rapid double reload access violation** in the ordinary mesh draw path,
   observed once on the S13g entry build (S04/S05 owner), not reproduced here.
6. **Random environment map** in the default unit-test world, which makes
   image-based validation depend on chance unless the procedural sky is used.
7. **Black OpenGL scene** with the Advanced pipeline on this machine, in
   every binary including the pre-S13 baseline; blocks OpenGL measurement here.
8. **Per-frame profiling observer** of the `DevelopmentProfile` mode: about
   1 ms on every frame and a periodic slow frame every 1.36 seconds that
   grows with the frame rate. Measurement tooling, not engine work, but it
   sets the floor of what this harness configuration can resolve.

Test clearance: no test was added, modified or run by this gate. Temporary
settings: none in the main checkout. Owned sessions: every editor launched by
the harness was shut down by it; no process from another session was stopped.

## Measurement notes

- The first attempt at this matrix failed because the driver enabled
  PowerShell strict mode, which is dynamically scoped and therefore applied to
  the harness; its MCP response checks read optional properties. An earlier
  version of this record wrongly attributed that failure to the baseline's
  MCP response shape.
- The worktree holding B and C had been checked out with Git LFS smudging
  skipped, so four native libraries in its build outputs (including the
  Vulkan memory allocator bridge) were pointer stubs. They were replaced with
  the materialized files, verified identical to A's by hash, before any run.
- The engine keeps the three newest log sessions per build folder, and the
  per-frame stream is about 1.4 GB per run. A watcher reduced each stream to a
  compact table during the next run's unmeasured warmup, at idle priority.
- The two read-only sidecars stayed attached to every later run of B and C
  (images, stereo, sparse sampling); the main matrix ran before they were
  started and is unaffected. In the stereo pair only C carried the resource
  sidecar's stats poll every five seconds.
- The harness looks for the editor's log session under its own repository
  root, so a binary outside that root produces zero samples. Running each
  binary with the harness copy inside its own worktree avoids this.

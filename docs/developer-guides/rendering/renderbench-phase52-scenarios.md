# RenderBench Phase 5.2 Headless Scenarios

`XREngine.RenderBench` contains bounded correctness scenarios for the Phase 5.2
visibility and native-buffer-lifetime work. They use the production Vulkan
renderer through a presentationless target: they are not component recipes,
desktop smoke tests, editor sessions, or performance benchmarks.

## Run a scenario matrix

Run from the repository root and provide a bounded evidence directory. For
example:

```powershell
dotnet run --project .\XREngine.RenderBench\XREngine.RenderBench.csproj -- `
  --output-dir .\Build\_AgentValidation\<run>\renderbench-phase52 `
  --scenario phase52-visibility --scenario-workload all `
  --scenario-depth both `
  --width 1279 --height 719 `
  --scenario-frames 24 `
  --scenario-repeats 2
```

Scenario execution is Vulkan-only, presentationless, and MCP-disabled. The
valid scenarios are:

- `phase52-visibility` runs the visibility oracle only.
- `phase52-buffers` runs the native buffer growth/lifetime lane only.
- `phase52-all` runs both.

`--scenario-depth` accepts `normal`, `reversed`, or `both`. `both` runs
independent normal and reverse-depth cohorts. `--scenario-frames` is bounded
to 12 through 240 and `--scenario-repeats` is bounded to 2 through 4. Scenario
targets are bounded to RGBA8, one layer/sample, 2–4 frame slots, and dimensions
no larger than 4096×4096. Supply a single explicit depth convention when
invoking an internal child lane (`--scenario-lane`); the matrix runner creates
those child processes itself.

Component warmup, stability, capture-count, and frozen-world controls are
rejected for these scenarios: every scripted frame, including frame one, is
retained. `--scenario-frames` controls the visibility sequence, while the
buffer lane runs its bounded capacity/probe/slot-drain sequence.

`--scenario-workload all` runs open/static, moderate/static, heavy/static,
heavy/moving-cut, masked/static, and masked/moving fixtures in independent
processes. Individual names are `open-static`, `moderate-static`, `heavy-static`,
`heavy-moving-cut`, `masked-static`, and `masked-moving`; `default` retains
the original six-color moving/cut fixture. The open fixture does not require
an occluded reference set. Other fixtures must demonstrate actual hidden-object
culling, not merely a conservative bypass.

`--scenario-timing` enables optional, nonblocking Hi-Z timestamp diagnostics.
Build/test samples retain their own source frame, age, sequence, and availability;
only samples matching completed receipts in that child are accepted. CPU
planning time and renderer-authored temporal/camera-cut decisions are also
reported. These instrumented correctness runs still report
`PerformanceEvidence=false`: diagnostic readbacks and captures are not a
zero-readback performance or crossover benchmark.

The fixed clock and seed are part of the scenario identity. Override them only
deliberately with `--fixed-step` and `--random-seed`; do not compare evidence
whose input, executable, or shader SHA-256 identities differ.

Each scenario uses the actual `DefaultRenderPipeline`, not a replacement
diagnostic pipeline. Its deterministic color oracle fixes manual exposure and
gamma to 1, uses linear tonemapping, disables HDR/anti-aliasing, and disables
bloom, ambient occlusion, atmosphere, vignette, chromatic aberration, lens
distortion, depth/volumetric fog, motion blur, and depth of field. This makes
the output suitable for a bounded color oracle; it is not a claim of full
post-processing coverage.

First-use program linking is synchronous inside the explicit production
preparation scope. That cold correctness preparation prevents a missing shader
link from being represented as an empty first frame. It is not a hot-path or
steady-state performance measurement.

## RenderDoc capture of one child

`--scenario-renderdoc` requests a capture of one frame from exactly one
visibility child (`eligibility`, `disabled`, or `hiz`). It is rejected for a
matrix invocation and for the buffer lane. Launch that child through RenderDoc
injection from the repository root; this remains presentationless and creates
no desktop target. `--scenario-renderdoc-step N` selects the zero-based step
(default 0), which must be less than `--scenario-frames`. Captures are named
`step-NNN_capture.rdc`:

```powershell
& "C:\Program Files\RenderDoc\renderdoccmd.exe" capture -w -d . `
  -c .\Build\_AgentValidation\<run>\renderbench-phase52\first-frame.rdc `
  dotnet .\Build\RenderBench\Debug\AnyCPU\net10.0-windows7.0\XREngine.RenderBench.dll `
  --output-dir .\Build\_AgentValidation\<run>\renderbench-phase52\normal-repeat-0-hiz `
  --scenario phase52-visibility `
  --scenario-lane hiz `
  --scenario-depth normal `
  --scenario-frames 12 `
  --scenario-repeats 2 `
  --scenario-renderdoc --scenario-renderdoc-step 8
```

The capture flag is a diagnostic aid, not acceptance evidence by itself.

## Visibility oracle

For each depth convention and repeat, the matrix starts three fresh child
processes:

- `eligibility` renders the scene without the occluder to establish E.
- `disabled` renders the occluder with Hi-Z disabled to establish V and O,
  where O is `E − V`.
- `hiz` renders the same scene with production GPU Hi-Z and records K.

Every visibility lane reads the sealed native streams from its exact completed
submission receipt before performing the color readback. The early and late
DrawID streams are resolved to stable candidate IDs; K is their union, not the
raw engine DrawIDs. Each raw DrawID has an explicit candidate-or-known-occluder
mapping; unknown commands and duplicate mappings fail the evidence checks.
A passing cohort requires aligned nonempty frames, matching
input, executable, engine-assembly, and shader identities, valid per-frame
submission receipts, increasing engine/explicit/collect provenance, and
nonempty SHA-256 evidence. It also rejects false occlusion (`V − K`) and
missing visible output (`V − rendered`). Conservative overdraw is reported as
`K ∩ O`; demonstrated culling is `O − K`.

If a child fails after retaining completed frames, the report still analyzes
the common completed prefix for false-occlusion/missing-output diagnostics.
`CohortComplete=false` and `Passed=false` keep that partial evidence separate
from acceptance; missing lanes or an empty common prefix cannot be analyzed.

The first frame may bypass two-pass Hi-Z, but at least one later frame must
execute it and, except in the open fixture, remove an eligible hidden candidate. These reports are
correctness evidence only and never feed visibility back into the renderer.

Heavy fixtures use distinct raw-albedo codes for IDs 7–70, so visible heavy
objects participate in the same image/stream oracle as the six primary color
anchors. Disabled streams must contain the complete heavy candidate range;
the Hi-Z cohort must demonstrate actual heavy-candidate removal. Masked
fixtures use an RGBA albedo texture with alpha-zero holes, matching the generated
zero-readback material-table shader, and alternate cutout coverage with an
identical opaque control. A per-material coverage flag drives cutoff even in
the deferred opaque pass; the entire pass is not reclassified as masked. Texture
readiness is established before first collection, not by discarding early frames.
Each half of the moving-mask sequence contains continuous movement followed by
four settled frames. This separately verifies conservative visibility during
view changes and actual culling after history becomes valid; the positive-cull
requirement is not waived for this workload.

A post-color buffer read also verifies that the original submission receipt
survives the auxiliary color-copy command buffer's reset/reseal. Receipt
authorization uses its independent captured resource vector, not mutable
command-buffer workspace.

Child results include `nativeValidation`, captured from the device before
teardown. It reports actual standard/synchronization validation and debug
messenger state, cumulative error/warning counts, suppressed unused-attachment
warnings, overflow, and bounded message samples. Native errors fail the child.
For a separate validation run, set `XRE_VULKAN_VALIDATION=1` and
`XRE_VULKAN_SYNC_VALIDATION=1` in the launching process and require both enabled
flags and the active messenger in the resulting evidence. Disabled layers plus
zero counts are not validation proof. Teardown diagnostics are outside this
snapshot and must not be confused with completed-frame diagnostics.

## Cold repeats

The coordinator compares every non-buffer lane across its isolated repeats for
each depth convention. Missing, failed, or count-mismatched repeats fail the
summary; there is no vacuous pass. The comparison requires matching input,
executable, engine-assembly, and shader identities plus equal per-frame color
SHA-256 values and candidate sets. Adapter/driver/device identity must match;
native handles and receipt-owner identities are not compared across child
processes. Buffer-only matrices mark image cold-repeat analysis
`not-applicable`, never as a vacuous pass.

## Native buffer lifetime lane

The `buffers` lane deliberately has no color capture or GPU buffer readback.
It observes actual native allocation descriptions while moving the scene command
count through C-1/C/C+1 (7, 8, and 9 commands), then grows an exact frozen
native barrier binding after logical seal. The normal pre-acquire validator
must reject that superseded packet without acquiring or submitting, and a
fresh retry must succeed. It then submits a separate
`AfterNativeRecording` growth probe against the submitted writable LateDrawIDs
stream. The request is larger than that stream's observed native byte capacity.

The resulting evidence records the capacity observations, actual old/new native
handle and generation, exact submission receipts, a pending/completion query,
and post-probe submissions sufficient to drain frame slots. Passing requires
recorded-generation retention, GPU overlap, and post-completion reclamation from
the Vulkan lifetime ledger. Recorded-frame pin retention alone is not GPU
overlap proof. Observed reclamation before completion is a permanent failure,
even if a later snapshot observes completion.

Use a separate 4096×4096 buffer matrix on fast GPUs:

```powershell
dotnet run --project .\XREngine.RenderBench\XREngine.RenderBench.csproj -- `
  --output-dir .\Build\_AgentValidation\<run>\renderbench-phase52-buffers `
  --scenario phase52-buffers --scenario-depth both --scenario-repeats 2 `
  --width 4096 --height 4096
```

At small extents, the GPU can genuinely finish before the first pending query;
that is an inconclusive overlap check and fails this acceptance lane. The larger
target increases real production work without inserting an artificial hold.
The drain bound is `2 * frameSlots + 1`: recorded command reset can release a
descriptor pool after the old buffer's slot has already drained, requiring a
second normal rotation. No resource is force-freed to satisfy the checker.

## Evidence layout

The matrix writes `<output-dir>/scenario-result.json`. Each child writes an
isolated `<depth>-repeat-<n>-<workload>-<lane>/scenario-result.json`, plus `stdout.log`
and `stderr.log`. Visibility children additionally write `scenario-input.json`
and completed `frame-###.png` captures; all visibility frame records include native-buffer
descriptions and readback routes. The buffer lane records its lifetime evidence
in JSON and intentionally produces no per-frame color/readback artifacts.

Treat a failed or incomplete JSON result as diagnostic evidence, not an
acceptance result. Keep temporary outputs under `Build/_AgentValidation/`; copy
only durable findings into tracked documentation.

# Vulkan 1.4 final policy and validation — 2026-09-14

This continues the code closeout with runtime qualification and final policy
decisions. The selected shader work remains incremental: native Slang pilots
are opt-in, and authored GLSL remains the OpenGL route. No broader shader
conversion is selected.

## H6 water investigation

The authored OpenGL tessellation water control received draw calls but was
absent from the final image. Manual exposure 1 corrected a separate washed-out
view, making the floor and reference objects readable without restoring water.
Both separable and combined-program controls showed the missing surface.

Cold-start isolation used a constant magenta fragment output and then a fixed
clip-space triangle in the tessellation evaluation shader. Their changed
source hashes appeared in issued-command coverage, but neither appeared in
the final image. A temporary probe around the patch draw recorded:

| Probe | Native draw framebuffer | Pipeline validation | Samples passing depth/stencil | GL error |
| --- | ---: | --- | ---: | --- |
| Before, fixed clip-space/magenta control | 0 | valid | 1,492,992 on each of four draws | none |
| After, original water shaders | 10 | valid | 651,638–652,609 across four draws | none |

The missing surface was a framebuffer restoration error, not failed shader
execution. `GLFrameBuffer.VerifyAttached` calls `XRFrameBuffer.AttachAll` and
`DetachTargets`, which use a general `BindState` scope. That scope had a stack
independent from read/write bindings. When a grab texture attached or resized
inside the write-bound forward pass, disposing its general scope bound native
framebuffer 0 while the logical write stack still named the forward target.
Water rendered successfully into the window and was overwritten by the later
final image.

The scene-color grab's scale is 0.75. Its resize check compared the unscaled
source extent with its scaled destination, so it also requested resize and
attachment work on every sampling operation. This repeatedly exposed the
restoration bug.

`XRFrameBuffer.Bind/Unbind` now tracks a general bind in both read/write stacks
and restores the two previous targets independently. Nested general, read-only,
write-only and distinct read/write scopes preserve the same binding semantics.
Grab resizing compares the scaled, nonzero destination dimensions before
resizing. The original shaders were restored unchanged. The two viewed camera
positions show blue displaced water, refraction and foam around the reference
objects. Temporary GL query/source probes were removed from production code.

Evidence is under the H run's `reports/final-water-*`,
`mcp-captures/final-water-fixed*`, and
`logs/water-draw-state-before.txt` / `water-draw-state-after.txt`.
Ignored captures are disposable; the findings and numeric results above are
the durable record. No user confirmation of the visual fix has been received.

## H6 OpenGL replacement preparation

The in-memory source-mutation probe also exposed a separate hot-reload defect:
`GLRenderProgram.PrepareLinkData` and `BeginPrepareLinkData` returned early
whenever the last program remained linked. Clone-and-swap intentionally retains
that program while preparing its replacement, so the replacement stayed at
`SourceQueued` indefinitely. The guards now permit preparation while a
replacement is pending. Live mutation and recovery validation is recorded below
for the final build.

| Live fragment-source step | Adopted source revision | Direct graphics commands | Viewed result |
| --- | ---: | ---: | --- |
| Authored water, SHA-256 `C56D2510750FD10079C78691D2A4628BA74A793292AD5849BAD9E5F8D6E1B0AE` | 3 | 689 | Blue water |
| Constant magenta edit, SHA-256 `17A0F7559541FDFB5D46F789A6862FB93548634C2FE3F8329321EF9249907E21` | 4 | 2,672 | Magenta water surface |
| Deliberate invalid token | 5, rejected | 0 | Last valid magenta surface retained; metadata reports `Failed` and keeping the previous program |
| Original source restored | 6 | 1,153 | Authored water restored |

The source mutations used MCP on the loaded `TextFile`; the authored shader
assets are unchanged. Counts identify successfully adopted source revisions,
not merely requested edits. `final-gl-reload-*`, `final-gl-coverage.json` and
the corresponding viewed captures record the sequence.

The point-light shadow mode was also explicitly changed to `GeometryShader`.
The effective mode reported `2`, and `PointLightAtlasShadowDepth.gs` revision 4
issued 5,368 direct graphics commands. Its final scene image was viewed. The
final water tessellation control/evaluation stages each issued 4,514 commands.
These controls qualify the selected OpenGL stages; they do not enable Vulkan
tessellation or qualify every shader permutation.

## Final XR capture and source/backend controls

The same probe-free Release editor build ran the controls below. Slang pilots
were disabled and `XRE_SLANGC` deliberately named an unavailable executable, so
these are retained GLSL routes. Vulkan requested descriptor indexing and
standard/synchronization validation. Monado supplied the OpenXR runtime.

| Explicit request / actual renderer | Result | Selected-eye copy proof | Runtime observations |
| --- | --- | --- | --- |
| Vulkan / `SinglePassStereo` | Both 896×1007 eye images captured and viewed; distinct perspectives of the red box grid | Left render/copy frame 393/393; right 398/398 | True single-pass supported; 178 submitted / 192 completed frames at the snapshot, zero end-frame failures, zero sequential-fallback attempts, zero Vulkan validation messages/errors |
| OpenGL / `SinglePassStereo` | Explicitly unsupported; no eye output substituted | Capture returns no preview copy from the latest rendered frame | True single-pass unsupported; zero submitted layers across 116 completed no-layer frames, zero fallback attempts |
| OpenGL / `SequentialViews` | Both 896×1007 eye images captured and viewed with the OpenGL vertical-origin correction | Left render/copy frame 308/308; right 314/314 | 91 submitted / 93 completed frames at the snapshot, zero end-frame failures; this mode was explicitly selected in a new process |

The OpenGL preview copy now checks both framebuffer statuses, rejects an
existing GL error before copying, and publishes its frame ID only after an
error-free blit. Disabling `VrCopyEyePreviewTextures` produced an actionable
capture error in 67 ms. Restoring the property produced a fresh successful
OpenGL capture at render/copy frame 3902/3902. The unsupported-mode and
disabled-preview controls cannot return a desktop or stale eye image.

Evidence: `reports/final-monado-vulkan-*`,
`reports/final-monado-opengl-sequential-*`,
`reports/final-gl-unsupported-sps-capture.json`, and
`reports/final-gl-*preview*`, plus their `mcp-captures/` images under the H run.
Vulkan validation counters are not OpenGL error counters. These captures are
correctness diagnostics, not CPU/GPU performance samples, and the two backends
were not captured at an identical headset pose/time for pixel equality.

The [H6 qualification matrix](vulkan14-h6-qualification-2026-09-14.md) identifies
nonzero source/stage/instancing coverage and exclusions. Vulkan accounting
proves recorded commands, not completed GPU execution; the viewed output is
separate evidence. Registered-only mesh identities, raw native mesh/task paths,
cached Vulkan primary replays, every permutation, and Vulkan geometry/
tessellation are not qualified by these counters. Slang-generated OpenGL GLSL
remains unqualified; the authored GLSL counterparts remain the supported route.

## Final policy decisions

- D4: reject the early-visibility barrier candidate and retain the generic
  barrier. Its 2.95% frame and 1.27% GPU median improvements missed the declared
  5% threshold; GPU controls were noisy and frame p99 did not meet the tail gate.
  Actual GPU scheduling-gap evidence was unavailable. Further specialization
  remains an explicitly deferred, unimplemented follow-up, gated on counter
  access, a measured overlap opportunity, complete consumers and stable controls.
- I1: retain specialized layouts. Twelve matched RTX 3090 runs passed their
  gates with identical output; GENERAL provided no material median benefit and
  no evidence of a better tail. See the [paired measurements](vulkan14-phase-i-policy-validation-2026-09-14.md).
- I2: defer address-command operands. The available RTX 3090 does not advertise
  `VK_KHR_device_address_commands`; ordinary BDA support does not satisfy that
  gate. Re-entry requires a supporting device/feature and measured lookup cost.
- I3: retain resource-specific VMA/staging placement. Actual mapped allocation,
  growth, descriptor pinning and retirement evidence is recorded in the policy
  note. Final timeline-gated controls prove that the exact submission remains
  pending while its old buffer is retained, then demonstrate reclamation after
  completion and slot drain. Earlier completed-before-query attempts remain
  failed evidence; none of these controls measures physical bandwidth.
  No changed placement is selected without a measured workload justification.

## Reproduction without retained scratch files

The committed [H6 world settings](../../../examples/rendering/vulkan14-h6-world.jsonc)
preserve the final 32-box/16-material cohort. Run from the repository root with
an installed Monado runtime at `Build/Deps/Monado/openxr_monado.json`:

```powershell
pwsh Tools/Limit-AgentValidation.ps1 -ReserveTaskRun
$run = [IO.Path]::GetFullPath((Join-Path 'Build/_AgentValidation' "$(Get-Date -Format yyyyMMdd-HHmmss)-h6-recheck"))
New-Item -ItemType Directory -Force "$run/scratch", "$run/mcp-captures" | Out-Null
$settings = Get-Content docs/examples/rendering/vulkan14-h6-world.jsonc -Raw | ConvertFrom-Json -AsHashtable
$settings | ConvertTo-Json -Depth 8 | Set-Content "$run/scratch/world.json"
$sessionEnvironment = @{
  XRE_UNIT_TEST_WORLD_SETTINGS_PATH = "$run/scratch/world.json"
  XRE_UNIT_TEST_RENDER_API = 'Vulkan'
  XRE_UNIT_TEST_VR_MODE = 'MonadoOpenXR'
  XRE_UNIT_TEST_VR_VIEW_RENDER_MODE = 'SinglePassStereo'
  XRE_OPENXR_VIEW_RENDER_MODE = 'SinglePassStereo'
  XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS = '1'
  XR_RUNTIME_JSON = (Resolve-Path Build/Deps/Monado/openxr_monado.json).Path
  XRE_VK_DESCRIPTOR_BACKEND = 'DescriptorIndexing'
  XRE_VULKAN_VALIDATION = '1'
  XRE_VULKAN_SYNC_VALIDATION = '1'
  XRE_VULKAN_VALIDATE_SPIRV = '1'
  XRE_SLANG_PILOTS = '0'
  XRE_SLANGC = "$run/missing-slangc.exe"
}
& Tools/Manage-McpEditorSession.ps1 Start -Name h6-recheck -Configuration Release `
  -SessionEnvironment $sessionEnvironment
& Tools/Invoke-Mcp.ps1 -Session h6-recheck -Method tools/call -Params @{
  name='configure_shader_command_coverage'; arguments=@{enabled=$true}
}
foreach ($eye in @('left','right')) {
  & Tools/Invoke-Mcp.ps1 -Session h6-recheck -Method tools/call -Params @{
    name='capture_viewport_screenshot'; arguments=@{vr_eye=$eye;output_dir="$run/mcp-captures"}
  }
}
& Tools/Manage-McpEditorSession.ps1 Stop -Name h6-recheck
```

Wait for actual world/XR frame readiness before capture; MCP readiness alone
does not establish rendered output. Query `get_shader_command_coverage`,
`get_render_profiler_stats`, and `invoke_method` for
`XREngine.RuntimeEngine.get_VRState`. Verify the actual renderer, requested/
effective mode, successful layer submissions and selected-eye copy frame IDs,
then view both PNGs. For the OpenGL positive control, explicitly set
`XRE_UNIT_TEST_RENDER_API=OpenGL` and both view-mode variables to
`SequentialViews` before a fresh start. The OpenGL `SinglePassStereo` request
is the negative control. It must not yield a substituted mode or renderer.

For the water/geometry desktop cohort, use the same settings with
`UnitBoxCount=4`, `UnitBoxMaterialCount=4`, `DynamicWaterQuad=true`,
`DynamicPointLightCount=1`, `DynamicLightsCastShadows=true`, and
`DynamicLightsForceShadowAtlas=true`. Select OpenGL and `VR_MODE=Desktop` through
the corresponding `XRE_UNIT_TEST_*` variables. Use `set_editor_camera_view` at
(-10, 6, 2), looking at (0, 0, -10), with `duration=0`. Resolve the camera component
with `get_render_state`, then invoke `SetManualExposure(1.0)`. Capture with
`include_screen_space_ui=true`, change the camera position and capture again.
Set the dynamic point light's `ShadowRenderMode` to `GeometryShader` and read
`EffectiveShadowRenderMode` directly. Avoid broad recursive component-property
dumps as a substitute for these targeted reads.

For reload, resolve the active water fragment shader's `TextFile` and program
metadata from its material. Mutate only the loaded `Text`: first insert a
constant magenta `OutColor` and return at the start of `main`, then submit an
invalid token, then restore the original text. Capture/view each state and
verify adopted source hashes/revisions and the failed replacement's last-good
behavior. Do not infer execution from loaded shader identities.

I1 uses the committed [paired RenderBench workflow](../../../developer-guides/rendering/renderbench-layout-policy.md)
and eight-pass recipe. The final twelve-process order was shuffled from six
alternating pairs using .NET `Random(20260914)` and descending Fisher–Yates;
record executable/recipe hashes and actual adapter/driver before comparison.
The I3 invocation against that Release RenderBench build was:

```powershell
dotnet $benchPath --backend Vulkan --execution-mode Presentationless `
  --scenario phase52-buffers --scenario-depth normal --scenario-repeats 2 `
  --scenario-frames 24 --width 1920 --height 1080 --frame-slots 3 `
  --output-dir "$run/i3-retained-native-buffers"
```

The final rebuilt control enables the scenario's pending-submission lifetime
probe. Set `XRE_VULKAN_VALIDATION=1`, `XRE_VULKAN_SYNC_VALIDATION=1`, and
`XRE_VULKAN_DIAGNOSTIC_PRESET=SyncValidation` for the qualified run. Both final
24-frame children passed. Keep the earlier failed attempts in the record.

## J outcome ledger

Each linked investigation retains the exact workloads, requested/executed
features, reproduction commands, correctness checks, measurements and limits.
Earlier phase measurements were not rerun or pooled with the final H/I controls.

| Task IDs | Final behavior and disposition | Correctness / measurement evidence |
| --- | --- | --- |
| A1–A5 | Retain the explicit Vulkan 1.4 contract and independent OpenGL route | [A/B](vulkan14-phases-ab-2026-09-08.md): feature/compiler inventory, clean desktop and true-SPS Monado controls; no universal speedup claimed |
| B1–B4 | Retain the corrected native descriptor-heap root, pipeline and lifetime contract; keep explicit binding selection | [A/B](vulkan14-phases-ab-2026-09-08.md): constants no longer overwrite descriptor indices; mutation, compute, UI, resize and XR controls; final build 52 had zero warnings/errors |
| C1–C2 | Retain reproducible baselines and declared total-cost/tail gates | [C](vulkan14-phase-c-baselines-2026-09-09.md): separate startup, mutation and steady-state cohorts with completed GPU timestamps and run variation; no optimization claim from baseline collection |
| D1–D3 | Retain existing slot wait placement and the dependency map; conditional wait relocation is not selected | [D](vulkan14-phase-d-waits-and-barriers-2026-09-09.md): pre-publication wait median 0.013–0.014 ms, below 0.1% of frame cost |
| D4 | Reject the tested specialization; defer further implementation behind explicit evidence gates | [D](vulkan14-phase-d-waits-and-barriers-2026-09-09.md), [barrier research](vulkan14-barrier-performance-research-2026-09-09.md): frame/GPU medians improve 2.95%/1.27%, below the 5% gate; GPU control spread 10.07%, frame p99 increases 14.03%; actual GPU-gap evidence unavailable |
| E1–E5 | Retain exact heap/reuse identities, scoped background replay, stable publication storage, redundant-bind suppression and bounded material reserves; keep existing default binding policy | [E](vulkan14-phase-e-reuse-and-descriptors-2026-09-09.md), [replay](vulkan14-background-replay-and-queue-overlap-2026-09-09.md), [follow-up](vulkan14-followup-stability-and-validation-2026-09-14.md): 70 native background reuses; eight final mutation/streaming controls, 11,080 captured frames with zero rejected frames, validation errors or capture readbacks; four native publication/idle controls; no general FPS claim |
| F1–F3 | Retain implemented explicit split submission for the supported presentationless workload; `Auto` stays graphics-only | [F](vulkan14-background-replay-and-queue-overlap-2026-09-09.md): byte-identical output, 140 frames without Vulkan errors, three-slot failure recovery; split GPU median 0.939312→1.371344 ms (+45.9945%); CPU change within control spread |
| G1–G3 | Retain implemented opt-in address-root pilot; `Immediate` stays default | [G](vulkan14-address-shading-root-2026-09-10.md): ABI/lifetime and byte-equal controls, 75% fewer pushed bytes; final immediate+BDA long-resize controls submitted 481 frames and produced 397 completed GPU timings each, with zero synchronization-validation errors and four loader warnings; all four paired images were identical. The earlier long-resize failure remains historical. |
| H1–H5 | Retain independent native Slang frontend and two explicit Vulkan pilots, full reflected ABI checks and cache version 7 | [H](vulkan14-native-slang-2026-09-10.md), [code closeout](vulkan14-code-closeout-2026-09-13.md): actual compute/material output and reload comparisons; higher compile cost, no general frame-time promotion |
| H6 | Retain the qualified authored GLSL/OpenGL routes and explicit supported XR modes; retain capture/coverage diagnostics and water/reload fixes | This note and [matrix](vulkan14-h6-qualification-2026-09-14.md): viewed source edits, failure recovery, geometry/tessellation and both XR eyes; unsupported/uncounted lanes remain explicit |
| I1 | Retain specialized layouts by default and GENERAL only as an explicit experiment | [I](vulkan14-phase-i-policy-validation-2026-09-14.md): twelve matched processes, identical hashes; pooled GPU median/p95 107.328/224.576 µs specialized versus 107.040/338.656 µs GENERAL, insufficient benefit for promotion |
| I2 | Defer unsupported address-command operands; retain the independent capability inventory | [I](vulkan14-phase-i-policy-validation-2026-09-14.md): `VK_KHR_device_address_commands` absent on the measured adapter; BDA is not a substitute |
| I3 | Retain existing resource-specific VMA/staging placement; no allocator rewrite selected | [I](vulkan14-phase-i-policy-validation-2026-09-14.md): final two 24-frame, three-slot children passed the gated pending-submission proof, retaining the old buffer while the exact submission remained pending and reclaiming it after completion and slot drain; bandwidth remains unmeasured |
| J1 | Finalize the selected implementation, qualification and policy record with explicit follow-up gates | This ledger, the updated TODO, H6 matrix, OpenGL framebuffer/linking guides, MCP capture/coverage reference and RenderBench layout-policy workflow |

D4 is the sole unchecked implementation item in the TODO. It remains
unimplemented under the original completion rule; closing J does not convert a
rejected experiment into delivered barrier specialization. Re-entry requires
usable scheduling/counter evidence, a measured critical-path opportunity, a
complete producer/consumer proof and stable paired controls. I2's evaluation is
complete with a defer decision; operand implementation requires a supporting
device/feature and measured lookup cost. I3 selects no placement change; its
final pending-submission proof passes, while bandwidth remains unmeasured. Broader Slang conversion
is outside the selected scope.

## Validation and handoff

- The H6 isolated Release editor build passed with **zero warnings and zero
  errors** in 64.61 seconds (`logs/final-monado-build.log`). It contains the
  final framebuffer, grab-resize, reload and OpenGL preview-provenance fixes;
  temporary native probes are removed and original water assets are unchanged.
- H6 runtime validation used the named isolated `vulkan14-h-code-xr`
  session. It was stopped after the controls. The existing external Monado
  service was not stopped by this work.
- Existing CPU/source-contract test-project blockers were repaired under the
  separately cleared test/API maintenance. The selected 95-test rerun passed
  95/95 with no skips, and the native frame-slot two/three-slot cases passed
  2/2. Three additional material readiness/publication checks also pass. The
  committed follow-up filter ran all **100 distinct cases together**, with zero
  failures/skips, in approximately two seconds. The broadened 335-case class run
  is separate: 284 passed and 51 failed;
  it is not a fully passing result and its stale-contract/review failures remain
  recorded in the September follow-up note.
- The follow-up Release Editor and RenderBench builds passed with zero warnings
  or errors in 44.16 s and 39.11 s. The final material-reporting-only RenderBench
  build passed in 4.75 s. Its four native controls also verified the new default
  240-boundary material budget and explicit published-generation/pop-in policy.
  Parent and child reports correctly identify their cold diagnostic readbacks.
  Eight frozen-Editor controls retained 11,080 capture samples, all with standard
  and synchronization validation active and zero validation errors, rejected
  frames or capture readbacks. The [follow-up](vulkan14-followup-stability-and-validation-2026-09-14.md)
  records scope, startup costs, hashes, old failed attempts and reproduction.
- Consequential native review found no remaining code blocker in H3/H6/I,
  framebuffer restoration or replacement preparation. It verified the ABI
  merge/schema/cache path, copy-provenance checks, feature admission and memory
  barriers. Runtime output checks remain the evidence for visible behavior.
- `git diff --check` passes. Local Markdown targets in the TODO, final ledger,
  H6/I notes and layout-policy guide resolve. Only D4 remains unchecked, with
  its reason and next decision explicit.
- No user visual confirmation has been received. No cross-vendor, physical-HMD,
  exhaustive shader-permutation, general frame-time improvement or new GPU-gap
  claim follows from this closeout.

The [committed follow-up filter](../../../examples/rendering/vulkan14-followup-test-filter.txt)
reproduces the 100 selected cases using a reserved `$run` as above:

```powershell
$filter = (Get-Content docs/examples/rendering/vulkan14-followup-test-filter.txt -Raw).Trim()
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj -c Release `
  --artifacts-path "$run/temp-build/final-tests" --filter $filter `
  --logger 'trx;LogFileName=final-targeted.trx' --results-directory "$run/reports/final-tests"
```

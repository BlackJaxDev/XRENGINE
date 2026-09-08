# Advanced pipeline orientation and periodic GPU stalls

Opened: 2026-09-08
Status: Targeted desktop runtime checks completed; user Windows-cursor confirmation remains pending. XR, decal publication, and imported-model lifetime validation are outside this completed slice.

## Report and scope

Following the skybox and AA-switch fixes, the user reports that Advanced is
upside down despite Y-up, and that GPU time is about 50 ms at idle and 150 ms
while moving the camera. Windows cursor movement stutters every 1–2 seconds
during movement and settles a few seconds after stopping; closing XRENGINE
restores normal desktop behavior.

The user also requests one unit-testing pipeline enum, serialized as known
render-pipeline class names, replacing UseAdvancedRenderPipeline and
ForceDebugOpaquePipeline. CustomRenderPipeline selects a separate script path.

## Orientation findings

The Advanced native and ordinary Vulkan raster paths use the configured Y-up
viewport convention. Both Advanced final window-output paths, however, omit
VPRC_RenderToWindow.FlipSourceYOnVulkan. Default sets that field from
RenderClipSpacePolicy.RequiresVulkanFramebufferTexturePresentationYFlip().
Advanced therefore applies a different final sampling orientation.

Earlier MCP screenshots captured the intermediate R16G16B16A16 HDR target,
before this final window copy. Their upright appearance did not validate the
swapchain presentation. The new validation must inspect the final output too.

The Advanced desktop window command helper now applies the same clip-space
presentation policy in both AA and no-AA branches. A separate native
surface-reconstruction Y conversion was also corrected. Both MCP camera angles
and the final RenderDoc swapchain thumbnail were viewed upright with sky and
cube content, including an admitted native draw.

AA switching now commits rather than freezing. The latest FXAA and TSR switches
committed in 2.011 seconds and 1.815 seconds respectively, with both pipelines
admitted and their histories committed.

## Performance evidence

User run:
Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-09-08_14-38-30_pid53272/.
The Advanced resource switch commits in 278.47 ms. Later recurring frames show
about 100–137 ms waiting for GPU timeline completion, with 14–23 ms of CPU work.
No recurring pipeline-compilation or GC event has yet been tied to the stalls.

The full vector-store production shader before workgroup allocation validated in
PID 38072 at 2.4991 ms GPU idle; eight motion samples measured 2.2926–2.7284 ms,
with 0.047–0.062 ms waits. Native execution was admitted and history advanced.
The allocator rejection applies only to the zero-local-light baseline, not to
populated local-light workloads.

An independent populated test in PID 55080 held a shadows-off point light at
radius 1 stable at 3.7702 ms. Raising only its radius to 8 lost the device.
PID 4636 replaced the allocation result with the existing exact GPU overflow
repair sentinel for nonzero cells; radius 8 then remained stable at 6.7–7.1 ms,
and a cube measured 3.7876 ms. The storage layout was unchanged.

The production workgroup allocator validated in PID 15172 after a 0-warning,
0-error build in 3:14.61. It uses ten uniform barriers, vector stores, at most
64 global CAS attempts per workgroup, and exact repair on reservation failure.
Radius 1 measured 5.5376 ms; five radius-8 samples measured 6.4792–9.537 ms;
and four radius-100 samples measured 7.5638–8.5336 ms. Native execution was
admitted, history advanced, and no device loss occurred. A live cube plus
point-radius-100 plus spot-radius-8 scene measured 9.768 ms. Motion eventually
settled at 2.44–2.61 ms, but the full sample range was 0.565–10.678 ms.

RenderDoc capture `renderdoc/workgroup-full-point-spot.rdc` shows event 167
`BuildFroxels` at 6.96832 ms under populated load. The empty-profile average was
0.808 ms, compared with the earlier 128.535 ms baseline. Its grid has 25,092
real list indices and 30,616 cells using the exact-repair sentinel; bounded
retries did not exhaust the 1M capacity. Decals were all zero because no runtime
publisher currently feeds Advanced decals, so decal integration validation is
deferred explicitly.

The current build has all eight cached native GPU timer path names verified, including HiZ,
covering the dispatches and indirect loop. Timer output filenames use hyphens,
fixing the returned-path mismatch; the returned dump path exists. Real vector
CPU/GPU statistics are independently verified. Windows cursor behavior under the
production path remains pending.

## Capture GPU timings

In the editor, open **Profiler > GPU Timings**, enable the GPU profiling toggle,
and choose **Dump** to write the current timing capture. For a session-only
PowerShell launch, set the process environment before starting the editor:

```powershell
$env:XRE_PROFILE_CAPTURE = "1"
$env:XRE_GPU_TIMESTAMP_DENSE = "1"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

These values apply to that PowerShell session and do not change saved editor
preferences. GPU timing scopes are inclusive, so parent and child timings must
not be summed.

## Pipeline selector validation

All targeted selector checks completed. UnitTesting PID 52008 loaded a valid
relative `.xrs` path and assigned `CustomRenderPipeline` to the camera. Its
clear-plus-mesh script validates selector/bootstrap loading, not a full
presentation fixture. Advanced on the minimal no-model fixture in PID 39076 was
admitted, reached history 471, measured 3.4563 ms GPU, and had no device loss.
DebugOpaque in PID 3220 and Default in PID 30460 selected the expected concrete
types and reached histories 31 and 11.

Invalid Custom selection failed explicitly: PID 44388 reported a
`FileNotFoundException` with the resolved missing path, and PID 58792 reported
an `InvalidOperationException` with the resolved path and inner `Unknown render
command 'not_a_render_command'`. These startup failures did not silently fall
back to another pipeline.

The original saved UnitTesting three-model fixture reached the expected selection
before later failures: Debug PID 39660 exited through the
`VkShader.Invalidate`/compile-drain path; Default PID 52532 terminated with
0xC0000005 during `vkDestroyPipeline` through `DestroyPipelineImmediate`,
`DrainComputePipelineCompileJobs`, `AcquireCompilationMutationLease`, and
`VkRenderProgram.Link`. The preserved stderr is
`logs/default-selector-stderr.log`. This is an observed imported-model/shader
lifetime scope; it remains unfixed and is not claimed independent of selection.
The minimal fixture passes.

## Reproduction and evidence ownership

The owned `skybox-aa-debug` isolated session is stopped. Scratch evidence remains
under Build/_AgentValidation/20260908-113838-vulkan-skybox-aa/. Session-only
profiling overrides avoid changing saved editor preferences. Capture tooling uses
the matching RenderDoc 1.41 runtime already available locally. No tests are added
or modified before feature validation and explicit user clearance. Preserve the
existing Vulkan XR/Advanced TODO edits.

The user-confirmed smooth no-op evidence is retained as
`scratch/BuildFroxels-user-confirmed-smooth-noop.comp`,
`renderdoc/user-confirmed-smooth-noop.rdc`, and
`reports/user-confirmed-smooth-working-tree.patch`. The no-op diagnostic remains
separate from the production result. The final checkpoint is in
`reports/validated-workgroup-checkpoint` with its SHA-256 manifest. The
checkpoint directory and manifest include the needed new/untracked code;
`reports/validated-workgroup-working-tree.patch` remains a tracked-diff-only
artifact.

Targeted desktop runtime checks: completed. User Windows-cursor confirmation remains pending; full XR, decal-publication, and imported-model lifetime validation remain out of scope.
## Final saved-selector confirmation

The local ignored `Assets/UnitTestingWorldSettings.jsonc` now selects
`Rendering.RenderPipeline: AdvancedRenderPipeline`, matching the requested debug
pipeline. PID 24264 launched normal Default World from that saved selector,
without `XRE_UNIT_TEST_RENDER_PIPELINE`. It admitted Advanced with TSR, displayed
the sky and grid (PNG inspected), and advanced history to 3327 without a pending
generation or device loss. Four idle GPU command-buffer samples were
3.9903–4.0709 ms; six camera-motion samples were 1.2130–4.2289 ms, with GPU waits
0.0412–0.0529 ms. Its named froxel timer averaged 0.774 ms over 128 samples.
The retained dump is `logs/stored-selector-defaultworld-gpu-profile.log`.
This verifies the final source and stored selection; Windows cursor behavior
still needs the user's confirmation. The named editor session was stopped after
validation. Normal editor outputs must be rebuilt to consume the C# changes.

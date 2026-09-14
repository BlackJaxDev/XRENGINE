# RenderBench Vulkan image-layout policy

RenderBench exposes a paired layout experiment for GPU-pass fixtures. The
default is `specialized`; `general` is an explicit candidate that requires the
host to advertise and enable `VK_KHR_unified_image_layouts`.

The option is parsed by `XREngine.RenderBench/RenderBenchOptions.cs`:

```text
--layout-policy specialized|general
```

`specialized` is the default. `general` is accepted only for a `GpuPass`
fixture and is rejected for scenario profiles. The host admission check is in
`RenderBenchProfileExecutor`: the result is valid only when
`SupportsUnifiedImageLayouts` is true. The effective configuration and result
both carry `LayoutPolicy`, so reports identify the requested/effective variant.
The environment request is the RenderBench-only
`XRE_VK_RENDER_BENCH_UNIFIED_IMAGE_LAYOUTS=1` path; it does not change
ordinary renderer image-layout policy, and the video feature is tracked
separately from the ordinary unified-layout feature.

The current fixture implementation is in
`XREngine.RenderBench/RenderBenchFullscreenPipeline.cs`. Each pass transitions
to `ColorAttachmentOptimal` for `specialized`, or `General` for the candidate,
then renders. The specialized control restores `General` between passes so the
paired experiment retains the same inter-pass dependency. The candidate keeps
the corresponding barriers and does not remove the final transfer transition.
`Undefined` initialization and required final transfer/presentation layouts
remain intact.

The owned multi-pass recipe is
`docs/examples/profiling/recipes/gpu-layout-transitions.jsonc`. It uses the
existing `gpu-lighting` fixture with eight repeated fullscreen
color-attachment passes and eight draws. This is a synthetic repeated
color-attachment transition workload, not the production lighting pipeline.
The original `gpu-lighting-pass.jsonc` remains the one-pass control.

Example paired invocation (write evidence below the current task run):

```powershell
pwsh Tools/Limit-AgentValidation.ps1 -ReserveTaskRun
$run = Join-Path 'Build/_AgentValidation' "$(Get-Date -Format yyyyMMdd-HHmmss)-layout-policy"
$benchRoot = Join-Path $run 'temp-build'
dotnet build .\XREngine.RenderBench\XREngine.RenderBench.csproj -c Release `
  --artifacts-path $benchRoot
$benchPath = Join-Path $benchRoot 'bin\XREngine.RenderBench\release\XREngine.RenderBench.dll'
dotnet $benchPath `
  --backend Vulkan --execution-mode Component `
  --recipe-file .\docs\examples\profiling\recipes\gpu-layout-transitions.jsonc `
  --layout-policy specialized --output-dir "$run\specialized"

dotnet $benchPath `
  --backend Vulkan --execution-mode Component `
  --recipe-file .\docs\examples\profiling\recipes\gpu-layout-transitions.jsonc `
  --layout-policy general --output-dir "$run\general"
```

Use `Tools/Manage-McpRenderBenchSession.ps1` for an isolated managed session
when MCP control is needed. Keep outputs under the task run root. Synthetic
fullscreen fixtures provide attribution for command recording, barriers,
layout admission, and output hashes; they do not represent production
post-process workload or establish a general performance result.

Future acceptance requires: host requested/available/enabled feature records;
identical output correctness and validation results; transition and barrier
counts; completed GPU timing distributions; CPU preparation/recording cost;
and a retain, reject, or defer decision across repeated matched controls.
Keep presentation, external-image, transfer, initialization, queue ownership,
and resource lifetime transitions in the comparison. Do not infer benefit from
fewer transitions or from a single FPS/timing sample.

The [September 14 comparison](../../work/investigations/rendering/vulkan14-phase-i-policy-validation-2026-09-14.md)
ran six fresh processes per policy on RTX 3090 / driver 610.88. All runs passed
the gates and produced identical output hashes. The pooled GPU median/p95 was
107.328/224.576 microseconds for specialized and 107.040/338.656 for GENERAL.
Keep specialized as the default: this experiment provides no material median
benefit and no evidence of a safer tail. GENERAL remains an explicit benchmark
option, with no promotion to ordinary renderer image use.

`VK_KHR_device_address_commands` is a separate capability from existing buffer
device address support. The September 13 local RTX 3090 inventory records no
advertised command extension; see the Vulkan 1.4 TODO's capability evidence.
I2 therefore remains deferred. I3 should report the existing VMA allocation
memory type, heap, property flags, mapped/coherent/device-local status, traffic,
and completion lifetime for the retained heap/address workload; it does not
justify a universal allocator-policy change.

Related contracts:

- `VulkanPhysicalImageGroup` and `VulkanDescriptorImageLayouts` own tracked and descriptor image layouts.
- `VulkanDeviceCapabilityReporter` records available/enabled capability state.
- `VulkanDeviceContext.FeatureQueries.cs` queries unified image-layout features.
- The phase I and D4 evidence gates are recorded in [the Vulkan 1.4 TODO](../../work/todo/rendering/vulkan-14-performance-and-shader-modernization-todo.md) and [the barrier investigation](../../work/investigations/rendering/vulkan14-phase-d-waits-and-barriers-2026-09-09.md).

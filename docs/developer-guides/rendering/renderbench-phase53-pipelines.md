# RenderBench Phase 5.3 pipeline evidence

`phase53-pipelines` is a presentationless, fresh-process RenderBench scenario
for Vulkan shader artifacts, native pipeline-cache identity, asynchronous native
pipeline creation, and steady-state readiness. It is a correctness/provenance
scenario, not a frame-time benchmark.

Run it from the repository root with an isolated output and cache root. Native
standard and synchronization validation are intentionally enabled for this
evidence run.

```powershell
$env:XRE_VULKAN_VALIDATION = '1'
$env:XRE_VULKAN_SYNC_VALIDATION = '1'

dotnet .\Build\RenderBench\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.RenderBench.dll `
  --scenario phase53-pipelines `
  --scenario-depth both `
  --scenario-frames 12 `
  --scenario-repeats 2 `
  --width 1279 --height 719 `
  --output-dir Build\_AgentValidation\<run>\reports\phase53-pipelines `
  --scenario-cache-root Build\_AgentValidation\<run>\temp-build\pipeline-cache
```

The parent launches eight children: cold then warm for normal and reversed
depth, repeated twice. Each cold/warm pair receives its own cache root; the
warm child uses exactly the cache root populated by its cold sibling.

Child `scenario-result.json` files record:

- device/driver/target-mode/build/shader-artifact cache identity;
- native cache bytes loaded by a warm child;
- cold pipeline admission retries for history-dependent late compute work;
- queued and worker native-pipeline creation during preparation;
- completed production receipts; and
- a steady interval with zero graphics/compute/worker creates, pending graphics
  or compute jobs, foreground waits, and render-thread shader compilation.

The pipeline lane rejects a cold cohort that does not observe its required
late-compute admission retry. A retry aborts before submission and retries the
same logical step only after scheduled work advances; it never substitutes an
empty pipeline or records a partial dispatch. `VulkanExplicitProductionAdmissionPendingException`
is the explicit public signal for that condition.

## Latest implementation evidence

The isolated `reports/phase53-pipelines-final` run under the Phase 5.2
bounded rendering validation root completed all eight children (96 production
receipts) with standard and
synchronization validation enabled and zero validation errors. Every child
reported a preparation queue and worker-native creates; every steady interval
reported zero native creates, pending jobs, foreground waits, and render-thread
shader compiles. The warm children loaded persisted native cache bytes.

This closes the Phase 5.3 pipeline acceptance alongside its material and
streaming integration. See [the headless closeout](../../work/progress/rendering/vulkan-phase53-headless-completion.md).
It does not establish live desktop/OpenXR behavior or cross-vendor performance.

## Pending-propagation contract

The ordinary compute planner, auto-exposure dispatch admission, ordered compute
producer, and advanced visibility family all preserve the typed readiness
result. Advanced visibility distinguishes `Missing`, `Pending`, and `Failed`
when resolving its capability, early closure, and late closure. Missing or
pending native pipelines abort the unsubmitted frame as retryable admission;
failed programs remain unsupported. No path binds a zero native pipeline or
silently drops an advanced visibility dispatch.

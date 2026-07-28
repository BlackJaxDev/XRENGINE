# LLM Self-Iterating Performance Pipeline Progress

## Objective

Provide a bounded pipeline in `XREngine.Benchmarks` that can diagnose and
attempt rendering performance fixes against explicit Unit Testing World
workloads, validate reload behavior, formally compare before/after captures,
retain improvements, restore regressions, and avoid repeating recorded
attempts.

## Implemented

- Added a schema-backed JSONC campaign and scenario matrix supporting
  Vulkan/OpenGL, every mesh-submission strategy, alternate Unit Testing World
  JSONC files, and per-scenario rendering/VR/diagnostic overrides.
- Extended the process profiler harness to select a backend and settings JSONC,
  allocate an isolated read-only MCP port, and capture a CPU hierarchy plus all
  executing GPU render-pipeline timing dumps for every repetition.
- Split scenario evidence into a dense diagnostic cohort for attribution and a
  separately relaunched clean cohort for formal acceptance.
- Added crash, hang, missing-evidence, backend/strategy mismatch, workload
  identity, fallback, readback, and output-policy gates.
- Added a two-phase external LLM protocol: read-only proposal first, then one
  allow-listed implementation.
- Added deterministic proposal fingerprints read from separate accepted and
  rejected Markdown ledgers.
- Added working-tree checkpoints that retain accepted changes and restore
  rejected or unauthorized attempts without staging or committing.
- Added Git commit/branch guards, controller-owned ledger protection, and
  fail-closed cleanup reporting so the campaign never attempts to rewrite
  repository history or silently claims a failed rollback succeeded.
- Added owned MCP editor-session reload validation with shader reload, OpenGL
  renderer reload, Vulkan/core editor relaunch, and relaunch recovery after a
  reload failure or delayed post-reload crash.
- Added a render-readiness gate separate from MCP readiness and bounded
  validation-time activation of CPU logging, render statistics, and GPU
  pipeline timestamps. The LLM edit window does not continuously stream
  profiler samples.
- Added a clean rebuild/relaunch of the full scenario matrix before accepting
  any candidate.
- Added a Windows PowerShell launch wrapper and VS Code validate, baseline, and
  full-run tasks.

## Validation

- `dotnet build .\XREngine.Benchmarks\XREngine.Benchmarks.csproj -c Debug
  --no-restore -p:VulkanPerformanceToolOnly=false`: passed with zero errors.
  The remaining output was the repository's existing Magick.NET advisory and
  `OscCore-NET9` warning set.
- PowerShell parser validation passed for
  `Tools/Measure-GameLoopRenderPipeline.ps1`,
  `Tools/Manage-McpEditorSession.ps1`, and
  `Tools/Benchmarks/Invoke-SelfIteration.ps1`.
- The sample smoke campaign passed `--validate-only`.
- The two alternate Unit Testing World settings-path tests passed, including
  resolution relative to the editor working directory and fail-fast handling
  for a missing explicitly selected JSONC file.
- A complete `--baseline-only` orchestration run passed for Vulkan plus
  `GpuIndirectZeroReadback`. The harness built and launched an owned editor,
  captured the dense diagnostic cohort, shut it down, relaunched a clean formal
  cohort, validated both, and exited without invoking an LLM.
  - Dense diagnostic: 1,371 steady-state samples, 6.120 ms render p95,
    1,371 ready GPU samples, 6.245 ms GPU p95, and both CPU hierarchy and
    per-pipeline GPU timing dumps.
  - Clean formal: 1,755 steady-state samples and 5.075 ms render p95.
  - Both captures reported the requested Vulkan backend and
    `GpuIndirectZeroReadback` strategy with a stable workload identity.
  - Both steady-state windows reported zero GPU readback bytes and zero mapped
    buffers. Bounded initialization readback remained visible in the
    all-phase diagnostic counters and was not misclassified as a steady-state
    zero-readback violation.
  - Evidence:
    `Build/_AgentValidation/self-iteration/vulkan-baseline-smoke/20260728-144129/`.
- OpenGL `CpuDirect` smoke captures reached stable formal windows and produced
  CPU evidence, but issued timestamp queries without resolving any GPU result.
  The campaign correctly fails closed when GPU diagnostic dumps are required.
  This pre-existing instrumentation issue is tracked in
  `docs/work/investigations/rendering/opengl-gpu-pipeline-timestamp-readiness-2026-07-28.md`.
- The owned reload-controller smoke reached an active Vulkan
  `DefaultRenderPipeline`, enabled the three profiling preferences through MCP,
  resolved GPU timestamps, and wrote both
  `profiler-cpu-frame-2026-07-28-15-47-36-137-b77fe21f.log` and
  `profiler-gpu-pipeline-defaultrenderpipeline-2-2026-07-28_15-47-36-215-7d67272d.log`.
  A Vulkan shader invalidation returned success and then exposed a native draw
  crash; the controller now treats this delayed failure as a relaunch trigger,
  not as a successful hot reload. The exact owned recovery session was stopped
  through `Manage-McpEditorSession.ps1`.
- A three-repetition aggregation smoke confirmed that one nonzero
  zero-readback invariant rejects the measurement even when the other two
  repetitions are zero.

## Follow-Up

Formal performance campaigns require representative local settings, stable GPU
clock policy, an otherwise idle clean worktree, and a separately invocable
autonomous LLM command. RenderDoc remains an escalation tool for GPU-bound
passes whose timestamp dumps are insufficient; it is not included in formal
timing repetitions because the capture layer can perturb results.

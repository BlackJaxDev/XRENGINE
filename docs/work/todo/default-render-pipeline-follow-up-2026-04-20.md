# Default Render Pipeline Backlog

Audit basis: 2026-04-20.
Status: backlog closed on 2026-04-20. The default render pipeline remains stable, and the remaining follow-up items from this audit are now implemented. The broader implementation history and resolved behavior notes live in [default-render-pipeline-notes.md](../../architecture/rendering/default-render-pipeline-notes.md).

This backlog replaces:

- `default-render-pipeline-correctness-and-maintainability-2026-03-31.md`
- `default-render-pipeline-regression-fixes-2026-03-28.md`

No active correctness blockers were confirmed during the audit that produced this backlog.

## Backlog

- [x] Move probe sync and rebuild work out of bind-time lighting setup.
  Implemented with [VPRC_SyncLightProbeResources.cs](../../../XRENGINE/Rendering/Pipelines/Commands/VPRC_SyncLightProbeResources.cs), plus once-per-frame sync entrypoints in both pipeline variants.
- [x] Split V1 command-chain construction into its own partial and named append helpers.
  Implemented in [DefaultRenderPipeline.CommandChain.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs), preserving the live V1 behavior while keeping the old inline builder only as legacy reference code.
- [x] Share AA invalidation dependency lists across V1 and V2.
  Implemented with [RenderPipelineAntiAliasingResources.cs](../../../XRENGINE/Rendering/Pipelines/Types/RenderPipelineAntiAliasingResources.cs), which now owns the shared dependency lists and invalidation helpers.
- [x] Add a dedicated `ForwardPassFBO` compatibility predicate.
  Implemented in both [DefaultRenderPipeline.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs) and [DefaultRenderPipeline2.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs), with explicit attachment validation wired into the command chains.

## Keep Out Of Backlog

- Do not reopen the March runtime-validation checklists unless a current scene reproduces one of those issues.
- Do not treat the now-implemented follow-up files as evidence of current instability; they were backlog cleanup and cache-safety hardening.
- Do not treat old unchecked March items as evidence that the pipeline is currently unstable; most were already implemented or superseded by [default-render-pipeline-notes.md](../../architecture/rendering/default-render-pipeline-notes.md).
using System.Globalization;
using System.Runtime.InteropServices;
using AssetStats = XREngine.RuntimeEngine.Rendering.Stats.SceneAssets;
using FrameOutputStats = XREngine.RuntimeEngine.Rendering.Stats.FrameOutputs;
using LifecycleStats = XREngine.RuntimeEngine.Rendering.Stats.FrameLifecycle;
using RendererStats = XREngine.RuntimeEngine.Rendering.Stats.RendererState;
using OutputManifest = XREngine.RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot;
using OutputEntry = XREngine.RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot;

namespace XREngine.Editor;

public partial class ProfilerPanel
{
    private int _outputPageIndex;
    private int _outputCount;
    private long _outputManifestRevision;
    private readonly OutputEntry[] _outputPageEntries = new OutputEntry[NativeProfilerOutputPage.Size];
    private OutputManifest _outputManifest;
    private bool _outputManifestValid;
    private bool _outputManifestFrozen;

    private static void CaptureRendererState(List<NativeProfilerDisplayRow> rows)
    {
        if (!Engine.EditorPreferences.Debug.EnableRenderStatisticsTracking)
            rows.Add(new("Render tracking disabled", string.Empty,
                "Enable tracking in Settings. Last-frame counters can be empty or stale."));
        rows.Add(new("Backend", RendererStats.ActiveRenderBackend, RendererStats.ActiveSubmissionStrategy));
        rows.Add(new("Advanced pipeline", RendererStats.AdvancedPipelineEffectiveKind.ToString(),
            RendererStats.AdvancedPipelineRejectionReason));
        AddCount(rows, "Program switches", RendererStats.ShaderProgramSwitches);
        AddCount(rows, "Pipeline switches", RendererStats.ProgramPipelineSwitches);
        AddCount(rows, "VAO binds", RendererStats.VaoBinds);
        AddCount(rows, "VAO bind skips", RendererStats.VaoBindSkips);
        AddCount(rows, "Texture binds", RendererStats.TextureBinds);
        AddCount(rows, "Texture bind skips", RendererStats.TextureBindSkips);
        AddCount(rows, "Uniform calls", RendererStats.UniformCalls);
        AddCount(rows, "Barrier calls", RendererStats.BarrierCalls);
        AddCount(rows, "Timestamp queries", RendererStats.TimestampQueryCount);
        AddCount(rows, "CPU direct draws", RendererStats.CpuDirectDrawCalls);
        AddCount(rows, "GPU indirect draws", RendererStats.GpuIndirectDrawCalls);
        AddCount(rows, "GPU meshlet draws", RendererStats.GpuMeshletDrawCalls);
        AddCount(rows, "Upload bytes", RendererStats.BufferUploadBytes);
        AddCount(rows, "Timestamp readback bytes", RendererStats.TimestampQueryReadbackBytes);
    }

    private static void CaptureSceneAssets(List<NativeProfilerDisplayRow> rows)
    {
        if (!Engine.EditorPreferences.Debug.EnableRenderStatisticsTracking)
            rows.Add(new("Render tracking disabled", string.Empty,
                "Enable tracking in Settings. Last-frame counters can be empty or stale."));
        AddCount(rows, "Visible renderers", AssetStats.VisibleRendererCount);
        AddCount(rows, "Visible submeshes", AssetStats.VisibleSubmeshCount);
        AddCount(rows, "Visible triangles", AssetStats.VisibleTriangleCount);
        AddCount(rows, "Material slots", AssetStats.MaterialSlotCount);
        AddCount(rows, "Active materials", AssetStats.ActiveMaterialCount);
        AddCount(rows, "Textures", AssetStats.TextureCount);
        AddCount(rows, "Resident texture bytes", AssetStats.ResidentTextureMemoryBytes);
        AddCount(rows, "Texture upload jobs", AssetStats.TextureUploadJobs);
        AddCount(rows, "Texture upload bytes", AssetStats.TextureUploadBytes);
        rows.Add(new("Texture upload time", AssetStats.TextureUploadMs.ToString("F3", CultureInfo.InvariantCulture), "ms"));
        AddCount(rows, "Shader variants requested", AssetStats.ShaderVariantsRequested);
        AddCount(rows, "Shader variants linked", AssetStats.ShaderVariantsLinked);
        AddCount(rows, "Shader variants failed", AssetStats.ShaderVariantsFailed);
        AddCount(rows, "Skinned renderers", AssetStats.SkinnedRendererCount);
        AddCount(rows, "Bone upload bytes", AssetStats.BoneMatrixUploadBytes);
        AddCount(rows, "Blendshape delta bytes", AssetStats.BlendshapeDeltaBytes);
    }

    private static void CaptureFrameLifecycle(List<NativeProfilerDisplayRow> rows)
    {
        rows.Add(new("Frame IDs", LifecycleStats.UpdateFrameId.ToString(CultureInfo.InvariantCulture),
            $"collect {LifecycleStats.CollectFrameId} | swap {LifecycleStats.SwapFrameId} | render {LifecycleStats.RenderFrameId} | present {LifecycleStats.PresentFrameId}"));
        rows.Add(new("Collect generation", LifecycleStats.PublishedCollectGeneration.ToString(CultureInfo.InvariantCulture),
            $"requested {LifecycleStats.RequestedCollectGeneration} | completed {LifecycleStats.CompletedCollectGeneration} | consumed {LifecycleStats.ConsumedCollectGeneration}"));
        rows.Add(new("Collect wait", LifecycleStats.CollectWaitForRenderMs.ToString("F3", CultureInfo.InvariantCulture), LifecycleStats.CollectWaitReason));
        rows.Add(new("Render wait", LifecycleStats.RenderWaitForCollectMs.ToString("F3", CultureInfo.InvariantCulture), LifecycleStats.RenderWaitReason));
        AddCount(rows, "Skipped collect", LifecycleStats.SkippedCollectFrames);
        AddCount(rows, "Stale visibility reuse", LifecycleStats.StaleCollectReuseFrames);
        rows.Add(new("Package production", LifecycleStats.FramePackageProductionMs.ToString("F3", CultureInfo.InvariantCulture), "ms"));
        rows.Add(new("Package publication", LifecycleStats.FramePackagePublicationMs.ToString("F3", CultureInfo.InvariantCulture), "ms"));
        rows.Add(new("Package consumption", LifecycleStats.FramePackageConsumptionMs.ToString("F3", CultureInfo.InvariantCulture), "ms"));
        AddCount(rows, "Packages rejected", LifecycleStats.FramePackagesRejected);
    }

    private void CaptureFrameOutputs(List<NativeProfilerDisplayRow> rows)
    {
        _outputCount = FrameOutputStats.CopyLastManifestPage(_outputPageEntries,
            _outputPageIndex, out _outputManifest, out _outputPageIndex,
            out _outputManifestRevision);
        _outputManifestValid = true;
        _outputManifestFrozen = false;
        FormatFrameOutputs(rows, _outputManifest);
    }

    private void FreezeOutputManifest()
    {
        if (!_outputManifestValid || _rows.View != ENativeProfilerView.FrameOutputs)
            return;
        _outputManifest = FrameOutputStats.GetLastManifest(out _outputManifestRevision);
        _outputCount = _outputManifest.Outputs.Length;
        _outputManifestFrozen = true;
        int previousCount = _rows.Count;
        _diagnosticScratch.Clear();
        FormatFrameOutputs(_diagnosticScratch, _outputManifest);
        if (_rows.SetDiagnostics(CollectionsMarshal.AsSpan(_diagnosticScratch)))
            SignalRowsChanged(previousCount);
    }

    private void ReleaseFrozenOutputManifest()
    {
        _outputManifestFrozen = false;
        _outputManifest = default;
        _outputManifestValid = false;
    }

    private void ChangeOutputPage(int direction)
    {
        if (!_outputManifestValid || direction is not (-1 or 1))
            return;
        NativeProfilerOutputPage current = NativeProfilerOutputPage.Resolve(
            _outputCount, _outputPageIndex);
        int next = Math.Clamp(current.Index + direction, 0, Math.Max(0, current.PageCount - 1));
        if (next == current.Index)
            return;
        _outputPageIndex = next;
        if (!_outputManifestFrozen)
            _outputCount = FrameOutputStats.CopyLastManifestPage(_outputPageEntries,
                _outputPageIndex, out _outputManifest, out _outputPageIndex,
                out _outputManifestRevision);
        int previousCount = _rows.Count;
        _diagnosticScratch.Clear();
        FormatFrameOutputs(_diagnosticScratch, _outputManifest);
        if (_rows.SetDiagnostics(CollectionsMarshal.AsSpan(_diagnosticScratch)))
            SignalRowsChanged(previousCount);
    }

    private void FormatFrameOutputs(List<NativeProfilerDisplayRow> rows, OutputManifest manifest)
    {
        rows.Add(new("Frame", manifest.FrameId.ToString(CultureInfo.InvariantCulture),
            $"publication {_outputManifestRevision} | VR {manifest.VrActive} | mirror {manifest.MirrorMode} | visibility {manifest.VisibilityPolicy} | budget {manifest.BudgetBand} | workload {manifest.WorkloadIdentityHash}"));
        rows.Add(new("Whole frame", manifest.WholeFrameMs.ToString("F3", CultureInfo.InvariantCulture),
            $"budget {manifest.BudgetMs:F2} ms | p50 {manifest.WholeFrameP50Ms:F3} | p90 {manifest.WholeFrameP90Ms:F3} | p95 {manifest.WholeFrameP95Ms:F3} | p99 {manifest.WholeFrameP99Ms:F3} | worst {manifest.WholeFrameWorstMs:F3} ms"));
        var work = manifest.Work;
        rows.Add(new("Work items", work.RecordedWorkItemCount.ToString(CultureInfo.InvariantCulture),
            $"requests/events {work.OutputRequestCount}/{work.OutputEventCount} | recorded/reused/duplicated {work.RecordedWorkItemCount}/{work.ReusedWorkItemCount}/{work.DuplicatedWorkItemCount}"));
        rows.Add(new("Output events", work.CollectEventCount.ToString(CultureInfo.InvariantCulture),
            $"collect/swap/render/submit/overlay/present {work.CollectEventCount}/{work.SwapEventCount}/{work.RenderEventCount}/{work.SubmitEventCount}/{work.OverlayEventCount}/{work.PresentEventCount}"));
        rows.Add(new("Visibility work", work.SceneSnapshotCount.ToString(CultureInfo.InvariantCulture),
            $"scene snapshots {work.SceneSnapshotCount} | visibility builds {work.VisibilityBuildCount} | view families {work.UniqueViewFamilyCount} | target variants {work.TargetVariantCount}"));
        rows.Add(new("Plan cache", work.CompiledPlanCacheHits.ToString(CultureInfo.InvariantCulture),
            $"compiled hits/misses {work.CompiledPlanCacheHits}/{work.CompiledPlanCacheMisses} | physical hits/misses {work.PhysicalPlanCacheHits}/{work.PhysicalPlanCacheMisses} | generations {work.PhysicalPlanGenerations} | alias reuse {work.PhysicalPlanAliasReuses}"));
        rows.Add(new("Planner", work.PlannerArenaHighWater.ToString(CultureInfo.InvariantCulture),
            $"arena high-water | graph generation {work.RenderGraphPlanGeneration} | shared passes {work.SharedPassReuseCount} | prunes {work.PlannerPruneCount} | eviction deferrals {work.PlannerEvictionDeferralCount}"));
        rows.Add(new("Output policy", work.MissedDeadlineCount.ToString(CultureInfo.InvariantCulture),
            $"missed deadlines | CPU/GPU deferrals {work.CpuBudgetDeferralCount}/{work.GpuBudgetDeferralCount} | stale reuse {work.StaleResultReuseCount} | unauthorized {work.UnapprovedPolicyEventCount} | submission rejected {work.SubmissionRejectionCount} | in-flight waits {work.GlobalInFlightWaitCount} | flushes {work.ForceFlushCount}"));

        NativeProfilerOutputPage page = NativeProfilerOutputPage.Resolve(_outputCount, _outputPageIndex);
        _outputPageIndex = page.Index;
        rows.Add(new("Output page", page.PageCount == 0 ? "0 / 0" : $"{page.Index + 1} / {page.PageCount}",
            $"{page.Total} outputs; {page.Count} on this page. Activate Previous/Next to reach every output."));
        if (page.Index > 0)
            rows.Add(new("Previous outputs", string.Empty, "Activate to show the preceding 16 outputs.", OutputPageAction: -1));
        if (page.Index + 1 < page.PageCount)
            rows.Add(new("Next outputs", string.Empty, "Activate to show the following 16 outputs.", OutputPageAction: 1));
        for (int index = page.Start; index < page.Start + page.Count; index++)
        {
            OutputEntry output = _outputManifestFrozen
                ? manifest.Outputs[index] : _outputPageEntries[index - page.Start];
            var request = output.Request;
            var target = request.Target;
            var schedule = request.Schedule;
            double cpuMs = output.CollectCpuMs + output.SwapCpuMs + output.RenderCpuMs +
                output.SubmitCpuMs + output.OverlayCpuMs + output.PresentCpuMs;
            rows.Add(new($"Output {index + 1}: {output.Name}", output.GpuMs.ToString("F3", CultureInfo.InvariantCulture),
                $"{output.OutputKind}/{output.ViewKind} | {output.WorkDisposition} | CPU {cpuMs:F3} ms | target/actual {output.ConfiguredTargetRateHz:F1}/{output.AchievedRateHz:F1} Hz | {output.PipelineName}"));
            rows.Add(new("  Identity", request.OutputId.ToString(CultureInfo.InvariantCulture),
                $"frame {output.FrameId}/{request.FrameId} | family {request.ViewFamilyId} | pipeline {output.PipelineInstanceId} | plan {output.ResourcePlanGeneration} | commands gen {output.CommandGeneration} | AA {output.AntiAliasingMode}"));
            rows.Add(new("  State", output.Rendered ? "Rendered" : output.Skipped ? "Skipped" : "Held",
                $"active {output.Active} | scene {output.SceneRendered}/{output.RenderPhaseSceneRendered} | mirror {output.Mirror} | separate {output.SeparateSceneRender} | shared visibility {output.SharedVisibility} | due {output.Due} | cadence/auto skip {output.CadenceSkipped}/{output.AutoSkipped} | {output.SkipReason}"));
            rows.Add(new("  Result", output.ContentAgeFrames.ToString(CultureInfo.InvariantCulture),
                $"age frames | deadline missed {output.DeadlineMissed} | policy {output.PolicyAuthorized}/{output.PolicyReason} | submitted {output.SubmitObserved} | presented {output.PresentObserved} | renders/skips {output.TotalRenderCount}/{output.TotalSkipCount} | source {output.SourceRateHz:F1} Hz"));
            rows.Add(new("  Target", target.TargetClass.ToString(),
                $"ID/gen {target.StableTargetId}/{target.TargetGeneration} | display {target.DisplayWidth}x{target.DisplayHeight} | internal {target.InternalWidth}x{target.InternalHeight} | format/compat {target.FormatCompatibilityKey}/{target.CompatibilityKey} | samples {target.SampleCount} | mask {target.ViewMask} | image {target.ExternalImageSlot}"));
            rows.Add(new("  Request", request.OutputClass.ToString(),
                $"priority {schedule.Priority} | rate {schedule.DesiredRateHz:F1} Hz | deadline {schedule.DeadlineMs:F2} ms/hard {schedule.HardDeadline} | CPU/GPU budget {schedule.MaxCpuBudgetMs:F2}/{schedule.MaxGpuBudgetMs:F2} ms | max age {schedule.MaxContentAgeFrames}"));
            rows.Add(new("  Policy", request.QualityRequirements.ToString(),
                $"fallback {request.FallbackPolicy} | completion {request.CompletionRequirement} | readiness {request.ReadinessPolicy} | work {request.WorkClass} | aspect {request.ExpectedWriteAspect} | dependencies {request.ProducerDependencySetId}/{request.ConsumerDependencySetId} | product {request.ProductCompatibilityKey}"));
            rows.Add(new("  Work", output.CommandCount.ToString(CultureInfo.InvariantCulture),
                $"commands | draws/multi/triangles {output.DrawCalls}/{output.MultiDrawCalls}/{output.Triangles} | output/collect/swap/render/submit/overlay/present events {output.OutputEventCount}/{output.CollectEventCount}/{output.SwapEventCount}/{output.RenderEventCount}/{output.SubmitEventCount}/{output.OverlayEventCount}/{output.PresentEventCount}"));
            rows.Add(new("  Timing", cpuMs.ToString("F3", CultureInfo.InvariantCulture),
                $"CPU collect/swap/render/submit/overlay/present {output.CollectCpuMs:F3}/{output.SwapCpuMs:F3}/{output.RenderCpuMs:F3}/{output.SubmitCpuMs:F3}/{output.OverlayCpuMs:F3}/{output.PresentCpuMs:F3} ms | GPU {output.GpuMs:F3} ms"));
        }
    }

    private static void AddCount(List<NativeProfilerDisplayRow> rows, string name, long count)
        => rows.Add(new(name, count.ToString(CultureInfo.InvariantCulture), "Last completed frame"));
}

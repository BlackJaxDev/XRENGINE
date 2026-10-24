using System.Globalization;
using AssetStats = XREngine.RuntimeEngine.Rendering.Stats.SceneAssets;
using FrameOutputStats = XREngine.RuntimeEngine.Rendering.Stats.FrameOutputs;
using LifecycleStats = XREngine.RuntimeEngine.Rendering.Stats.FrameLifecycle;
using RendererStats = XREngine.RuntimeEngine.Rendering.Stats.RendererState;

namespace XREngine.Editor;

public partial class ProfilerPanel
{
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

    private static void CaptureFrameOutputs(List<NativeProfilerDisplayRow> rows)
    {
        var manifest = FrameOutputStats.LastManifest;
        rows.Add(new("Frame", manifest.FrameId.ToString(CultureInfo.InvariantCulture),
            $"{manifest.MirrorMode} | {manifest.VisibilityPolicy} | {manifest.BudgetBand}"));
        rows.Add(new("Whole frame", manifest.WholeFrameMs.ToString("F3", CultureInfo.InvariantCulture),
            $"budget {manifest.BudgetMs.ToString("F2", CultureInfo.InvariantCulture)} ms | p95 {manifest.WholeFrameP95Ms.ToString("F3", CultureInfo.InvariantCulture)} ms"));
        var work = manifest.Work;
        rows.Add(new("Work items", work.RecordedWorkItemCount.ToString(CultureInfo.InvariantCulture),
            $"reused {work.ReusedWorkItemCount} | duplicated {work.DuplicatedWorkItemCount}"));
        rows.Add(new("Plan cache", work.CompiledPlanCacheHits.ToString(CultureInfo.InvariantCulture),
            $"misses {work.CompiledPlanCacheMisses} | physical hits {work.PhysicalPlanCacheHits}"));
        AddCount(rows, "Missed deadlines", work.MissedDeadlineCount);
        AddCount(rows, "Submission rejected", work.SubmissionRejectionCount);
        int count = Math.Min(manifest.Outputs.Length, 64);
        for (int index = 0; index < count; index++)
        {
            var output = manifest.Outputs[index];
            rows.Add(new(output.Name, output.GpuMs.ToString("F3", CultureInfo.InvariantCulture),
                $"{output.OutputKind} | {output.ViewKind} | {output.WorkDisposition} | CPU {(output.CollectCpuMs + output.SwapCpuMs + output.RenderCpuMs + output.SubmitCpuMs + output.OverlayCpuMs + output.PresentCpuMs).ToString("F3", CultureInfo.InvariantCulture)} ms"));
        }
        if (manifest.Outputs.Length > count)
            rows.Add(new("Additional outputs", (manifest.Outputs.Length - count).ToString(CultureInfo.InvariantCulture),
                "This view shows the first 64 outputs."));
    }

    private static void AddCount(List<NativeProfilerDisplayRow> rows, string name, long count)
        => rows.Add(new(name, count.ToString(CultureInfo.InvariantCulture), "Last completed frame"));
}

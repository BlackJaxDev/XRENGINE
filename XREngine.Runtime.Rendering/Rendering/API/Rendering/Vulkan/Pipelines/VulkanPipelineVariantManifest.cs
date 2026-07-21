using System.Collections.ObjectModel;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int MaxCachedPipelineVariantManifests = 64;
    private readonly Dictionary<VulkanPipelineManifestCacheKey, VulkanPipelineVariantManifest> _pipelineVariantManifestCache = new();
    private readonly Queue<VulkanPipelineManifestCacheKey> _pipelineVariantManifestInsertionOrder = new();
    private readonly Lock _pipelineVariantManifestCacheLock = new();

    private readonly record struct VulkanPipelineManifestCacheKey(
        ulong PlanCompatibilityIdentity,
        ulong RecordingStructuralSignature,
        EMeshSubmissionStrategy SubmissionStrategy,
        bool DynamicRendering);

    /// <summary>
    /// Immutable pipeline-variant demand derived from a compiled graph plan and its
    /// prepared frame operations. External image handles and frame-local matrices are excluded.
    /// </summary>
    internal sealed class VulkanPipelineVariantManifest
    {
        private readonly ReadOnlyCollection<VulkanPipelineVariantRequirement> _requirements;
        private int _warmupCompleted;

        private VulkanPipelineVariantManifest(
            ulong compatibilityIdentity,
            VulkanPipelineVariantRequirement[] requirements)
        {
            CompatibilityIdentity = compatibilityIdentity;
            _requirements = Array.AsReadOnly(requirements);
        }

        public ulong CompatibilityIdentity { get; }
        public ReadOnlyCollection<VulkanPipelineVariantRequirement> Requirements => _requirements;
        public bool WarmupCompleted => Volatile.Read(ref _warmupCompleted) != 0;

        public void MarkWarmupCompleted() => Volatile.Write(ref _warmupCompleted, 1);

        internal static VulkanPipelineVariantManifest Build(
            VulkanRenderGraphPlan plan,
            FrameOp[] ops,
            EMeshSubmissionStrategy submissionStrategy,
            bool dynamicRendering,
            ulong recordingStructuralSignature)
        {
            int requirementCount = 0;
            for (int opIndex = 0; opIndex < ops.Length; opIndex++)
            {
                if (ops[opIndex] is MeshDrawOp or IndirectDrawOp)
                    requirementCount++;
            }

            var requirements = new VulkanPipelineVariantRequirement[requirementCount];
            var hash = new VulkanStableHash64(1u);
            hash.Add(plan.CompatibilityIdentity);
            hash.Add(recordingStructuralSignature);
            hash.Add((int)submissionStrategy);
            hash.Add(dynamicRendering);

            int requirementIndex = 0;
            for (int opIndex = 0; opIndex < ops.Length; opIndex++)
            {
                if (ops[opIndex] is not MeshDrawOp && ops[opIndex] is not IndirectDrawOp)
                    continue;

                PendingMeshDraw draw = ops[opIndex] switch
                {
                    MeshDrawOp direct => direct.Draw,
                    IndirectDrawOp indirect => indirect.Draw,
                    _ => default,
                };
                int passIndex = ops[opIndex].PassIndex;
                RenderGraphPlanPass? planPass = FindPass(plan.Passes, passIndex);
                string passName = planPass?.Name ?? $"Pass{passIndex}";
                bool shadow = passName.Contains("Shadow", StringComparison.OrdinalIgnoreCase);
                bool velocity = passName.Contains("Velocity", StringComparison.OrdinalIgnoreCase) ||
                    passName.Contains("Motion", StringComparison.OrdinalIgnoreCase);
                bool editor = passName.Contains("Editor", StringComparison.OrdinalIgnoreCase) ||
                    passName.Contains("Picking", StringComparison.OrdinalIgnoreCase) ||
                    passName.Contains("TransformId", StringComparison.OrdinalIgnoreCase);
                bool materialOverride = draw.MaterialOverride is not null;
                bool stereo = draw.IsStereoPass || ops[opIndex].Context.StereoEnabled;
                bool multiview = ops[opIndex].Context.MultiviewEnabled;

                requirements[requirementIndex++] = new VulkanPipelineVariantRequirement(
                    opIndex,
                    passIndex,
                    passName,
                    Required: planPass?.RequiresPipelineReady ?? true,
                    submissionStrategy,
                    shadow,
                    velocity,
                    editor,
                    materialOverride,
                    stereo,
                    multiview,
                    dynamicRendering,
                    LegacyRenderPass: !dynamicRendering);

                hash.Add(opIndex);
                hash.Add(passIndex);
                hash.Add(draw.PreparedProgramIdentity);
                hash.Add(planPass?.RequiresPipelineReady ?? true);
                hash.Add(materialOverride);
                hash.Add(stereo);
                hash.Add(multiview);
            }

            return new VulkanPipelineVariantManifest(hash.Value, requirements);
        }

        private static RenderGraphPlanPass? FindPass(ReadOnlyCollection<RenderGraphPlanPass> passes, int passIndex)
        {
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i].PassIndex == passIndex)
                    return passes[i];
            }

            return null;
        }
    }

    private VulkanPipelineVariantManifest GetOrBuildPipelineVariantManifest(
        VulkanRenderGraphPlan plan,
        FrameOp[] ops,
        EMeshSubmissionStrategy submissionStrategy,
        bool dynamicRendering,
        ulong recordingStructuralSignature)
    {
        var key = new VulkanPipelineManifestCacheKey(
            plan.CompatibilityIdentity,
            recordingStructuralSignature,
            submissionStrategy,
            dynamicRendering);
        lock (_pipelineVariantManifestCacheLock)
        {
            if (_pipelineVariantManifestCache.TryGetValue(key, out VulkanPipelineVariantManifest? manifest))
                return manifest;

            manifest = VulkanPipelineVariantManifest.Build(
                plan,
                ops,
                submissionStrategy,
                dynamicRendering,
                recordingStructuralSignature);
            while (_pipelineVariantManifestCache.Count >= MaxCachedPipelineVariantManifests &&
                   _pipelineVariantManifestInsertionOrder.TryDequeue(out VulkanPipelineManifestCacheKey evictedKey))
            {
                _pipelineVariantManifestCache.Remove(evictedKey);
            }

            _pipelineVariantManifestCache.Add(key, manifest);
            _pipelineVariantManifestInsertionOrder.Enqueue(key);
            return manifest;
        }
    }

    internal readonly record struct VulkanPipelineVariantRequirement(
        int OpIndex,
        int PassIndex,
        string PassName,
        bool Required,
        EMeshSubmissionStrategy SubmissionStrategy,
        bool Shadow,
        bool Velocity,
        bool EditorId,
        bool MaterialOverride,
        bool Stereo,
        bool Multiview,
        bool DynamicRendering,
        bool LegacyRenderPass);
}

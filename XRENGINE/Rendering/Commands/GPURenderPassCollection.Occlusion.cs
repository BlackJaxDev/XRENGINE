using XREngine.Rendering;
using static XREngine.Rendering.GpuDispatchLogger;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        private EOcclusionCullingMode _lastLoggedOcclusionMode = (EOcclusionCullingMode)(-1);
        private bool _loggedGpuHiZOcclusionScaffold;
        private bool _loggedCpuQueryAsyncScaffold;

        private uint _occlusionCandidatesTested;
        private uint _occlusionAccepted;
        private uint _occlusionFalsePositiveRecoveries;
        private uint _occlusionTemporalOverrides;

        public EOcclusionCullingMode ActiveOcclusionMode => ResolveActiveOcclusionMode();
        public uint OcclusionCandidatesTested => _occlusionCandidatesTested;
        public uint OcclusionAccepted => _occlusionAccepted;
        public uint OcclusionFalsePositiveRecoveries => _occlusionFalsePositiveRecoveries;
        public uint OcclusionTemporalOverrides => _occlusionTemporalOverrides;

        private void ResetOcclusionFrameStats()
        {
            _occlusionCandidatesTested = 0u;
            _occlusionAccepted = 0u;
            _occlusionFalsePositiveRecoveries = 0u;
            _occlusionTemporalOverrides = 0u;
        }

        private void RecordOcclusionFrameStats(
            uint candidatesTested,
            uint occludedAccepted,
            uint falsePositiveRecoveries,
            uint temporalOverrides)
        {
            _occlusionCandidatesTested = candidatesTested;
            _occlusionAccepted = occludedAccepted;
            _occlusionFalsePositiveRecoveries = falsePositiveRecoveries;
            _occlusionTemporalOverrides = temporalOverrides;
        }

        private EOcclusionCullingMode ResolveActiveOcclusionMode()
        {
            // Passthrough mode is a debug-only escape hatch; keep it behaviorally stable.
            if (ForcePassthroughCulling)
                return EOcclusionCullingMode.Disabled;

            return Engine.EffectiveSettings.GpuOcclusionCullingMode;
        }

        private void ApplyOcclusionCulling(GPUScene scene, XRCamera? camera)
        {
            uint visibleBeforeOcclusion = VisibleCommandCount;
            if (visibleBeforeOcclusion == 0u)
                return;

            EOcclusionCullingMode mode = ResolveActiveOcclusionMode();
            LogOcclusionModeActivation(mode);

            if (mode == EOcclusionCullingMode.Disabled)
                return;

            switch (mode)
            {
                case EOcclusionCullingMode.GpuHiZ:
                    ApplyGpuHiZOcclusionScaffold(scene, camera, visibleBeforeOcclusion);
                    break;

                case EOcclusionCullingMode.CpuQueryAsync:
                    ApplyCpuQueryAsyncOcclusionScaffold(scene, camera, visibleBeforeOcclusion);
                    break;
            }
        }

        private void LogOcclusionModeActivation(EOcclusionCullingMode mode)
        {
            if (_lastLoggedOcclusionMode == mode)
                return;

            bool isFirstObservation = _lastLoggedOcclusionMode == (EOcclusionCullingMode)(-1);
            _lastLoggedOcclusionMode = mode;

            if (isFirstObservation && mode == EOcclusionCullingMode.Disabled)
                return;

            Log(LogCategory.Culling, LogLevel.Info, "Occlusion mode active: {0} (pass={1})", mode, RenderPass);
        }

        private void ApplyGpuHiZOcclusionScaffold(GPUScene scene, XRCamera? camera, uint candidates)
        {
            _ = scene;
            _ = camera;

            // Phase 3 starter: matrix + runtime switch + instrumentation.
            // Keep all commands visible until Hi-Z prepass and refinement are wired into the active flow.
            RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);

            if (!_loggedGpuHiZOcclusionScaffold)
            {
                _loggedGpuHiZOcclusionScaffold = true;
                Log(LogCategory.Culling, LogLevel.Warning,
                    "Occlusion mode {0} is in scaffold state for pass {1}; keeping {2} candidates visible.",
                    EOcclusionCullingMode.GpuHiZ,
                    RenderPass,
                    candidates);
            }
        }

        private void ApplyCpuQueryAsyncOcclusionScaffold(GPUScene scene, XRCamera? camera, uint candidates)
        {
            _ = scene;
            _ = camera;

            // Phase 3 starter: reserve async query mode without introducing same-frame query stalls.
            // Default visible behavior is preserved until query submission/resolve is fully integrated.
            RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);

            if (!_loggedCpuQueryAsyncScaffold)
            {
                _loggedCpuQueryAsyncScaffold = true;
                Log(LogCategory.Culling, LogLevel.Warning,
                    "Occlusion mode {0} is in scaffold state for pass {1}; keeping {2} candidates visible.",
                    EOcclusionCullingMode.CpuQueryAsync,
                    RenderPass,
                    candidates);
            }
        }
    }
}

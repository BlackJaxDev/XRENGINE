namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// Conservative, opt-in admission policy for CPU software occlusion. It only admits
    /// non-forced frames when real CPU draw-submission timing has been supplied; no
    /// synthetic savings threshold is used.
    /// </summary>
    internal sealed class CpuSoftwareOcclusionProfitabilityPolicy
    {
        // Safety/learning cadence only. This is not a profitability crossover value.
        private const ulong ProbeIntervalFrames = 120;

        private bool _hasCompletedProbe;
        private bool _hasMeasuredSubmissionCost;
        private ulong _lastProbeFrame;
        private double _millisecondsPerDraw;
        private double _sampledSubmissionMilliseconds;
        private int _sampledSubmissionDrawCount;
        private int _lastProbeCulled;
        private double _lastProbeCostMilliseconds;

        public void RecordMeasuredSubmissionCost(double milliseconds, int drawCount)
        {
            if (!double.IsFinite(milliseconds) || milliseconds < 0.0 || drawCount <= 0)
                return;

            _sampledSubmissionMilliseconds += milliseconds;
            _sampledSubmissionDrawCount += drawCount;
        }

        public void RecordCompletedProbe(int culledDrawCount, double measuredSocCostMilliseconds)
        {
            if (culledDrawCount < 0 || !double.IsFinite(measuredSocCostMilliseconds) || measuredSocCostMilliseconds < 0.0)
                return;

            _hasCompletedProbe = true;
            _lastProbeCulled = culledDrawCount;
            _lastProbeCostMilliseconds = measuredSocCostMilliseconds;
        }

        public CpuSoftwareOcclusionProfitabilityAdmission Decide(ulong frameId, bool forced, bool debugBypass)
        {
            ConsumeMeasuredSubmissionSamples();
            if (debugBypass)
                return new(ECpuSoftwareOcclusionProfitabilityDecision.DebugBypass, runSoc: true);
            if (forced)
                return new(ECpuSoftwareOcclusionProfitabilityDecision.Forced, runSoc: true);

            bool dueProbe = !_hasCompletedProbe || frameId - _lastProbeFrame >= ProbeIntervalFrames;
            if (!_hasCompletedProbe)
            {
                _lastProbeFrame = frameId;
                return new(ECpuSoftwareOcclusionProfitabilityDecision.Cold, runSoc: true);
            }

            if (!_hasMeasuredSubmissionCost)
            {
                if (dueProbe)
                {
                    _lastProbeFrame = frameId;
                    return new(ECpuSoftwareOcclusionProfitabilityDecision.Probing, runSoc: true);
                }

                return new(ECpuSoftwareOcclusionProfitabilityDecision.Unmeasured, runSoc: false);
            }

            double estimatedSavedMilliseconds = _lastProbeCulled * _millisecondsPerDraw;
            if (estimatedSavedMilliseconds > _lastProbeCostMilliseconds)
                return new(ECpuSoftwareOcclusionProfitabilityDecision.Profitable, runSoc: true);

            if (dueProbe)
            {
                _lastProbeFrame = frameId;
                return new(ECpuSoftwareOcclusionProfitabilityDecision.Probing, runSoc: true);
            }

            return new(ECpuSoftwareOcclusionProfitabilityDecision.Unprofitable, runSoc: false);
        }

        private void ConsumeMeasuredSubmissionSamples()
        {
            if (_sampledSubmissionDrawCount <= 0)
                return;

            _millisecondsPerDraw = _sampledSubmissionMilliseconds / _sampledSubmissionDrawCount;
            _hasMeasuredSubmissionCost = double.IsFinite(_millisecondsPerDraw) && _millisecondsPerDraw > 0.0;
            _sampledSubmissionMilliseconds = 0.0;
            _sampledSubmissionDrawCount = 0;
        }
    }

}

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Rejects an authored attempt unless its caller takes the candidate.</summary>
internal readonly struct TemporalHistoryCaptureScope(XRRenderPipelineInstance instance, ulong serial) : IDisposable
{
    public bool HasTemporalAttemptOrBlocked
        => VPRC_TemporalAccumulationPass.HasStrictHistoryAttempt(instance, serial);

    public void Dispose() => VPRC_TemporalAccumulationPass.EndStrictSpsCapture(instance, serial);
}

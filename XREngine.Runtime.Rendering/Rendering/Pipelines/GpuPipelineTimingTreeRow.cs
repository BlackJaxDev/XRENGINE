namespace XREngine.Rendering;

/// <summary>Contains one displayed node from a retained worst GPU frame.</summary>
public readonly record struct GpuPipelineTimingTreeRow(
    string Name, int Depth, double Milliseconds, int TimerSampleCount);

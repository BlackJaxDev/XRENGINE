namespace XREngine.Execution;

/// <summary>
/// Allocation-free result of an asynchronous render-work admission request.
/// </summary>
public readonly record struct RenderWorkAdmissionResult(ERenderWorkAdmissionReason Reason)
{
    public static RenderWorkAdmissionResult Admitted => new(ERenderWorkAdmissionReason.Admitted);

    public bool IsAdmitted => Reason == ERenderWorkAdmissionReason.Admitted;
}

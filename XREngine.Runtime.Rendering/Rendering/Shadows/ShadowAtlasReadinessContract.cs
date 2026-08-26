namespace XREngine.Rendering.Shadows;

/// <summary>
/// The terminal-output policy that owns a shadow-atlas dependency. This is
/// captured before recording so shadow work cannot silently inherit a
/// background tile budget.
/// </summary>
public readonly record struct ShadowAtlasReadinessContract(
    ulong OutputId,
    ulong FrameId,
    ERenderOutputReadinessPolicy ReadinessPolicy,
    ERenderOutputWorkClass WorkClass)
{
    public bool RequiresExactCurrentContent
        => WorkClass == ERenderOutputWorkClass.PresentNow &&
           ReadinessPolicy == ERenderOutputReadinessPolicy.BlockForExact;

    public bool AllowsDeclaredResidentGpuFallback
        => WorkClass == ERenderOutputWorkClass.PresentNow &&
           ReadinessPolicy == ERenderOutputReadinessPolicy.MeetDeadlineWithGpuFallback;

    public static ShadowAtlasReadinessContract FromOutputRequest(in RenderOutputRequest request)
        => new(request.OutputId, request.FrameId, request.ReadinessPolicy, request.WorkClass);
}

namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>One physical barrier that a backend records before a pass.</summary>
/// <param name="PassIndex">The pass that the barrier precedes.</param>
/// <param name="ResourceName">The logical resource name, or the physical resource label when no name maps.</param>
/// <param name="Kind">The barrier kind, such as image, buffer, or swapchain.</param>
/// <param name="Range">The subresource range text.</param>
/// <param name="OldLayout">The image layout before the barrier, or an empty string for buffers.</param>
/// <param name="NewLayout">The image layout after the barrier, or an empty string for buffers.</param>
/// <param name="SourceStages">The source pipeline stages.</param>
/// <param name="DestinationStages">The destination pipeline stages.</param>
/// <param name="SourceAccess">The source access mask.</param>
/// <param name="DestinationAccess">The destination access mask.</param>
/// <param name="RawText">The native barrier text of the backend.</param>
public sealed record PlanBarrierSnapshot(
    int PassIndex,
    string ResourceName,
    string Kind,
    string Range,
    string OldLayout,
    string NewLayout,
    string SourceStages,
    string DestinationStages,
    string SourceAccess,
    string DestinationAccess,
    string RawText);

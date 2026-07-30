namespace XREngine.Rendering;

/// <summary>
/// Stable capture/debug names for all document-04 images and buffers.
/// </summary>
public static class AdvancedVisibilityResourceNames
{
    public const string Identity = "Advanced.Visibility.Identity";
    public const string Metadata = "Advanced.Visibility.Metadata";
    public const string Selection = "Advanced.Visibility.Selection";
    public const string Barycentrics = "Advanced.Visibility.Barycentrics";
    public const string Coverage = "Advanced.Visibility.Coverage";
    public const string DepthStencil = "Advanced.Visibility.DepthStencil";
    public const string CurrentDepthPyramid = "Advanced.Visibility.DepthPyramid.Current";
    public const string PreviousDepthPyramid = "Advanced.Visibility.DepthPyramid.Previous";
    public const string FrameBuffer = "Advanced.Visibility.FrameBuffer";
    public const string DebugOutput = "Advanced.Visibility.DebugOutput";
    public const string DebugFrameBuffer = "Advanced.Visibility.DebugFrameBuffer";
    public const string Candidates = "Advanced.Visibility.Candidates";
    public const string Payloads = "Advanced.Visibility.Payloads";
    public const string Producers = "Advanced.Visibility.Producers";
    public const string PersistentState = "Advanced.Visibility.PersistentState";
    public const string SourceArguments = "Advanced.Visibility.SourceArguments";
    public const string PayloadRangeIndices = "Advanced.Visibility.PayloadRangeIndices";
    public const string RangeArgumentOffsets = "Advanced.Visibility.RangeArgumentOffsets";

    public static string EarlyArguments(uint frameSlot)
        => $"Advanced.Visibility.EarlyArguments.Slot{frameSlot}";
    public static string LateArguments(uint frameSlot)
        => $"Advanced.Visibility.LateArguments.Slot{frameSlot}";
    public static string EarlyMeshTaskArguments(uint frameSlot)
        => $"Advanced.Visibility.EarlyMeshTaskArguments.Slot{frameSlot}";
    public static string LateMeshTaskArguments(uint frameSlot)
        => $"Advanced.Visibility.LateMeshTaskArguments.Slot{frameSlot}";
    public static string EarlyMeshPayloads(uint frameSlot)
        => $"Advanced.Visibility.EarlyMeshPayloads.Slot{frameSlot}";
    public static string LateMeshPayloads(uint frameSlot)
        => $"Advanced.Visibility.LateMeshPayloads.Slot{frameSlot}";
    public static string RangeCounts(uint frameSlot)
        => $"Advanced.Visibility.RangeCounts.Slot{frameSlot}";
    public static string DeferredCandidates(uint frameSlot)
        => $"Advanced.Visibility.DeferredCandidates.Slot{frameSlot}";
    public static string EarlyVisiblePayloads(uint frameSlot)
        => $"Advanced.Visibility.EarlyPayloads.Slot{frameSlot}";
    public static string LateVisiblePayloads(uint frameSlot)
        => $"Advanced.Visibility.LatePayloads.Slot{frameSlot}";
    public static string Counters(uint frameSlot)
        => $"Advanced.Visibility.Counters.Slot{frameSlot}";
}

namespace XREngine.Rendering;

/// <summary>
/// Stable resource names and sizing constants for the RVC shadelet-cache GPU passes
/// declared by the Advanced stage family when <see cref="RvcRenderPipeline"/> runs an
/// active shadelet-cache mode.
/// </summary>
/// <remarks>
/// Frame-slot names below <see cref="AdvancedFrameSlotContract.DefaultSlotCount"/> are
/// cached at startup so per-frame render-graph and recording lookups never allocate.
/// </remarks>
public static class RvcShadeletCacheResourceNames
{
    public const string PixelToShadelet = "RvcShadelet.PixelToShadelet";
    public const string BaseRecords = "RvcShadelet.Records";
    public const string BaseBinIndices = "RvcShadelet.BinIndices";
    public const string BaseShadedOutputs = "RvcShadelet.ShadedOutputs";
    public const string BaseDispatchAndCounters = "RvcShadelet.DispatchAndCounters";

    /// <summary>Maximum deduplicated shadelets stored per frame before indirect overflow repair handles remaining pixels.</summary>
    public const uint DefaultMaxShadelets = 2_097_152u;

    /// <summary>Byte stride of <c>RvcShadeletRecordGpu</c> (32 bytes).</summary>
    public const uint RecordByteStride = 32u;

    /// <summary>Byte stride of one bin index entry (4 bytes).</summary>
    public const uint BinIndexByteStride = sizeof(uint);

    /// <summary>Byte stride of packed <c>RvcShadeletShadedOutputGpu</c> (32 bytes).</summary>
    public const uint ShadedOutputByteStride = 32u;

    /// <summary>Byte offset of the Standard shadelet indirect dispatch command inside DispatchAndCounters.</summary>
    public const ulong StandardDispatchByteOffset = 0UL;

    /// <summary>Byte offset of the Uber shadelet indirect dispatch command inside DispatchAndCounters.</summary>
    public const ulong UberDispatchByteOffset = 16UL;

    /// <summary>Byte offset of the overflow direct-repair indirect dispatch command inside DispatchAndCounters.</summary>
    public const ulong OverflowDispatchByteOffset = 32UL;

    private static readonly string[] RecordSlotNames = CreateSlotNames(BaseRecords);
    private static readonly string[] BinIndexSlotNames = CreateSlotNames(BaseBinIndices);
    private static readonly string[] ShadedOutputSlotNames = CreateSlotNames(BaseShadedOutputs);
    private static readonly string[] DispatchAndCounterSlotNames = CreateSlotNames(BaseDispatchAndCounters);

    /// <summary>Gets the shadelet-records storage buffer name for a frame slot.</summary>
    public static string Records(uint slot)
        => slot < AdvancedFrameSlotContract.DefaultSlotCount ? RecordSlotNames[slot] : $"{BaseRecords}.Slot{slot}";

    /// <summary>Gets the Standard/Uber shadelet bin-indices storage buffer name for a frame slot.</summary>
    public static string BinIndices(uint slot)
        => slot < AdvancedFrameSlotContract.DefaultSlotCount ? BinIndexSlotNames[slot] : $"{BaseBinIndices}.Slot{slot}";

    /// <summary>Gets the packed shadelet shaded-output storage buffer name for a frame slot.</summary>
    public static string ShadedOutputs(uint slot)
        => slot < AdvancedFrameSlotContract.DefaultSlotCount ? ShadedOutputSlotNames[slot] : $"{BaseShadedOutputs}.Slot{slot}";

    /// <summary>Gets the indirect dispatch and counter buffer name for a frame slot.</summary>
    public static string DispatchAndCounters(uint slot)
        => slot < AdvancedFrameSlotContract.DefaultSlotCount ? DispatchAndCounterSlotNames[slot] : $"{BaseDispatchAndCounters}.Slot{slot}";

    private static string[] CreateSlotNames(string prefix)
    {
        string[] names = new string[AdvancedFrameSlotContract.DefaultSlotCount];
        for (uint slot = 0u; slot < names.Length; slot++)
            names[slot] = $"{prefix}.Slot{slot}";
        return names;
    }
}

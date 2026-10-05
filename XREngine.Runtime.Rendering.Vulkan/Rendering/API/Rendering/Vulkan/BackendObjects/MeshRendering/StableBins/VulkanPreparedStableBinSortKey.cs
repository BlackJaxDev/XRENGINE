namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compact ordering key for freezing a stable-bin stream. It carries the
/// record fields the frozen order depends on plus the record's appended
/// position as the final tiebreaker, so sorting keys reproduces the exact
/// order of a stable insertion sort over the full records while each record
/// itself moves at most once.
/// </summary>
internal readonly record struct VulkanPreparedStableBinSortKey(
    ulong PassCompatibility,
    ulong PipelineVariant,
    ulong GeometryPage,
    uint ViewMask,
    uint TemplatePrimaryIndex,
    int IngressIndex,
    int SourceIndex) : IComparable<VulkanPreparedStableBinSortKey>
{
    internal static VulkanPreparedStableBinSortKey From(
        in VulkanPreparedStableBinRecord record,
        int sourceIndex)
        => new(
            record.Key.PassCompatibility,
            record.Key.PipelineVariant,
            record.Key.GeometryPage,
            record.Key.ViewMask,
            record.Template.PrimaryIndex,
            record.IngressIndex,
            sourceIndex);

    public int CompareTo(VulkanPreparedStableBinSortKey other)
    {
        int result = PassCompatibility.CompareTo(other.PassCompatibility);
        if (result != 0) return result;
        result = PipelineVariant.CompareTo(other.PipelineVariant);
        if (result != 0) return result;
        result = GeometryPage.CompareTo(other.GeometryPage);
        if (result != 0) return result;
        result = ViewMask.CompareTo(other.ViewMask);
        if (result != 0) return result;
        result = TemplatePrimaryIndex.CompareTo(other.TemplatePrimaryIndex);
        if (result != 0) return result;
        result = IngressIndex.CompareTo(other.IngressIndex);
        return result != 0 ? result : SourceIndex.CompareTo(other.SourceIndex);
    }
}

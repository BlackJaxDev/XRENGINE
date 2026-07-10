namespace XREngine;

public readonly record struct RvcMaterialBindingContract(
    ERvcDescriptorBackend SelectedBackend,
    bool DescriptorHeapPreferred,
    bool DescriptorIndexingRowsSemanticallyIdentical,
    bool ShadeletKeysExcludeBackendDescriptorHandles)
{
    public static RvcMaterialBindingContract FromResolution(in RvcPipelineResolution resolution)
        => new(
            resolution.DescriptorBackend,
            DescriptorHeapPreferred: true,
            DescriptorIndexingRowsSemanticallyIdentical: true,
            ShadeletKeysExcludeBackendDescriptorHandles: true);
}

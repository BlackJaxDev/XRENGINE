namespace XREngine.Rendering.Commands;

/// <summary>Independent resident-publication invalidation domain.</summary>
public enum EAdvancedGpuMutationDomain : byte
{
    Content,
    ResourceBinding,
    LayoutTopology,
    RecordingTopology,
}

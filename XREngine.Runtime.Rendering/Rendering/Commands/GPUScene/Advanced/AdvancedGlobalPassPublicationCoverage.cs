namespace XREngine.Rendering.Commands;

/// <summary>
/// Explicit immutable publication coverage for one canonical pass. Consumers
/// validate the pass index and publication sequence instead of scanning mutable
/// global-resource tables after a mutation.
/// </summary>
public readonly record struct AdvancedGlobalPassPublicationCoverage(
    int PassIndex,
    ulong Sequence,
    AdvancedGpuOwnerGenerations ShadowGenerations,
    AdvancedGpuOwnerGenerations ProbeGenerations,
    AdvancedGpuDirtyRange ShadowDirtyRange,
    AdvancedGpuDirtyRange ProbeDirtyRange,
    bool UsesShadows,
    bool UsesProbes)
{
    public bool CoversShadows => UsesShadows && Sequence != 0u;
    public bool CoversProbes => UsesProbes && Sequence != 0u;

    /// <summary>
    /// Dependency contribution for the owners actually consumed by this pass.
    /// The sequence and dirty ranges remain validation metadata; only the used
    /// owner generations invalidate the pass dependency signature.
    /// </summary>
    public ulong UsedOwnerGenerationSignature
    {
        get
        {
            ulong hash = 14695981039346656037UL;
            if (CoversShadows)
                AddGenerations(ref hash, ShadowGenerations);
            if (CoversProbes)
                AddGenerations(ref hash, ProbeGenerations);
            return hash;
        }
    }

    internal static AdvancedGlobalPassPublicationCoverage Capture(
        ulong sequence,
        AdvancedGlobalResourceDatabase resources)
        => new(
            -1,
            sequence,
            resources.Shadows.Generations,
            resources.Probes.Generations,
            resources.Shadows.DirtyRange,
            resources.Probes.DirtyRange,
            UsesShadows: false,
            UsesProbes: false);

    internal AdvancedGlobalPassPublicationCoverage ForPass(
        int passIndex,
        bool usesShadows,
        bool usesProbes)
        => this with
        {
            PassIndex = passIndex,
            UsesShadows = usesShadows,
            UsesProbes = usesProbes,
        };

    private static void AddGenerations(
        ref ulong hash,
        in AdvancedGpuOwnerGenerations generations)
    {
        Add(ref hash, generations.Topology);
        Add(ref hash, generations.Content);
        Add(ref hash, generations.Lookup);
    }

    private static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }
}

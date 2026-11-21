using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// An explicit pass dependency that an authored pipeline stores as data. The render graph orders
/// <see cref="PassIndex"/> after <see cref="DependsOnPassIndex"/> even when no resource edge connects them.
/// </summary>
public sealed class AuthoredPassDependency
{
    /// <summary>Gets or sets the pass that must run later.</summary>
    public int PassIndex { get; set; }

    /// <summary>Gets or sets the pass that must run first.</summary>
    public int DependsOnPassIndex { get; set; }

    /// <summary>Returns a copy of this dependency.</summary>
    public AuthoredPassDependency Clone()
        => new() { PassIndex = PassIndex, DependsOnPassIndex = DependsOnPassIndex };

    /// <summary>
    /// Adds each dependency to <paramref name="metadata"/>. A dependency of a pass that no command described is
    /// ignored, because the validator reports it.
    /// </summary>
    public static void Apply(RenderPassMetadataCollection metadata, IReadOnlyList<AuthoredPassDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(dependencies);
        for (int i = 0; i < dependencies.Count; i++)
            metadata.TryAddDependency(dependencies[i].PassIndex, dependencies[i].DependsOnPassIndex);
    }
}

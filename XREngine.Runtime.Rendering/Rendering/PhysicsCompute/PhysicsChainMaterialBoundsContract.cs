namespace XREngine.Rendering.Compute;

/// <summary>
/// Describes how declared post-skinning material vertex effects change one
/// GPU-owned physics-chain bound. A supported contract adds a world-space
/// padding after the palette-box reduction. An unsupported contract rejects
/// the bound before publication; it never selects CPU bounds.
/// </summary>
/// <param name="Padding">Largest per-axis displacement, in skinned (world) space units.</param>
/// <param name="RejectionReason">Constant reason text, or null when the contract is supported.</param>
public readonly record struct PhysicsChainMaterialBoundsContract(float Padding, string? RejectionReason)
{
    /// <summary>True when the padded palette box contains every displaced vertex.</summary>
    public bool IsSupported => RejectionReason is null;

    /// <summary>
    /// Combines the contracts of two materials that draw the same bound. The
    /// bound must contain both draws, so the larger padding applies. The first
    /// rejection wins.
    /// </summary>
    public static PhysicsChainMaterialBoundsContract Combine(
        in PhysicsChainMaterialBoundsContract first,
        in PhysicsChainMaterialBoundsContract second)
    {
        if (!first.IsSupported)
            return first;
        if (!second.IsSupported)
            return second;
        return new(MathF.Max(first.Padding, second.Padding), null);
    }
}

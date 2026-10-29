namespace XREngine.Rendering.UI;

/// <summary>
/// Declares the Advanced late-pass lane of UI materials that draw directly in the world.
/// </summary>
/// <remarks>
/// The Advanced pipeline admits a draw to a late pass only when its material has explicit late-pass metadata.
/// UI materials keep the opaque transparency mode that batch lowering requires, and blend through their raster state.
/// The <see cref="EAdvancedLatePassKind.UserInterface"/> kind admits them to the transparent and on-top lanes.
/// </remarks>
internal static class UIAdvancedLatePass
{
    /// <summary>Gets the shared metadata of a direct-world UI material. The record is immutable.</summary>
    internal static AdvancedLatePassMetadata Metadata { get; } =
        new(EAdvancedLatePassKind.UserInterface, isOrderDependent: true);

    /// <summary>
    /// Gives the material the UI late-pass metadata when it has no metadata.
    /// Authored metadata stays unchanged.
    /// </summary>
    internal static void ClassifyIfUnset(XRMaterial? material)
    {
        if (material is { AdvancedLatePassMetadata: null })
            material.AdvancedLatePassMetadata = Metadata;
    }
}

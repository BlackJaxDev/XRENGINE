using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Input;

/// <summary>
/// Holds the cooked content that a bake reads. An avatar or an item gives
/// <see cref="PrefabRoot"/>; a world gives <see cref="World"/>.
/// </summary>
public sealed class CookedBakeInput
{
    /// <summary>Gets the root node of the prefab instance. It is null for a world.</summary>
    public SceneNode? PrefabRoot { get; init; }

    /// <summary>Gets the world. It is null for an avatar or an item.</summary>
    public XRWorld? World { get; init; }

    /// <summary>Gets the type of content in this input.</summary>
    public required GaussianBakeKind Kind { get; init; }

    /// <summary>Gets the SHA-256 hash of the input <c>.pak</c> file, as 64 lowercase hexadecimal characters.</summary>
    public required string InputSha256 { get; init; }

    /// <summary>Gets the path of the asset in the archive, with forward slashes.</summary>
    public required string AssetPath { get; init; }
}

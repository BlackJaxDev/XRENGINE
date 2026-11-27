using XREngine.Components.Scene.Mesh;

namespace XREngine.Scene.Importers;

/// <summary>
/// Describes the avatar face mesh selected for blendshape path remapping and coverage reporting.
/// </summary>
public sealed class VrcftFaceMeshResolution
{
    /// <summary>
    /// Gets the resolved node path relative to the avatar root, using forward slashes.
    /// An empty string targets the avatar root itself; <see langword="null"/> means no target was found.
    /// </summary>
    public string? RelativeNodePath { get; init; }

    /// <summary>Gets the resolved scene node when present in the avatar hierarchy.</summary>
    public SceneNode? TargetNode { get; init; }

    /// <summary>Gets the resolved <see cref="ModelComponent"/> when present on the target node.</summary>
    public ModelComponent? ModelComponent { get; init; }

    /// <summary>Gets the priority rule that produced this resolution.</summary>
    public EVrcftFaceMeshResolutionSource Source { get; init; }

    /// <summary>Gets the number of known face-tracking blendshape signatures matched on the model.</summary>
    public int MatchedSignatureCount { get; init; }

    /// <summary>Gets all distinct blendshape names exposed by the resolved model.</summary>
    public required IReadOnlySet<string> BlendshapeNames { get; init; }
}

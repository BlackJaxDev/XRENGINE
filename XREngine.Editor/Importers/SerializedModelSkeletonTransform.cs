using System.Numerics;

namespace XREngine.Scene.Importers;

/// <summary>
/// Describes one transform of the avatar T-pose stored in Unity's <c>humanDescription.skeleton</c>.
/// </summary>
public sealed class SerializedModelSkeletonTransform
{
    public string Name { get; init; } = string.Empty;
    public string ParentName { get; init; } = string.Empty;
    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Scale { get; init; } = Vector3.One;
}

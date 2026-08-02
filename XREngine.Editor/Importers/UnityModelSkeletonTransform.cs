using System.Numerics;

namespace XREngine.Scene.Importers;

/// <summary>
/// Describes one transform from Unity ModelImporter's authoritative imported skeleton pose.
/// </summary>
public sealed class UnityModelSkeletonTransform
{
    public string Name { get; init; } = string.Empty;
    public string ParentName { get; init; } = string.Empty;
    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Scale { get; init; } = Vector3.One;
}

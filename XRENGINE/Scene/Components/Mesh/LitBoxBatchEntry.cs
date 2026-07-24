using System.Numerics;

namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Serialized description of one box in a <see cref="LitBoxBatchComponent"/>.
/// The scene-node ID is resolved back to the restored hierarchy after a Play snapshot clone.
/// </summary>
[Serializable, CookedBinaryReflectionOnly]
public sealed class LitBoxBatchEntry
{
    public Guid SceneNodeId { get; set; }
    public Vector3 HalfExtents { get; set; }
    public Vector4 Color { get; set; }

    public LitBoxBatchEntry()
    {
    }

    public LitBoxBatchEntry(Guid sceneNodeId, Vector3 halfExtents, Vector4 color)
    {
        SceneNodeId = sceneNodeId;
        HalfExtents = halfExtents;
        Color = color;
    }
}

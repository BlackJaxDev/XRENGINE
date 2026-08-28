using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Eye-look references and authored rotations retained from a Unity avatar descriptor.
/// </summary>
[Serializable]
public sealed class AvatarGazeBinding
{
    public bool Enabled { get; set; }
    public TransformBase? LeftEye { get; set; }
    public TransformBase? RightEye { get; set; }
    public Quaternion LeftStraight { get; set; } = Quaternion.Identity;
    public Quaternion RightStraight { get; set; } = Quaternion.Identity;
    public Quaternion LeftUp { get; set; } = Quaternion.Identity;
    public Quaternion RightUp { get; set; } = Quaternion.Identity;
    public Quaternion LeftDown { get; set; } = Quaternion.Identity;
    public Quaternion RightDown { get; set; } = Quaternion.Identity;
    public Quaternion LeftLookLeft { get; set; } = Quaternion.Identity;
    public Quaternion RightLookLeft { get; set; } = Quaternion.Identity;
    public Quaternion LeftLookRight { get; set; } = Quaternion.Identity;
    public Quaternion RightLookRight { get; set; } = Quaternion.Identity;
    public int EyelidType { get; set; }
    public ModelComponent? EyelidRenderer { get; set; }
    public List<int> EyelidBlendShapeIndices { get; set; } = [];
}

using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>Immutable role-indexed orientation landmark used by a compiled body definition.</summary>
internal readonly struct CompiledHumanoidBodyPoint
{
    public CompiledHumanoidBodyPoint(int roleIndex, Vector3 localPosition)
    {
        RoleIndex = roleIndex;
        LocalPosition = localPosition;
    }

    public int RoleIndex { get; }
    public Vector3 LocalPosition { get; }
}

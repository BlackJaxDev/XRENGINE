using System.Numerics;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Complete authored Body-relative foot-goal positions for one projection
/// sample. A missing goal is distinct from an authored zero position.
/// </summary>
public struct ImportedHumanoidProjectionFootGoals
{
    public Vector3 LeftPosition;
    public Vector3 RightPosition;
    private byte _leftComponentMask;
    private byte _rightComponentMask;

    public readonly bool HasLeft => _leftComponentMask == 0b111;
    public readonly bool HasRight => _rightComponentMask == 0b111;
    public readonly bool HasAny => HasLeft || HasRight;
    public readonly bool HasCompletePair => HasLeft && HasRight;

    public void Clear()
    {
        LeftPosition = Vector3.Zero;
        RightPosition = Vector3.Zero;
        _leftComponentMask = 0;
        _rightComponentMask = 0;
    }

    public void Set(ELimbEndEffector goal, Vector3 position)
    {
        if (!IsFinite(position))
            return;

        switch (goal)
        {
            case ELimbEndEffector.LeftFoot:
                LeftPosition = position;
                _leftComponentMask = 0b111;
                break;
            case ELimbEndEffector.RightFoot:
                RightPosition = position;
                _rightComponentMask = 0b111;
                break;
        }
    }

    public void SetComponent(
        ELimbEndEffector goal,
        int componentIndex,
        float value)
    {
        if ((uint)componentIndex >= 3U || !float.IsFinite(value))
            return;

        bool left = goal == ELimbEndEffector.LeftFoot;
        if (!left && goal != ELimbEndEffector.RightFoot)
            return;

        Vector3 position = left ? LeftPosition : RightPosition;
        switch (componentIndex)
        {
            case 0:
                position.X = value;
                break;
            case 1:
                position.Y = value;
                break;
            case 2:
                position.Z = value;
                break;
        }

        if (left)
        {
            LeftPosition = position;
            _leftComponentMask |= (byte)(1 << componentIndex);
        }
        else
        {
            RightPosition = position;
            _rightComponentMask |= (byte)(1 << componentIndex);
        }
    }

    public void Mirror()
    {
        Vector3 left = LeftPosition;
        byte leftMask = _leftComponentMask;
        LeftPosition = ImportedHumanoidMirrorOperator.MirrorPosition(RightPosition);
        _leftComponentMask = _rightComponentMask;
        RightPosition = ImportedHumanoidMirrorOperator.MirrorPosition(left);
        _rightComponentMask = leftMask;
    }

    public void SwapSides()
    {
        (LeftPosition, RightPosition) = (RightPosition, LeftPosition);
        (_leftComponentMask, _rightComponentMask) = (_rightComponentMask, _leftComponentMask);
    }

    public void FlipZ()
    {
        LeftPosition.Z = -LeftPosition.Z;
        RightPosition.Z = -RightPosition.Z;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);
}

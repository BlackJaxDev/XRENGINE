using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Captured concrete hierarchy contract. Fixed helpers below Hips are not pose
/// inputs; changing one requires refreshing the definition, not stale scratch FK.
/// </summary>
internal readonly struct CompiledHumanoidHierarchyGuard(TransformBase transform, Matrix4x4? fixedLocal)
{
    private readonly TransformBase _transform = transform;
    private readonly TransformBase? _parent = transform.Parent;
    private readonly Matrix4x4? _fixedLocal = fixedLocal;

    public bool IsValid()
    {
        if (!ReferenceEquals(_transform.Parent, _parent))
            return false;
        if (_fixedLocal is not Matrix4x4 neutral)
            return true;
        if (_transform.IsLocalMatrixDirty)
            _transform.RecalcLocal();
        return HumanoidBodyFrameMath.ApproximatelyEqual(_transform.LocalMatrix, neutral);
    }
}

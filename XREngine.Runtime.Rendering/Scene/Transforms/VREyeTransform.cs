using System.Numerics;

namespace XREngine.Scene.Transforms;

public class VREyeTransform : TransformBase
{
    private bool _isLeftEye = true;

    public bool IsLeftEye
    {
        get => _isLeftEye;
        set => SetField(ref _isLeftEye, value);
    }

    public VREyeTransform()
    {
    }

    public VREyeTransform(TransformBase? parent)
        : base(parent)
    {
    }

    public VREyeTransform(bool isLeftEye, TransformBase? parent = null)
        : this(parent)
    {
        IsLeftEye = isLeftEye;
    }

    protected override Matrix4x4 CreateLocalMatrix()
        => Matrix4x4.Identity;
}
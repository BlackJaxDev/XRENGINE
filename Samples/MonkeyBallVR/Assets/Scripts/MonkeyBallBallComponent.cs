using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Scene.Transforms;

namespace VR_MonkeyBall_Sample.Assets;

public sealed class MonkeyBallBallComponent : XRComponent
{
    public float SpinSpeedDegreesPerSecond { get; set; } = 180.0f;
    public float BobAmplitude { get; set; } = 0.07f;
    public float BobFrequencyHz { get; set; } = 1.15f;

    private Transform? _transform;
    private float _yawDegrees;
    private float _pitchDegrees;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _transform = TransformAs<Transform>(forceConvert: true);
        RegisterTick(ETickGroup.Normal, ETickOrder.Logic, Tick);
    }

    protected override void OnComponentDeactivated()
    {
        _transform = null;
        base.OnComponentDeactivated();
    }

    private void Tick()
    {
        if (_transform is null)
            return;

        _yawDegrees += SpinSpeedDegreesPerSecond * Engine.Delta;
        _pitchDegrees += SpinSpeedDegreesPerSecond * 0.65f * Engine.Delta;

        float bob = MathF.Sin(Engine.ElapsedTime * BobFrequencyHz * (2.0f * MathF.PI)) * BobAmplitude;

        _transform.Rotation = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, DegreesToRadians(_yawDegrees)) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, DegreesToRadians(_pitchDegrees)));

        Vector3 translation = _transform.Translation;
        translation.Y = 1.0f + bob;
        _transform.Translation = translation;
    }

    private static float DegreesToRadians(float value)
        => value * (MathF.PI / 180.0f);
}

using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Components.VR;

/// <summary>Body-relative follow lens and damping settings; never applied to XR eyes.</summary>
public sealed class VrSpectatorFollowSettings : XRBase
{
    private float _distance = 3f, _height = .5f, _shoulderOffset, _smoothing = 8f, _fieldOfView = 60f;
    private Vector3 _aimOffset = new(0, .25f, 0);
    public float Distance { get => _distance; set => SetField(ref _distance, FiniteRange(value, .1f, 100f)); }
    public float Height { get => _height; set => SetField(ref _height, FiniteRange(value, -10f, 20f)); }
    public float ShoulderOffset { get => _shoulderOffset; set => SetField(ref _shoulderOffset, FiniteRange(value, -10f, 10f)); }
    public float Smoothing { get => _smoothing; set => SetField(ref _smoothing, FiniteRange(value, 0f, 100f)); }
    public float FieldOfView { get => _fieldOfView; set => SetField(ref _fieldOfView, FiniteRange(value, 10f, 150f)); }
    public Vector3 AimOffset
    {
        get => _aimOffset;
        set
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _aimOffset, value);
        }
    }
    private static float FiniteRange(float value, float min, float max)
        => float.IsFinite(value) ? Math.Clamp(value, min, max) : throw new ArgumentOutOfRangeException(nameof(value));
}

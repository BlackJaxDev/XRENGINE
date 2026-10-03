using System.Numerics;

namespace XREngine.Components.VR;

/// <summary>In-memory calibration data without device handles or scene references.</summary>
internal sealed class VrSessionCalibration
{
    internal readonly string?[] Identities = new string?[11];
    internal readonly Matrix4x4?[] Offsets = new Matrix4x4?[11];
    internal EBodyMeasurementMode Mode;
    internal float Measurement;
}

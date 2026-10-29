using System.Numerics;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private void SetSkyMoonCase(int sampleCase)
    {
        if (sampleCase is < 0 or > 11)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _skyCase = sampleCase;
        _skyComponent!.TimeOfDay = sampleCase switch
        {
            1 => 0.6f,
            2 => 0.9f,
            3 => 0.5f,
            4 => 0,
            5 => 0.25f,
            _ => 0.75f,
        };
        _skyComponent.MoonDiscSize = sampleCase switch
        {
            6 => 0.998f,
            7 => 0.9999f,
            _ => 0.99965f,
        };
        _skyComponent.Intensity = sampleCase == 8 ? 4 : 1;
        _skyComponent.Rotation = sampleCase == 10 ? 90 : 0;
        _skyCameraParameters!.VerticalFieldOfView = 10;
        // Keep the foreground outside the view while sampling the moon surface.
        _skyCameraTransform!.Translation = sampleCase == 9 ? new Vector3(500, 600, 700) : new Vector3(100, 200, 300);
        Vector3 forward = _skyComponent.GetMoonDirection();
        if (sampleCase == 10)
            forward = new Vector3(forward.Z, forward.Y, -forward.X);
        Matrix4x4 view = Matrix4x4.CreateLookAt(Vector3.Zero, forward, Vector3.UnitY);
        if (!Matrix4x4.Invert(view, out Matrix4x4 inverseView))
            throw new InvalidOperationException("EngineMeshDiagnostic.MoonCameraInvalid.");
        _skyCameraTransform.Rotation = Quaternion.CreateFromRotationMatrix(inverseView);
    }
}

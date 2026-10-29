using System.Numerics;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private void SetSkySunCase(int sampleCase)
    {
        if (sampleCase is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _skyCase = sampleCase;
        _skyComponent!.TimeOfDay = sampleCase switch
        {
            1 or 6 or 8 => 0,
            2 => 0.5f,
            3 => 0.98f,
            _ => 0.25f,
        };
        _skyComponent.SunDiscSize = sampleCase switch
        {
            4 => 0.998f,
            5 => 0.9999f,
            _ => 0.9994f,
        };
        _skyComponent.Intensity = sampleCase == 6 ? 0.01f : 1;
        _skyComponent.Rotation = sampleCase == 8 ? 90 : 0;
        _skyCameraParameters!.VerticalFieldOfView = sampleCase == 8 ? 90 : 10;
        _skyCameraTransform!.Translation = sampleCase == 7 ? new Vector3(100, 200, 300) : Vector3.Zero;
        if (sampleCase == 8)
        {
            _skyCameraTransform.Rotation = Quaternion.Identity;
            return;
        }

        // Aim the camera at the component's authored sun direction.
        Matrix4x4 view = Matrix4x4.CreateLookAt(Vector3.Zero, _skyComponent.GetSunDirection(), Vector3.UnitY);
        if (!Matrix4x4.Invert(view, out Matrix4x4 inverseView))
            throw new InvalidOperationException("EngineMeshDiagnostic.SunCameraInvalid.");
        _skyCameraTransform.Rotation = Quaternion.CreateFromRotationMatrix(inverseView);
    }
}

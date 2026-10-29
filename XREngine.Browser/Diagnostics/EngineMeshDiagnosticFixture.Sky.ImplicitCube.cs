using System.Numerics;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private void SetSkyImplicitCubeCase(int sampleCase)
    {
        if (sampleCase is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _skyCase = sampleCase;
        _skyTexture!.MinLOD = sampleCase == 8 ? 2 : 0;
        _skyTexture.MaxLOD = sampleCase == 7 ? 1 : 8;
        _skyComponent!.Intensity = sampleCase == 6 ? 4 : 1;
        _skyCameraParameters!.VerticalFieldOfView = sampleCase switch
        {
            0 => 90,
            2 or 5 or 6 => 160,
            3 or 7 => 170,
            _ => 140,
        };
        // Move the foreground outside the view. Only the camera footprint changes LOD.
        _skyCameraTransform!.Translation = sampleCase == 5 ? new Vector3(500, 600, 700) : new Vector3(100, 200, 300);
        _skyCameraTransform.Rotation = sampleCase == 4
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2)
            : Quaternion.Identity;
    }
}

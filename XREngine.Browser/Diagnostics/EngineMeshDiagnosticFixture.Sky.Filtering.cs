using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private void SetSkyFilteredImageCase(int sampleCase)
    {
        if (sampleCase is < 0 or > 14)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        SetSkyImageCase(sampleCase <= 9 ? sampleCase : sampleCase == 13 ? 7 : 0);
        _skyCase = sampleCase;
        int level = sampleCase switch
        {
            10 or 13 => 1,
            11 => 2,
            12 => 3,
            _ => 0,
        };
        _skyTexture!.MinLOD = level;
        _skyTexture.MaxLOD = level;
        if (sampleCase == 13)
            _skyComponent!.Intensity = 4;
    }
}

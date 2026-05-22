using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Rendering;

namespace XREngine.Scene;

public class RuntimeWorldSettings : XRAsset, IRuntimeAmbientSettings
{
    private bool _previewOctrees;
    private bool _previewQuadtrees;

    public ColorF3 AmbientLightColor { get; set; } = new(0.03f, 0.03f, 0.03f);
    public float AmbientLightIntensity { get; set; } = 1.0f;
    public Vector3 Gravity { get; set; } = new(0.0f, -9.81f, 0.0f);

    public bool PreviewOctrees
    {
        get => _previewOctrees;
        set => SetField(ref _previewOctrees, value);
    }

    public bool PreviewQuadtrees
    {
        get => _previewQuadtrees;
        set => SetField(ref _previewQuadtrees, value);
    }
}

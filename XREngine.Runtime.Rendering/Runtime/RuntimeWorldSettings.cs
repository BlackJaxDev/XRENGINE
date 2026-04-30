using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Rendering;

namespace XREngine.Scene;

public class RuntimeWorldSettings : XRAsset, IRuntimeAmbientSettings
{
    public ColorF3 AmbientLightColor { get; set; } = new(0.03f, 0.03f, 0.03f);
    public float AmbientLightIntensity { get; set; } = 1.0f;
    public Vector3 Gravity { get; set; } = new(0.0f, -9.81f, 0.0f);
}
using System.ComponentModel;
using System.Numerics;

namespace XREngine.Scene;

public partial class WorldSettings
{
    private bool _ambientSphericalHarmonicsEnabled;
    private AmbientSphericalHarmonics _ambientSphericalHarmonics;

    /// <summary>
    /// Uses authored directional ambient instead of probe diffuse for source
    /// stylized lighting. Probe reflections and generic PBR retain their behavior.
    /// </summary>
    [Category("Lighting")]
    public bool AmbientSphericalHarmonicsEnabled
    {
        get => _ambientSphericalHarmonicsEnabled;
        set => SetField(ref _ambientSphericalHarmonicsEnabled, value);
    }

    /// <summary>
    /// Authored linear diffuse coefficients. Enabled zero coefficients mean black
    /// ambient. These values are not generated from reflection probe textures.
    /// </summary>
    [Category("Lighting")]
    public AmbientSphericalHarmonics AmbientSphericalHarmonics
    {
        get => _ambientSphericalHarmonics;
        set
        {
            if (!value.IsFinite)
                throw new ArgumentOutOfRangeException(nameof(value), "Ambient SH coefficients must be finite.");
            SetField(ref _ambientSphericalHarmonics, value);
        }
    }

    /// <summary>Returns authored SH, or the current constant ambient when disabled.</summary>
    public AmbientSphericalHarmonics GetEffectiveAmbientSphericalHarmonics() =>
        AmbientSphericalHarmonicsEnabled ? AmbientSphericalHarmonics :
            AmbientSphericalHarmonics.Constant((Vector3)GetEffectiveAmbientColor());
}

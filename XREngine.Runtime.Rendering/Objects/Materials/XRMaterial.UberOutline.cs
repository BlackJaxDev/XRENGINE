using System.ComponentModel;
using XREngine.Core.Files;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private UberOutlineMaterialProfile? _cookedOutlineProfile;
    private XRMaterial? _uberOutlineSourceMaterial;
    private XRShader? _ownedCookedOutlineShader;
    private TextFile? _ownedCookedOutlineShaderSource;

    internal void OwnCookedOutlineShader(XRShader shader)
    {
        SetField(ref _ownedCookedOutlineShader, shader, publishNotifications: false);
        SetField(ref _ownedCookedOutlineShaderSource, shader.Source, publishNotifications: false);
    }

    private void DestroyOwnedCookedOutlineShader()
    {
        if (_ownedCookedOutlineShader is { IsDestroyed: false } shader)
        {
            shader.Destroy(now: true);
            if (!shader.IsDestroyed)
                throw new InvalidOperationException("Cooked outline shader destruction was vetoed.");
        }
        if (_ownedCookedOutlineShaderSource is { IsDestroyed: false } source)
        {
            source.Destroy(now: true);
            if (!source.IsDestroyed)
                throw new InvalidOperationException("Cooked outline shader source destruction was vetoed.");
        }
    }

    private void ReviveOwnedCookedOutlineShader()
    {
        if (_ownedCookedOutlineShaderSource is { IsDestroyed: true } source)
            source.Generate();
        if (_ownedCookedOutlineShader is { IsDestroyed: true } shader)
            shader.Generate();
    }

    /// <summary>
    /// Target-cooked outline behavior and texture metadata. Ordinary reflection
    /// serialization preserves the source parameters' surrounding graph identity.
    /// </summary>
    [Browsable(false)]
    public UberOutlineMaterialProfile? CookedOutlineProfile
    {
        get => _cookedOutlineProfile;
        set => SetField(ref _cookedOutlineProfile, value);
    }

    /// <summary>The live source behind its owned cooked inverse-hull material.</summary>
    [Browsable(false), YamlIgnore]
    public XRMaterial? UberOutlineSourceMaterial
    {
        get => _uberOutlineSourceMaterial;
        internal set => SetField(ref _uberOutlineSourceMaterial, value);
    }
}

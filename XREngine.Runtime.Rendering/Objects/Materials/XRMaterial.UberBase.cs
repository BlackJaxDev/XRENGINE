using System.ComponentModel;
using MemoryPack;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    [RuntimeOnly, YamlIgnore, MemoryPackIgnore]
    private UberBaseMaterialProfile? _cookedUberBaseProfile;

    /// <summary>Target-only exact Uber companion; original parameters, authored state and source graph remain authoritative.</summary>
    [Browsable(false), RuntimeOnly, YamlIgnore, MemoryPackIgnore]
    public UberBaseMaterialProfile? CookedUberBaseProfile
    {
        get => _cookedUberBaseProfile;
        set => SetField(ref _cookedUberBaseProfile, value);
    }
}

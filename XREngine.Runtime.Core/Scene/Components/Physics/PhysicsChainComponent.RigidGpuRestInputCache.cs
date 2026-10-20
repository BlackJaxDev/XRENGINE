using YamlDotNet.Serialization;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    private bool _enableRigidGpuRestInputCache;

    /// <summary>Enables retained child rest inputs for an explicitly rigid GPU chain.</summary>
    [YamlIgnore]
    public bool EnableRigidGpuRestInputCache
    {
        get => _enableRigidGpuRestInputCache;
        set => SetField(ref _enableRigidGpuRestInputCache, value);
    }

    /// <summary>Gets cumulative cache capture counts for this chain's physics world.</summary>
    public PhysicsChainRigidGpuRestInputCacheDiagnostics RigidGpuRestInputCacheDiagnostics
        => World is { } world && PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler)
            ? scheduler?.CaptureRigidGpuRestInputCacheDiagnostics() ?? default
            : default;
}

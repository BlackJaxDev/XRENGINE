using XREngine.Core.Files;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public interface IRuntimeShaderServices
{
    /// <summary>Whether shader loading and source warming may run or wait synchronously on this host.</summary>
    bool SupportsSynchronousShaderWork => true;
    /// <summary>Changes when the host replaces the owner of cached shader assets.</summary>
    int ShaderAssetCacheVersion => 0;
    /// <summary>Returns an already published shader without starting asset work, or null when absent.</summary>
    XRShader? GetCachedEngineShader(string assetRoot, string relativePath) => null;
    T? LoadAsset<T>(string filePath) where T : XRAsset, new();
    T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new();
    Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new();
    void LogWarning(string message);
}

public static class RuntimeShaderServices
{
    internal static readonly object ServiceGate = new();
    private static IRuntimeShaderServices? _current;
    private static int _serviceVersion;
    private static Action<ShaderSourceFileChange>? _sourceChangeHandler;

    internal static int ServiceVersion => Volatile.Read(ref _serviceVersion);

    /// <summary>
    /// Admits one synchronous source invalidation against an installed owner.
    /// Already admitted notifications may finish after replacement; callbacks
    /// and shader cache work must run after this method releases the gate.
    /// </summary>
    internal static bool TryAdmitSourceInvalidation(
        IRuntimeShaderServices? sourceOwner, int serviceVersion, int sourceVersion)
    {
        // Capability and epoch getters belong to external owners and can reenter
        // Current. Sample them first, then check only local installation state.
        // The asset epoch is a snapshot: its owner does not share this gate.
        if (!ShaderSourceResolver.CanAccessHostShaderFiles ||
            sourceVersion != (sourceOwner?.ShaderAssetCacheVersion ?? 0))
            return false;

        lock (ServiceGate)
            return ReferenceEquals(_current, sourceOwner) && _serviceVersion == serviceVersion;
    }

    public static IRuntimeShaderServices? Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            lock (ServiceGate)
            {
                if (ReferenceEquals(_current, value))
                    return;

                if (_current is IRuntimeShaderChangeSource previousChangeSource)
                    previousChangeSource.ShaderSourceFileChanged -= _sourceChangeHandler;

                Volatile.Write(ref _current, value);
                int installedVersion = Interlocked.Increment(ref _serviceVersion);
                _sourceChangeHandler = null;
                ShaderHelper.ClearServiceBoundCaches();
                if (value is IRuntimeShaderChangeSource nextChangeSource)
                {
                    // A detached source may already have copied its event delegate.
                    // Preserve that source's identity through queue admission too.
                    _sourceChangeHandler = change =>
                        ShaderSourceDependencyIndex.QueueFileChange(change, value, installedVersion);
                    nextChangeSource.ShaderSourceFileChanged += _sourceChangeHandler;
                }
            }
        }
    }

}

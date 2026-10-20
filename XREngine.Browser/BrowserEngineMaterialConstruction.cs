using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Installs source-free construction for engine-owned browser materials.</summary>
internal static class BrowserEngineMaterialConstruction
{
    internal static IDisposable Install()
        => RuntimeEngineMaterialConstructionServices.Install(EngineMaterialConstructionTarget.WebGpuCooked);
}

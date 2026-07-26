using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeProfilerFacade
{
    public IDisposable Start(string? label = null)
        => RuntimeRenderingHostServices.Profiling.StartProfileScope(label) ?? DisposableAction.Empty;

    public IDisposable Start(string? label, ProfilerScopeKind scopeKind)
        => RuntimeRenderingHostServices.Profiling.StartProfileScope(label) ?? DisposableAction.Empty;
}

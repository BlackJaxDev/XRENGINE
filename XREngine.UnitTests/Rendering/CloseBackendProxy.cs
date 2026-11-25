using System.Reflection;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>Probes native close request ordering without creating a desktop window.</summary>
public class CloseBackendProxy : DispatchProxy
{
    public Action? OnClose { get; set; }
    public bool ThrowAfterClose { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IRuntimeWindowBackend.RequestClose))
            throw new NotSupportedException();
        OnClose?.Invoke();
        if (ThrowAfterClose)
            throw new InvalidOperationException("backend close failed");
        return null;
    }
}

using System.Reflection;
using System.Runtime.ExceptionServices;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>Injects one profiler failure without changing other host services.</summary>
public class AdvancedGpuSceneIdentityFaultHost : DispatchProxy, IDisposable
{
    private IRuntimeRenderingHostServices _inner = null!;
    private string _scopeName = string.Empty;
    private bool _throwOnDispose;
    private Action _beforeFailure = null!;
    private bool _armed = true;

    public int FailureCount { get; private set; }

    public static IRuntimeRenderingHostServices Create(
        string scopeName,
        bool throwOnDispose,
        Action beforeFailure,
        out AdvancedGpuSceneIdentityFaultHost fault)
    {
        IRuntimeRenderingHostServices proxy =
            Create<IRuntimeRenderingHostServices, AdvancedGpuSceneIdentityFaultHost>();
        fault = (AdvancedGpuSceneIdentityFaultHost)proxy;
        fault._inner = RuntimeRenderingHostServices.Current;
        fault._scopeName = scopeName;
        fault._throwOnDispose = throwOnDispose;
        fault._beforeFailure = beforeFailure;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (_armed && targetMethod.Name == nameof(IRuntimeRenderProfilingServices.StartProfileScope) &&
            args is [string scopeName] && scopeName == _scopeName)
        {
            if (!_throwOnDispose)
                Fail();
            return this;
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    public void Dispose() => Fail();

    private void Fail()
    {
        _armed = false;
        ++FailureCount;
        _beforeFailure();
        throw new InvalidOperationException("Injected identity-delivery boundary failure.");
    }
}

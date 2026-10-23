using System.Reflection;
using System.Runtime.ExceptionServices;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>Installs the headless host defaults for explicit UI lifecycle tests.</summary>
public class UITestRenderingHost : DispatchProxy
{
    private IRuntimeRenderingHostServices _defaults = null!;

    public static IDisposable Install()
    {
        var host = Create<IRuntimeRenderingHostServices, UITestRenderingHost>();
        ((UITestRenderingHost)(object)host)._defaults = RuntimeRenderingHostServices.Current;
        return RuntimeRenderingHostServices.Install(host);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        try { return targetMethod.Invoke(_defaults, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

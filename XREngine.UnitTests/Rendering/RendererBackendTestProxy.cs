using System.Reflection;

namespace XREngine.UnitTests.Rendering;

public class RendererBackendTestProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.ReturnType.IsValueType == true
            ? Activator.CreateInstance(targetMethod.ReturnType)
            : null;
}

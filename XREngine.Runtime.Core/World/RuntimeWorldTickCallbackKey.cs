using System.Reflection;
using System.Runtime.CompilerServices;
using XREngine.Components;

namespace XREngine;

/// <summary>
/// Identifies a kind of tick callback for observation: the method that runs,
/// the type of the object it runs on, the method a compiler-generated closure
/// forwards to (when there is one), and the queue it was registered in. Every
/// instance registered with the same values shares one key, so a key names an
/// owner in code and not one scene object.
/// </summary>
internal readonly record struct RuntimeWorldTickCallbackKey(
    MethodInfo Method,
    Type? TargetType,
    MethodInfo? InnerMethod,
    ETickGroup Group,
    int Order)
{
    /// <summary>
    /// Builds the key of a callback. Reflects over closure fields, so call it
    /// only when a registration is applied, never per invocation.
    /// </summary>
    public static RuntimeWorldTickCallbackKey From(ETickGroup group, int order, WorldTick callback)
    {
        object? target = callback.Target;
        Type? targetType = target?.GetType();
        return new RuntimeWorldTickCallbackKey(callback.Method, targetType, ResolveInnerMethod(target, targetType), group, order);
    }

    /// <summary>
    /// A registration helper such as an animation tick wraps the caller's
    /// delegate in a closure, which would otherwise hide the real owner.
    /// Returns the method of the first delegate the closure captured.
    /// </summary>
    private static MethodInfo? ResolveInnerMethod(object? target, Type? targetType)
    {
        if (target is null || targetType is null || !targetType.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            return null;

        foreach (FieldInfo field in targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (typeof(Delegate).IsAssignableFrom(field.FieldType) && field.GetValue(target) is Delegate captured)
                return captured.Method;
        }

        return null;
    }
}

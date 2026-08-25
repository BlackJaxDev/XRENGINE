namespace XREngine.Core.Files;

/// <summary>
/// Optional upper-runtime hooks used while reflection-deserializing object graphs. The lower
/// serializer knows nothing about scene nodes, components, worlds, or other runtime owners.
/// </summary>
public interface ICookedBinaryObjectLifecycleServices
{
    /// <summary>Prepares a newly constructed object before any serialized members are assigned.</summary>
    void PrepareInstance(object instance);

    /// <summary>
    /// Enters an owner-specific member scope. Implementations return <see langword="null"/> when
    /// no scope is required.
    /// </summary>
    IDisposable? EnterMemberScope(object instance, string memberName);
}

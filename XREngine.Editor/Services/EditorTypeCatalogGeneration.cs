using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace XREngine.Editor.Services;

/// <summary>
/// Identifies the currently loaded editor type universe so reflection caches can
/// discard descriptors that would otherwise retain collectible script assemblies.
/// </summary>
internal static class EditorTypeCatalogGeneration
{
    private sealed class CollectibleContextRegistration
    {
        private int _isUnloading;

        public bool IsUnloading => Volatile.Read(ref _isUnloading) != 0;

        public void MarkUnloading()
            => Volatile.Write(ref _isUnloading, 1);
    }

    private static readonly object RegistrationSync = new();
    private static readonly ConditionalWeakTable<AssemblyLoadContext, CollectibleContextRegistration> RegisteredCollectibleContexts = new();
    private static long _current;

    static EditorTypeCatalogGeneration()
    {
        AppDomain.CurrentDomain.AssemblyLoad += HandleAssemblyLoad;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            RegisterCollectibleLoadContext(assembly);
    }

    /// <summary>Raised after an assembly is loaded or a dynamic game assembly begins unloading.</summary>
    public static event Action<long>? Changed;

    /// <summary>Gets the current loaded-type generation.</summary>
    public static long Current => Volatile.Read(ref _current);

    /// <summary>
    /// Returns false after a collectible context begins unloading, even while its
    /// assemblies remain visible through <see cref="AppDomain.GetAssemblies"/>.
    /// </summary>
    public static bool IsAssemblyDiscoverable(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (assembly.IsDynamic)
            return false;

        AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(assembly);
        if (context is not { IsCollectible: true })
            return true;

        RegisterCollectibleLoadContext(assembly);
        return RegisteredCollectibleContexts.TryGetValue(context, out CollectibleContextRegistration? registration)
            && !registration.IsUnloading;
    }

    /// <summary>Returns false when a type or any constructed element/argument belongs to an unloading context.</summary>
    public static bool IsTypeDiscoverable(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!IsAssemblyDiscoverable(type.Assembly))
            return false;

        if (type.HasElementType && type.GetElementType() is Type elementType
            && !IsTypeDiscoverable(elementType))
        {
            return false;
        }

        if (!type.IsGenericType)
            return true;

        foreach (Type argument in type.GetGenericArguments())
            if (!IsTypeDiscoverable(argument))
                return false;

        return true;
    }

    private static void HandleAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        RegisterCollectibleLoadContext(args.LoadedAssembly);
        Advance();
    }

    private static void RegisterCollectibleLoadContext(Assembly assembly)
    {
        AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(assembly);
        if (context is not { IsCollectible: true })
            return;

        lock (RegistrationSync)
        {
            if (RegisteredCollectibleContexts.TryGetValue(context, out _))
                return;

            RegisteredCollectibleContexts.Add(context, new CollectibleContextRegistration());
            context.Unloading += HandleCollectibleContextUnloading;
        }
    }

    private static void HandleCollectibleContextUnloading(AssemblyLoadContext context)
    {
        lock (RegistrationSync)
        {
            if (RegisteredCollectibleContexts.TryGetValue(context, out CollectibleContextRegistration? registration))
                registration.MarkUnloading();
        }

        Advance();
    }

    private static void Advance()
    {
        long generation = Interlocked.Increment(ref _current);
        Action<long>? handlers = Changed;
        if (handlers is null)
            return;

        foreach (Action<long> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(generation);
            }
            catch (Exception ex)
            {
                Debug.UIException(ex, "An editor type-catalog generation listener failed.");
            }
        }
    }
}

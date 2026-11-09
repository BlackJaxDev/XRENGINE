using System.Collections.Concurrent;
using System.Reflection;
using XREngine.Core;

namespace XREngine.Editor.Services;

/// <summary>
/// Fills the shared loadable-type catalog on a background thread while the editor starts,
/// and for each assembly that loads later.
/// </summary>
/// <remarks>
/// Editor panels scan the loaded assemblies for types, for example the camera inspector's
/// render-pipeline selector. The first scan of an assembly enumerates every type in it and can
/// take hundreds of milliseconds. Without a warm-up, that first scan runs inside an ImGui frame
/// on the render thread and stalls the visible frames. The catalog caches each assembly once.
/// Most engine assemblies load during engine start, after this warm-up begins, so the warm-up
/// also reads each assembly that loads while it runs.
/// </remarks>
internal static class EditorTypeCatalogWarmup
{
    private static readonly ConcurrentQueue<Assembly> PendingAssemblies = new();
    private static readonly SemaphoreSlim PendingSignal = new(0);
    private static int _started;
    private static int _pendingCount;

    /// <summary>
    /// Gets whether a full type scan is cheap now. This is true when no queued assembly is
    /// waiting for the warm-up, and also when no warm-up was started, so hosts without a
    /// warm-up keep scanning on demand.
    /// </summary>
    public static bool IsReady
        => Volatile.Read(ref _started) == 0 || Volatile.Read(ref _pendingCount) == 0;

    /// <summary>
    /// Starts the warm-up once. It reads the loaded assemblies and then each assembly that loads later.
    /// </summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        // Subscribe before the snapshot so no assembly loads between the two without a queue entry.
        // An assembly in both is read once; the catalog cache makes the second read free.
        AppDomain.CurrentDomain.AssemblyLoad += HandleAssemblyLoad;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            Enqueue(assembly);

        Thread thread = new(ProcessQueue)
        {
            IsBackground = true,
            Name = "Editor type catalog warm-up",
        };
        thread.Start();
    }

    private static void HandleAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        => Enqueue(args.LoadedAssembly);

    private static void Enqueue(Assembly assembly)
    {
        if (assembly.IsDynamic)
            return;

        Interlocked.Increment(ref _pendingCount);
        PendingAssemblies.Enqueue(assembly);
        PendingSignal.Release();
    }

    private static void ProcessQueue()
    {
        while (true)
        {
            PendingSignal.Wait();
            if (!PendingAssemblies.TryDequeue(out Assembly? assembly))
                continue;

            try
            {
                // Read the same assemblies that the editor scans read.
                if (EditorTypeCatalogGeneration.IsAssemblyDiscoverable(assembly))
                    _ = XRLoadableTypeCatalog.GetTypes(assembly);
            }
            catch (Exception ex)
            {
                Debug.UIWarning($"Type catalog warm-up skipped assembly '{assembly.GetName().Name}': {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingCount);
            }
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using XREngine.Components;
using XREngine.Core;

namespace XREngine.Components.Scripting
{
    /// <summary>Loads and unloads authoring game assemblies in collectible desktop contexts.</summary>
    public static class GameCSProjLoader
    {
        /// <summary>Repository-relative path of the policy that explains why these guards exist.</summary>
        public const string ContentExecutionPolicyDocumentPath = "docs/architecture/runtime/downloadable-content-execution-policy.md";

        private static readonly object ProtectedRootSync = new();
        private static string[] _protectedContentRoots = [];

        /// <summary>
        /// Registers a directory that holds downloaded or staged content. Assemblies under a protected
        /// root are refused in every build kind, including development, because downloaded content is
        /// data only.
        /// </summary>
        public static void ProtectContentRoot(string rootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
            string normalized = NormalizeRoot(rootDirectory);
            lock (ProtectedRootSync)
            {
                string[] current = _protectedContentRoots;
                for (int i = 0; i < current.Length; i++)
                {
                    if (string.Equals(current[i], normalized, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                string[] updated = new string[current.Length + 1];
                Array.Copy(current, updated, current.Length);
                updated[current.Length] = normalized;
                _protectedContentRoots = updated;
            }
        }

        /// <summary>Returns true when <paramref name="path"/> resolves under a protected content root.</summary>
        public static bool IsUnderProtectedContentRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return false;
            }

            string[] roots = Volatile.Read(ref _protectedContentRoots);
            for (int i = 0; i < roots.Length; i++)
            {
                if (full.StartsWith(roots[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string NormalizeRoot(string rootDirectory)
        {
            string full = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full + Path.DirectorySeparatorChar;
        }

        private static void EnsureRuntimeAssemblyLoadingSupported()
        {
            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                throw new NotSupportedException("Runtime managed assembly loading is disabled for NativeAOT runtime builds.");

            // Published CoreCLR players refuse runtime assembly loading as well. AssemblyLoadContext
            // is not a security boundary, so downloaded IL would run with the player's privileges.
            if (XRRuntimeEnvironment.IsPublishedBuild)
                throw new NotSupportedException($"Runtime managed assembly loading is disabled for published runtime builds. Downloaded content is data only; see {ContentExecutionPolicyDocumentPath}.");
        }

        private static void EnsureNotProtectedContent(string id, string assemblyPath)
        {
            if (!IsUnderProtectedContentRoot(assemblyPath))
                return;

            throw new NotSupportedException($"Assembly '{id}' at '{assemblyPath}' is inside a downloaded-content root and cannot be loaded. Downloaded content is data only; see {ContentExecutionPolicyDocumentPath}.");
        }

        public class DynamicEngineAssemblyLoadContext : AssemblyLoadContext
        {
            public DynamicEngineAssemblyLoadContext() : base(isCollectible: true) { }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                // Defer to the default context for dependencies (engine assemblies, etc.)
                return null;
            }
        }

        public static event Action<string, AssemblyData>? OnAssemblyLoaded;
        public static event Action<string>? OnAssemblyUnloaded;

        public class AssemblyData(Type[] components, Type[] menuItems)
        {
            public Type[] Components { get; } = components;
            public Type[] MenuItems { get; } = menuItems;
        }
        
        private static readonly Dictionary<string, (object source, Assembly assembly, AssemblyLoadContext context, AssemblyData data)> _loadedAssemblies = [];
        public static IReadOnlyDictionary<string, AssemblyData> LoadedAssemblies => _loadedAssemblies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.data);

        [RequiresUnreferencedCode("Calls System.Reflection.Assembly.GetExportedTypes()")]
        private static void LoadFromAssembly(string id, object source, AssemblyLoadContext context, Assembly assembly)
        {
            IReadOnlyList<Type> exported = XREngine.Core.XRLoadableTypeCatalog.GetExportedTypes(assembly);
            Type[] components = [.. exported.Where(t => t.IsSubclassOf(typeof(XRComponent)))];
            Type[] menuItems = [.. exported.Where(t => typeof(IRuntimeMenuItem).IsAssignableFrom(t) && !t.IsInterface)];
            
            // Keep the context alive for the logical load lifetime. A weak context can
            // be finalized and begin unloading even while its assembly remains in use.
            // Removing this entry in Unload releases ownership before requesting unload.
            _loadedAssemblies[id] = (source, assembly, context, new AssemblyData(components, menuItems));
            OnAssemblyLoaded?.Invoke(id, new AssemblyData(components, menuItems));
        }

        [RequiresUnreferencedCode("")]
        public static void LoadFromStream(string id, Stream stream)
        {
            EnsureRuntimeAssemblyLoadingSupported();
            if (stream is FileStream fileStream)
                EnsureNotProtectedContent(id, fileStream.Name);

            // Unload existing assembly with this ID first
            Unload(id);

            try
            {
                AssemblyLoadContext context = new DynamicEngineAssemblyLoadContext();
                Assembly assembly = context.LoadFromStream(stream);
                LoadFromAssembly(id, stream, context, assembly);
            }
            catch (Exception ex)
            {
                Debug.ScriptingException(ex, $"Failed to load assembly '{id}' from stream.");
            }
        }

        [RequiresUnreferencedCode("")]
        public static void LoadFromPath(string id, string assemblyPath)
        {
            EnsureRuntimeAssemblyLoadingSupported();
            EnsureNotProtectedContent(id, assemblyPath);

            if (!File.Exists(assemblyPath))
            {
                Debug.ScriptingWarning($"Assembly file not found: {assemblyPath}");
                return;
            }

            // Unload existing assembly with this ID first
            Unload(id);

            try
            {
                // Read the file into memory to avoid file locking
                // This allows the file to be recompiled while the assembly is loaded
                byte[] assemblyBytes = File.ReadAllBytes(assemblyPath);
                
                // Also try to load PDB for debugging support
                string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
                byte[]? pdbBytes = null;
                if (File.Exists(pdbPath))
                {
                    pdbBytes = File.ReadAllBytes(pdbPath);
                }

                AssemblyLoadContext context = new DynamicEngineAssemblyLoadContext();
                Assembly assembly;
                
                using (var assemblyStream = new MemoryStream(assemblyBytes))
                {
                    if (pdbBytes != null)
                    {
                        using var pdbStream = new MemoryStream(pdbBytes);
                        assembly = context.LoadFromStream(assemblyStream, pdbStream);
                    }
                    else
                    {
                        assembly = context.LoadFromStream(assemblyStream);
                    }
                }
                
                LoadFromAssembly(id, assemblyPath, context, assembly);
                Debug.Scripting($"Successfully loaded assembly '{id}' from {assemblyPath}");
            }
            catch (Exception ex)
            {
                Debug.ScriptingException(ex, $"Failed to load assembly '{id}' from path: {assemblyPath}");
            }
        }

        public static void Unload(string id)
        {
            if (!_loadedAssemblies.TryGetValue(id, out var data))
                return;
            
            _loadedAssemblies.Remove(id);
            
            data.context.Unload();
            
            if (data.source is Stream stream)
                stream.Dispose();

            OnAssemblyUnloaded?.Invoke(id);

            RuntimeMaintenanceServices.Current.RequestGarbageCollection(new EngineMaintenanceGcRequest(
                EngineMaintenanceGcReason.DynamicAssemblyUnload,
                $"Game assembly '{id}' unloaded; reclaim collectible AssemblyLoadContext file locks.",
                Generation: GC.MaxGeneration,
                CompactLargeObjectHeap: false,
                WaitForPendingFinalizers: true));
        }

        /// <summary>
        /// Checks if an assembly with the given ID is currently loaded.
        /// </summary>
        public static bool IsLoaded(string id) => _loadedAssemblies.ContainsKey(id);

        /// <summary>
        /// Gets the assembly data for a loaded assembly, or null if not loaded.
        /// </summary>
        public static AssemblyData? GetAssemblyData(string id)
            => _loadedAssemblies.TryGetValue(id, out var data) ? data.data : null;
    }
}

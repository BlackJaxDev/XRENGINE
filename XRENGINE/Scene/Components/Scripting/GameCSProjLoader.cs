using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using XREngine.Components;
using XREngine.Core;

namespace XREngine.Components.Scripting
{
    public static class GameCSProjLoader
    {
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
        
        private static readonly Dictionary<string, (object source, Assembly assembly, WeakReference<AssemblyLoadContext> contextRef, AssemblyData data)> _loadedAssemblies = [];
        public static IReadOnlyDictionary<string, AssemblyData> LoadedAssemblies => _loadedAssemblies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.data);

        [RequiresUnreferencedCode("Calls System.Reflection.Assembly.GetExportedTypes()")]
        private static void LoadFromAssembly(string id, object source, AssemblyLoadContext context, Assembly assembly)
        {
            Type[] exported = assembly.GetExportedTypes();
            Type[] components = [.. exported.Where(t => t.IsSubclassOf(typeof(XRComponent)))];
            Type[] menuItems = [.. exported.Where(t => t.IsSubclassOf(typeof(XRMenuItem)))];
            
            // Use WeakReference to allow GC to collect the context after unload
            _loadedAssemblies[id] = (source, assembly, new WeakReference<AssemblyLoadContext>(context), new AssemblyData(components, menuItems));
            OnAssemblyLoaded?.Invoke(id, new AssemblyData(components, menuItems));
        }

        [RequiresUnreferencedCode("")]
        public static void LoadFromStream(string id, Stream stream)
        {
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
            
            // Try to get the context and unload it
            if (data.contextRef.TryGetTarget(out var context))
            {
                context.Unload();
            }
            
            if (data.source is Stream stream)
                stream.Dispose();

            OnAssemblyUnloaded?.Invoke(id);

            // Encourage garbage collection to release the assembly
            // This helps ensure file locks are released before recompilation
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
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

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using XREngine.Components;

namespace XREngine.Scene.Components.Scripting
{
    public static class GameCSProjLoader
    {
        public class DynamicEngineAssemblyLoadContext : AssemblyLoadContext
        {
            public DynamicEngineAssemblyLoadContext() : base(isCollectible: true) { }

            //[RequiresUnreferencedCode("")]
            protected override Assembly? Load(AssemblyName assemblyName)
            {
                return Assembly.Load(assemblyName);

                //try
                //{
                //    string assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName.Name}.dll");
                //    if (File.Exists(assemblyPath))
                //    {
                //        return LoadFromAssemblyPath(assemblyPath);
                //    }
                //}
                //catch (Exception ex)
                //{
                //    Debug.LogException(ex, "Error loading assembly.");
                //}
                //return null;
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
            Type[] exported = assembly.GetExportedTypes();
            Type[] components = [.. exported.Where(t => t.IsSubclassOf(typeof(XRComponent)))];
            Type[] menuItems = [.. exported.Where(t => t.IsSubclassOf(typeof(XRMenuItem)))];
            _loadedAssemblies.Add(id, (source, assembly, context, new AssemblyData(components, menuItems)));
            OnAssemblyLoaded?.Invoke(id, new AssemblyData(components, menuItems));
        }

        [RequiresUnreferencedCode("")]
        public static void LoadFromStream(string id, Stream stream)
        {
            AssemblyLoadContext context = new DynamicEngineAssemblyLoadContext();
            Assembly assembly = context.LoadFromStream(stream);
            LoadFromAssembly(id, stream, context, assembly);
        }

        [RequiresUnreferencedCode("")]
        public static void LoadFromPath(string id, string assemblyPath)
        {
            AssemblyLoadContext context = new DynamicEngineAssemblyLoadContext();
            Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
            LoadFromAssembly(id, assemblyPath, context, assembly);
        }

        public static void Unload(string id)
        {
            if (!_loadedAssemblies.TryGetValue(id, out var data))
                return;
            
            data.context.Unload();
            if (data.source is Stream stream)
                stream.Dispose();

            _loadedAssemblies.Remove(id);
            OnAssemblyUnloaded?.Invoke(id);
        }
    }
}

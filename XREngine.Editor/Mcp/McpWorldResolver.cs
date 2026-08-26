using XREngine.Rendering;

namespace XREngine.Editor.Mcp
{
    public static class McpWorldResolver
    {
        public static RuntimeWorld? TryGetActiveWorld()
        {
            foreach (var window in RuntimeEngine.Windows)
            {
                if (window?.TargetWorldInstance is IRuntimeRenderWorld renderWorld
                    && renderWorld.WorldContext is RuntimeWorld world)
                    return world;
            }

            return RuntimeWorldRegistryServices.Current?.Snapshot().Values.FirstOrDefault();
        }
    }
}

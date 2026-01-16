using XREngine.Rendering;
using static XREngine.Engine;

namespace XREngine.Editor.Mcp
{
    public static class McpWorldResolver
    {
        public static XRWorldInstance? TryGetActiveWorldInstance()
        {
            foreach (var window in Windows)
            {
                var instance = window?.TargetWorldInstance;
                if (instance is not null)
                    return instance;
            }

            foreach (var instance in XRWorldInstance.WorldInstances.Values)
            {
                if (instance is not null)
                    return instance;
            }

            return null;
        }
    }
}

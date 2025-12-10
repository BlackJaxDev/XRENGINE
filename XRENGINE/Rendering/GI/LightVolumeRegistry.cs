using System.Collections.Generic;
using XREngine.Components.Lights;
using XREngine.Scene;

namespace XREngine.Rendering.GI
{
    /// <summary>
    /// Tracks light volume components per world so render passes can query active data quickly.
    /// </summary>
    internal static class LightVolumeRegistry
    {
        private static readonly Dictionary<XRWorldInstance, List<LightVolumeComponent>> s_perWorld = new();
        private static readonly object s_lock = new();

        public static void Register(XRWorldInstance world, LightVolumeComponent component)
        {
            lock (s_lock)
            {
                if (!s_perWorld.TryGetValue(world, out var list))
                {
                    list = new List<LightVolumeComponent>();
                    s_perWorld[world] = list;
                }

                if (!list.Contains(component))
                    list.Add(component);
            }
        }

        public static void Unregister(XRWorldInstance world, LightVolumeComponent component)
        {
            lock (s_lock)
            {
                if (!s_perWorld.TryGetValue(world, out var list))
                    return;

                list.Remove(component);
                if (list.Count == 0)
                    s_perWorld.Remove(world);
            }
        }

        public static bool TryGetFirstActive(XRWorldInstance world, out LightVolumeComponent? component)
        {
            lock (s_lock)
            {
                if (s_perWorld.TryGetValue(world, out var list))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var candidate = list[i];
                        if (candidate.HasValidVolume)
                        {
                            component = candidate;
                            return true;
                        }
                    }
                }
            }

            component = null;
            return false;
        }
    }
}

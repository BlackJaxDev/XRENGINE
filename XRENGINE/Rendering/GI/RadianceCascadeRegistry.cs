using System.Collections.Generic;
using XREngine.Components.Lights;
using XREngine.Scene;

namespace XREngine.Rendering.GI
{
    /// <summary>
    /// Tracks radiance cascade components per world so render passes can fetch the active data quickly.
    /// </summary>
    internal static class RadianceCascadeRegistry
    {
        private static readonly Dictionary<XRWorldInstance, List<RadianceCascadeComponent>> s_perWorld = [];
        private static readonly object s_lock = new();

        public static void Register(XRWorldInstance world, RadianceCascadeComponent component)
        {
            lock (s_lock)
            {
                if (!s_perWorld.TryGetValue(world, out var list))
                {
                    list = [];
                    s_perWorld[world] = list;
                }

                if (!list.Contains(component))
                    list.Add(component);
            }
        }

        public static void Unregister(XRWorldInstance world, RadianceCascadeComponent component)
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

        public static bool TryGetFirstActive(XRWorldInstance world, out RadianceCascadeComponent? component)
        {
            lock (s_lock)
            {
                if (s_perWorld.TryGetValue(world, out var list))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var candidate = list[i];
                        if (candidate.HasValidCascades)
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
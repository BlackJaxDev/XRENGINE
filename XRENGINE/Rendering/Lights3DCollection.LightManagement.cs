using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        #region Light Management

        public void Clear()
        {
            using var sample = Engine.Profiler.Start("Lights3DCollection.Clear");

            DynamicSpotLights.Clear();
            DynamicPointLights.Clear();
            DynamicDirectionalLights.Clear();
            LightProbes.Clear();

            // cached data derived from probe list
            _cells = null;
            LightProbeTree.Remake(new AABB());
            IBLCaptured = false;
        }

        internal void AddLightProbe(LightProbeComponent probe)
        {
            if (probe is null)
                return;

            if (!LightProbes.Contains(probe))
                LightProbes.Add(probe);
        }

        internal void RemoveLightProbe(LightProbeComponent probe)
        {
            if (probe is null)
                return;

            LightProbes.Remove(probe);
        }

        /// <summary>
        /// Clears cached light/probe lists and repopulates them from the world's current scene graph.
        /// Intended for editor edit/play transitions where the scene graph is reloaded but cached lists persist.
        /// </summary>
        public void RebuildCachesFromWorld()
        {
            Clear();

            // Repopulate caches by walking current root nodes.
            // RootNodes is a render-time cache that includes visible world roots and editor-only hidden roots.
            for (int i = 0; i < World.RootNodes.Count; i++)
            {
                var root = World.RootNodes[i];
                if (root is null)
                    continue;

                foreach (var light in root.FindAllDescendantComponents<DirectionalLightComponent>())
                    if (light.IsActiveInHierarchy && light.Type == ELightType.Dynamic)
                        DynamicDirectionalLights.Add(light);

                foreach (var light in root.FindAllDescendantComponents<SpotLightComponent>())
                    if (light.IsActiveInHierarchy && light.Type == ELightType.Dynamic)
                        DynamicSpotLights.Add(light);

                foreach (var light in root.FindAllDescendantComponents<PointLightComponent>())
                    if (light.IsActiveInHierarchy && light.Type == ELightType.Dynamic)
                        DynamicPointLights.Add(light);

                foreach (var probe in root.FindAllDescendantComponents<LightProbeComponent>())
                    if (probe.IsActiveInHierarchy)
                        AddLightProbe(probe);
            }
        }

        #endregion
    }
}

using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Rendering.Lightmapping;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        #region Buffer Management

        public void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("Lights3DCollection.SwapBuffers");

            bool cullByCameraFrusta = Engine.Rendering.Settings.CullShadowCollectionByCameraFrusta && !Engine.VRState.IsInVR;
            bool gateShadowSwaps = cullByCameraFrusta;

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.DirectionalLights"))
            {
                // Index-based iteration avoids EventList ThreadSafe snapshot allocation.
                for (int i = 0; i < DynamicDirectionalLights.Count; i++)
                {
                    DirectionalLightComponent l = DynamicDirectionalLights[i];
                    if (!gateShadowSwaps || _shadowLightsCollectedThisTick.Contains(l))
                        l.SwapBuffers(LightmapBaking);
                }
            }

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.SpotLights"))
            {
                for (int i = 0; i < DynamicSpotLights.Count; i++)
                {
                    SpotLightComponent l = DynamicSpotLights[i];
                    if (!gateShadowSwaps || _shadowLightsCollectedThisTick.Contains(l))
                        l.SwapBuffers(LightmapBaking);
                }
            }

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.PointLights"))
            {
                for (int i = 0; i < DynamicPointLights.Count; i++)
                {
                    PointLightComponent l = DynamicPointLights[i];
                    if (!gateShadowSwaps || _shadowLightsCollectedThisTick.Contains(l))
                        l.SwapBuffers(LightmapBaking);
                }
            }

            using (Engine.Profiler.Start("WorldInstance.GlobalSwapBuffers.LightmapBaking"))
            {
                LightmapBaking.ProcessManualRequests();
            }

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.SceneCaptures"))
            {
                foreach (SceneCaptureComponentBase sc in CaptureComponents)
                    sc.SwapBuffers();
            }

        }

        #endregion
    }
}

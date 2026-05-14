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
            using var sample = RuntimeEngine.Profiler.Start("Lights3DCollection.SwapBuffers");
            ShadowScratch scratch = CurrentShadowScratch;

            PopulateLocalShadowRelevanceCameras(scratch);
            bool cullByCameraFrusta = RuntimeEngine.Rendering.Settings.CullShadowCollectionByCameraFrusta && !RuntimeEngine.VRState.IsInVR;
            bool gateShadowSwaps = cullByCameraFrusta;

            using (RuntimeEngine.Profiler.Start("Lights3DCollection.SwapBuffers.DirectionalLights"))
            {
                // Index-based iteration avoids EventList ThreadSafe snapshot allocation.
                for (int i = 0; i < DynamicDirectionalLights.Count; i++)
                {
                    DirectionalLightComponent l = DynamicDirectionalLights[i];
                    if (!gateShadowSwaps || _shadowLightsCollectedThisTick.Contains(l))
                        l.SwapBuffers(LightmapBaking);
                }
            }

            using (RuntimeEngine.Profiler.Start("Lights3DCollection.SwapBuffers.SpotLights"))
            {
                for (int i = 0; i < DynamicSpotLights.Count; i++)
                {
                    SpotLightComponent l = DynamicSpotLights[i];
                    if (UpdateLocalShadowRelevanceState(l, scratch) &&
                        (!gateShadowSwaps || _shadowLightsCollectedThisTick.Contains(l)))
                    {
                        l.SwapBuffers(LightmapBaking);
                    }
                }
            }

            using (RuntimeEngine.Profiler.Start("Lights3DCollection.SwapBuffers.PointLights"))
            {
                for (int i = 0; i < DynamicPointLights.Count; i++)
                {
                    PointLightComponent l = DynamicPointLights[i];
                    if (UpdateLocalShadowRelevanceState(l, scratch) &&
                        (!gateShadowSwaps || _shadowLightsCollectedThisTick.Contains(l)))
                    {
                        l.SwapBuffers(LightmapBaking);
                    }
                }
            }

            using (RuntimeEngine.Profiler.Start("WorldInstance.GlobalSwapBuffers.LightmapBaking"))
            {
                LightmapBaking.ProcessManualRequests();
            }

            using (RuntimeEngine.Profiler.Start("Lights3DCollection.SwapBuffers.SceneCaptures"))
            {
                foreach (SceneCaptureComponentBase sc in CaptureComponents)
                    sc.SwapBuffers();
            }

        }

        #endregion
    }
}

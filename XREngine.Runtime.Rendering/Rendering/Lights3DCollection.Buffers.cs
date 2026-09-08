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

            using (RuntimeEngine.Profiler.Start("Lights3DCollection.SwapBuffers.ShadowAtlas"))
            {
                PublishShadowAtlasFrame(collectVisibleNow: false, scratch);
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

        /// <summary>Settles probe writers while this world's renderer context is current.</summary>
        public void PublishCompletedLightProbeOutputs()
        {
            // Buffer swapping has no current OpenGL context. Polling GLsync
            // there can leave a submitted writer pending forever. Publication
            // here is captured by the next immutable global-resource snapshot.
            for (int i = 0; i < LightProbes.Count; i++)
                LightProbes[i].PublishCompletedIblOutput();
        }
    }
}

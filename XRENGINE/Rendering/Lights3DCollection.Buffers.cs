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
                foreach (DirectionalLightComponent l in DynamicDirectionalLights)
                    if (!gateShadowSwaps || _shadowLightsCollectedThisTick.Contains(l))
                        l.SwapBuffers(LightmapBaking);
            }

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.SpotLights"))
            {
                foreach (SpotLightComponent l in DynamicSpotLights)
                    if (!gateShadowSwaps || _shadowLightsCollectedThisTick.Contains(l))
                        l.SwapBuffers(LightmapBaking);
            }

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.PointLights"))
            {
                foreach (PointLightComponent l in DynamicPointLights)
                    if (!gateShadowSwaps || _shadowLightsCollectedThisTick.Contains(l))
                        l.SwapBuffers(LightmapBaking);
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

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.CaptureBags"))
            {
                _captureBagRendering.Clear();
                (_captureBagUpdating, _captureBagRendering) = (_captureBagRendering, _captureBagUpdating);

                double budgetMs = CaptureBudgetMilliseconds;
                _captureBudgetStopwatch.Restart();

                foreach (SceneCaptureComponentBase capture in _captureBagRendering)
                {
                    if (ShouldDeferAuxiliaryCaptures())
                    {
                        _captureQueue.Enqueue(capture);
                        continue;
                    }

                    if (_captureBudgetStopwatch.Elapsed.TotalMilliseconds > budgetMs)
                    {
                        // push remaining work to next frame
                        _captureQueue.Enqueue(capture);
                        continue;
                    }

                    capture.SwapBuffers();
                }
            }
        }

        #endregion
    }
}

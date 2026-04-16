using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        #region Shadow Collection & Culling

        public void CollectVisibleItems()
        {
            using var sample = Engine.Profiler.Start("Lights3DCollection.CollectVisibleItems");

            //CollectingVisibleShadowMaps = true;

            // Cascaded shadow slices must be finalized before directional-light shadow
            // viewports collect and swap their render-command buffers for this frame.
            // If we build the cascades later during RenderShadowMaps(), lighting can
            // switch over to ShadowMapArray while the cascade buffers are still empty.
            PrepareDirectionalShadowMaps();

            _shadowLightsCollectedThisTick.Clear();

            // In VR the active view frusta can change extremely rapidly (late-latched HMD pose), and can
            // also differ from the desktop/mirror camera. Frustum-based shadow collection culling can
            // therefore cause visible shadow caster popping that looks like lighting/material flicker.
            // Prefer stability over CPU savings while in VR.
            bool cullByCameraFrusta = Engine.Rendering.Settings.CullShadowCollectionByCameraFrusta && !Engine.VRState.IsInVR;

            if (cullByCameraFrusta)
            {
                var cameraFrustumScratch = _cameraFrustumScratch;
                cameraFrustumScratch.Clear();
                foreach ((XRWindow window, XRViewport viewport) in Engine.EnumerateActiveWindowViewports())
                {
                    if (!ReferenceEquals(window.TargetWorldInstance, World))
                        continue;

                    if (!ReferenceEquals(viewport.World, World))
                        continue;

                    XRCamera? camera = viewport.ActiveCamera;
                    if (camera is null)
                        continue;

                    float maxDist = camera.ShadowCollectMaxDistance;
                    if (float.IsFinite(maxDist))
                        maxDist = MathF.Min(maxDist, camera.FarZ);
                    else
                        maxDist = camera.FarZ;

                    cameraFrustumScratch.Add((camera.WorldFrustum(), camera.Transform.WorldTranslation, maxDist));
                }

                if (cameraFrustumScratch.Count > 0)
                {
                    CollectRelevantShadowItems(DynamicDirectionalLights, cameraFrustumScratch);
                    CollectRelevantShadowItems(DynamicSpotLights, cameraFrustumScratch);
                    CollectRelevantShadowItems(DynamicPointLights, cameraFrustumScratch);
                }
                else
                {
                    CollectShadowItems(DynamicDirectionalLights);
                    CollectShadowItems(DynamicSpotLights);
                    CollectShadowItems(DynamicPointLights);
                }
            }
            else
            {
                CollectShadowItems(DynamicDirectionalLights);
                CollectShadowItems(DynamicSpotLights);
                CollectShadowItems(DynamicPointLights);
            }

            //CollectingVisibleShadowMaps = false;
        }

        private static bool ShouldCollectShadowItems(LightComponent light)
            => light.IsActiveInHierarchy && light.CastsShadows && light.ShadowMap is not null;

        private void CollectShadowItems<TLight>(IReadOnlyList<TLight> lights) where TLight : LightComponent
        {
            int count = lights.Count;
            for (int i = 0; i < count; i++)
            {
                TLight light = lights[i];
                if (!ShouldCollectShadowItems(light))
                    continue;

                light.CollectVisibleItems();
                _shadowLightsCollectedThisTick.Add(light);
            }
        }

        private void CollectRelevantShadowItems<TLight>(IReadOnlyList<TLight> lights, List<(Frustum Frustum, Vector3 Position, float MaxDistance)> cameras)
            where TLight : LightComponent
        {
            int count = lights.Count;
            for (int i = 0; i < count; i++)
            {
                TLight light = lights[i];
                if (!ShouldCollectShadowItems(light) || !IsLightShadowRelevant(light, cameras))
                    continue;

                light.CollectVisibleItems();
                _shadowLightsCollectedThisTick.Add(light);
            }
        }

        private static bool IsLightShadowRelevant(LightComponent light, List<(Frustum Frustum, Vector3 Position, float MaxDistance)> cameras) => light switch
        {
            DirectionalLightComponent dir => AnyCameraIntersectsDirectional(dir, cameras),
            SpotLightComponent spot => AnyCameraIntersectsSpot(spot, cameras),
            PointLightComponent point => AnyCameraIntersectsPoint(point, cameras),
            _ => true,
        };

        private static bool AnyCameraIntersectsPoint(PointLightComponent light, List<(Frustum Frustum, Vector3 Position, float MaxDistance)> cameras)
        {
            Vector3 center = light.Transform.RenderTranslation;
            float radius = light.Radius;
            Sphere sphere = new(center, radius);

            for (int i = 0; i < cameras.Count; i++)
            {
                var cam = cameras[i];
                if (cam.MaxDistance > 0)
                {
                    float maxDist = cam.MaxDistance + radius;
                    if (Vector3.DistanceSquared(cam.Position, center) > maxDist * maxDist)
                        continue;
                }

                if (cam.Frustum.ContainsSphere(sphere) != EContainment.Disjoint)
                    return true;
            }

            return false;
        }

        private static bool AnyCameraIntersectsSpot(SpotLightComponent light, List<(Frustum Frustum, Vector3 Position, float MaxDistance)> cameras)
        {
            Cone cone = light.OuterCone;

            // Bounding sphere for the cone (centered at cone.Center).
            float halfHeight = cone.Height * 0.5f;
            float boundRadius = MathF.Sqrt((halfHeight * halfHeight) + (cone.Radius * cone.Radius));

            for (int i = 0; i < cameras.Count; i++)
            {
                var cam = cameras[i];
                if (cam.MaxDistance > 0)
                {
                    float maxDist = cam.MaxDistance + boundRadius;
                    if (Vector3.DistanceSquared(cam.Position, cone.Center) > maxDist * maxDist)
                        continue;
                }

                if (cam.Frustum.ContainsCone(cone) != EContainment.Disjoint)
                    return true;
            }

            return false;
        }

        private static bool AnyCameraIntersectsDirectional(DirectionalLightComponent light, List<(Frustum Frustum, Vector3 Position, float MaxDistance)> cameras)
        {
            // Directional shadow volume is represented as a unit box scaled/rotated/translated by LightMeshMatrix.
            Box box = new()
            {
                LocalCenter = Vector3.Zero,
                LocalSize = Vector3.One,
                Transform = light.LightMeshMatrix,
            };

            Vector3 center = light.Transform.RenderTranslation;
            Vector3 halfExtents = light.Scale * 0.5f;
            float boundRadius = halfExtents.Length();

            for (int i = 0; i < cameras.Count; i++)
            {
                var cam = cameras[i];
                if (cam.MaxDistance > 0)
                {
                    float maxDist = cam.MaxDistance + boundRadius;
                    if (Vector3.DistanceSquared(cam.Position, center) > maxDist * maxDist)
                        continue;
                }

                if (cam.Frustum.Contains(box) != EContainment.Disjoint)
                    return true;
            }

            return false;
        }

        #endregion

        #region Shadow Map Rendering

        public void EnsureShadowMapsCurrentForCapture(bool collectVisibleNow)
        {
            if (RenderingShadowMaps)
                return;

            ulong frameId = Engine.Rendering.State.RenderFrameId;
            if (_lastShadowMapsRenderFrameId == frameId)
                return;

            RenderShadowMapsInternal(collectVisibleNow, includeAuxiliaryCaptures: false);
        }

        public void RenderShadowMaps(bool collectVisibleNow)
        {
            using var sample = Engine.Profiler.Start("Lights3DCollection.RenderShadowMaps");
            RenderShadowMapsInternal(collectVisibleNow, includeAuxiliaryCaptures: true);
        }

        private void RenderShadowMapsInternal(bool collectVisibleNow, bool includeAuxiliaryCaptures)
        {
            if (collectVisibleNow)
                PrepareDirectionalShadowMaps();

            RenderingShadowMaps = true;

            // Index-based iteration avoids EventList ThreadSafe snapshot allocation.
            for (int i = 0; i < DynamicDirectionalLights.Count; i++)
                DynamicDirectionalLights[i].RenderShadowMap(collectVisibleNow);
            for (int i = 0; i < DynamicSpotLights.Count; i++)
                DynamicSpotLights[i].RenderShadowMap(collectVisibleNow);
            for (int i = 0; i < DynamicPointLights.Count; i++)
                DynamicPointLights[i].RenderShadowMap(collectVisibleNow);

            RenderingShadowMaps = false;

            _lastShadowMapsRenderFrameId = Engine.Rendering.State.RenderFrameId;

            if (!includeAuxiliaryCaptures)
                return;

            double budgetMs = CaptureBudgetMilliseconds;
            _captureBudgetStopwatch.Restart();

            if (ShouldDeferAuxiliaryCaptures())
                return;

            while (_captureWorkQueue.TryPeek(out _))
            {
                if (_captureBudgetStopwatch.Elapsed.TotalMilliseconds > budgetMs)
                    break;

                if (ShouldDeferAuxiliaryCaptures())
                    break;

                if (!_captureWorkQueue.TryDequeue(out CaptureWorkItem item))
                    break;

                NoteCaptureWorkItemDequeued();

                switch (item.WorkType)
                {
                    case ECaptureWorkType.CubemapFace:
                        if (item.Component is SceneCaptureComponent scc)
                            scc.ExecuteCaptureFace(item.FaceIndex);
                        break;

                    case ECaptureWorkType.CaptureFinalize:
                        if (item.Component is SceneCaptureComponent finScc)
                            finScc.FinalizeCubemapCapture();
                        CompletePendingCapture(item.Component);
                        break;

                    case ECaptureWorkType.FullCapture:
                        item.Component.CollectVisible();
                        item.Component.SwapBuffers();
                        item.Component.Render();
                        CompletePendingCapture(item.Component);
                        break;
                }
            }
        }

        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Shadows;

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
                    if (!ViewportTargetsWorld(window, viewport, World))
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

        private static bool ShouldCollectShadowItems(LightComponent? light)
            => light is not null &&
               light.IsActiveInHierarchy &&
               light.CastsShadows &&
               (light.ShadowMap is not null || UsesShadowAtlasCollectionPath(light));

        private static bool UsesShadowAtlasCollectionPath(LightComponent light)
            => light switch
            {
                SpotLightComponent => Engine.Rendering.Settings.UseSpotShadowAtlas,
                DirectionalLightComponent => Engine.Rendering.Settings.UseDirectionalShadowAtlas,
                _ => false,
            };

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
            try
            {
                UpdateShadowAtlasRequests(collectVisibleNow);

                // Index-based iteration avoids EventList ThreadSafe snapshot allocation.
                for (int i = 0; i < DynamicDirectionalLights.Count; i++)
                {
                    DirectionalLightComponent light = DynamicDirectionalLights[i];
                    if (ShouldRenderLegacyDirectionalShadowMap(light, out bool renderCascades))
                        light.RenderShadowMap(collectVisibleNow, renderCascades);
                }
                for (int i = 0; i < DynamicSpotLights.Count; i++)
                {
                    SpotLightComponent light = DynamicSpotLights[i];
                    if (!Engine.Rendering.Settings.UseSpotShadowAtlas || ShouldRenderLegacySpotShadowMap(light))
                        light.RenderShadowMap(collectVisibleNow);
                }
                for (int i = 0; i < DynamicPointLights.Count; i++)
                    DynamicPointLights[i].RenderShadowMap(collectVisibleNow);
            }
            finally
            {
                RenderingShadowMaps = false;
            }

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

        #region Shadow Atlas Requests

        private void UpdateShadowAtlasRequests(bool collectVisibleNow)
        {
            using var sample = Engine.Profiler.Start("Lights3DCollection.UpdateShadowAtlasRequests");

            PopulateShadowAtlasActiveCameras();
            ShadowAtlas.BeginFrame(World, CollectionsMarshal.AsSpan(_shadowAtlasCameraScratch));

            SubmitDirectionalShadowAtlasRequests();
            SubmitSpotShadowAtlasRequests();
            SubmitPointShadowAtlasRequests();

            ShadowAtlas.SolveAllocations();
            ShadowAtlas.RenderScheduledTiles(collectVisibleNow);
            ShadowAtlas.PublishFrameData();
            PublishShadowAtlasDiagnostics();
        }

        private void PopulateShadowAtlasActiveCameras()
        {
            _shadowAtlasCameraScratch.Clear();

            foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                AddShadowAtlasCamera(viewport);

            AddShadowAtlasCamera(Engine.VRState.LeftEyeViewport);
            AddShadowAtlasCamera(Engine.VRState.RightEyeViewport);
        }

        private void AddShadowAtlasCamera(XRViewport? viewport)
        {
            if (viewport is null ||
                !ViewportTargetsWorld(viewport, World) ||
                viewport.Suppress3DSceneRendering ||
                viewport.ActiveCamera is not XRCamera camera)
                return;

            for (int i = 0; i < _shadowAtlasCameraScratch.Count; i++)
                if (ReferenceEquals(_shadowAtlasCameraScratch[i], camera))
                    return;

            _shadowAtlasCameraScratch.Add(camera);
        }

        private void SubmitDirectionalShadowAtlasRequests()
        {
            if (!Engine.Rendering.Settings.UseDirectionalShadowAtlas)
                return;

            int count = DynamicDirectionalLights.Count;
            for (int i = 0; i < count; i++)
            {
                DirectionalLightComponent light = DynamicDirectionalLights[i];
                if (!ShouldSubmitShadowAtlasRequest(light))
                    continue;

                int activeCascadeCount = light.ActiveCascadeCount;
                if (light.EnableCascadedShadows)
                {
                    for (int cascadeIndex = 0; cascadeIndex < activeCascadeCount; cascadeIndex++)
                    {
                        XRCamera? cascadeCamera = light.GetCascadeCamera(cascadeIndex);
                        if (cascadeCamera is null)
                            continue;

                        SubmitShadowAtlasRequest(
                            light,
                            EShadowProjectionType.DirectionalCascade,
                            cascadeIndex,
                            cascadeCamera,
                            priority: 10000.0f - cascadeIndex * 100.0f,
                            fallback: ShadowFallbackMode.StaleTile);
                    }
                }

                bool submitPrimary = !light.EnableCascadedShadows ||
                    activeCascadeCount <= 0 ||
                    NeedsPrimaryDirectionalShadowMap();
                if (submitPrimary && light.ShadowCamera is XRCamera primaryCamera)
                {
                    SubmitShadowAtlasRequest(
                        light,
                        EShadowProjectionType.DirectionalPrimary,
                        faceOrCascadeIndex: 0,
                        primaryCamera,
                        priority: 9000.0f,
                        fallback: ShadowFallbackMode.Legacy);
                }
            }
        }

        private void SubmitSpotShadowAtlasRequests()
        {
            if (!Engine.Rendering.Settings.UseSpotShadowAtlas)
                return;

            int count = DynamicSpotLights.Count;
            for (int i = 0; i < count; i++)
            {
                SpotLightComponent light = DynamicSpotLights[i];
                if (!ShouldSubmitShadowAtlasRequest(light) || light.ShadowCamera is not XRCamera camera)
                    continue;

                SubmitShadowAtlasRequest(
                    light,
                    EShadowProjectionType.SpotPrimary,
                    faceOrCascadeIndex: 0,
                    camera,
                    priority: 2000.0f + EstimateSpotPriority(light),
                    fallback: ShadowFallbackMode.Legacy);
            }
        }

        private void SubmitPointShadowAtlasRequests()
        {
            // Point atlas rendering is not implemented yet; point lights continue to
            // use their cubemap shadow path until the atlas renderer supports faces.
        }

        private void SubmitShadowAtlasRequest(
            LightComponent light,
            EShadowProjectionType projectionType,
            int faceOrCascadeIndex,
            XRCamera camera,
            float priority,
            ShadowFallbackMode fallback)
        {
            EShadowMapEncoding encoding = EShadowMapEncoding.Depth;
            ShadowRequestKey key = light.CreateShadowRequestKey(projectionType, faceOrCascadeIndex, encoding);
            Matrix4x4 view = camera.Transform.InverseRenderMatrix;
            Matrix4x4 projection = camera.ProjectionMatrix;
            float projNear = MathF.Max(0.0f, camera.NearZ);
            float projFar = MathF.Max(camera.FarZ, camera.NearZ + 0.001f);
            uint desiredResolution = GetDesiredShadowAtlasResolution(light);
            uint minimumResolution = GetMinimumShadowAtlasResolution(desiredResolution);
            ulong contentHash = BuildShadowContentHash(light, projectionType, faceOrCascadeIndex, encoding, view, projNear, projFar);
            bool canReusePreviousFrame = light.Type != ELightType.Dynamic;
            bool isDirty = !ShadowAtlas.PublishedFrameData.TryGetAllocation(key, out ShadowAtlasAllocation previous) ||
                previous.ContentVersion != contentHash ||
                !canReusePreviousFrame ||
                !previous.IsResident;

            ShadowMapRequest request = new(
                key,
                light,
                projectionType,
                encoding,
                ShadowCasterFilterMode.Opaque,
                fallback,
                faceOrCascadeIndex,
                view,
                projection,
                MathF.Max(0.0f, camera.NearZ),
                MathF.Max(camera.FarZ, camera.NearZ + 0.001f),
                desiredResolution,
                minimumResolution,
                priority,
                contentHash,
                isDirty,
                canReusePreviousFrame,
                EditorPinned: false,
                StereoVis: Engine.VRState.IsInVR ? StereoVisibility.BothEyes : StereoVisibility.Mono);

            ShadowAtlas.Submit(request);
        }

        private static bool ShouldSubmitShadowAtlasRequest(LightComponent light)
            => light is { IsActiveInHierarchy: true, CastsShadows: true };

        private bool ShouldRenderLegacySpotShadowMap(SpotLightComponent light)
        {
            if (light.ShadowMap is null)
                return false;

            if (!TryGetSpotShadowAtlasAllocation(light, out ShadowAtlasAllocation allocation, out _))
                return true;

            return !allocation.IsResident ||
                allocation.LastRenderedFrame == 0u ||
                allocation.ActiveFallback == ShadowFallbackMode.Legacy;
        }

        private bool ShouldRenderLegacyDirectionalShadowMap(DirectionalLightComponent light, out bool renderCascades)
        {
            renderCascades = true;
            if (!Engine.Rendering.Settings.UseDirectionalShadowAtlas)
                return true;

            int activeCascadeCount = light.ActiveCascadeCount;
            bool needsPrimaryShadowMap = !light.EnableCascadedShadows ||
                activeCascadeCount <= 0 ||
                NeedsPrimaryDirectionalShadowMap();
            bool needsLegacyCascades = light.EnableCascadedShadows &&
                activeCascadeCount > 0 &&
                !AreDirectionalCascadeAtlasTilesReady(light, activeCascadeCount);
            bool needsLegacyPrimary = needsPrimaryShadowMap &&
                !IsDirectionalPrimaryAtlasTileReady(light);

            renderCascades = needsLegacyCascades;
            return (needsLegacyPrimary && light.ShadowMap is not null) || needsLegacyCascades;
        }

        private bool AreDirectionalCascadeAtlasTilesReady(DirectionalLightComponent light, int activeCascadeCount)
        {
            for (int cascadeIndex = 0; cascadeIndex < activeCascadeCount; cascadeIndex++)
            {
                if (!TryGetDirectionalCascadeShadowAtlasAllocation(light, cascadeIndex, out ShadowAtlasAllocation allocation, out _) ||
                    !allocation.IsResident ||
                    allocation.LastRenderedFrame == 0u ||
                    allocation.ActiveFallback != ShadowFallbackMode.None)
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsDirectionalPrimaryAtlasTileReady(DirectionalLightComponent light)
            => TryGetDirectionalPrimaryShadowAtlasAllocation(light, out ShadowAtlasAllocation allocation, out _) &&
               allocation.IsResident &&
               allocation.LastRenderedFrame != 0u &&
               allocation.ActiveFallback == ShadowFallbackMode.None;

        private static bool ViewportTargetsWorld(XRViewport viewport, IRuntimeRenderWorld world)
            => ReferenceEquals(viewport.World, world) ||
               ReferenceEquals(viewport.Window?.TargetWorldInstance, world);

        private static bool ViewportTargetsWorld(XRWindow window, XRViewport viewport, IRuntimeRenderWorld world)
            => ReferenceEquals(viewport.World, world) ||
               ReferenceEquals(window.TargetWorldInstance, world);

        private uint GetDesiredShadowAtlasResolution(LightComponent light)
        {
            uint desired = Math.Max(light.ShadowMapResolutionWidth, light.ShadowMapResolutionHeight);
            desired = Math.Max(desired, ShadowAtlas.Settings.MinTileResolution);
            desired = Math.Min(desired, ShadowAtlas.Settings.MaxTileResolution);
            desired = Math.Min(desired, ShadowAtlas.Settings.PageSize);
            return desired;
        }

        private uint GetMinimumShadowAtlasResolution(uint desiredResolution)
            => Math.Min(desiredResolution, ShadowAtlas.Settings.MinTileResolution);

        private float EstimateSpotPriority(SpotLightComponent light)
        {
            Cone cone = light.OuterCone;
            float halfHeight = cone.Height * 0.5f;
            float radius = MathF.Sqrt((halfHeight * halfHeight) + (cone.Radius * cone.Radius));
            return EstimateLocalPriority(cone.Center, radius, light.Brightness);
        }

        private float EstimatePointPriority(PointLightComponent light)
            => EstimateLocalPriority(light.Transform.RenderTranslation, light.Radius, light.Brightness);

        private float EstimateLocalPriority(Vector3 center, float radius, float intensity)
        {
            if (_shadowAtlasCameraScratch.Count == 0)
                return MathF.Max(1.0f, intensity);

            float best = 0.0f;
            float safeRadius = MathF.Max(radius, 0.001f);
            for (int i = 0; i < _shadowAtlasCameraScratch.Count; i++)
            {
                XRCamera camera = _shadowAtlasCameraScratch[i];
                float distance = MathF.Max(0.001f, Vector3.Distance(camera.Transform.RenderTranslation, center) - safeRadius);
                float score = safeRadius / distance;
                best = MathF.Max(best, score);
            }

            return MathF.Max(1.0f, best * MathF.Max(0.001f, intensity) * 1000.0f);
        }

        private void PublishShadowAtlasDiagnostics()
        {
            ShadowAtlasFrameData frameData = ShadowAtlas.PublishedFrameData;
            for (int i = 0; i < DynamicDirectionalLights.Count; i++)
            {
                DynamicDirectionalLights[i].ClearCascadeAtlasSlots();
                DynamicDirectionalLights[i].SetShadowAtlasDiagnostic(default);
            }

            for (int i = 0; i < DynamicSpotLights.Count; i++)
                DynamicSpotLights[i].SetShadowAtlasDiagnostic(default);

            for (int i = 0; i < DynamicPointLights.Count; i++)
                DynamicPointLights[i].SetShadowAtlasDiagnostic(default);

            for (int i = 0; i < ShadowAtlas.Requests.Count; i++)
            {
                ShadowMapRequest request = ShadowAtlas.Requests[i];
                if (!frameData.TryGetAllocationIndex(request.Key, out int recordIndex, out ShadowAtlasAllocation allocation))
                    continue;

                AccumulateShadowAtlasDiagnostic(request.Light, request, allocation, recordIndex, frameData.FrameId);
            }
        }

        private static void AccumulateShadowAtlasDiagnostic(
            LightComponent light,
            ShadowMapRequest request,
            ShadowAtlasAllocation allocation,
            int recordIndex,
            ulong frameId)
        {
            ShadowRequestDiagnostic previous = light.ShadowAtlasDiagnostic;
            if (previous.LastFrameId != frameId)
                previous = default;
            int previousRecordIndex = previous.LastFrameId == frameId ? previous.ShadowRecordIndex : -1;

            int requestCount = previous.RequestCount + 1;
            int residentCount = previous.ResidentCount + (allocation.IsResident ? 1 : 0);
            uint maxRequested = Math.Max(previous.MaxRequestedResolution, request.DesiredResolution);
            uint maxAllocated = Math.Max(previous.MaxAllocatedResolution, allocation.Resolution);
            float highestPriority = requestCount == 1
                ? request.Priority
                : MathF.Max(previous.HighestPriority, request.Priority);
            SkipReason lastSkip = allocation.SkipReason != SkipReason.None
                ? allocation.SkipReason
                : previous.LastSkipReason;
            ShadowFallbackMode fallback = allocation.ActiveFallback != ShadowFallbackMode.None
                ? allocation.ActiveFallback
                : previous.ActiveFallback;

            int shadowRecordIndex =
                request.ProjectionType == EShadowProjectionType.SpotPrimary ||
                request.ProjectionType == EShadowProjectionType.DirectionalPrimary ||
                request.ProjectionType == EShadowProjectionType.DirectionalCascade
                    ? recordIndex
                    : previousRecordIndex;

            if (request.Light is DirectionalLightComponent directionalLight)
            {
                if (request.ProjectionType == EShadowProjectionType.DirectionalCascade)
                {
                    directionalLight.SetCascadeAtlasSlot(
                        request.FaceOrCascadeIndex,
                        allocation,
                        recordIndex,
                        request.NearPlane,
                        request.FarPlane);
                }
                else if (request.ProjectionType == EShadowProjectionType.DirectionalPrimary)
                {
                    directionalLight.SetPrimaryAtlasSlot(
                        allocation,
                        recordIndex,
                        request.NearPlane,
                        request.FarPlane);
                }
            }

            light.SetShadowAtlasDiagnostic(new ShadowRequestDiagnostic(
                requestCount,
                residentCount,
                maxRequested,
                maxAllocated,
                highestPriority,
                lastSkip,
                fallback,
                shadowRecordIndex,
                allocation.PageIndex,
                allocation.PixelRect,
                allocation.InnerPixelRect,
                allocation.LastRenderedFrame,
                frameId));
        }

        internal bool TryGetSpotShadowAtlasAllocation(
            SpotLightComponent light,
            out ShadowAtlasAllocation allocation,
            out int shadowRecordIndex)
        {
            ShadowRequestKey key = light.CreateShadowRequestKey(EShadowProjectionType.SpotPrimary, 0, EShadowMapEncoding.Depth);
            if (ShadowAtlas.PublishedFrameData.TryGetAllocationIndex(key, out shadowRecordIndex, out allocation))
                return true;

            allocation = default;
            shadowRecordIndex = -1;
            return false;
        }

        internal bool TryGetResidentSpotShadowAtlasTexture(
            SpotLightComponent light,
            out XRTexture2D texture,
            out ShadowAtlasAllocation allocation,
            out int shadowRecordIndex)
        {
            if (Engine.Rendering.Settings.UseSpotShadowAtlas &&
                TryGetSpotShadowAtlasAllocation(light, out allocation, out shadowRecordIndex) &&
                allocation.IsResident &&
                allocation.LastRenderedFrame != 0u &&
                ShadowAtlas.TryGetPageTexture(EShadowMapEncoding.Depth, allocation.PageIndex, out texture))
            {
                return true;
            }

            texture = null!;
            allocation = default;
            shadowRecordIndex = -1;
            return false;
        }

        internal bool TryGetDirectionalCascadeShadowAtlasAllocation(
            DirectionalLightComponent light,
            int cascadeIndex,
            out ShadowAtlasAllocation allocation,
            out int shadowRecordIndex)
        {
            ShadowRequestKey key = light.CreateShadowRequestKey(EShadowProjectionType.DirectionalCascade, cascadeIndex, EShadowMapEncoding.Depth);
            if (ShadowAtlas.PublishedFrameData.TryGetAllocationIndex(key, out shadowRecordIndex, out allocation))
                return true;

            allocation = default;
            shadowRecordIndex = -1;
            return false;
        }

        internal bool TryGetDirectionalPrimaryShadowAtlasAllocation(
            DirectionalLightComponent light,
            out ShadowAtlasAllocation allocation,
            out int shadowRecordIndex)
        {
            ShadowRequestKey key = light.CreateShadowRequestKey(EShadowProjectionType.DirectionalPrimary, 0, EShadowMapEncoding.Depth);
            if (ShadowAtlas.PublishedFrameData.TryGetAllocationIndex(key, out shadowRecordIndex, out allocation))
                return true;

            allocation = default;
            shadowRecordIndex = -1;
            return false;
        }

        private static ulong BuildShadowContentHash(
            LightComponent light,
            EShadowProjectionType projectionType,
            int faceOrCascadeIndex,
            EShadowMapEncoding encoding,
            in Matrix4x4 view,
            float projectionNear,
            float projectionFar)
        {
            ulong hash = 14695981039346656037UL;
            AddGuid(ref hash, light.ID);
            Add(ref hash, (uint)projectionType);
            Add(ref hash, (uint)faceOrCascadeIndex);
            Add(ref hash, (uint)encoding);
            Add(ref hash, light.MovementVersion);
            Add(ref hash, light.ShadowMapResolutionWidth);
            Add(ref hash, light.ShadowMapResolutionHeight);
            Add(ref hash, (uint)light.SoftShadowMode);
            AddFloat(ref hash, light.ShadowMinBias);
            AddFloat(ref hash, light.ShadowMaxBias);
            AddMatrix(ref hash, view);
            AddFloat(ref hash, projectionNear);
            AddFloat(ref hash, projectionFar);
            return hash;
        }

        private static void AddGuid(ref ulong hash, Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            value.TryWriteBytes(bytes);
            for (int i = 0; i < bytes.Length; i++)
                AddByte(ref hash, bytes[i]);
        }

        private static void AddMatrix(ref ulong hash, in Matrix4x4 value)
        {
            AddFloat(ref hash, value.M11); AddFloat(ref hash, value.M12); AddFloat(ref hash, value.M13); AddFloat(ref hash, value.M14);
            AddFloat(ref hash, value.M21); AddFloat(ref hash, value.M22); AddFloat(ref hash, value.M23); AddFloat(ref hash, value.M24);
            AddFloat(ref hash, value.M31); AddFloat(ref hash, value.M32); AddFloat(ref hash, value.M33); AddFloat(ref hash, value.M34);
            AddFloat(ref hash, value.M41); AddFloat(ref hash, value.M42); AddFloat(ref hash, value.M43); AddFloat(ref hash, value.M44);
        }

        private static void AddFloat(ref ulong hash, float value)
            => Add(ref hash, BitConverter.SingleToUInt32Bits(value));

        private static void Add(ref ulong hash, uint value)
        {
            AddByte(ref hash, (byte)value);
            AddByte(ref hash, (byte)(value >> 8));
            AddByte(ref hash, (byte)(value >> 16));
            AddByte(ref hash, (byte)(value >> 24));
        }

        private static void AddByte(ref ulong hash, byte value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        #endregion
    }
}

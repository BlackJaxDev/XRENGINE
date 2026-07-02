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

        private const float LocalShadowMovementFreshSeconds = 0.25f;

        public void CollectVisibleItems()
        {
            using var sample = RuntimeEngine.Profiler.Start("Lights3DCollection.CollectVisibleItems");
            ShadowScratch scratch = CurrentShadowScratch;

            //CollectingVisibleShadowMaps = true;

            // Cascaded shadow slices must be finalized before directional-light shadow
            // viewports collect and swap their render-command buffers for this frame.
            // If we build the cascades later during RenderShadowMaps(), lighting can
            // switch over to ShadowMapArray while the cascade buffers are still empty.
            PrepareDirectionalShadowMaps();
            PopulateLocalShadowRelevanceCameras(scratch);

            _shadowLightsCollectedThisTick.Clear();

            // In VR the active view frusta can change extremely rapidly (late-latched HMD pose), and can
            // also differ from the desktop/mirror camera. Frustum-based shadow collection culling can
            // therefore cause visible shadow caster popping that looks like lighting/material flicker.
            // Prefer stability over CPU savings while in VR.
            bool cullByCameraFrusta = RuntimeEngine.Rendering.Settings.CullShadowCollectionByCameraFrusta && !RuntimeEngine.VRState.IsInVR;

            if (cullByCameraFrusta)
            {
                var cameraFrustumScratch = scratch.CameraFrusta;
                cameraFrustumScratch.Clear();
                foreach ((XRWindow window, XRViewport viewport) in RuntimeEngine.EnumerateActiveWindowViewports())
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
                    CollectRelevantShadowItems(DynamicDirectionalLights, cameraFrustumScratch, scratch);
                    CollectRelevantShadowItems(DynamicSpotLights, cameraFrustumScratch, scratch);
                    CollectRelevantShadowItems(DynamicPointLights, cameraFrustumScratch, scratch);
                }
                else
                {
                    CollectShadowItems(DynamicDirectionalLights, scratch);
                    CollectShadowItems(DynamicSpotLights, scratch);
                    CollectShadowItems(DynamicPointLights, scratch);
                }
            }
            else
            {
                CollectShadowItems(DynamicDirectionalLights, scratch);
                CollectShadowItems(DynamicSpotLights, scratch);
                CollectShadowItems(DynamicPointLights, scratch);
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
                SpotLightComponent spot => spot.UsesSpotShadowAtlasForCurrentEncoding,
                DirectionalLightComponent directional => directional.UsesDirectionalShadowAtlasForCurrentEncoding,
                PointLightComponent point => point.UsesPointShadowAtlasForCurrentEncoding,
                _ => false,
            };

        private void CollectShadowItems<TLight>(IReadOnlyList<TLight> lights, ShadowScratch scratch) where TLight : LightComponent
        {
            int count = lights.Count;
            for (int i = 0; i < count; i++)
            {
                TLight light = lights[i];
                if (!ShouldCollectShadowItems(light))
                    continue;

                if (!UpdateLocalShadowRelevanceState(light, scratch))
                    continue;

                light.CollectVisibleItems();
                _shadowLightsCollectedThisTick.Add(light);
            }
        }

        private void CollectRelevantShadowItems<TLight>(
            IReadOnlyList<TLight> lights,
            List<(Frustum Frustum, Vector3 Position, float MaxDistance)> cameras,
            ShadowScratch scratch)
            where TLight : LightComponent
        {
            int count = lights.Count;
            for (int i = 0; i < count; i++)
            {
                TLight light = lights[i];
                if (!ShouldCollectShadowItems(light) ||
                    !IsLightShadowRelevant(light, cameras) ||
                    !UpdateLocalShadowRelevanceState(light, scratch))
                {
                    continue;
                }

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

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (_lastShadowMapsRenderFrameId == frameId)
                return;

            RenderShadowMapsInternal(collectVisibleNow, includeAuxiliaryCaptures: false);
        }

        public void RenderShadowMaps(bool collectVisibleNow)
        {
            using var sample = RuntimeEngine.Profiler.Start("Lights3DCollection.RenderShadowMaps");
            RenderShadowMapsInternal(collectVisibleNow, includeAuxiliaryCaptures: true);
        }

        private void RenderShadowMapsInternal(bool collectVisibleNow, bool includeAuxiliaryCaptures)
        {
            ShadowScratch scratch = CurrentShadowScratch;

            if (collectVisibleNow)
                PrepareDirectionalShadowMaps();

            RenderingShadowMaps = true;
            try
            {
                UpdateShadowAtlasRequests(collectVisibleNow, scratch);

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
                    bool relevant = IsSpotShadowRelevant(light, scratch);
                    light.SetShadowRelevance(relevant, RuntimeEngine.Rendering.State.RenderFrameId);
                    if (relevant && (!light.UsesSpotShadowAtlasForCurrentEncoding || ShouldRenderLegacySpotShadowMap(light)))
                        light.RenderShadowMap(collectVisibleNow);
                }
                for (int i = 0; i < DynamicPointLights.Count; i++)
                {
                    PointLightComponent light = DynamicPointLights[i];
                    int faceMask = CalculatePointShadowFaceMask(light, scratch);
                    light.SetShadowFaceRelevanceMask(faceMask, RuntimeEngine.Rendering.State.RenderFrameId);
                    if (!light.UsesPointShadowAtlasForCurrentEncoding)
                        light.RenderShadowMap(collectVisibleNow);
                }
            }
            finally
            {
                RenderingShadowMaps = false;
            }

            _lastShadowMapsRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;

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

        private void UpdateShadowAtlasRequests(bool collectVisibleNow, ShadowScratch scratch)
        {
            using var sample = RuntimeEngine.Profiler.Start("Lights3DCollection.UpdateShadowAtlasRequests");

            PopulateShadowAtlasActiveCameras(scratch);
            PrepareLocalShadowRelevanceFrusta(scratch);
            ShadowAtlas.BeginFrame(World, CollectionsMarshal.AsSpan(scratch.ShadowAtlasCameras));

            SubmitDirectionalShadowAtlasRequests();
            SubmitSpotShadowAtlasRequests(scratch);
            SubmitPointShadowAtlasRequests(scratch);

            ShadowAtlas.SolveAllocations();
            ShadowAtlas.RenderScheduledTiles(collectVisibleNow);
            ShadowAtlas.PublishFrameData();
            PublishShadowAtlasDiagnostics();
            LogShadowAtlasFrameSummary(collectVisibleNow, scratch);
        }

        private void PopulateShadowAtlasActiveCameras(ShadowScratch scratch)
        {
            List<XRCamera> cameras = scratch.ShadowAtlasCameras;
            cameras.Clear();

            foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports())
                AddShadowAtlasCamera(viewport, cameras);

            AddShadowAtlasCamera(RuntimeEngine.VRState.LeftEyeViewport, cameras);
            AddShadowAtlasCamera(RuntimeEngine.VRState.RightEyeViewport, cameras);
        }

        private void AddShadowAtlasCamera(XRViewport? viewport, List<XRCamera> cameras)
        {
            if (viewport is null ||
                !ViewportTargetsWorld(viewport, World) ||
                viewport.Suppress3DSceneRendering ||
                viewport.ActiveCamera is not XRCamera camera)
                return;

            for (int i = 0; i < cameras.Count; i++)
                if (ReferenceEquals(cameras[i], camera))
                    return;

            cameras.Add(camera);
        }

        private void PopulateLocalShadowRelevanceCameras(ShadowScratch scratch)
        {
            PopulateShadowAtlasActiveCameras(scratch);
            PrepareLocalShadowRelevanceFrusta(scratch);
        }

        private void PrepareLocalShadowRelevanceFrusta(ShadowScratch scratch)
        {
            List<PreparedFrustum> frusta = scratch.LocalShadowRelevanceFrusta;
            List<XRCamera> cameras = scratch.ShadowAtlasCameras;
            frusta.Clear();

            for (int i = 0; i < cameras.Count; i++)
            {
                try
                {
                    frusta.Add(cameras[i].WorldFrustum().Prepare());
                }
                catch
                {
                    frusta.Clear();
                    return;
                }
            }
        }

        private static ShadowRelevanceCameraSet CurrentLocalShadowRelevanceCameras(ShadowScratch scratch)
            => new(scratch.LocalShadowRelevanceFrusta);

        private bool UpdateLocalShadowRelevanceState(LightComponent light, ShadowScratch scratch)
        {
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (light is PointLightComponent pointLight)
            {
                int faceMask = CalculatePointShadowFaceMask(pointLight, scratch);
                pointLight.SetShadowFaceRelevanceMask(faceMask, frameId);
                return faceMask != 0;
            }

            if (light is SpotLightComponent spotLight)
            {
                bool relevant = IsSpotShadowRelevant(spotLight, scratch);
                spotLight.SetShadowRelevance(relevant, frameId);
                return relevant;
            }

            return true;
        }

        private int CalculatePointShadowFaceMask(PointLightComponent light, ShadowScratch scratch)
        {
            if (scratch.LocalShadowRelevanceFrusta.Count <= 0)
                return LocalShadowFrustumRelevance.AllPointFacesMask;

            ShadowRelevanceCameraSet cameras = CurrentLocalShadowRelevanceCameras(scratch);
            int mask = 0;
            for (int faceIndex = 0; faceIndex < PointLightComponent.ShadowFaceCount; faceIndex++)
            {
                if (LocalShadowFrustumRelevance.IsPointFaceRelevant(
                    light,
                    faceIndex,
                    cameras,
                    scratch.LocalShadowIntersections))
                {
                    mask |= 1 << faceIndex;
                }
            }

            return mask;
        }

        private bool IsSpotShadowRelevant(SpotLightComponent light, ShadowScratch scratch)
        {
            ShadowRelevanceCameraSet cameras = CurrentLocalShadowRelevanceCameras(scratch);
            return LocalShadowFrustumRelevance.IsSpotShadowRelevant(light, cameras, scratch.LocalShadowIntersections);
        }

        private void SubmitDirectionalShadowAtlasRequests()
        {
            if (!RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas)
                return;

            int count = DynamicDirectionalLights.Count;
            for (int i = 0; i < count; i++)
            {
                DirectionalLightComponent light = DynamicDirectionalLights[i];
                if (!light.UsesDirectionalShadowAtlasForCurrentEncoding ||
                    !ShouldSubmitShadowAtlasRequest(light))
                    continue;

                ShadowMapFormatSelection shadowFormat = light.ResolveShadowMapFormat(preferredStorageFormat: null);
                EShadowMapEncoding encoding = shadowFormat.Encoding;
                SubmitDirectionalCascadeShadowAtlasRequests(light, ShadowRequestSource.Desktop, encoding);
                SubmitDirectionalCascadeShadowAtlasRequests(light, ShadowRequestSource.Hmd, encoding);

                bool hasAnyDirectionalCascades =
                    light.HasPublishedCascades(ShadowRequestSource.Desktop) ||
                    light.HasPublishedCascades(ShadowRequestSource.Hmd);
                bool submitPrimary = !light.EnableCascadedShadows ||
                    !hasAnyDirectionalCascades ||
                    NeedsPrimaryDirectionalShadowMap();
                if (submitPrimary && light.ShadowCamera is XRCamera primaryCamera)
                {
                    SubmitShadowAtlasRequest(
                        light,
                        EShadowProjectionType.DirectionalPrimary,
                        faceOrCascadeIndex: 0,
                        primaryCamera,
                        priority: 9000.0f,
                        fallback: ShadowFallbackMode.StaleTile,
                        encoding: encoding);
                }
            }
        }

        private void SubmitDirectionalCascadeShadowAtlasRequests(
            DirectionalLightComponent light,
            ShadowRequestSource source,
            EShadowMapEncoding encoding)
        {
            int activeCascadeCount = light.GetActiveCascadeCount(source);
            bool useCascadeAtlas = light.EnableCascadedShadows &&
                light.CanUseDirectionalCascadeShadowAtlasForCurrentBackend(activeCascadeCount);
            if (!useCascadeAtlas)
                return;

            for (int cascadeIndex = 0; cascadeIndex < activeCascadeCount; cascadeIndex++)
            {
                XRCamera? cascadeCamera = light.GetCascadeCamera(source, cascadeIndex);
                if (cascadeCamera is null)
                    continue;

                SubmitShadowAtlasRequest(
                    light,
                    EShadowProjectionType.DirectionalCascade,
                    cascadeIndex,
                    cascadeCamera,
                    priority: 10000.0f - cascadeIndex * 100.0f + (source == ShadowRequestSource.Hmd ? 25.0f : 0.0f),
                    fallback: ShadowFallbackMode.StaleTile,
                    encoding: encoding,
                    source: source);
            }
        }

        private void SubmitSpotShadowAtlasRequests(ShadowScratch scratch)
        {
            if (!RuntimeEngine.Rendering.Settings.UseSpotShadowAtlas)
                return;

            int count = DynamicSpotLights.Count;
            for (int i = 0; i < count; i++)
            {
                SpotLightComponent light = DynamicSpotLights[i];
                if (!light.UsesSpotShadowAtlasForCurrentEncoding ||
                    !ShouldSubmitShadowAtlasRequest(light) ||
                    light.ShadowCamera is not XRCamera camera)
                {
                    continue;
                }

                bool relevant = IsSpotShadowRelevant(light, scratch);
                light.SetShadowRelevance(relevant, RuntimeEngine.Rendering.State.RenderFrameId);
                SubmitShadowAtlasRequest(
                    light,
                    EShadowProjectionType.SpotPrimary,
                    faceOrCascadeIndex: 0,
                    camera,
                    priority: 4000.0f + EstimateSpotPriority(light, scratch),
                    fallback: ShadowFallbackMode.StaleTile,
                    forcedSkipReason: relevant ? SkipReason.None : SkipReason.NotRelevant);
            }
        }

        private void SubmitPointShadowAtlasRequests(ShadowScratch scratch)
        {
            if (!RuntimeEngine.Rendering.Settings.UsePointShadowAtlas)
                return;

            int count = DynamicPointLights.Count;
            for (int i = 0; i < count; i++)
            {
                PointLightComponent light = DynamicPointLights[i];
                if (!light.UsesPointShadowAtlasForCurrentEncoding ||
                    !ShouldSubmitShadowAtlasRequest(light))
                    continue;

                int faceMask = CalculatePointShadowFaceMask(light, scratch);
                light.SetShadowFaceRelevanceMask(faceMask, RuntimeEngine.Rendering.State.RenderFrameId);
                float basePriority = 3000.0f + EstimatePointPriority(light, scratch);
                for (int faceIndex = 0; faceIndex < PointLightComponent.ShadowFaceCount; faceIndex++)
                {
                    if (!light.TryGetShadowFaceCamera(faceIndex, out XRCamera faceCamera))
                        continue;

                    bool faceRelevant = (faceMask & (1 << faceIndex)) != 0;
                    float faceRelevance = EstimatePointFaceRelevance(light, faceIndex, scratch);
                    uint desiredResolution = GetDesiredPointFaceShadowAtlasResolution(light, faceRelevance);
                    SubmitShadowAtlasRequest(
                        light,
                        EShadowProjectionType.PointFace,
                        faceIndex,
                        faceCamera,
                        priority: basePriority + faceRelevance * 750.0f - faceIndex * 0.01f,
                        fallback: ShadowFallbackMode.StaleTile,
                        desiredResolutionOverride: desiredResolution,
                        forcedSkipReason: faceRelevant ? SkipReason.None : SkipReason.NotRelevant);
                }
            }
        }

        private void SubmitShadowAtlasRequest(
            LightComponent light,
            EShadowProjectionType projectionType,
            int faceOrCascadeIndex,
            XRCamera camera,
            float priority,
            ShadowFallbackMode fallback,
            EShadowMapEncoding encoding = EShadowMapEncoding.Depth,
            uint? desiredResolutionOverride = null,
            SkipReason forcedSkipReason = SkipReason.None,
            ShadowRequestSource source = ShadowRequestSource.Default)
        {
            ShadowRequestKey key = light.CreateShadowRequestKey(projectionType, faceOrCascadeIndex, encoding, source: source);
            Matrix4x4 view = camera.Transform.InverseRenderMatrix;
            Matrix4x4 projection = camera.ProjectionMatrix;
            float projNear = MathF.Max(0.0f, camera.NearZ);
            float projFar = MathF.Max(camera.FarZ, camera.NearZ + 0.001f);
            uint desiredResolution = desiredResolutionOverride ?? GetDesiredShadowAtlasResolution(light);
            uint minimumResolution = GetMinimumShadowAtlasResolution(desiredResolution);
            bool hasPrevious = ShadowAtlas.PublishedFrameData.TryGetAllocation(key, out ShadowAtlasAllocation previous);
            ulong contentHash = BuildShadowContentHash(light, projectionType, faceOrCascadeIndex, encoding, view, projection, projNear, projFar);
            bool canReusePreviousFrame = CanReuseShadowAtlasPreviousFrame(light, hasPrevious, previous, contentHash);
            ShadowDirtyReason dirtyReason = ResolveShadowDirtyReason(
                light,
                projectionType,
                encoding,
                contentHash,
                canReusePreviousFrame,
                hasPrevious,
                previous);
            bool isDirty = dirtyReason != ShadowDirtyReason.None;
            float refreshPriority = priority + EstimateRefreshPriorityBonus(hasPrevious, previous);

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
                refreshPriority,
                contentHash,
                isDirty,
                dirtyReason,
                canReusePreviousFrame,
                EditorPinned: false,
                StereoVis: RuntimeEngine.VRState.IsInVR ? StereoVisibility.BothEyes : StereoVisibility.Mono,
                ForcedSkipReason: forcedSkipReason);

            LogDirectionalAtlasSubmit(light, request, hasPrevious, previous);
            ShadowAtlas.Submit(request);
        }

        private static bool CanReuseShadowAtlasPreviousFrame(
            LightComponent light,
            bool hasPrevious,
            in ShadowAtlasAllocation previous,
            ulong contentHash)
        {
            if (light.Type != ELightType.Dynamic)
                return true;

            return hasPrevious &&
                previous.IsResident &&
                previous.LastRenderedFrame != 0u &&
                previous.ContentVersion == contentHash;
        }

        private static ShadowDirtyReason ResolveShadowDirtyReason(
            LightComponent light,
            EShadowProjectionType projectionType,
            EShadowMapEncoding encoding,
            ulong contentHash,
            bool canReusePreviousFrame,
            bool hasPrevious,
            in ShadowAtlasAllocation previous)
        {
            ShadowDirtyReason reason = ShadowDirtyReason.None;

            if (!hasPrevious)
                reason |= ShadowDirtyReason.FirstSubmission;
            else
            {
                if (!previous.IsResident)
                    reason |= ShadowDirtyReason.AllocationMissing;
                if (previous.LastRenderedFrame == 0u)
                    reason |= ShadowDirtyReason.NeverRendered;
                if (previous.ContentVersion != contentHash)
                {
                    reason |= ShadowDirtyReason.ContentChanged;
                    reason |= IsProjectionOrCameraFitChange(light, projectionType)
                        ? ShadowDirtyReason.ProjectionOrCameraFitChanged
                        : ShadowDirtyReason.LightOrSettingsChanged;
                }
                if (previous.AtlasId != (((int)ResolveAtlasKind(projectionType) << 8) | (int)encoding))
                    reason |= ShadowDirtyReason.EncodingChanged | ShadowDirtyReason.AllocationChanged;
            }

            if (!canReusePreviousFrame)
            {
                reason |= ShadowDirtyReason.ReuseDisabled;
                if (light.Type == ELightType.Dynamic)
                    reason |= ShadowDirtyReason.DynamicLight;
            }

            return reason;
        }

        private static bool IsProjectionOrCameraFitChange(LightComponent light, EShadowProjectionType projectionType)
        {
            if (projectionType is EShadowProjectionType.DirectionalCascade or EShadowProjectionType.DirectionalPrimary)
                return true;

            return projectionType is EShadowProjectionType.PointFace or EShadowProjectionType.SpotPrimary &&
                light.MovementVersion != 0u &&
                light.TimeSinceLastMovement <= LocalShadowMovementFreshSeconds;
        }

        private static EShadowAtlasKind ResolveAtlasKind(EShadowProjectionType projectionType)
            => projectionType switch
            {
                EShadowProjectionType.DirectionalPrimary or EShadowProjectionType.DirectionalCascade => EShadowAtlasKind.Directional,
                EShadowProjectionType.PointFace => EShadowAtlasKind.Point,
                EShadowProjectionType.SpotPrimary => EShadowAtlasKind.Spot,
                _ => EShadowAtlasKind.Directional,
            };

        private float EstimateRefreshPriorityBonus(bool hasPrevious, in ShadowAtlasAllocation previous)
        {
            const float neverRenderedBonus = 5000.0f;
            const float perFrameAgeBonus = 250.0f;
            const ulong maxAgeFrames = 20u;

            if (!hasPrevious || !previous.IsResident || previous.LastRenderedFrame == 0u)
                return neverRenderedBonus;

            ulong currentFrame = ShadowAtlas.CurrentFrameId;
            if (currentFrame <= previous.LastRenderedFrame)
                return 0.0f;

            ulong ageFrames = Math.Min(currentFrame - previous.LastRenderedFrame, maxAgeFrames);
            return ageFrames * perFrameAgeBonus;
        }

        private static bool ShouldSubmitShadowAtlasRequest(LightComponent light)
            => light is { IsActiveInHierarchy: true, CastsShadows: true };

        private bool ShouldRenderLegacySpotShadowMap(SpotLightComponent light)
        {
            if (light.ShadowMap is null)
                return false;

            if (!light.UsesSpotShadowAtlasForCurrentEncoding)
                return true;

            return false;
        }

        private bool ShouldRenderLegacyDirectionalShadowMap(DirectionalLightComponent light, out bool renderCascades)
        {
            renderCascades = true;
            int desktopCascadeCount = light.GetActiveCascadeCount(ShadowRequestSource.Desktop);
            int hmdCascadeCount = light.GetActiveCascadeCount(ShadowRequestSource.Hmd);
            int activeCascadeCount = Math.Max(desktopCascadeCount, hmdCascadeCount);
            bool needsCascadeAtlas = light.EnableCascadedShadows && (desktopCascadeCount > 0 || hmdCascadeCount > 0);
            bool cascadeAtlasUnsupported = light.UsesDirectionalShadowAtlasForCurrentEncoding &&
                needsCascadeAtlas &&
                ((desktopCascadeCount > 0 && !light.CanUseDirectionalCascadeShadowAtlasForCurrentBackend(desktopCascadeCount)) ||
                 (hmdCascadeCount > 0 && !light.CanUseDirectionalCascadeShadowAtlasForCurrentBackend(hmdCascadeCount)));
            if (!light.UsesDirectionalShadowAtlasForCurrentEncoding || cascadeAtlasUnsupported)
            {
                renderCascades = light.EnableCascadedShadows && activeCascadeCount > 0;
                LogDirectionalLegacyDecision(
                    light,
                    cascadeAtlasUnsupported
                        ? "AtlasGroupedCascadeBackendUnavailable"
                        : "AtlasDisabled",
                    legacyRender: true,
                    renderCascades,
                    needsLegacyCascades: renderCascades,
                    needsLegacyPrimary: light.ShadowMap is not null,
                    desktopCascadeCount,
                    hmdCascadeCount);
                return true;
            }

            bool directionalAtlasReady = needsCascadeAtlas
                ? (desktopCascadeCount <= 0 || HasSampleableDirectionalCascadeAtlas(light, ShadowRequestSource.Desktop, desktopCascadeCount)) &&
                  (hmdCascadeCount <= 0 || HasSampleableDirectionalCascadeAtlas(light, ShadowRequestSource.Hmd, hmdCascadeCount))
                : HasSampleableDirectionalPrimaryAtlas(light);
            if (!directionalAtlasReady)
            {
                renderCascades = needsCascadeAtlas;
                LogDirectionalLegacyDecision(
                    light,
                    needsCascadeAtlas
                        ? "AtlasCascadeNotSampleable"
                        : "AtlasPrimaryNotSampleable",
                    legacyRender: true,
                    renderCascades,
                    needsLegacyCascades: renderCascades,
                    needsLegacyPrimary: light.ShadowMap is not null,
                    desktopCascadeCount,
                    hmdCascadeCount);
                return true;
            }

            LogDirectionalLegacyDecision(
                light,
                "AtlasEnabledNoLegacyFallback",
                legacyRender: false,
                renderCascades: false,
                needsLegacyCascades: false,
                needsLegacyPrimary: false,
                desktopCascadeCount,
                hmdCascadeCount);
            renderCascades = false;
            return false;
        }

        private bool HasSampleableDirectionalPrimaryAtlas(DirectionalLightComponent light)
        {
            if (!TryGetDirectionalAtlasPageCount(light, out int pageCount))
                return false;

            return light.TryGetPrimaryAtlasSlot(out DirectionalLightComponent.DirectionalCascadeAtlasSlot slot) &&
                   IsSampleableDirectionalAtlasSlot(slot, pageCount);
        }

        private bool HasSampleableDirectionalCascadeAtlas(DirectionalLightComponent light, ShadowRequestSource source, int activeCascadeCount)
        {
            if (!TryGetDirectionalAtlasPageCount(light, out int pageCount))
                return false;

            int count = Math.Clamp(activeCascadeCount, 0, 8);
            if (count <= 0)
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!light.TryGetCascadeAtlasSlot(source, i, out DirectionalLightComponent.DirectionalCascadeAtlasSlot slot) ||
                    !IsSampleableDirectionalAtlasSlot(slot, pageCount))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryGetDirectionalAtlasPageCount(DirectionalLightComponent light, out int pageCount)
        {
            ShadowMapFormatSelection shadowFormat = light.ResolveShadowMapFormat(preferredStorageFormat: null);
            if (ShadowAtlas.TryGetPageTexture(
                EShadowAtlasKind.Directional,
                shadowFormat.Encoding,
                pageIndex: 0,
                out XRTexture2DArray atlasTexture))
            {
                pageCount = checked((int)Math.Max(1u, atlasTexture.Depth));
                return true;
            }

            pageCount = 0;
            return false;
        }

        private static bool IsSampleableDirectionalAtlasSlot(
            in DirectionalLightComponent.DirectionalCascadeAtlasSlot slot,
            int pageCount)
            => slot.HasAllocation &&
               slot.IsResident &&
               slot.LastRenderedFrame != 0u &&
               slot.PageIndex >= 0 &&
               slot.PageIndex < pageCount &&
               slot.Fallback is ShadowFallbackMode.None or ShadowFallbackMode.StaleTile;

        private static bool ViewportTargetsWorld(XRViewport viewport, IRuntimeRenderWorld world)
            => ReferenceEquals(viewport.World, world) ||
               ReferenceEquals(viewport.Window?.TargetWorldInstance, world);

        private static bool ViewportTargetsWorld(XRWindow window, XRViewport viewport, IRuntimeRenderWorld world)
            => ReferenceEquals(viewport.World, world) ||
               ReferenceEquals(window.TargetWorldInstance, world);

        private uint GetDesiredShadowAtlasResolution(LightComponent light)
            => light.GetDesiredShadowAtlasResolution();

        private uint GetMinimumShadowAtlasResolution(uint desiredResolution)
            => Math.Min(desiredResolution, ShadowAtlas.Settings.MinTileResolution);

        private float EstimateSpotPriority(SpotLightComponent light, ShadowScratch scratch)
        {
            Cone cone = light.OuterCone;
            float halfHeight = cone.Height * 0.5f;
            float radius = MathF.Sqrt((halfHeight * halfHeight) + (cone.Radius * cone.Radius));
            return EstimateLocalPriority(cone.Center, radius, light.Brightness, scratch);
        }

        private float EstimatePointPriority(PointLightComponent light, ShadowScratch scratch)
            => EstimateLocalPriority(light.Transform.RenderTranslation, light.Radius, light.Brightness, scratch);

        private uint GetDesiredPointFaceShadowAtlasResolution(PointLightComponent light, float faceRelevance)
        {
            uint desired = GetDesiredShadowAtlasResolution(light);
            if (faceRelevance < 0.15f)
                desired = Math.Max(ShadowAtlas.Settings.MinTileResolution, desired >> 2);
            else if (faceRelevance < 0.45f)
                desired = Math.Max(ShadowAtlas.Settings.MinTileResolution, desired >> 1);

            return ShadowAtlasManager.NormalizeTileResolution(
                desired,
                ShadowAtlas.Settings.MinTileResolution,
                ShadowAtlas.Settings.MaxTileResolution,
                ShadowAtlas.Settings.PageSize);
        }

        private float EstimatePointFaceRelevance(PointLightComponent light, int faceIndex, ShadowScratch scratch)
        {
            List<XRCamera> cameras = scratch.ShadowAtlasCameras;
            if (cameras.Count == 0)
                return 1.0f;

            Vector3 lightPosition = light.Transform.RenderTranslation;
            Vector3 faceForward = PointLightComponent.GetShadowFaceForward(faceIndex);
            float best = 0.0f;
            for (int i = 0; i < cameras.Count; i++)
            {
                Vector3 toCamera = cameras[i].Transform.RenderTranslation - lightPosition;
                float lengthSq = toCamera.LengthSquared();
                if (lengthSq <= 0.000001f)
                {
                    best = 1.0f;
                    continue;
                }

                Vector3 direction = toCamera / MathF.Sqrt(lengthSq);
                float alignment = Vector3.Dot(direction, faceForward);
                best = MathF.Max(best, Math.Clamp((alignment + 1.0f) * 0.5f, 0.0f, 1.0f));
            }

            return MathF.Max(best, 0.05f);
        }

        private float EstimateLocalPriority(Vector3 center, float radius, float intensity, ShadowScratch scratch)
        {
            List<XRCamera> cameras = scratch.ShadowAtlasCameras;
            if (cameras.Count == 0)
                return MathF.Max(1.0f, intensity);

            float best = 0.0f;
            float safeRadius = MathF.Max(radius, 0.001f);
            for (int i = 0; i < cameras.Count; i++)
            {
                XRCamera camera = cameras[i];
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
                DynamicDirectionalLights[i].BeginDirectionalAtlasSlotPublish();
                DynamicDirectionalLights[i].SetShadowAtlasDiagnostic(default);
            }

            for (int i = 0; i < DynamicSpotLights.Count; i++)
                DynamicSpotLights[i].SetShadowAtlasDiagnostic(default);

            for (int i = 0; i < DynamicPointLights.Count; i++)
            {
                DynamicPointLights[i].ClearShadowAtlasFaceSlots();
                DynamicPointLights[i].SetShadowAtlasDiagnostic(default);
            }

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
            ShadowDirtyReason lastDirtyReason = request.DirtyReason != ShadowDirtyReason.None
                ? request.DirtyReason
                : previous.LastDirtyReason;
            ShadowFallbackMode fallback = allocation.ActiveFallback != ShadowFallbackMode.None
                ? allocation.ActiveFallback
                : previous.ActiveFallback;

            int shadowRecordIndex =
                request.ProjectionType == EShadowProjectionType.SpotPrimary ||
                (request.ProjectionType == EShadowProjectionType.PointFace && request.FaceOrCascadeIndex == 0) ||
                request.ProjectionType == EShadowProjectionType.DirectionalPrimary ||
                request.ProjectionType == EShadowProjectionType.DirectionalCascade
                    ? recordIndex
                    : previousRecordIndex;

            if (request.Light is DirectionalLightComponent directionalLight)
            {
                if (request.ProjectionType == EShadowProjectionType.DirectionalCascade)
                {
                    directionalLight.SetCascadeAtlasSlot(
                        request.Key.Source,
                        request.FaceOrCascadeIndex,
                        allocation,
                        recordIndex,
                        request.NearPlane,
                        request.FarPlane,
                        request.DesiredResolution);
                }
                else if (request.ProjectionType == EShadowProjectionType.DirectionalPrimary)
                {
                    directionalLight.SetPrimaryAtlasSlot(
                        allocation,
                        recordIndex,
                        request.NearPlane,
                        request.FarPlane,
                        request.DesiredResolution);
                }
            }
            else if (request.Light is PointLightComponent pointLight &&
                request.ProjectionType == EShadowProjectionType.PointFace)
            {
                pointLight.SetShadowAtlasFaceSlot(
                    request.FaceOrCascadeIndex,
                    allocation,
                    recordIndex,
                    request.NearPlane,
                    request.FarPlane,
                    request.DesiredResolution);
            }

            light.SetShadowAtlasDiagnostic(new ShadowRequestDiagnostic(
                requestCount,
                residentCount,
                maxRequested,
                maxAllocated,
                highestPriority,
                lastSkip,
                lastDirtyReason,
                fallback,
                shadowRecordIndex,
                allocation.PageIndex,
                allocation.PixelRect,
                allocation.InnerPixelRect,
                allocation.LastRenderedFrame,
                frameId));
        }

        private void LogShadowAtlasFrameSummary(bool collectVisibleNow, ShadowScratch scratch)
        {
            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                !Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.AtlasFrame.{GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            ShadowAtlasFrameData frameData = ShadowAtlas.PublishedFrameData;
            ShadowAtlasMetrics metrics = frameData.Metrics;
            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][AtlasFrame] frame={0} generation={1} useDirAtlas={2} useSpotAtlas={3} collectVisibleNow={4} activeAtlasCameras={5} requests={6} allocations={7} resident={8} skipped={9} notRelevant={10} pages={11} renderedThisFrame={12} queueOverflow={13} budgetTiles={14} budgetMs={15:F2} directionalGroupedFrames={16} directionalSequentialFallbackFrames={17} directionalLightDiagnostics={18}",
                frameData.FrameId,
                frameData.Generation,
                RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas,
                RuntimeEngine.Rendering.Settings.UseSpotShadowAtlas,
                collectVisibleNow,
                scratch.ShadowAtlasCameras.Count,
                metrics.RequestCount,
                frameData.AllocationCount,
                metrics.ResidentTileCount,
                metrics.SkippedRequestCount,
                metrics.NotRelevantSkipCount,
                metrics.PageCount,
                metrics.TilesScheduledThisFrame,
                metrics.QueueOverflowCount,
                ShadowAtlas.Settings.MaxTilesRenderedPerFrame,
                ShadowAtlas.Settings.MaxRenderMilliseconds,
                metrics.DirectionalGroupedFrameCount,
                metrics.DirectionalSequentialFallbackFrameCount,
                frameData.DirectionalLightDiagnosticCount);
        }

        private static void LogDirectionalAtlasSubmit(
            LightComponent light,
            ShadowMapRequest request,
            bool hasPrevious,
            ShadowAtlasAllocation previous)
        {
            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                request.ProjectionType is not EShadowProjectionType.DirectionalCascade and not EShadowProjectionType.DirectionalPrimary ||
                !Debug.ShouldLogEvery(
                    $"DirectionalShadowAudit.AtlasSubmit.{request.Key}",
                    TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            bool contentChanged = !hasPrevious || previous.ContentVersion != request.ContentHash;
            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
            "[DirectionalShadowAudit][AtlasSubmit] frame={0} light='{1}' projection={2} cascadeOrFace={3} dirty={4} dirtyReason={5} contentChanged={6} canReuse={7} fallback={8} desired={9} min={10} near={11:F3} far={12:F3} priority={13:F1} previousResident={14} previousRenderedFrame={15} previousFallback={16} previousPage={17} previousRect={18}",
                RuntimeEngine.Rendering.State.RenderFrameId,
                light.SceneNode?.Name ?? light.Name ?? light.GetType().Name,
                request.ProjectionType,
                request.FaceOrCascadeIndex,
                request.IsDirty,
                request.DirtyReason,
                contentChanged,
                request.CanReusePreviousFrame,
                request.Fallback,
                request.DesiredResolution,
                request.MinimumResolution,
                request.NearPlane,
                request.FarPlane,
                request.Priority,
                hasPrevious && previous.IsResident,
                hasPrevious ? previous.LastRenderedFrame : 0u,
                hasPrevious ? previous.ActiveFallback : ShadowFallbackMode.None,
                hasPrevious ? previous.PageIndex : -1,
                hasPrevious ? FormatRect(previous.PixelRect) : "<none>");
        }

        private void LogDirectionalLegacyDecision(
            DirectionalLightComponent light,
            string reason,
            bool legacyRender,
            bool renderCascades,
            bool needsLegacyCascades,
            bool needsLegacyPrimary,
            int desktopCascadeCount,
            int hmdCascadeCount)
        {
            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                !Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.LegacyDecision.{light.GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][LegacyDecision] frame={0} light='{1}' reason={2} useDirAtlas={3} legacyRender={4} renderCascades={5} needsLegacyCascades={6} needsLegacyPrimary={7} casts={8} cascadesEnabled={9} desktopCascades={10} hmdCascades={11} shadowMap={12} desktopColorTex={13} hmdColorTex={14} desktopRasterDepthTex={15} hmdRasterDepthTex={16} useRasterCascadeReceiver={17} desktopSlots={18} hmdSlots={19}",
                RuntimeEngine.Rendering.State.RenderFrameId,
                light.SceneNode?.Name ?? light.Name ?? light.GetType().Name,
                reason,
                RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas,
                legacyRender,
                renderCascades,
                needsLegacyCascades,
                needsLegacyPrimary,
                light.CastsShadows,
                light.EnableCascadedShadows,
                desktopCascadeCount,
                hmdCascadeCount,
                light.ShadowMap is not null,
                light.HasCascadeColorTextureForSource(ShadowRequestSource.Desktop),
                light.HasCascadeColorTextureForSource(ShadowRequestSource.Hmd),
                light.HasCascadeRasterDepthTextureForSource(ShadowRequestSource.Desktop),
                light.HasCascadeRasterDepthTextureForSource(ShadowRequestSource.Hmd),
                light.UsesCascadeRasterDepthReceiver,
                DescribeDirectionalCascadeSlots(light, ShadowRequestSource.Desktop, desktopCascadeCount),
                DescribeDirectionalCascadeSlots(light, ShadowRequestSource.Hmd, hmdCascadeCount));
        }

        private static string DescribeDirectionalCascadeSlots(DirectionalLightComponent light, ShadowRequestSource source, int activeCascadeCount)
        {
            int count = Math.Clamp(activeCascadeCount, 0, 4);
            if (count == 0)
                return "<none>";

            string result = string.Empty;
            for (int i = 0; i < count; i++)
            {
                string entry;
                if (light.TryGetCascadeAtlasSlot(source, i, out DirectionalLightComponent.DirectionalCascadeAtlasSlot slot))
                {
                    entry = $"c{i}:alloc={slot.HasAllocation},resident={slot.IsResident},page={slot.PageIndex},rec={slot.RecordIndex},fb={slot.Fallback},last={slot.LastRenderedFrame},rect={FormatRect(slot.InnerPixelRect)}";
                }
                else
                {
                    entry = $"c{i}:<missing>";
                }

                result = string.IsNullOrEmpty(result) ? entry : $"{result}; {entry}";
            }

            return result;
        }

        private static string FormatRect(BoundingRectangle rect)
            => $"{rect.X},{rect.Y},{rect.Width}x{rect.Height}";

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

        internal bool TryGetPointShadowAtlasFaceAllocation(
            PointLightComponent light,
            int faceIndex,
            out ShadowAtlasAllocation allocation,
            out int shadowRecordIndex)
        {
            ShadowRequestKey key = light.CreateShadowRequestKey(EShadowProjectionType.PointFace, faceIndex, EShadowMapEncoding.Depth);
            if (ShadowAtlas.PublishedFrameData.TryGetAllocationIndex(key, out shadowRecordIndex, out allocation))
                return true;

            allocation = default;
            shadowRecordIndex = -1;
            return false;
        }

        internal bool TryGetResidentSpotShadowAtlasTexture(
            SpotLightComponent light,
            out XRTexture2DArray texture,
            out ShadowAtlasAllocation allocation,
            out int shadowRecordIndex)
        {
            if (light.UsesSpotShadowAtlasForCurrentEncoding &&
                TryGetSpotShadowAtlasAllocation(light, out allocation, out shadowRecordIndex) &&
                allocation.IsResident &&
                allocation.LastRenderedFrame != 0u &&
                ShadowAtlas.TryGetPageTexture(allocation.AtlasKind, EShadowMapEncoding.Depth, allocation.PageIndex, out texture))
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
            => TryGetDirectionalCascadeShadowAtlasAllocation(light, ShadowRequestSource.Desktop, cascadeIndex, out allocation, out shadowRecordIndex);

        internal bool TryGetDirectionalCascadeShadowAtlasAllocation(
            DirectionalLightComponent light,
            ShadowRequestSource source,
            int cascadeIndex,
            out ShadowAtlasAllocation allocation,
            out int shadowRecordIndex)
        {
            EShadowMapEncoding encoding = light.ResolveShadowMapFormat(preferredStorageFormat: null).Encoding;
            ShadowRequestKey key = light.CreateShadowRequestKey(EShadowProjectionType.DirectionalCascade, cascadeIndex, encoding, source: source);
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
            EShadowMapEncoding encoding = light.ResolveShadowMapFormat(preferredStorageFormat: null).Encoding;
            ShadowRequestKey key = light.CreateShadowRequestKey(EShadowProjectionType.DirectionalPrimary, 0, encoding);
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
            in Matrix4x4 projection,
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
            AddMatrix(ref hash, projection);
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

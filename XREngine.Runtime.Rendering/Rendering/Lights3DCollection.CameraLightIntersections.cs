using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        #region Camera-Light Intersections

        /// <summary>
        /// Tests the active player camera frustum against each light's shadow frusta and records intersection AABBs for cascaded shadows.
        /// </summary>
        /// <param name="camera">Active player camera.</param>
        public void UpdateCameraLightIntersections(XRCamera camera)
        {
            if (camera is null)
                return;

            PreparedFrustum preparedCamera = camera.WorldFrustum().Prepare();

            UpdateDirectionalCameraLightIntersections(DynamicDirectionalLights, camera, preparedCamera, new ColorF4(0.2f, 0.8f, 1.0f, 1.0f));
            UpdateCameraLightIntersections(DynamicSpotLights, preparedCamera, new ColorF4(1.0f, 0.85f, 0.2f, 1.0f));
            UpdateCameraLightIntersections(DynamicPointLights, preparedCamera, new ColorF4(1.0f, 0.2f, 0.8f, 1.0f));
        }

        private static bool ViewportPrefersCascadedDirectionalShadows(XRViewport viewport)
            => viewport.CameraComponent is { DirectionalShadowRenderingMode: EDirectionalShadowRenderingMode.Cascaded };

        private bool HasActiveCascadedDirectionalShadowViewport()
        {
            bool Matches(XRViewport? viewport)
                => viewport is not null
                    && ViewportTargetsWorld(viewport, World)
                    && !viewport.Suppress3DSceneRendering
                    && viewport.ActiveCamera is not null
                    && ViewportPrefersCascadedDirectionalShadows(viewport);

            foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                if (Matches(viewport))
                    return true;

            return Matches(Engine.VRState.LeftEyeViewport) || Matches(Engine.VRState.RightEyeViewport);
        }

        internal bool NeedsPrimaryDirectionalShadowMap()
        {
            bool sawRelevantViewport = false;

            bool PrefersPrimary(XRViewport? viewport)
            {
                if (viewport is null ||
                    !ViewportTargetsWorld(viewport, World) ||
                    viewport.Suppress3DSceneRendering ||
                    viewport.ActiveCamera is null)
                    return false;

                sawRelevantViewport = true;
                return !ViewportPrefersCascadedDirectionalShadows(viewport);
            }

            foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                if (PrefersPrimary(viewport))
                    return true;

            if (PrefersPrimary(Engine.VRState.LeftEyeViewport) || PrefersPrimary(Engine.VRState.RightEyeViewport))
                return true;

            return !sawRelevantViewport;
        }

        private XRCamera? ResolveDirectionalShadowSourceCamera()
        {
            XRCamera? preferredCascaded = null;
            XRCamera? preferredFallback = null;
            XRCamera? cascadedFallback = null;
            XRCamera? fallback = null;

            void ConsiderViewport(XRViewport viewport)
            {
                if (!ViewportTargetsWorld(viewport, World) || viewport.Suppress3DSceneRendering)
                    return;

                XRCamera? camera = viewport.ActiveCamera;
                if (camera is null)
                    return;

                if (ViewportPrefersCascadedDirectionalShadows(viewport))
                {
                    if (viewport.AssociatedPlayer is not null)
                    {
                        preferredCascaded = camera;
                        return;
                    }

                    cascadedFallback ??= camera;
                    return;
                }

                if (viewport.AssociatedPlayer is not null)
                    preferredFallback ??= camera;
                fallback ??= camera;
            }

            foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
            {
                ConsiderViewport(viewport);
                if (preferredCascaded is not null)
                    return preferredCascaded;
            }

            if (Engine.VRState.LeftEyeViewport is XRViewport leftEye)
                ConsiderViewport(leftEye);
            if (preferredCascaded is not null)
                return preferredCascaded;

            if (Engine.VRState.RightEyeViewport is XRViewport rightEye)
                ConsiderViewport(rightEye);

            return preferredCascaded ?? cascadedFallback ?? preferredFallback ?? fallback;
        }

        private void PrepareDirectionalShadowMaps()
        {
            bool wantsCascades = HasActiveCascadedDirectionalShadowViewport();
            XRCamera? cascadeCamera = wantsCascades ? ResolveDirectionalShadowSourceCamera() : null;
            LogDirectionalShadowSourceAudit(wantsCascades, cascadeCamera);

            int lightCount = DynamicDirectionalLights.Count;
            for (int i = 0; i < lightCount; i++)
            {
                DirectionalLightComponent light = DynamicDirectionalLights[i];
                if (!light.IsActiveInHierarchy)
                    continue;

                if (wantsCascades && cascadeCamera is not null && light.CastsShadows && light.EnableCascadedShadows)
                    light.UpdateCascadeShadows(cascadeCamera);
                else
                    light.ClearCascadeShadows();
            }
        }

        private void LogDirectionalShadowSourceAudit(bool wantsCascades, XRCamera? cascadeCamera)
        {
            if (!Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.Source.{GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][Source] frame={0} useDirAtlas={1} wantsCascades={2} selectedCamera={3} dirLights={4}",
                Engine.Rendering.State.RenderFrameId,
                Engine.Rendering.Settings.UseDirectionalShadowAtlas,
                wantsCascades,
                DescribeDirectionalShadowCamera(cascadeCamera),
                DynamicDirectionalLights.Count);

            int ordinal = 0;
            foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                LogDirectionalShadowViewportAudit("active", viewport, ordinal++);

            LogDirectionalShadowViewportAudit("leftEye", Engine.VRState.LeftEyeViewport, ordinal++);
            LogDirectionalShadowViewportAudit("rightEye", Engine.VRState.RightEyeViewport, ordinal);
        }

        private void LogDirectionalShadowViewportAudit(string source, XRViewport? viewport, int ordinal)
        {
            if (viewport is null)
            {
                Debug.Lighting(
                    EOutputVerbosity.Normal,
                    false,
                    "[DirectionalShadowAudit][Viewport] frame={0} source={1} ordinal={2} viewport=<null>",
                    Engine.Rendering.State.RenderFrameId,
                    source,
                    ordinal);
                return;
            }

            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][Viewport] frame={0} source={1} ordinal={2} vpIndex={3} worldMatch={4} suppress3D={5} player={6} mode={7} activeCamera={8}",
                Engine.Rendering.State.RenderFrameId,
                source,
                ordinal,
                viewport.Index,
                ViewportTargetsWorld(viewport, World),
                viewport.Suppress3DSceneRendering,
                viewport.AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>",
                viewport.CameraComponent?.DirectionalShadowRenderingMode.ToString() ?? "<none>",
                DescribeDirectionalShadowCamera(viewport.ActiveCamera));
        }

        private static string DescribeDirectionalShadowCamera(XRCamera? camera)
        {
            if (camera is null)
                return "<null>";

            Vector3 pos = camera.Transform.RenderTranslation;
            return $"hash={camera.GetHashCode()},near={camera.NearZ:F3},far={camera.FarZ:F3},shadowMax={camera.ShadowCollectMaxDistance:F3},pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2})";
        }

        private void UpdateDirectionalCameraLightIntersections(IReadOnlyList<DirectionalLightComponent> lights, XRCamera camera, PreparedFrustum preparedCamera, ColorF4 debugColor)
        {
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                _frustumScratch.Clear();
                light.BuildShadowFrusta(_frustumScratch);
                light.UpdateCameraIntersections(preparedCamera, _frustumScratch);

                RenderIntersectionDebug(light, debugColor);
                RenderCascadeDebug(light, debugColor);
            }
        }

        private void UpdateCameraLightIntersections<TLight>(IReadOnlyList<TLight> lights, PreparedFrustum preparedCamera, ColorF4 debugColor)
            where TLight : LightComponent
        {
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                _frustumScratch.Clear();
                light.BuildShadowFrusta(_frustumScratch);
                light.UpdateCameraIntersections(preparedCamera, _frustumScratch);

                RenderIntersectionDebug(light, debugColor);
            }
        }

        private static void RenderIntersectionDebug(LightComponent light, ColorF4 color)
        {
            // Only render debug when explicitly enabled
            if (!light.PreviewBoundingVolume)
                return;

            switch (light)
            {
                case PointLightComponent pointLight:
                    Engine.Rendering.Debug.RenderSphere(pointLight.Transform.RenderTranslation, pointLight.Radius, false, color);
                    return;
                case SpotLightComponent spotLight:
                    Cone cone = spotLight.OuterCone;
                    Engine.Rendering.Debug.RenderCone(cone.Center, cone.Up, cone.Radius, cone.Height, false, color);
                    return;
            }

            var intersections = light.CameraIntersections;
            if (intersections.Count == 0)
                return;

            for (int i = 0; i < intersections.Count; i++)
            {
                var aabb = intersections[i];
                Vector3 min = aabb.Min;
                Vector3 max = aabb.Max;
                Vector3 center = (min + max) * 0.5f;
                Vector3 halfExtents = (max - min) * 0.5f;
                Engine.Rendering.Debug.RenderAABB(halfExtents, center, false, color);
            }
        }

        /// <summary>
        /// Distinct colors for cascade debug visualization (up to 8 cascades).
        /// </summary>
        private static readonly ColorF4[] CascadeDebugColors =
        [
            new(1.0f, 0.2f, 0.2f, 0.7f),  // Red
            new(0.2f, 1.0f, 0.2f, 0.7f),  // Green
            new(0.3f, 0.3f, 1.0f, 0.7f),  // Blue
            new(1.0f, 1.0f, 0.2f, 0.7f),  // Yellow
            new(1.0f, 0.5f, 0.0f, 0.7f),  // Orange
            new(0.8f, 0.2f, 1.0f, 0.7f),  // Purple
            new(0.0f, 1.0f, 1.0f, 0.7f),  // Cyan
            new(1.0f, 0.5f, 0.7f, 0.7f),  // Pink
        ];

        private static void RenderCascadeDebug(DirectionalLightComponent light, ColorF4 baseColor)
        {
            if (!light.PreviewBoundingVolume)
                return;

            var cascades = light.CascadedShadowAabbs;
            if (cascades.Count == 0)
                return;

            for (int i = 0; i < cascades.Count; i++)
            {
                var cascade = cascades[i];
                ColorF4 color = CascadeDebugColors[cascade.CascadeIndex % CascadeDebugColors.Length];
                Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(cascade.Orientation);
                Engine.Rendering.Debug.RenderBox(cascade.HalfExtents, cascade.Center, rotation, false, color);
            }
        }

        #endregion
    }
}

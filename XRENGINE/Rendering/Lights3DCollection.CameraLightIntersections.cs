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

        private XRCamera? ResolveDirectionalShadowSourceCamera()
        {
            XRCamera? fallback = null;

            foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
            {
                if (!ReferenceEquals(viewport.World, World) || viewport.Suppress3DSceneRendering)
                    continue;

                XRCamera? camera = viewport.ActiveCamera;
                if (camera is null)
                    continue;

                if (ViewportPrefersCascadedDirectionalShadows(viewport))
                    return camera;

                fallback ??= camera;
            }

            XRViewport?[] vrViewports =
            [
                Engine.VRState.LeftEyeViewport,
                Engine.VRState.RightEyeViewport,
            ];

            for (int i = 0; i < vrViewports.Length; i++)
            {
                XRViewport? viewport = vrViewports[i];
                if (viewport is null || !ReferenceEquals(viewport.World, World) || viewport.Suppress3DSceneRendering)
                    continue;

                XRCamera? camera = viewport.ActiveCamera;
                if (camera is null)
                    continue;

                if (ViewportPrefersCascadedDirectionalShadows(viewport))
                    return camera;

                fallback ??= camera;
            }

            return fallback;
        }

        private void PrepareDirectionalShadowMaps()
        {
            XRCamera? cascadeCamera = ResolveDirectionalShadowSourceCamera();

            foreach (DirectionalLightComponent light in DynamicDirectionalLights)
            {
                if (!light.IsActiveInHierarchy)
                    continue;

                if (cascadeCamera is not null && light.CastsShadows && light.EnableCascadedShadows)
                    light.UpdateCascadeShadows(cascadeCamera);
                else
                    light.ClearCascadeShadows();
            }
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

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Components.Capture.Lights;
using XREngine.Scene.Transforms;
using XREngine.Rendering.Commands;
using YamlDotNet.Serialization;
using MIConvexHull;
using XREngine.Data.Colors;

namespace XREngine.Scene
{
    public class Lights3DCollection(XRWorldInstance world) : XRBase
    {
        public bool IBLCaptured { get; private set; } = false;

        private bool _capturing = false;

        private ITriangulation<LightProbeComponent, LightProbeCell>? _cells;

        [YamlIgnore]
        public Octree<LightProbeCell> LightProbeTree { get; } = new(new AABB());
        
        public XRWorldInstance World { get; } = world;

        /// <summary>
        /// A 1x1 white texture used as a fallback shadow map when shadows are disabled.
        /// This prevents sampling from stale texture unit state.
        /// </summary>
        private static XRTexture2D? _dummyShadowMap;
        private static XRTexture2D DummyShadowMap => _dummyShadowMap ??= new XRTexture2D(1, 1, ColorF4.White);

        /// <summary>
        /// All spotlights that are not baked and need to be rendered.
        /// </summary>
        public EventList<SpotLightComponent> DynamicSpotLights { get; } = [];
        /// <summary>
        /// All point lights that are not baked and need to be rendered.
        /// </summary>
        public EventList<PointLightComponent> DynamicPointLights { get; } = [];
        /// <summary>
        /// All directional lights that are not baked and need to be rendered.
        /// </summary>
        public EventList<DirectionalLightComponent> DynamicDirectionalLights { get; } = [];
        /// <summary>
        /// All light probes in the scene.
        /// </summary>
        public EventList<LightProbeComponent> LightProbes { get; } = [];

        private const float ProbePositionQuantization = 0.001f;

        private readonly List<PreparedFrustum> _frustumScratch = new(6);

        private readonly ConcurrentQueue<SceneCaptureComponentBase> _captureQueue = new();
        private ConcurrentBag<SceneCaptureComponentBase> _captureBagUpdating = [];
        private ConcurrentBag<SceneCaptureComponentBase> _captureBagRendering = [];
        private readonly Stopwatch _captureBudgetStopwatch = new();

        /// <summary>
        /// Budget in milliseconds for processing capture work (collect + render) per frame on the main thread.
        /// </summary>
        public double CaptureBudgetMilliseconds { get; set; } = 2.0;

        public List<SceneCaptureComponentBase> CaptureComponents { get; } = [];

        /// <summary>
        /// Enqueues a scene capture component for rendering.
        /// </summary>
        /// <param name="component"></param>
        public void QueueForCapture(SceneCaptureComponentBase component)
        {
            if (_captureQueue.Contains(component))
                return;

            _captureQueue.Enqueue(component);
        }

        public bool RenderingShadowMaps { get; private set; } = false;

        private static bool _loggedForwardLightingOnce = false;
        private static bool _loggedShadowMapEnabledOnce = false;

        internal void SetForwardLightingUniforms(XRRenderProgram program)
        {
            // Debug: log that we're being called
            if (!_loggedForwardLightingOnce)
            {
                _loggedForwardLightingOnce = true;
                Debug.Out($"[ForwardLighting] SetForwardLightingUniforms called. DirLights={DynamicDirectionalLights.Count}, PointLights={DynamicPointLights.Count}, SpotLights={DynamicSpotLights.Count}");
            }

            // Global ambient light - required by ForwardLighting snippet
            program.Uniform("GlobalAmbient", new Vector3(0.1f, 0.1f, 0.1f));

            // Camera position for specular calculations
            program.Uniform("CameraPosition", Engine.Rendering.State.RenderingCamera?.Transform.RenderTranslation ?? Vector3.Zero);

            program.Uniform("DirLightCount", DynamicDirectionalLights.Count);
            program.Uniform("PointLightCount", DynamicPointLights.Count);
            program.Uniform("SpotLightCount", DynamicSpotLights.Count);

            // Forward+ bindings (optional). Shaders may ignore these if they don't declare Forward+ support.
            program.Uniform("ForwardPlusEnabled", Engine.Rendering.State.ForwardPlusEnabled);
            if (Engine.Rendering.State.ForwardPlusEnabled)
            {
                program.Uniform("ForwardPlusScreenSize", Engine.Rendering.State.ForwardPlusScreenSize);
                program.Uniform("ForwardPlusTileSize", Engine.Rendering.State.ForwardPlusTileSize);
                program.Uniform("ForwardPlusMaxLightsPerTile", Engine.Rendering.State.ForwardPlusMaxLightsPerTile);

                // Keep bindings in sync with the compute shader: 20 (local lights), 21 (visible indices).
                program.BindBuffer(Engine.Rendering.State.ForwardPlusLocalLightsBuffer!, 20u);
                program.BindBuffer(Engine.Rendering.State.ForwardPlusVisibleIndicesBuffer!, 21u);
            }

            // Support both legacy uniform names (DirLightData/PointLightData/SpotLightData)
            // and dynamically generated forward shader names (DirectionalLights/PointLights/SpotLights).
            // NOTE: We intentionally do not rely on program.HasUniform(...) here because uniforms may come
            // from #pragma snippet includes (resolved later), and HasUniform() operates on raw source text.
            const string dirArrayGenerated = "DirectionalLights";
            const string spotArrayGenerated = "SpotLights";
            const string pointArrayGenerated = "PointLights";

            const string dirArrayLegacy = "DirLightData";
            const string spotArrayLegacy = "SpotLightData";
            const string pointArrayLegacy = "PointLightData";

            // Forward materials bind their own textures at units [0..N) where N is the texture index.
            // Using a low fixed unit (like 4) for the shadow map collides with multi-texture materials
            // (e.g., Sponza) and manifests as "shadow" sampling a regular color texture.
            // Pick a dedicated high unit for forward shadow sampling.
            const int forwardShadowMapUnit = 15;
            XRTexture? forwardShadowTex = null;
            if (DynamicDirectionalLights.Count > 0)
            {
                var firstDirLight = DynamicDirectionalLights[0];
                if (firstDirLight.CastsShadows && firstDirLight.ShadowMap?.Material?.Textures.Count > 0)
                    forwardShadowTex = firstDirLight.ShadowMap.Material.Textures[0];
                else
                {
                    // Debug: log why shadow map isn't available
                    string reason = !firstDirLight.CastsShadows ? "CastsShadows=false" :
                                    firstDirLight.ShadowMap is null ? "ShadowMap=null" :
                                    firstDirLight.ShadowMap.Material is null ? "ShadowMap.Material=null" :
                                    $"Textures.Count={firstDirLight.ShadowMap.Material.Textures.Count}";
                    Debug.Out($"[ForwardShadow] No shadow tex: {reason}");
                }
            }
            bool shadowEnabled = forwardShadowTex != null;
            program.Uniform("ShadowMapEnabled", shadowEnabled);
            if (!_loggedShadowMapEnabledOnce)
            {
                _loggedShadowMapEnabledOnce = true;
                Debug.Out($"[ForwardShadow] ShadowMapEnabled={shadowEnabled}, forwardShadowTex={forwardShadowTex?.GetType().Name ?? "null"}");
            }

            // Set light space matrix for UberShader shadow mapping
            // This is set separately from the struct array uniforms for simpler UberShader integration
            if (DynamicDirectionalLights.Count > 0)
            {
                var firstDirLight = DynamicDirectionalLights[0];
                var shadowCam = firstDirLight.ShadowCamera;
                if (shadowCam != null)
                {
                    Matrix4x4 lightView = shadowCam.Transform.InverseRenderMatrix;
                    Matrix4x4 lightProj = shadowCam.ProjectionMatrix;
                    // Use View * Proj order for correct GLSL interpretation (see DirectionalLightComponent.SetUniforms)
                    Matrix4x4 lightViewProj = lightView * lightProj;
                    program.Uniform("u_LightSpaceMatrix", lightViewProj);
                }
            }

            // ALWAYS set the ShadowMap sampler to point to unit 15, even when shadows are disabled.
            // This prevents stale state from deferred passes (which use unit 4) from leaking through.
            // The shader's layout(binding=15) should handle this, but we force it to be safe against
            // cached shader binaries that might not have the layout qualifier.
            program.Uniform("ShadowMap", forwardShadowMapUnit);

            for (int i = 0; i < DynamicDirectionalLights.Count; ++i)
            {
                DynamicDirectionalLights[i].SetUniforms(program, $"{dirArrayGenerated}[{i}]");
                DynamicDirectionalLights[i].SetUniforms(program, $"{dirArrayLegacy}[{i}]");
            }
            for (int i = 0; i < DynamicSpotLights.Count; ++i)
            {
                DynamicSpotLights[i].SetUniforms(program, $"{spotArrayGenerated}[{i}]");
                DynamicSpotLights[i].SetUniforms(program, $"{spotArrayLegacy}[{i}]");
            }
            for (int i = 0; i < DynamicPointLights.Count; ++i)
            {
                DynamicPointLights[i].SetUniforms(program, $"{pointArrayGenerated}[{i}]");
                DynamicPointLights[i].SetUniforms(program, $"{pointArrayLegacy}[{i}]");
            }

            // Bind the actual shadow texture after per-light SetUniforms.
            // ALWAYS bind a texture to unit 15 - if no shadow map, use a 1x1 white dummy.
            // This prevents OpenGL from sampling stale texture state.
            program.Sampler("ShadowMap", forwardShadowTex ?? DummyShadowMap, forwardShadowMapUnit);
        }

        public bool CollectingVisibleShadowMaps { get; private set; } = false;

        public void CollectVisibleItems()
        {
            //CollectingVisibleShadowMaps = true;

            if (Engine.Rendering.Settings.CullShadowCollectionByCameraFrusta)
            {
                List<(Frustum Frustum, Vector3 Position, float MaxDistance)> cameraFrustumScratch = new(4);
                foreach (XRWindow window in Engine.Windows)
                {
                    if (!ReferenceEquals(window.TargetWorldInstance, World))
                        continue;

                    foreach (XRViewport viewport in window.Viewports)
                    {
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
                }

                if (cameraFrustumScratch.Count > 0)
                {
                    foreach (DirectionalLightComponent l in DynamicDirectionalLights)
                    {
                        if (!l.IsActiveInHierarchy || !l.CastsShadows || l.ShadowMap is null)
                            continue;

                        if (IsLightShadowRelevant(l, cameraFrustumScratch))
                            l.CollectVisibleItems();
                    }

                    foreach (SpotLightComponent l in DynamicSpotLights)
                    {
                        if (!l.IsActiveInHierarchy || !l.CastsShadows || l.ShadowMap is null)
                            continue;

                        if (IsLightShadowRelevant(l, cameraFrustumScratch))
                            l.CollectVisibleItems();
                    }

                    foreach (PointLightComponent l in DynamicPointLights)
                    {
                        if (!l.IsActiveInHierarchy || !l.CastsShadows || l.ShadowMap is null)
                            continue;

                        if (IsLightShadowRelevant(l, cameraFrustumScratch))
                            l.CollectVisibleItems();
                    }
                }
                else
                {
                    // Safe fallback: if we can't discover any active cameras, preserve previous behavior.
                    foreach (DirectionalLightComponent l in DynamicDirectionalLights)
                        if (l.IsActiveInHierarchy)
                            l.CollectVisibleItems();

                    foreach (SpotLightComponent l in DynamicSpotLights)
                        if (l.IsActiveInHierarchy)
                            l.CollectVisibleItems();

                    foreach (PointLightComponent l in DynamicPointLights)
                        if (l.IsActiveInHierarchy)
                            l.CollectVisibleItems();
                }
            }
            else
            {
                foreach (DirectionalLightComponent l in DynamicDirectionalLights)
                    if (l.IsActiveInHierarchy)
                        l.CollectVisibleItems();
                
                foreach (SpotLightComponent l in DynamicSpotLights)
                    if (l.IsActiveInHierarchy)
                        l.CollectVisibleItems();

                foreach (PointLightComponent l in DynamicPointLights)
                    if (l.IsActiveInHierarchy)
                        l.CollectVisibleItems();
            }

            double budgetMs = CaptureBudgetMilliseconds;
            _captureBudgetStopwatch.Restart();

            while (_captureQueue.TryPeek(out _))
            {
                if (_captureBudgetStopwatch.Elapsed.TotalMilliseconds > budgetMs)
                    break;

                if (!_captureQueue.TryDequeue(out SceneCaptureComponentBase? capture))
                    break;

                if (_captureBagUpdating.Contains(capture))
                    continue;

                _captureBagUpdating.Add(capture);
                capture.CollectVisible();
            }

            //CollectingVisibleShadowMaps = false;
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

        public void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("Lights3DCollection.SwapBuffers");

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.DirectionalLights"))
            {
                foreach (DirectionalLightComponent l in DynamicDirectionalLights)
                    l.SwapBuffers();
            }

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.SpotLights"))
            {
                foreach (SpotLightComponent l in DynamicSpotLights)
                    l.SwapBuffers();
            }

            using (Engine.Profiler.Start("Lights3DCollection.SwapBuffers.PointLights"))
            {
                foreach (PointLightComponent l in DynamicPointLights)
                    l.SwapBuffers();
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

        /// <summary>
        /// Tests the active player camera frustum against each light's shadow frusta and records intersection AABBs for cascaded shadows.
        /// </summary>
        /// <param name="camera">Active player camera.</param>
        public void UpdateCameraLightIntersections(XRCamera camera)
        {
            if (camera is null)
                return;

            PreparedFrustum preparedCamera = camera.WorldFrustum().Prepare();
            Vector3 cameraForward = camera.Transform.WorldForward;

            UpdateDirectionalCameraLightIntersections(DynamicDirectionalLights, preparedCamera, cameraForward, new ColorF4(0.2f, 0.8f, 1.0f, 1.0f));
            UpdateCameraLightIntersections(DynamicSpotLights, preparedCamera, new ColorF4(1.0f, 0.85f, 0.2f, 1.0f));
            UpdateCameraLightIntersections(DynamicPointLights, preparedCamera, new ColorF4(1.0f, 0.2f, 0.8f, 1.0f));
        }

        private void UpdateDirectionalCameraLightIntersections(IReadOnlyList<DirectionalLightComponent> lights, PreparedFrustum preparedCamera, Vector3 cameraForward, ColorF4 debugColor)
        {
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                _frustumScratch.Clear();
                light.BuildShadowFrusta(_frustumScratch);
                light.UpdateCameraIntersections(preparedCamera, _frustumScratch);
                light.UpdateCascadeAabbs(cameraForward);

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

        private static void RenderCascadeDebug(DirectionalLightComponent light, ColorF4 baseColor)
        {
            // Only render debug when explicitly enabled
            if (!light.PreviewBoundingVolume)
                return;

            var cascades = light.CascadedShadowAabbs;
            if (cascades.Count == 0)
                return;

            // Slightly vary alpha per cascade for readability.
            const float alphaStep = 0.15f;
            for (int i = 0; i < cascades.Count; i++)
            {
                var cascade = cascades[i];
                float alpha = MathF.Max(0.1f, 1.0f - (cascade.CascadeIndex * alphaStep));
                ColorF4 color = new(baseColor.R, baseColor.G, baseColor.B, alpha);
                Engine.Rendering.Debug.RenderAABB(cascade.HalfExtents, cascade.Center, false, color);
            }
        }

        public void RenderShadowMaps(bool collectVisibleNow)
        {
            RenderingShadowMaps = true;

            foreach (DirectionalLightComponent l in DynamicDirectionalLights)
                l.RenderShadowMap(collectVisibleNow);
            foreach (SpotLightComponent l in DynamicSpotLights)
                l.RenderShadowMap(collectVisibleNow);
            foreach (PointLightComponent l in DynamicPointLights)
                l.RenderShadowMap(collectVisibleNow);

            RenderingShadowMaps = false;

            double budgetMs = CaptureBudgetMilliseconds;
            _captureBudgetStopwatch.Restart();

            foreach (SceneCaptureComponentBase capture in _captureBagRendering)
            {
                if (_captureBudgetStopwatch.Elapsed.TotalMilliseconds > budgetMs)
                {
                    // Defer remaining captures to next frame
                    _captureQueue.Enqueue(capture);
                    continue;
                }

                capture.Render();
            }
        }

        public void Clear()
        {
            DynamicSpotLights.Clear();
            DynamicPointLights.Clear();
            DynamicDirectionalLights.Clear();
        }

        /// <summary>
        /// Renders the scene from each light probe's perspective.
        /// </summary>
        public void CaptureLightProbes()
            => CaptureLightProbes(
                Engine.Rendering.Settings.LightProbeResolution,
                Engine.Rendering.Settings.LightProbesCaptureDepth);

        /// <summary>
        /// Renders the scene from each light probe's perspective.
        /// </summary>
        /// <param name="colorResolution"></param>
        /// <param name="captureDepth"></param>
        /// <param name="depthResolution"></param>
        /// <param name="force"></param>
        public void CaptureLightProbes(uint colorResolution, bool captureDepth, bool force = false)
        {
            if (_capturing || (!force && IBLCaptured))
                return;

            IBLCaptured = true;
            Debug.Out(EOutputVerbosity.Verbose, true, true, true, true, 0, 10, "Capturing scene IBL...");
            _capturing = true;

            try
            {
                IReadOnlyList<LightProbeComponent> list = LightProbes;
                for (int i = 0; i < list.Count; i++)
                {
                    Debug.Out(EOutputVerbosity.Verbose, true, true, true, true, 0, 10, $"Capturing light probe {i + 1} of {list.Count}.");
                    list[i].FullCapture(colorResolution, captureDepth);
                }
            }
            catch (Exception e)
            {
                Debug.Out(EOutputVerbosity.Verbose, true, true, true, true, 0, 10, e.Message);
            }
            finally
            {
                _capturing = false;
            }
        }

        private XRMeshRenderer? _instancedCellRenderer;

        /// <summary>
        /// Triangulates the light probes to form a Delaunay triangulation and adds the tetrahedron cells to the render tree.
        /// </summary>
        /// <param name="scene"></param>
        public void GenerateDelauanyTriangulation(VisualScene scene)
        {
            if (!TryCreateDelaunay(LightProbes, out _cells) || _cells is null)
            {
                Debug.LogWarning("Light probe triangulation failed; skipping cell generation.");
                return;
            }
            //_instancedCellRenderer = new XRMeshRenderer(GenerateInstancedCellMesh(), new XRMaterial(XRShader.EngineShader("Common/DelaunayCell.frag", EShaderType.Fragment)));
            scene.GenericRenderTree.AddRange(_cells.Cells.Select(x => x.RenderInfo));
        }

        public static bool TryCreateDelaunay(IList<LightProbeComponent> probes, out ITriangulation<LightProbeComponent, LightProbeCell>? triangulation)
        {
            triangulation = null;
            if (probes is null || probes.Count < 5)
                return false;

            var filtered = FilterDistinctProbes(probes)
                .Where(p => IsFinite(p.Transform.WorldTranslation))
                .ToList();

            if (filtered.Count < 5)
                return false;

            //if (!Has3DSpan(filtered))
            //{
            //    Debug.LogWarning("Light probe triangulation skipped: probes are coplanar or degenerate.");
            //    return false;
            //}

            try
            {
                triangulation = Triangulation.CreateDelaunay<LightProbeComponent, LightProbeCell>(filtered);
                return triangulation.Cells.Any();
            }
            catch (ConvexHullGenerationException ex)
            {
                Debug.LogWarning($"Light probe triangulation failed: {ex.Message}");
                return false;
            }
            catch (ArgumentException ex)
            {
                Debug.LogWarning($"Light probe triangulation failed: {ex.Message}");
                return false;
            }
        }

        private static bool Has3DSpan(IList<LightProbeComponent> probes)
        {
            if (probes.Count < 4)
                return false;

            // Use a dynamic tolerance based on the probe extents so tiny volumes are treated as coplanar.
            Vector3 firstPos = probes[0].Transform.WorldTranslation;
            var bounds = new AABB(firstPos, firstPos);
            foreach (var probe in probes)
                bounds.ExpandToInclude(probe.Transform.WorldTranslation);

            float span = bounds.Size.Length();
            float minVolume6 = MathF.Max(1e-6f, MathF.Pow(span, 3) * 1e-6f);

            Vector3 origin = probes[0].Transform.WorldTranslation;
            for (int i = 1; i < probes.Count - 2; ++i)
                for (int j = i + 1; j < probes.Count - 1; ++j)
                    for (int k = j + 1; k < probes.Count; ++k)
                    {
                        Vector3 v1 = probes[i].Transform.WorldTranslation - origin;
                        Vector3 v2 = probes[j].Transform.WorldTranslation - origin;
                        Vector3 v3 = probes[k].Transform.WorldTranslation - origin;
                        float volume6 = MathF.Abs(Vector3.Dot(Vector3.Cross(v1, v2), v3));
                        if (volume6 > minVolume6)
                            return true;
                    }

            return false;
        }

        private static bool IsFinite(Vector3 v)
            => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private static IList<LightProbeComponent> FilterDistinctProbes(IList<LightProbeComponent> probes)
        {
            var distinct = new Dictionary<(int, int, int), LightProbeComponent>();
            float inv = 1.0f / ProbePositionQuantization;

            foreach (var probe in probes)
            {
                Vector3 pos = probe.Transform.WorldTranslation;
                var key = ((int)MathF.Round(pos.X * inv), (int)MathF.Round(pos.Y * inv), (int)MathF.Round(pos.Z * inv));
                if (!distinct.ContainsKey(key))
                    distinct[key] = probe;
            }

            return distinct.Values.ToList();
        }

        public void RenderCells(ICollection<LightProbeCell> probes)
        {
            int count = probes.Count;
            if (count <= 0)
                return;

            //_instancedCellRenderer!.Mesh.GetBuffer(0, probes.SelectMany(x => x.Vertices.Select(y => y.Transform.WorldTranslation)).ToArray());
            _instancedCellRenderer!.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, (uint)count);
        }

        //public static XRMesh GenerateInstancedCellMesh()
        //{
        //    //Create zero-verts for a tetrahedron that will be filled in with instanced positions on the gpu
        //    VertexTriangle[] triangles =
        //    [
        //        new(new Vertex(Vector3.Zero), new Vertex(Vector3.Zero), new Vertex(Vector3.Zero)),
        //            new(new Vertex(Vector3.Zero), new Vertex(Vector3.Zero), new Vertex(Vector3.Zero)),
        //            new(new Vertex(Vector3.Zero), new Vertex(Vector3.Zero), new Vertex(Vector3.Zero)),
        //            new(new Vertex(Vector3.Zero), new Vertex(Vector3.Zero), new Vertex(Vector3.Zero))
        //    ];
        //    XRMesh mesh = new(XRMeshDescriptor.Positions(), triangles);
        //    mesh.AddBuffer()
        //}

        public static void GenerateLightProbeGrid(SceneNode parent, AABB bounds, Vector3 probesPerMeter)
        {
            Vector3 size = bounds.Size;

            IVector3 probeCount = new(
                (int)(size.X * probesPerMeter.X),
                (int)(size.Y * probesPerMeter.Y),
                (int)(size.Z * probesPerMeter.Z));

            Vector3 localMin = bounds.Min;

            Vector3 probeInc = new(
                size.X / probeCount.X,
                size.Y / probeCount.Y,
                size.Z / probeCount.Z);

            Vector3 baseInc = probeInc * 0.5f;

            for (int x = 0; x < probeCount.X; ++x)
                for (int y = 0; y < probeCount.Y; ++y)
                    for (int z = 0; z < probeCount.Z; ++z)
                        new SceneNode(parent, $"Probe[{x},{y},{z}]", new Transform(localMin + baseInc + new Vector3(x, y, z) * probeInc)).AddComponent<LightProbeComponent>();
        }

        /// <summary>
        /// Represents a tetrehedron consisting of 4 light probes, searchable within the octree
        /// </summary>
        public class LightProbeCell : TriangulationCell<LightProbeComponent, LightProbeCell>, IOctreeItem, IRenderable, IVolume
        {
            public LightProbeCell()
            {
                _rc = new(0)
                {

                };
                RenderInfo = RenderInfo3D.New(this, _rc);
                RenderedObjects = [RenderInfo];
            }

            private RenderCommandMesh3D _rc;
            public RenderInfo3D RenderInfo { get; }
            public RenderInfo[] RenderedObjects { get; }
            public IVolume? LocalCullingVolume => this;
            [YamlIgnore]
            public OctreeNodeBase? OctreeNode { get; set; }
            public bool ShouldRender { get; } = true;
            AABB? IOctreeItem.LocalCullingVolume { get; }
            public Matrix4x4 CullingOffsetMatrix { get; }
            public IRenderableBase Owner => this;

            public bool Intersects(IVolume cullingVolume, bool containsOnly)
            {
                throw new NotImplementedException();
            }

            public EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
            {
                throw new NotImplementedException();
            }

            public EContainment ContainsSphere(Sphere sphere)
            {
                throw new NotImplementedException();
            }

            public EContainment ContainsCone(Cone cone)
            {
                throw new NotImplementedException();
            }

            public EContainment ContainsCapsule(Capsule shape)
            {
                throw new NotImplementedException();
            }

            public Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
            {
                throw new NotImplementedException();
            }

            public bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
            {
                throw new NotImplementedException();
            }

            public AABB GetAABB()
            {
                throw new NotImplementedException();
            }

            public bool IntersectsSegment(Segment segment, out Vector3[] points)
            {
                throw new NotImplementedException();
            }

            public bool IntersectsSegment(Segment segment)
            {
                throw new NotImplementedException();
            }

            public EContainment ContainsBox(Box box)
            {
                throw new NotImplementedException();
            }

            public AABB GetAABB(bool transformed)
            {
                throw new NotImplementedException();
            }
        }

        public LightProbeComponent[] GetNearestProbes(Vector3 position)
        {
            if (_cells is null)
                return [];

            //Find a tetrahedron cell that contains the point.
            //We'll use this group of probes to light whatever mesh is using the provided position as reference.
            LightProbeCell? cell = LightProbeTree.FindFirst(
                item => item.LocalCullingVolume?.ContainsPoint(position) ?? false,
                bounds => bounds.ContainsPoint(position));

            if (cell is null)
                return [];

            return cell.Vertices;
        }
    }
}
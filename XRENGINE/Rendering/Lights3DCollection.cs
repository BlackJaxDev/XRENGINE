using MIConvexHull;
using System.Collections.Concurrent;
using System.Numerics;
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
using XREngine.Components.Capture.Lights.Types;
using XREngine.Scene.Transforms;

namespace XREngine.Scene
{
    public class Lights3DCollection(XRWorldInstance world) : XRBase
    {
        public bool IBLCaptured { get; private set; } = false;

        private bool _capturing = false;

        private ITriangulation<LightProbeComponent, LightProbeCell>? _cells;

        public Octree<LightProbeCell> LightProbeTree { get; } = new(new AABB());
        
        public XRWorldInstance World { get; } = world;

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

        private readonly ConcurrentQueue<SceneCaptureComponentBase> _captureQueue = new();
        private ConcurrentBag<SceneCaptureComponentBase> _captureBagUpdating = [];
        private ConcurrentBag<SceneCaptureComponentBase> _captureBagRendering = [];

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

        internal void SetForwardLightingUniforms(XRRenderProgram program)
        {
            program.Uniform("DirLightCount", DynamicDirectionalLights.Count);
            program.Uniform("PointLightCount", DynamicPointLights.Count);
            program.Uniform("SpotLightCount", DynamicSpotLights.Count);

            for (int i = 0; i < DynamicDirectionalLights.Count; ++i)
                DynamicDirectionalLights[i].SetUniforms(program, $"DirLightData[{i}]");
            for (int i = 0; i < DynamicSpotLights.Count; ++i)
                DynamicSpotLights[i].SetUniforms(program, $"SpotLightData[{i}]");
            for (int i = 0; i < DynamicPointLights.Count; ++i)
                DynamicPointLights[i].SetUniforms(program, $"PointLightData[{i}]");
        }

        public bool CollectingVisibleShadowMaps { get; private set; } = false;

        public void CollectVisibleItems()
        {
            //CollectingVisibleShadowMaps = true;

            foreach (DirectionalLightComponent l in DynamicDirectionalLights)
                l.CollectVisibleItems();
            foreach (SpotLightComponent l in DynamicSpotLights)
                l.CollectVisibleItems();
            foreach (PointLightComponent l in DynamicPointLights)
                l.CollectVisibleItems();

            foreach (SceneCaptureComponentBase sc in CaptureComponents)
                sc.CollectVisible();

            while (_captureQueue.TryDequeue(out SceneCaptureComponentBase? capture))
            {
                if (_captureBagUpdating.Contains(capture))
                    continue;
                _captureBagUpdating.Add(capture);
                capture.CollectVisible();
            }

            //CollectingVisibleShadowMaps = false;
        }

        public void SwapBuffers()
        {
            //using var t = Engine.Profiler.Start();

            foreach (DirectionalLightComponent l in DynamicDirectionalLights)
                l.SwapBuffers();
            foreach (SpotLightComponent l in DynamicSpotLights)
                l.SwapBuffers();
            foreach (PointLightComponent l in DynamicPointLights)
                l.SwapBuffers();
            foreach (SceneCaptureComponentBase sc in CaptureComponents)
                sc.SwapBuffers();

            _captureBagRendering.Clear();
            (_captureBagUpdating, _captureBagRendering) = (_captureBagRendering, _captureBagUpdating);
            foreach (SceneCaptureComponentBase capture in _captureBagRendering)
                capture.SwapBuffers();
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

            foreach (SceneCaptureComponentBase sc in CaptureComponents)
                sc.Render();

            foreach (SceneCaptureComponentBase capture in _captureBagRendering)
                capture.Render();
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
            _cells = Triangulation.CreateDelaunay<LightProbeComponent, LightProbeCell>(LightProbes);
            //_instancedCellRenderer = new XRMeshRenderer(GenerateInstancedCellMesh(), new XRMaterial(XRShader.EngineShader("Common/DelaunayCell.frag", EShaderType.Fragment)));
            scene.GenericRenderTree.AddRange(_cells.Cells.Select(x => x.RenderInfo));
        }

        public void RenderCells(ICollection<LightProbeCell> probes)
        {
            int count = probes.Count;
            if (count <= 0)
                return;

            //_instancedCellRenderer!.Mesh.GetBuffer(0, probes.SelectMany(x => x.Vertices.Select(y => y.Transform.WorldTranslation)).ToArray());
            _instancedCellRenderer!.Render(Matrix4x4.Identity, null, (uint)count);
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
                //RenderInfo = RenderInfo3D.New(this);
                //RenderInfo.LocalCullingVolume = this;
                RenderedObjects = [];
            }

            private RenderCommandMesh3D _rc;
            public RenderInfo3D RenderInfo { get; }
            public RenderInfo[] RenderedObjects { get; }
            public IVolume? LocalCullingVolume => this;
            public OctreeNodeBase? OctreeNode { get; set; }
            public bool ShouldRender { get; } = true;
            AABB? IOctreeItem.LocalCullingVolume { get; }
            public Matrix4x4 CullingOffsetMatrix { get; }
            public IRenderableBase Owner { get; }

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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MIConvexHull;
using YamlDotNet.Serialization;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        #region Light Probe Capture

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

        #endregion

        #region Light Probe Triangulation

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

        #endregion

        #region Light Probe Rendering & Queries

        public void RenderCells(ICollection<LightProbeCell> probes)
        {
            int count = probes.Count;
            if (count <= 0)
                return;

            // The instanced cell renderer path is currently disabled until the dedicated probe-cell mesh is restored.
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

        #endregion

        #region Nested Types

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

        #endregion
    }
}

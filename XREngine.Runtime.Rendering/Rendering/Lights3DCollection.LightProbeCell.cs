using System;
using System.Numerics;
using MIConvexHull;
using YamlDotNet.Serialization;
using XREngine.Components.Capture.Lights;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
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

        #endregion
    }
}

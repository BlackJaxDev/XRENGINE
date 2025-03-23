using System.Numerics;
using XREngine.Data.Components;
using XREngine.Data.Geometry;
using XREngine.Physics;

namespace XREngine.Components.Scene.Shapes
{
    public class BoxComponent : CommonShape3DComponent<Box>
    {
        public BoxComponent()
            : base(new Box(1.0f)) { }

        public BoxComponent(Vector3 halfExtents)
            : base(new Box(halfExtents)) { }

        public BoxComponent(float halfExtentsX, float halfExtentsY, float halfExtentsZ)
            : base(new Box(halfExtentsX, halfExtentsY, halfExtentsZ)) { }

        public BoxComponent(float uniformHalfExtents)
            : base(new Box(uniformHalfExtents)) { }

        public BoxComponent(ICollisionObjectConstructionInfo info)
            : base(new Box(1.0f), info) { }

        public BoxComponent(Vector3 halfExtents, ICollisionObjectConstructionInfo info)
            : base(new Box(halfExtents), info) { }

        public BoxComponent(float halfExtentsX, float halfExtentsY, float halfExtentsZ, ICollisionObjectConstructionInfo info)
            : base(new Box(halfExtentsX, halfExtentsY, halfExtentsZ), info) { }

        public BoxComponent(float uniformHalfExtents, ICollisionObjectConstructionInfo info)
            : base(new Box(uniformHalfExtents), info) { }
        
        public IEnumerable<Vector3> GetTransformedCorners() => _shape.WorldCorners;
        public IEnumerable<Vector3> GetUntransformedCorners() => _shape.LocalCorners;

        public override XRCollisionShape GetCollisionShape()
        {
            return XRCollisionBox.New(_shape.LocalHalfExtents);
        }
    }
}

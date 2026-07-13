using JoltPhysicsSharp;
using MagicPhysX;
using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jolt;

namespace XREngine.UnitTests.Physics;

[NonParallelizable]
public sealed unsafe class PhysicsGeometryAdapterBoundaryTests
{
    [Test]
    public void PrimitiveAuthoring_IsConvertedOnlyByBackendAdapters()
    {
        (IPhysicsGeometry Geometry, PxGeometryType PhysxType, Type JoltType)[] fixtures =
        [
            (new IPhysicsGeometry.Sphere(0.5f), PxGeometryType.Sphere, typeof(SphereShape)),
            (new IPhysicsGeometry.Box(new Vector3(0.5f)), PxGeometryType.Box, typeof(BoxShape)),
            (new IPhysicsGeometry.Capsule(0.25f, 0.5f), PxGeometryType.Capsule, typeof(CapsuleShape)),
            (new IPhysicsGeometry.Plane(), PxGeometryType.Plane, typeof(PlaneShape)),
        ];

        JoltScene scene = new();
        scene.Initialize();
        try
        {
            foreach ((IPhysicsGeometry geometry, PxGeometryType physxType, Type joltType) in fixtures)
            {
                geometry.GetPhysxGeometryType().ShouldBe(physxType);
                using Shape shape = JoltShapeFactory.CreateShape(geometry);
                shape.GetType().ShouldBe(joltType);
            }
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Test]
    public void PhysxPointerGeometry_IsAnExplicitNonPortableExtension()
    {
        PhysxConvexMeshGeometryExtension geometry = new(
            mesh: null,
            scale: Vector3.One,
            scaleRotation: Quaternion.Identity,
            tightBounds: false);

        Should.Throw<NotSupportedException>(() => JoltShapeFactory.CreateShape(geometry));
        Should.Throw<InvalidOperationException>(() => geometry.GetPhysxGeometryType());
        (typeof(PhysxConvexMeshGeometryExtension).Namespace ?? string.Empty).ShouldContain("Physx");
        typeof(PhysxConvexMeshGeometryExtension).Name.ShouldContain("Physx");
        typeof(PhysxConvexMeshGeometryExtension).Name.ShouldEndWith("Extension");
    }
}

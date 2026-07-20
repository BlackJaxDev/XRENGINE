using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Volumes;
using XREngine.Scene.Physics;

namespace XREngine.UnitTests.Physics;

public sealed class BlockingVolumeComponentTests
{
    [Test]
    public void DefaultConstructorCreatesHalfUnitBlockingBox()
    {
        BlockingVolumeComponent component = new();

        component.Geometry.ShouldBeOfType<IPhysicsGeometry.Box>()
            .HalfExtents.ShouldBe(new Vector3(0.5f));
        component.CollisionGroup.ShouldBe((ushort)0);
        component.GroupsMask.Word0.ShouldBe(0u);
        component.GravityEnabled.ShouldBeFalse();
        component.SimulationEnabled.ShouldBeTrue();
    }

    [Test]
    public void ConstructorPreservesCollisionConfiguration()
    {
        BlockingVolumeComponent component = new(new Vector3(1.0f, 2.0f, 3.0f), 4, 0x12);

        component.Geometry.ShouldBeOfType<IPhysicsGeometry.Box>()
            .HalfExtents.ShouldBe(new Vector3(1.0f, 2.0f, 3.0f));
        component.CollisionGroup.ShouldBe((ushort)4);
        component.GroupsMask.Word0.ShouldBe(0x12u);
    }
}
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class GPUSoftbodyDispatcherTests
{
    [Test]
    public void Instance_ReturnsSameInstance()
    {
        GPUSoftbodyDispatcher.Instance.ShouldBe(GPUSoftbodyDispatcher.Instance);
    }

    [Test]
    public void Register_AndUnregister_TrackComponentCount()
    {
        var dispatcher = GPUSoftbodyDispatcher.Instance;
        dispatcher.Reset();
        int initialCount = dispatcher.RegisteredComponentCount;

        var component = new GPUSoftbodyComponent();
        dispatcher.Register(component);
        dispatcher.RegisteredComponentCount.ShouldBe(initialCount + 1);

        dispatcher.Unregister(component);
        dispatcher.RegisteredComponentCount.ShouldBe(initialCount);
    }

    [Test]
    public void SubmitData_AndProcessDispatches_DoNotThrowWithoutRenderer()
    {
        var dispatcher = GPUSoftbodyDispatcher.Instance;
        dispatcher.Reset();

        var component = new GPUSoftbodyComponent();
        dispatcher.Register(component);

        var particles = new List<GPUSoftbodyParticleData>
        {
            new() { CurrentPosition = Vector3.Zero, PreviousPosition = Vector3.Zero, RestPosition = Vector3.Zero, InverseMass = 0.0f, Radius = 0.05f },
            new() { CurrentPosition = new Vector3(0.0f, -0.1f, 0.0f), PreviousPosition = new Vector3(0.0f, -0.1f, 0.0f), RestPosition = new Vector3(0.0f, -0.1f, 0.0f), InverseMass = 1.0f, Radius = 0.05f },
        };

        var constraints = new List<GPUSoftbodyDistanceConstraintData>
        {
            new() { ParticleA = 0, ParticleB = 1, RestLength = 0.1f, Compliance = 0.0f },
        };

        var clusters = new List<GPUSoftbodyClusterData>
        {
            new() { RestCenter = new Vector3(0.0f, -0.05f, 0.0f), Radius = 0.1f, MemberStart = 0, MemberCount = 2, Stiffness = 1.0f },
        };

        var members = new List<GPUSoftbodyClusterMemberData>
        {
            new() { ClusterIndex = 0, ParticleIndex = 0, Weight = 0.5f },
            new() { ClusterIndex = 0, ParticleIndex = 1, Weight = 0.5f },
        };

        Should.NotThrow(() => dispatcher.SubmitData(
            component,
            particles,
            constraints,
            clusters,
            members,
            [],
            [],
            new GPUSoftbodyDispatchData
            {
                SimulationScalars = new Vector4(0.016f, 0.05f, 0.01f, 0.0f),
                GravitySubsteps = new Vector4(0.0f, -9.8f, 0.0f, 1.0f),
                ForceIterations = new Vector4(Vector3.Zero, 4.0f),
            }));

        Should.NotThrow(() => dispatcher.ProcessDispatches());

        dispatcher.Unregister(component);
    }
}
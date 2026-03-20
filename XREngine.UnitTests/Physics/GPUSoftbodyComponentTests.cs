using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class GPUSoftbodyComponentTests
{
    [Test]
    public void Constructor_SetsExpectedDefaults()
    {
        var component = new GPUSoftbodyComponent();

        component.Gravity.ShouldBe(new Vector3(0.0f, -9.8f, 0.0f));
        component.ExternalForce.ShouldBe(Vector3.Zero);
        component.Damping.ShouldBe(0.05f);
        component.SolverIterations.ShouldBe(4);
        component.Substeps.ShouldBe(1);
        component.ColliderMargin.ShouldBe(0.01f);
        component.SimulationStepSeconds.ShouldBe(0.0f);
        component.TargetMeshRenderer.ShouldBeNull();
        component.Particles.Count.ShouldBe(0);
        component.DistanceConstraints.Count.ShouldBe(0);
        component.Clusters.Count.ShouldBe(0);
        component.ClusterMembers.Count.ShouldBe(0);
        component.Colliders.Count.ShouldBe(0);
        component.RenderBindings.Count.ShouldBe(0);
        component.RenderedObjects.Length.ShouldBe(1);
    }

    [Test]
    public void SubmitCurrentFrameData_UpdatesDiagnosticsWithoutThrowing()
    {
        var component = new GPUSoftbodyComponent();
        component.Particles.Add(new GPUSoftbodyParticleData { CurrentPosition = Vector3.Zero, PreviousPosition = Vector3.Zero, RestPosition = Vector3.Zero, InverseMass = 1.0f, Radius = 0.05f });
        component.DistanceConstraints.Add(new GPUSoftbodyDistanceConstraintData { ParticleA = 0, ParticleB = 0, RestLength = 0.0f, Compliance = 0.0f });
        component.Clusters.Add(new GPUSoftbodyClusterData { RestCenter = Vector3.Zero, Radius = 0.1f, MemberStart = 0, MemberCount = 0, Stiffness = 1.0f });
        component.ClusterMembers.Add(new GPUSoftbodyClusterMemberData { ClusterIndex = 0, ParticleIndex = 0, Weight = 1.0f, LocalOffset = Vector3.Zero });
        component.Colliders.Add(new GPUSoftbodyColliderData { Type = 0, Margin = 0.01f, VelocityAndDrag = new Vector4(0.0f, 0.0f, 0.0f, 1.0f) });
        component.RenderBindings.Add(new GPUSoftbodyRenderBindingData { VertexIndex = 0, ClusterIndex = 0, Weight = 1.0f });
        component.SimulationStepSeconds = 1.0f / 60.0f;

        Should.NotThrow(component.SubmitCurrentFrameData);

        component.SubmittedParticleCount.ShouldBe(1);
        component.SubmittedConstraintCount.ShouldBe(1);
        component.SubmittedClusterCount.ShouldBe(1);
        component.SubmittedClusterMemberCount.ShouldBe(1);
        component.SubmittedColliderCount.ShouldBe(1);
        component.SubmittedRenderBindingCount.ShouldBe(1);
    }
}
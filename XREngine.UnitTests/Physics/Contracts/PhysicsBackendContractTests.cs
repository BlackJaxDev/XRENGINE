using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Physics;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jolt;
using XREngine.Scene.Physics.Physx;

namespace XREngine.UnitTests.Physics.Contracts;

/// <summary>Exercises the scene lifecycle shared by every backend installed in desktop composition.</summary>
[TestFixture]
[NonParallelizable]
public sealed class PhysicsBackendContractTests
{
    private static readonly EPhysicsLibrary[] DesktopBackendIds = [EPhysicsLibrary.Jolt, EPhysicsLibrary.PhysX];
    private IDisposable? _runtimeServicesLease;

    [SetUp]
    public void SetUp()
        => _runtimeServicesLease = RuntimePhysicsServices.Install(
            new PhysicsContractRuntimeServices(RuntimePhysicsServices.Current));

    [TearDown]
    public void TearDown()
    {
        _runtimeServicesLease?.Dispose();
        _runtimeServicesLease = null;
    }

    private static PhysicsBackendCatalog CreateDesktopCatalog()
    {
        PhysicsBackendCatalog catalog = new();
        catalog.Register(new JoltPhysicsBackendModule());
        catalog.Register(new PhysxPhysicsBackendModule());
        return catalog;
    }

    [TestCaseSource(nameof(DesktopBackendIds))]
    public void InstalledBackend_CreatesAndStepsScene(EPhysicsLibrary id)
    {
        PhysicsBackendCatalog catalog = CreateDesktopCatalog();
        catalog.TryGet(id, out IPhysicsBackendModule? module).ShouldBeTrue();
        module.ShouldNotBeNull();
        module.Capabilities.HasFlag(PhysicsBackendCapabilities.RigidBodies).ShouldBeTrue();

        AbstractPhysicsScene scene = catalog.CreateRequired(id);
        try
        {
            scene.Initialize();
            scene.Gravity = new Vector3(0.0f, -9.81f, 0.0f);
            scene.Gravity.Y.ShouldBe(-9.81f, tolerance: 0.01f);
            scene.StepSimulation();
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Test]
    public void MissingOptionalBackend_HasNamedDiagnostic()
    {
        PhysicsBackendCatalog catalog = CreateDesktopCatalog();
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => catalog.CreateRequired(EPhysicsLibrary.Jitter));
        error.Message.ShouldContain("Jitter");
    }

    [TestCaseSource(nameof(DesktopBackendIds))]
    public void InstalledBackend_QueriesStaticSphereThroughNeutralContract(EPhysicsLibrary id)
    {
        AbstractPhysicsScene scene = CreateDesktopCatalog().CreateRequired(id);
        IAbstractStaticRigidBody? sphere = null;
        try
        {
            scene.Initialize();
            PhysicsRigidBodyCreateInfo createInfo = CreateBodyInfo(
                new IPhysicsGeometry.Sphere(0.5f),
                new Vector3(2.5f, 0.0f, 0.0f));
            sphere = scene.BackendService.CreateStaticRigidBody(in createInfo);
            sphere.ShouldNotBeNull();
            scene.AddActor(sphere);

            Segment throughSphere = new(Vector3.Zero, new Vector3(5.0f, 0.0f, 0.0f));
            scene.RaycastAny(throughSphere, LayerMask.Everything, null, out _).ShouldBeTrue();
        }
        finally
        {
            sphere?.Destroy();
            scene.Destroy();
        }
    }

    [TestCaseSource(nameof(DesktopBackendIds))]
    public void InstalledBackend_DynamicBodyFallsOntoStaticFloor(EPhysicsLibrary id)
    {
        AbstractPhysicsScene scene = CreateDesktopCatalog().CreateRequired(id);
        IAbstractStaticRigidBody? floor = null;
        IAbstractDynamicRigidBody? body = null;
        try
        {
            scene.Initialize();
            PhysicsRigidBodyCreateInfo floorInfo = CreateBodyInfo(
                new IPhysicsGeometry.Box(new Vector3(5.0f, 0.5f, 5.0f)),
                new Vector3(0.0f, -0.5f, 0.0f));
            PhysicsRigidBodyCreateInfo bodyInfo = CreateBodyInfo(
                new IPhysicsGeometry.Sphere(0.5f),
                new Vector3(0.0f, 3.0f, 0.0f));
            floor = scene.BackendService.CreateStaticRigidBody(in floorInfo);
            body = scene.BackendService.CreateDynamicRigidBody(in bodyInfo);
            floor.ShouldNotBeNull();
            body.ShouldNotBeNull();
            scene.AddActor(floor);
            scene.AddActor(body);

            for (int step = 0; step < 180; step++)
                scene.StepSimulation();

            body.Transform.position.Y.ShouldBeInRange(0.15f, 0.75f);
            MathF.Abs(body.LinearVelocity.Y).ShouldBeLessThan(0.75f);
        }
        finally
        {
            body?.Destroy();
            floor?.Destroy();
            scene.Destroy();
        }
    }

    private static PhysicsRigidBodyCreateInfo CreateBodyInfo(IPhysicsGeometry geometry, Vector3 position)
        => new(
            Array.Empty<PhysicsColliderShape>(),
            geometry,
            null,
            null,
            (position, Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            1.0f,
            LayerMask.Everything);
}

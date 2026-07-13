using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Physics;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jolt;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[NonParallelizable]
public sealed class JoltControllerParityTests
{
    private const float FixedDelta = 1.0f / 60.0f;
    private JoltScene? _scene;

    [SetUp]
    public void SetUp()
    {
        _scene = new JoltScene();
        _scene.Initialize();
    }

    [TearDown]
    public void TearDown()
    {
        _scene?.Destroy();
        _scene = null;
    }

    [Test]
    public void GroundingThenJump_UpdatesContactStateAndMovesAwayFromFloor()
    {
        CreateFloor();
        _scene!.RaycastAny(
            new XREngine.Data.Geometry.Segment(Vector3.UnitY * 2.0f, -Vector3.UnitY),
            LayerMask.Everything,
            null,
            out _).ShouldBeTrue();
        JoltCharacterVirtualController controller = CreateController(new Vector3(0.0f, 1.0f, 0.0f));

        controller.Move(-Vector3.UnitY * 0.3f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);

        TestContext.Progress.WriteLine(
            $"ground pos={controller.Position} foot={controller.FootPosition} up={controller.CollidingUp} down={controller.CollidingDown} side={controller.CollidingSides} contacts={controller.ActiveContactCount} supported={controller.IsSupported} state={controller.GroundState}");
        controller.CollidingDown.ShouldBeTrue();
        controller.FootPosition.Y.ShouldBeGreaterThan(-0.05f);
        float groundedY = controller.Position.Y;

        controller.Move(Vector3.UnitY * 0.75f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);

        controller.Position.Y.ShouldBeGreaterThan(groundedY + 0.2f);
        controller.CollidingDown.ShouldBeFalse();
    }

    [Test]
    public void StepOffset_ClimbsFixtureLowerThanConfiguredStepHeight()
    {
        CreateFloor();
        _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(new Vector3(0.3f, 0.1f, 1.0f)),
            (new Vector3(0.8f, 0.1f, 0.0f), Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            LayerMask.Everything).ShouldNotBeNull();

        JoltCharacterVirtualController controller = CreateController(new Vector3(0.0f, 0.77f, 0.0f));
        controller.StepOffset = 0.3f;
        controller.Move(Vector3.UnitX, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);

        controller.Position.X.ShouldBeGreaterThan(0.45f);
        controller.FootPosition.Y.ShouldBeGreaterThan(0.08f);
    }

    [Test]
    public void SteepSlope_CancelsMotionIntoTheSurfaceAndReportsASideContact()
    {
        Quaternion steepRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 3.0f);
        _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(new Vector3(2.0f, 0.1f, 1.0f)),
            (new Vector3(0.0f, -0.1f, 0.0f), steepRotation),
            Vector3.Zero,
            Quaternion.Identity,
            LayerMask.Everything).ShouldNotBeNull();

        JoltCharacterVirtualController controller = CreateController(new Vector3(-0.6f, 1.2f, 0.0f));
        controller.SlopeLimit = MathF.Cos(MathF.PI / 4.0f);
        controller.Move(new Vector3(1.0f, -1.5f, 0.0f), 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);

        TestContext.Progress.WriteLine(
            $"slope pos={controller.Position} up={controller.CollidingUp} down={controller.CollidingDown} side={controller.CollidingSides} contacts={controller.ActiveContactCount} supported={controller.IsSupported} state={controller.GroundState}");
        controller.CollidingSides.ShouldBeTrue();
        controller.Position.ShouldNotBe(new Vector3(0.4f, -0.3f, 0.0f));
    }

    [Test]
    public void CrouchResizeAndUpDirectionChange_PreserveTheAuthoredFootPosition()
    {
        JoltCharacterVirtualController controller = CreateController(new Vector3(1.0f, 2.0f, 3.0f));
        Vector3 originalFoot = controller.FootPosition;

        controller.Resize(0.45f);
        Vector3.Distance(controller.FootPosition, originalFoot).ShouldBeLessThan(0.0001f);

        controller.UpDirection = Vector3.UnitZ;
        Vector3.Distance(controller.FootPosition, originalFoot).ShouldBeLessThan(0.0001f);
        controller.UpDirection.ShouldBe(Vector3.UnitZ);
    }

    [Test]
    public void MoveBuffer_RemainsAllocationBoundedUnderFixedTimestepLoad()
    {
        JoltCharacterVirtualController controller = CreateController(Vector3.Zero);
        controller.Move(Vector3.UnitX * 0.001f, 0.0f, FixedDelta);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
            controller.Move(Vector3.UnitX * 0.001f, 0.0f, FixedDelta);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBeLessThanOrEqualTo(128L);
        controller.ConsumeInputBuffer(FixedDelta);
        TestContext.Progress.WriteLine($"buffer move={controller.LastConsumedDisplacement} pos={controller.Position}");
        controller.LastConsumedDisplacement.X.ShouldBe(10.001f, 0.01f);
        controller.Position.X.ShouldBeGreaterThan(1.0f);
    }

    [Test]
    public void BackendNeutralComponentContactEvent_FiresOnlyForStateTransitions()
    {
        CharacterControllerComponent component = new();
        FakeController controller = new();
        typeof(CharacterControllerComponent)
            .GetProperty(nameof(CharacterControllerComponent.Controller), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(component, controller);
        MethodInfo synchronize = typeof(CharacterControllerComponent)
            .GetMethod("OnPhysicsSimulationStep", BindingFlags.Instance | BindingFlags.NonPublic)!;
        List<CharacterControllerContactState> transitions = [];
        component.ContactStateChanged += (_, state) => transitions.Add(state);

        synchronize.Invoke(component, null);
        controller.CollidingDownValue = true;
        synchronize.Invoke(component, null);
        synchronize.Invoke(component, null);
        controller.CollidingDownValue = false;
        controller.CollidingUpValue = true;
        synchronize.Invoke(component, null);

        transitions.ShouldBe([
            new CharacterControllerContactState(false, true),
            new CharacterControllerContactState(true, false),
        ]);
    }

    private JoltCharacterVirtualController CreateController(Vector3 position)
        => new(_scene!, position)
        {
            Radius = 0.25f,
            Height = 1.0f,
            ContactOffset = 0.02f,
            StepOffset = 0.3f,
            SlopeLimit = MathF.Cos(MathF.PI / 4.0f),
            UpDirection = Vector3.UnitY,
        };

    private void CreateFloor()
        => _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(new Vector3(5.0f, 0.5f, 5.0f)),
            (new Vector3(0.0f, -0.5f, 0.0f), Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            LayerMask.Everything).ShouldNotBeNull();

    private sealed class FakeController : IAbstractCharacterController
    {
        public bool CollidingUpValue { get; set; }
        public bool CollidingDownValue { get; set; }
        public Vector3 Position { get; set; }
        public Vector3 FootPosition { get; set; }
        public Vector3 UpDirection { get; set; } = Vector3.UnitY;
        public float Radius { get; set; }
        public float Height { get; private set; }
        public float SlopeLimit { get; set; }
        public float StepOffset { get; set; }
        public float ContactOffset { get; set; }
        public bool CollidingUp => CollidingUpValue;
        public bool CollidingDown => CollidingDownValue;
        public (Vector3 position, Quaternion rotation) Transform => (Position, Quaternion.Identity);
        public Vector3 LinearVelocity => Vector3.Zero;
        public Vector3 AngularVelocity => Vector3.Zero;
        public bool IsSleeping => false;
        public void Move(Vector3 delta, float minDist, float elapsedTime) => Position += delta;
        public void Resize(float height) => Height = height;
        public void RequestRelease() { }
        public void Destroy(bool wakeOnLostTouch = false) { }
    }
}

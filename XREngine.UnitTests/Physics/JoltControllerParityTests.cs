using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Physics;
using XREngine.Data.Core;
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
        JoltCharacterVirtualController controller = CreateController(new Vector3(0.0f, 0.55f, 0.0f));

        controller.Move(-Vector3.UnitY * 0.2f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);

        TestContext.Progress.WriteLine(
            $"ground pos={controller.Position} foot={controller.FootPosition} up={controller.CollidingUp} down={controller.CollidingDown} side={controller.CollidingSides} contacts={controller.ActiveContactCount} supported={controller.IsSupported} state={controller.GroundState}");
        controller.CollidingDown.ShouldBeTrue();
        controller.IsGrounded.ShouldBeTrue();
        controller.SupportState.ShouldBe(CharacterSupportState.Supported);
        controller.FootPosition.Y.ShouldBeGreaterThan(-0.05f);
        float groundedY = controller.Position.Y;

        controller.Move(Vector3.UnitY * 0.75f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);

        controller.Position.Y.ShouldBeGreaterThan(groundedY + 0.2f);
        controller.CollidingDown.ShouldBeFalse();
        controller.IsGrounded.ShouldBeFalse();
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
        controller.CollidingDown.ShouldBeFalse();
        controller.IsGrounded.ShouldBeFalse();
        controller.SupportState.ShouldBe(CharacterSupportState.TooSteep);
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
        controller.LastConsumedDisplacement.X.ShouldBe(0.001f, 0.0001f);
        controller.Position.X.ShouldBeGreaterThan(0.0f);
    }

    [Test]
    public void IdleFixedStep_RefreshesSupportWithoutAQueuedMove()
    {
        CreateFloor();
        JoltCharacterVirtualController controller = CreateController(new Vector3(0.0f, 0.55f, 0.0f));

        controller.Move(-Vector3.UnitY * 0.2f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);
        controller.IsGrounded.ShouldBeTrue();

        Vector3 groundedPosition = controller.Position;
        controller.ConsumeInputBuffer(FixedDelta);

        controller.IsGrounded.ShouldBeTrue();
        Vector3.Distance(controller.Position, groundedPosition).ShouldBeLessThan(0.001f);
    }

    [Test]
    public void VelocityAndDisplacementInput_ProduceEquivalentFreeMotion()
    {
        JoltCharacterVirtualController displacement = CreateController(Vector3.Zero);
        JoltCharacterVirtualController velocity = CreateController(new Vector3(0.0f, 0.0f, 2.0f));
        displacement.MotionInputModel = CharacterMotionInputModel.Displacement;
        velocity.MotionInputModel = CharacterMotionInputModel.Velocity;

        displacement.Move(Vector3.UnitX * 0.1f, 0.0f, FixedDelta);
        velocity.Move(Vector3.UnitX * (0.1f / FixedDelta), 0.0f, FixedDelta);
        displacement.ConsumeInputBuffer(FixedDelta);
        velocity.ConsumeInputBuffer(FixedDelta);

        displacement.Position.X.ShouldBe(velocity.Position.X, 0.0001f);
        displacement.RequestedVelocity.X.ShouldBe(velocity.RequestedVelocity.X, 0.0001f);
    }

    [Test]
    public void TotalHeight_IsMeasuredAcrossTheCompleteCapsule()
    {
        JoltCharacterVirtualController controller = CreateController(new Vector3(0.0f, 2.0f, 0.0f));
        controller.Radius = 0.3f;
        controller.TotalHeight = 1.8f;
        controller.ContactOffset = 0.02f;

        controller.CylinderHeight.ShouldBe(1.2f, 0.0001f);
        (controller.Position.Y - controller.FootPosition.Y).ShouldBe(0.92f, 0.0001f);
    }

    [Test]
    public void ZUpController_UsesSupportAndCollisionFlagsRelativeToUpDirection()
    {
        _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(new Vector3(5.0f, 5.0f, 0.5f)),
            (new Vector3(0.0f, 0.0f, -0.5f), Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            LayerMask.Everything).ShouldNotBeNull();
        JoltCharacterVirtualController controller = CreateController(
            new Vector3(0.0f, 0.0f, 0.55f),
            Vector3.UnitZ);

        controller.Move(-Vector3.UnitZ * 0.2f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);

        controller.IsGrounded.ShouldBeTrue();
        controller.CollidingDown.ShouldBeTrue();
        Vector3.Dot(controller.GroundNormal, Vector3.UnitZ).ShouldBeGreaterThan(0.9f);
    }

    [Test]
    public void NonAxisAlignedUp_UsesRotatedShapeAndSupportingVolume()
    {
        Vector3 up = Vector3.Normalize(new Vector3(1.0f, 1.0f, 1.0f));
        Quaternion floorRotation = XRMath.RotationBetweenVectors(Vector3.UnitY, up);
        _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(new Vector3(5.0f, 0.5f, 5.0f)),
            (-up * 0.5f, floorRotation),
            Vector3.Zero,
            Quaternion.Identity,
            LayerMask.Everything).ShouldNotBeNull();
        JoltCharacterVirtualController controller = CreateController(up * 0.55f, up);

        controller.Move(-up * 0.2f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);

        controller.IsGrounded.ShouldBeTrue();
        controller.CollidingDown.ShouldBeTrue();
        Vector3.Dot(controller.GroundNormal, up).ShouldBeGreaterThan(0.9f);
    }

    [Test]
    public void IndependentJoltSettings_DoNotAliasPaddingOrStepHeight()
    {
        JoltCharacterVirtualController controller = CreateController(Vector3.Zero);

        controller.ContactOffset = 0.03f;
        controller.PredictiveContactDistance = 0.17f;
        controller.CollisionTolerance = 0.004f;
        controller.StickToFloorDistance = 0.21f;
        controller.StepOffset = 0.28f;
        controller.StepDownExtra = 0.06f;

        controller.ContactOffset.ShouldBe(0.03f);
        controller.PredictiveContactDistance.ShouldBe(0.17f);
        controller.CollisionTolerance.ShouldBe(0.004f);
        controller.StickToFloorDistance.ShouldBe(0.21f);
        controller.StepOffset.ShouldBe(0.28f);
        controller.StepDownExtra.ShouldBe(0.06f);
    }

    [Test]
    public void CollisionLayerMask_FiltersCharacterContactsAndUpdatesAtRuntime()
    {
        _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(new Vector3(5.0f, 0.5f, 5.0f)),
            (new Vector3(0.0f, -0.5f, 0.0f), Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            new LayerMask(1)).ShouldNotBeNull();
        JoltCharacterVirtualController controller = CreateController(new Vector3(0.0f, 0.55f, 0.0f));
        controller.CollisionLayerMask = new LayerMask(2);

        controller.Move(-Vector3.UnitY * 0.2f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);
        controller.IsGrounded.ShouldBeFalse();

        controller.Position = new Vector3(0.0f, 0.55f, 0.0f);
        controller.CollisionLayerMask = new LayerMask(1);
        controller.Move(-Vector3.UnitY * 0.2f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);
        controller.IsGrounded.ShouldBeTrue();
    }

    [Test]
    public void BackendCreation_RejectsInvalidCapsuleSettingsBeforeNativeCreation()
    {
        PhysicsCharacterControllerCreateInfo invalid = new(
            Vector3.Zero,
            Vector3.UnitY,
            0.5f,
            0.75f,
            0.7f,
            0.02f,
            0.3f,
            1.0f,
            null);

        Should.Throw<ArgumentOutOfRangeException>(() =>
            _scene!.BackendService.CreateCharacterController(in invalid));
    }

    [Test]
    public void BackendCapabilities_ExposeJoltSupportAndUnsupportedPhysxFeatures()
    {
        PhysicsCharacterControllerCapabilities capabilities =
            _scene!.BackendService.CharacterControllerCapabilities;

        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.VelocityInput).ShouldBeTrue();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.DisplacementInput).ShouldBeTrue();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.MovingGround).ShouldBeTrue();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.MaximumStrength).ShouldBeTrue();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.Materials).ShouldBeFalse();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.CharacterVsCharacter).ShouldBeFalse();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.QueryVisibility).ShouldBeFalse();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.InvisibleWalls).ShouldBeFalse();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.ConstrainedClimbing).ShouldBeFalse();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.ScaleCoefficient).ShouldBeFalse();
        capabilities.HasFlag(PhysicsCharacterControllerCapabilities.VolumeGrowth).ShouldBeFalse();
    }

    [Test]
    public void IdleController_FollowsAcceleratingGroundWithoutDoubleApplyingVelocity()
    {
        JoltDynamicRigidBody platform = CreateDynamicPlatform();
        JoltCharacterVirtualController controller = CreateController(new Vector3(0.0f, 0.55f, 0.0f));
        GroundController(controller);

        platform.SetLinearVelocity(Vector3.UnitX * 0.5f);
        AdvancePhysics(controller);
        float firstStepX = controller.Position.X;

        platform.SetLinearVelocity(Vector3.UnitX * 1.5f);
        AdvancePhysics(controller);
        float secondStepDistance = controller.Position.X - firstStepX;

        controller.IsGrounded.ShouldBeTrue();
        controller.GroundActor.ShouldBeSameAs(platform);
        controller.GroundVelocity.X.ShouldBe(1.5f, 0.05f);
        firstStepX.ShouldBe(0.5f * FixedDelta, 0.002f);
        secondStepDistance.ShouldBe(1.5f * FixedDelta, 0.003f);
    }

    [TestCase(0.5f)]
    [TestCase(1.5f)]
    public void IdleController_InheritsRotatingGroundPointVelocity(float platformRadius)
    {
        JoltDynamicRigidBody platform = CreateDynamicPlatform();
        JoltCharacterVirtualController controller = CreateController(
            new Vector3(platformRadius, 0.55f, 0.0f));
        GroundController(controller);

        platform.SetAngularVelocity(Vector3.UnitY);
        AdvancePhysics(controller);

        float expectedTangentialSpeed = platformRadius;
        controller.IsGrounded.ShouldBeTrue();
        controller.GroundActor.ShouldBeSameAs(platform);
        controller.GroundVelocity.Z.ShouldBe(-expectedTangentialSpeed, 0.08f);
        controller.Position.Z.ShouldBe(-expectedTangentialSpeed * FixedDelta, 0.005f);
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
        controller.SupportStateValue = CharacterSupportState.Supported;
        synchronize.Invoke(component, null);
        synchronize.Invoke(component, null);
        controller.CollidingDownValue = false;
        controller.CollidingUpValue = true;
        controller.SupportStateValue = CharacterSupportState.InAir;
        synchronize.Invoke(component, null);

        transitions.ShouldBe([
            new CharacterControllerContactState(false, true, false, CharacterSupportState.Supported),
            new CharacterControllerContactState(true, false, false, CharacterSupportState.InAir),
        ]);
    }

    private JoltCharacterVirtualController CreateController(Vector3 position)
        => CreateController(position, Vector3.UnitY);

    private JoltCharacterVirtualController CreateController(Vector3 position, Vector3 upDirection)
        => new(_scene!, position, upDirection)
        {
            Radius = 0.25f,
            TotalHeight = 1.0f,
            ContactOffset = 0.02f,
            StepOffset = 0.3f,
            SlopeLimit = MathF.Cos(MathF.PI / 4.0f),
            MotionInputModel = CharacterMotionInputModel.Displacement,
        };

    private void CreateFloor()
        => _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(new Vector3(5.0f, 0.5f, 5.0f)),
            (new Vector3(0.0f, -0.5f, 0.0f), Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            LayerMask.Everything).ShouldNotBeNull();

    private JoltDynamicRigidBody CreateDynamicPlatform()
    {
        PhysicsRigidBodyCreateInfo createInfo = new(
            Array.Empty<PhysicsColliderShape>(),
            new IPhysicsGeometry.Box(new Vector3(3.0f, 0.1f, 3.0f)),
            null,
            null,
            (new Vector3(0.0f, -0.1f, 0.0f), Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            1000.0f,
            LayerMask.Everything)
        {
            GravityEnabled = false,
        };
        return _scene!.CreateDynamicRigidBody(in createInfo)
            .ShouldBeOfType<JoltDynamicRigidBody>();
    }

    private static void GroundController(JoltCharacterVirtualController controller)
    {
        controller.Move(-controller.UpDirection * 0.2f, 0.0f, FixedDelta);
        controller.ConsumeInputBuffer(FixedDelta);
        controller.IsGrounded.ShouldBeTrue();
    }

    private void AdvancePhysics(JoltCharacterVirtualController controller)
    {
        controller.ConsumeInputBuffer(FixedDelta);
        _scene!.PhysicsSystem!.Update(FixedDelta, 3, _scene.JobSystem!);
    }

    private sealed class FakeController : IAbstractCharacterController
    {
        public bool CollidingUpValue { get; set; }
        public bool CollidingDownValue { get; set; }
        public CharacterSupportState SupportStateValue { get; set; } = CharacterSupportState.Unknown;
        public Vector3 Position { get; set; }
        public Vector3 FootPosition { get; set; }
        public Vector3 UpDirection { get; set; } = Vector3.UnitY;
        public float Radius { get; set; }
        public float TotalHeight { get; private set; }
        public float SlopeLimit { get; set; }
        public float StepOffset { get; set; }
        public float ContactOffset { get; set; }
        public CharacterMotionInputModel MotionInputModel { get; set; }
        public PhysicsCharacterControllerCapabilities Capabilities
            => PhysicsCharacterControllerCapabilities.DisplacementInput
                | PhysicsCharacterControllerCapabilities.VelocityInput;
        public CharacterSupportState SupportState => SupportStateValue;
        public bool IsGrounded => SupportState == CharacterSupportState.Supported;
        public bool CollidingUp => CollidingUpValue;
        public bool CollidingDown => CollidingDownValue;
        public bool CollidingSides => false;
        public Vector3 GroundNormal => UpDirection;
        public Vector3 GroundVelocity => Vector3.Zero;
        public IAbstractRigidPhysicsActor? GroundActor => null;
        public CharacterMotionCommand LastMotionCommand { get; private set; }
        public Vector3 RequestedVelocity { get; private set; }
        public Vector3 EffectiveVelocity => LinearVelocity;
        public (Vector3 position, Quaternion rotation) Transform => (Position, Quaternion.Identity);
        public Vector3 LinearVelocity => Vector3.Zero;
        public Vector3 AngularVelocity => Vector3.Zero;
        public bool IsSleeping => false;
        public void SubmitMotion(in CharacterMotionCommand command)
        {
            LastMotionCommand = command;
            Vector3 displacement = command.InputModel == CharacterMotionInputModel.Velocity
                ? command.Value * command.ElapsedTime
                : command.Value;
            RequestedVelocity = displacement / command.ElapsedTime;
            Position += displacement;
        }
        public void Move(Vector3 value, float minDist, float elapsedTime)
            => SubmitMotion(new CharacterMotionCommand(value, MotionInputModel, minDist, elapsedTime));
        public void Resize(float totalHeight) => TotalHeight = totalHeight;
        public void RequestRelease() { }
        public void Destroy(bool wakeOnLostTouch = false) { }
    }
}

using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Physics;

public sealed class PhysicsP0ApiContractTests
{
    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = TestContext.CurrentContext.WorkDirectory;
        while (!File.Exists(Path.Combine(root, "XRENGINE.slnx")))
        {
            DirectoryInfo? parent = Directory.GetParent(root);
            parent.ShouldNotBeNull($"Unable to locate workspace root while reading {relativePath}.");
            root = parent.FullName;
        }

        return File.ReadAllText(Path.Combine(root, relativePath)).Replace("\r\n", "\n");
    }

    [Test]
    public void CharacterMovement_ExposesBackendNeutralControllerSurface()
    {
        string source = ReadWorkspaceFile("XRENGINE/Scene/Components/Movement/CharacterMovementComponent.cs");

        source.ShouldContain("public IAbstractCharacterController? CharacterController => ActiveController;");
        source.ShouldContain("public IAbstractDynamicRigidBody? RigidBodyReference");
        source.ShouldContain("[Category(\"Physics / PhysX Extensions\")]");
        source.ShouldContain("public PhysxCapsuleController? Controller => _physxController;");
    }

    [Test]
    public void AbstractPhysicsScene_DefinesPortableQueryAndControllerContracts()
    {
        string source = ReadWorkspaceFile("XRENGINE/Scene/Physics/AbstractPhysicsScene.cs");

        source.ShouldContain("public readonly struct PhysicsQueryFilter(");
        source.ShouldContain("public interface IAbstractCharacterController : IAbstractRigidPhysicsActor");
        source.ShouldContain("PhysicsQueryActorTypes ActorTypes { get; }");
        source.ShouldContain("PhysicsQueryHitDetail HitDetail { get; }");
    }

    [Test]
    public void JoltSweepQueries_UseNeutralQueryFilterInsteadOfPhysxCompatibilityFlags()
    {
        string source = ReadWorkspaceFile("XRENGINE/Scene/Physics/Jolt/JoltScene.cs");

        source.ShouldContain("GetQueryActorTypeInclusion(filter, out bool includeStatic, out bool includeDynamic);");
        source.ShouldNotContain("PhysxScene.PhysxQueryFilter physxFilter");
        source.ShouldNotContain("MagicPhysX.PxQueryFlags.Static");
    }
    [Test]
    public void PhysicsP1_DefinesReusableControllerAndColliderAuthoringContracts()
    {
        string controller = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/CharacterControllerComponent.cs");
        string authoring = ReadWorkspaceFile("XRENGINE/Scene/Physics/PhysicsAuthoring.cs");
        string dynamicBody = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/DynamicRigidBodyComponent.cs");

        controller.ShouldContain("public class CharacterControllerComponent : XRComponent");
        controller.ShouldContain("public event Action<CharacterControllerComponent, IAbstractCharacterController>? ControllerCreated;");
        controller.ShouldContain("public event Action<CharacterControllerComponent, CharacterControllerContactState>? ContactStateChanged;");
        authoring.ShouldContain("public class PhysicsMaterialDefinition : XRBase");
        authoring.ShouldContain("public class PhysicsColliderShape : XRBase");
        dynamicBody.ShouldContain("public List<PhysicsColliderShape> ColliderShapes");
        dynamicBody.ShouldContain("public void RebuildCollisionShapes(bool wakeOnLostTouch = true)");
    }

    [Test]
    public void PhysicsP2_DefinesProductionHardeningContracts()
    {
        string authoring = ReadWorkspaceFile("XRENGINE/Scene/Physics/PhysicsAuthoring.cs");
        string dynamicBody = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/DynamicRigidBodyComponent.cs");
        string staticBody = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/StaticRigidBodyComponent.cs");
        string joltScene = ReadWorkspaceFile("XRENGINE/Scene/Physics/Jolt/JoltScene.cs");

        authoring.ShouldContain("public enum PhysicsReplicationAuthority");
        dynamicBody.ShouldContain("public PhysicsReplicationAuthority ReplicationAuthority");
        staticBody.ShouldContain("public PhysicsReplicationAuthority ReplicationAuthority");
        joltScene.ShouldContain("public JoltPhysicsDiagnostics GetDiagnostics()");
        joltScene.ShouldContain("public override void DebugRenderCollect()");
        joltScene.ShouldContain("public readonly record struct JoltPhysicsDiagnostics");
    }

}

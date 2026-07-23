using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;

namespace XREngine.UnitTests.Physics;

public sealed class PhysicsBackendBoundaryTests
{
    [Test]
    public void NeutralPhysicsContracts_LiveOutsideBackendNamespaces()
    {
        Type[] neutralTypes =
        [
            typeof(PhysicsMaterialDefinition),
            typeof(PhysicsColliderShape),
            typeof(PhysicsReplicationAuthority),
            typeof(IAbstractCharacterController),
            typeof(IPhysicsQueryFilter),
            typeof(IPhysicsGeometry),
            typeof(IAbstractJoint),
            typeof(IPhysicsBackendService),
            typeof(IPhysicsReplicationTarget),
        ];

        foreach (Type type in neutralTypes)
        {
            type.Namespace.ShouldStartWith("XREngine.Scene.Physics");
            type.Namespace.ShouldNotContain("Physx", Case.Insensitive);
            type.Namespace.ShouldNotContain("Jolt", Case.Insensitive);
        }
    }

    [Test]
    public void GameplayComponentApis_DoNotExposeBackendTypesUnlessExplicitlyBackendNamed()
    {
        Assembly engineAssembly = typeof(XRComponent).Assembly;
        List<string> violations = [];

        foreach (Type type in engineAssembly.GetExportedTypes())
        {
            if (type.Namespace?.StartsWith("XREngine.Components", StringComparison.Ordinal) != true)
                continue;

            const BindingFlags flags = BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly;

            foreach (MemberInfo member in type.GetMembers(flags))
            {
                if (!IsGameplayApiMember(member) || IsExplicitBackendExtension(type.Name, member.Name))
                    continue;

                foreach (Type exposedType in GetExposedTypes(member))
                {
                    if (ContainsBackendType(exposedType))
                    {
                        violations.Add(
                            $"{type.FullName}.{member.Name} exposes {exposedType.FullName ?? exposedType.Name}");
                    }
                }
            }
        }

        violations.ShouldBeEmpty(
            "Gameplay APIs must expose neutral physics contracts. Backend extension types/members must be explicitly named Physx/Jolt.");
    }

    [Test]
    public void GameplayFactories_RequestBackendServicesInsteadOfConcreteScenes()
    {
        string characterController = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Scene/Components/Physics/CharacterControllerComponent.cs");
        string characterMovement = ReadWorkspaceFile(
            "XRENGINE/Scene/Components/Movement/CharacterMovementComponent.cs");
        string dynamicBody = ReadWorkspaceFile(
            "XRENGINE/Scene/Components/Physics/DynamicRigidBodyComponent.cs");
        string staticBody = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Scene/Components/Physics/StaticRigidBodyComponent.cs");

        characterController.ShouldContain("physicsScene.BackendService.CreateCharacterController(");
        characterController.ShouldContain("World is not IRuntimePhysicsWorldContext physicsWorld");
        characterController.ShouldContain("RuntimeThreadServices.Current.EnqueuePhysicsThread(");
        characterController.ShouldContain("RuntimeThreadServices.Current.EnqueueUpdateThread(");
        characterController.ShouldNotContain("XRWorldInstance");
        characterController.ShouldNotContain("Engine.Enqueue");
        characterController.ShouldNotContain("physicsScene is PhysxScene");
        characterController.ShouldNotContain("physicsScene is JoltScene");

        characterMovement.ShouldContain("physicsScene.BackendService.CreateCharacterController(");
        characterMovement.ShouldNotContain("physicsScene is PhysxScene");
        characterMovement.ShouldNotContain("physicsScene is JoltScene");
        characterMovement.ShouldNotContain("new JoltCharacterVirtualController");
        characterMovement.ShouldNotContain("GetOrCreateControllerManager");

        dynamicBody.ShouldContain("physicsScene.BackendService.CreateDynamicRigidBody(");
        dynamicBody.ShouldNotContain("CreatePhysxDynamicRigidBody");
        dynamicBody.ShouldNotContain("CreateJoltDynamicRigidBody");

        staticBody.ShouldContain("scene.BackendService.CreateStaticRigidBody(");
        staticBody.ShouldNotContain("CreatePhysxStaticRigidBody");
        staticBody.ShouldNotContain("CreateJoltStaticRigidBody");
        staticBody.ShouldContain("World is IRuntimePhysicsWorldContext physicsWorld");
        staticBody.ShouldContain("RuntimeThreadServices.Current.EnqueuePhysicsThread(");
        staticBody.ShouldNotContain("XRWorldInstance");
        staticBody.ShouldNotContain("PhysxStaticRigidBody");
        staticBody.ShouldNotContain("JoltStaticRigidBody");
    }

    [Test]
    public void GameplayPhysicsConsumers_UseNeutralQueriesAndBodyMutationContracts()
    {
        string boom = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Transforms/Misc/Boom.cs");
        string boostVolume = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Components/Volumes/BoostVolumeComponent.cs");
        string gravityVolume = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Components/Volumes/GravityVolumeComponent.cs");

        boom.ShouldContain("private PhysicsQueryFilter _queryFilter");
        boom.ShouldContain("public PhysicsQueryFilter QueryFilter");
        boom.ShouldNotContain("PhysxScene");
        boom.ShouldNotContain("JoltScene");
        boom.ShouldNotContain("PhysxRigidBody");
        boom.ShouldNotContain("JoltRigidBody");

        boostVolume.ShouldContain("IAbstractDynamicRigidBody? body = rb.RigidBody;");
        boostVolume.ShouldContain("body?.SetLinearVelocity(");
        boostVolume.ShouldNotContain("PhysxScene");
        boostVolume.ShouldNotContain("JoltScene");
        boostVolume.ShouldNotContain("PhysxDynamicRigidBody");
        boostVolume.ShouldNotContain("JoltDynamicRigidBody");

        gravityVolume.ShouldContain("body?.SetLinearVelocity(");
        gravityVolume.ShouldContain("body.GravityEnabled = false;");
        gravityVolume.ShouldNotContain("PhysxScene");
        gravityVolume.ShouldNotContain("JoltScene");
        gravityVolume.ShouldNotContain("PhysxDynamicRigidBody");
        gravityVolume.ShouldNotContain("JoltDynamicRigidBody");
    }

    [Test]
    public void NeutralContractSources_DoNotImportBackendNamespaces()
    {
        string[] contractFiles =
        [
            "XREngine.Runtime.Core/Scene/Physics/IPhysicsGeometry.cs",
            "XREngine.Runtime.Core/Scene/Physics/PhysicsAuthoring.cs",
            "XREngine.Runtime.Core/Scene/Physics/PhysicsContracts.cs",
            "XREngine.Runtime.Core/Scene/Physics/PhysicsBackendService.cs",
            "XREngine.Runtime.Core/Scene/Physics/PhysicsMaterial.cs",
            "XREngine.Runtime.Core/Scene/Physics/PhysicsMeshGeometry.cs",
            "XREngine.Runtime.Core/Scene/Physics/Joints/IAbstractJoint.cs",
        ];

        foreach (string file in contractFiles)
        {
            string source = ReadWorkspaceFile(file);
            Assert.That(source, Does.Not.Contain("XREngine.Scene.Physics.Physx"), file);
            Assert.That(source, Does.Not.Contain("MagicPhysX"), file);
            Assert.That(source, Does.Not.Contain("JoltPhysicsSharp"), file);
        }
    }

    [Test]
    public void NeutralGeometryContracts_DoNotOwnNativeBackendConversions()
    {
        typeof(IPhysicsGeometry).GetMethods().ShouldBeEmpty();

        Type[] authoredGeometryTypes =
        [
            typeof(IPhysicsGeometry.Sphere),
            typeof(IPhysicsGeometry.Box),
            typeof(IPhysicsGeometry.Capsule),
            typeof(IPhysicsGeometry.Plane),
            typeof(PhysicsConvexHullGeometry),
            typeof(PhysicsTriangleMeshGeometry),
            typeof(PhysicsHeightFieldGeometry),
        ];

        foreach (Type geometryType in authoredGeometryTypes)
        {
            MemberInfo[] backendNamedMembers = geometryType
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(static member => HasBackendName(member.Name))
                .ToArray();
            backendNamedMembers.ShouldBeEmpty(
                $"{geometryType.FullName} must remain portable; native conversion belongs in backend adapters.");
        }
    }

    [Test]
    public void ActorDependentJoints_RebindWhenActorsBecomeAvailableOrChange()
    {
        string actorComponent = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Scene/Components/Physics/PhysicsActorComponent.cs");
        string jointComponent = ReadWorkspaceFile(
            "XRENGINE/Scene/Components/Physics/Joints/PhysicsJointComponent.cs");

        actorComponent.ShouldContain("public abstract IAbstractPhysicsActor? PhysicsActor { get; }");
        actorComponent.ShouldContain("PhysicsActorChanged;");
        jointComponent.ShouldContain("PhysicsActorChanged += Actor_PhysicsActorChanged;");
        jointComponent.ShouldContain("protected virtual void RebindJoint()");
        jointComponent.ShouldContain("(_localBody is not null && actorA is null)");
        jointComponent.ShouldContain("(_connectedBody is not null && actorB is null)");
    }

    private static bool IsGameplayApiMember(MemberInfo member)
        => member switch
        {
            MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
            FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
            PropertyInfo property => IsGameplayAccessor(property.GetMethod) || IsGameplayAccessor(property.SetMethod),
            EventInfo @event => IsGameplayAccessor(@event.AddMethod) || IsGameplayAccessor(@event.RemoveMethod),
            Type nestedType => nestedType.IsNestedPublic || nestedType.IsNestedFamily || nestedType.IsNestedFamORAssem,
            _ => false,
        };

    private static bool IsGameplayAccessor(MethodInfo? method)
        => method is not null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);

    private static bool IsExplicitBackendExtension(string declaringTypeName, string memberName)
        => HasBackendName(declaringTypeName) || HasBackendName(memberName);

    private static bool HasBackendName(string name)
        => name.Contains("Physx", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Jolt", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Type> GetExposedTypes(MemberInfo member)
    {
        switch (member)
        {
            case MethodInfo method:
                yield return method.ReturnType;
                foreach (ParameterInfo parameter in method.GetParameters())
                    yield return parameter.ParameterType;
                break;
            case ConstructorInfo constructor:
                foreach (ParameterInfo parameter in constructor.GetParameters())
                    yield return parameter.ParameterType;
                break;
            case PropertyInfo property:
                yield return property.PropertyType;
                foreach (ParameterInfo parameter in property.GetIndexParameters())
                    yield return parameter.ParameterType;
                break;
            case FieldInfo field:
                yield return field.FieldType;
                break;
            case EventInfo @event when @event.EventHandlerType is not null:
                yield return @event.EventHandlerType;
                break;
            case Type nestedType:
                yield return nestedType;
                break;
        }
    }

    private static bool ContainsBackendType(Type type)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
            return ContainsBackendType(type.GetElementType()!);

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition != type && ContainsBackendType(definition))
                return true;
            foreach (Type argument in type.GetGenericArguments())
            {
                if (ContainsBackendType(argument))
                    return true;
            }
        }

        string fullName = type.FullName ?? type.Name;
        return fullName.StartsWith("MagicPhysX.", StringComparison.Ordinal)
            || fullName.Contains(".Physics.Physx.", StringComparison.Ordinal)
            || fullName.Contains(".Physics.Jolt.", StringComparison.Ordinal)
            || type.Name.StartsWith("Physx", StringComparison.OrdinalIgnoreCase)
            || type.Name.StartsWith("Jolt", StringComparison.OrdinalIgnoreCase)
            || (type.Namespace == "MagicPhysX" && type.Name.StartsWith("Px", StringComparison.Ordinal));
    }

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
}

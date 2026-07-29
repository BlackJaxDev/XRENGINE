using System.Numerics;
using System.Text;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Mesh.Shapes;
using XREngine.Components.Physics;
using XREngine.Components.VR;
using XREngine.Data.Colors;
using XREngine.Data.Components.Scene;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace MonkeyBallVR;

/// <summary>
/// Explicit, reflection-free cooked serializer for the asset-authored MonkeyBall world graph.
/// </summary>
internal static class MonkeyBallWorldCookedSerializer
{
    private const uint Magic = 0x4D425752;
    private const int Version = 3;
    private const int MaximumCollectionCount = 16_384;

    private enum TransformKind : byte
    {
        Standard,
        RigidBody,
        VrHeadset,
        VrController,
    }

    private enum ComponentKind : byte
    {
        Game,
        Pawn,
        DebugDraw,
        Camera,
        VrHeadset,
        VrControllerModel,
        VrTrackers,
        DirectionalLight,
        DynamicRigidBody,
        BoxMesh,
        SphereMesh,
    }

    private enum ShapeKind : byte
    {
        Box,
        Sphere,
        Cylinder,
        Line,
    }

    private enum GeometryKind : byte
    {
        None,
        Box,
        Sphere,
        Capsule,
        Plane,
    }

    public static byte[] Serialize(MonkeyBallWorldAsset world)
    {
        ArgumentNullException.ThrowIfNull(world);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        WriteString(writer, world.Name);
        WriteWorldSettings(writer, world.Settings);
        writer.Write(world.Scenes.Count);
        for (int i = 0; i < world.Scenes.Count; i++)
            WriteScene(writer, world.Scenes[i]);
        writer.Flush();
        return stream.ToArray();
    }

    public static MonkeyBallWorldAsset Deserialize(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using MemoryStream stream = new(payload, writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("The cooked MonkeyBall world has an invalid signature.");

        int version = reader.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"Unsupported cooked MonkeyBall world version {version}.");

        MonkeyBallWorldAsset world = new()
        {
            Name = ReadString(reader),
            Settings = ReadWorldSettings(reader),
        };
        int sceneCount = ReadCount(reader, "scene");
        for (int i = 0; i < sceneCount; i++)
            world.Scenes.Add(ReadScene(reader));

        if (stream.Position != stream.Length)
            throw new InvalidDataException("The cooked MonkeyBall world contains trailing data.");
        return world;
    }

    private static void WriteWorldSettings(BinaryWriter writer, WorldSettings settings)
    {
        WriteVector3(writer, settings.Gravity);
        writer.Write(settings.PhysicsResetMinYDist);
        writer.Write(settings.PhysicsTimestep);
        writer.Write(settings.PhysicsSubsteps);
        writer.Write(settings.DefaultLinearDamping);
        writer.Write(settings.DefaultAngularDamping);
        writer.Write(settings.DefaultFriction);
        writer.Write(settings.DefaultRestitution);
        writer.Write(settings.EnableContinuousCollision);
        writer.Write(settings.TimeDilation);
        WriteVector3(writer, settings.Bounds.Min);
        WriteVector3(writer, settings.Bounds.Max);
        WriteString(writer, settings.SkyboxTexturePath);
        writer.Write(settings.SkyboxRotation);
        writer.Write(settings.SkyboxIntensity);
        writer.Write(settings.RenderSkybox);
        WriteColor(writer, settings.ClearColor);
        WriteColor(writer, settings.AmbientLightColor);
        writer.Write(settings.AmbientLightIntensity);
        writer.Write(settings.EnvironmentLightingIntensity);
        writer.Write(settings.ReflectionIntensity);
        writer.Write(settings.LightProbeResolution);
        writer.Write(settings.AutoCaptureLightProbes);
        writer.Write(settings.EnableFog);
        WriteColor(writer, settings.FogColor);
        writer.Write(settings.FogDensity);
        writer.Write(settings.FogStartDistance);
        writer.Write(settings.FogEndDistance);
        writer.Write((int)settings.FogMode);
        writer.Write(settings.FogHeightFalloff);
        writer.Write(settings.FogBaseHeight);
        writer.Write(settings.SpeedOfSound);
        writer.Write(settings.DopplerFactor);
        writer.Write(settings.DefaultAudioAttenuation);
        writer.Write(settings.MasterVolume);
        writer.Write(settings.PreviewWorldBounds);
        writer.Write(settings.PreviewOctrees);
        writer.Write(settings.PreviewQuadtrees);
        writer.Write(settings.PreviewPhysics);
        writer.Write(settings.PreviewLightProbes);
    }

    private static WorldSettings ReadWorldSettings(BinaryReader reader)
        => new()
        {
            Gravity = ReadVector3(reader),
            PhysicsResetMinYDist = reader.ReadSingle(),
            PhysicsTimestep = reader.ReadSingle(),
            PhysicsSubsteps = reader.ReadInt32(),
            DefaultLinearDamping = reader.ReadSingle(),
            DefaultAngularDamping = reader.ReadSingle(),
            DefaultFriction = reader.ReadSingle(),
            DefaultRestitution = reader.ReadSingle(),
            EnableContinuousCollision = reader.ReadBoolean(),
            TimeDilation = reader.ReadSingle(),
            Bounds = new AABB(ReadVector3(reader), ReadVector3(reader)),
            SkyboxTexturePath = ReadString(reader),
            SkyboxRotation = reader.ReadSingle(),
            SkyboxIntensity = reader.ReadSingle(),
            RenderSkybox = reader.ReadBoolean(),
            ClearColor = ReadColor(reader),
            AmbientLightColor = ReadColor3(reader),
            AmbientLightIntensity = reader.ReadSingle(),
            EnvironmentLightingIntensity = reader.ReadSingle(),
            ReflectionIntensity = reader.ReadSingle(),
            LightProbeResolution = reader.ReadUInt32(),
            AutoCaptureLightProbes = reader.ReadBoolean(),
            EnableFog = reader.ReadBoolean(),
            FogColor = ReadColor3(reader),
            FogDensity = reader.ReadSingle(),
            FogStartDistance = reader.ReadSingle(),
            FogEndDistance = reader.ReadSingle(),
            FogMode = (EFogMode)reader.ReadInt32(),
            FogHeightFalloff = reader.ReadSingle(),
            FogBaseHeight = reader.ReadSingle(),
            SpeedOfSound = reader.ReadSingle(),
            DopplerFactor = reader.ReadSingle(),
            DefaultAudioAttenuation = reader.ReadSingle(),
            MasterVolume = reader.ReadSingle(),
            PreviewWorldBounds = reader.ReadBoolean(),
            PreviewOctrees = reader.ReadBoolean(),
            PreviewQuadtrees = reader.ReadBoolean(),
            PreviewPhysics = reader.ReadBoolean(),
            PreviewLightProbes = reader.ReadBoolean(),
        };

    private static void WriteScene(BinaryWriter writer, XRScene scene)
    {
        WriteString(writer, scene.Name);
        writer.Write(scene.IsVisible);
        writer.Write(scene.RootNodes.Count);
        for (int i = 0; i < scene.RootNodes.Count; i++)
            WriteNode(writer, scene.RootNodes[i]);
    }

    private static XRScene ReadScene(BinaryReader reader)
    {
        XRScene scene = new(ReadString(reader))
        {
            IsVisible = reader.ReadBoolean(),
        };
        int rootCount = ReadCount(reader, "root node");
        for (int i = 0; i < rootCount; i++)
            scene.RootNodes.Add(ReadNode(reader, parent: null));
        return scene;
    }

    private static void WriteNode(BinaryWriter writer, SceneNode node)
    {
        WriteString(writer, node.Name);
        writer.Write(node.IsActiveSelf);
        writer.Write(node.IsEditorOnly);
        writer.Write(node.Layer);
        WriteTransform(writer, node.Transform);

        writer.Write(node.Components.Count);
        for (int i = 0; i < node.Components.Count; i++)
            WriteComponent(writer, node.Components[i]);

        SceneNode[] children = node.ChildNodesSerialized;
        int authoredChildCount = 0;
        for (int i = 0; i < children.Length; i++)
            if (!IsRuntimeOwnedVrEye(children[i]))
                authoredChildCount++;

        writer.Write(authoredChildCount);
        for (int i = 0; i < children.Length; i++)
            if (!IsRuntimeOwnedVrEye(children[i]))
                WriteNode(writer, children[i]);
    }

    private static SceneNode ReadNode(BinaryReader reader, SceneNode? parent)
    {
        string name = ReadString(reader);
        bool isActive = reader.ReadBoolean();
        bool isEditorOnly = reader.ReadBoolean();
        int layer = reader.ReadInt32();
        TransformBase transform = ReadTransform(reader);
        SceneNode node = new(name, transform)
        {
            IsActiveSelf = isActive,
            IsEditorOnly = isEditorOnly,
            Layer = layer,
        };
        if (parent is not null)
            node.Parent = parent;

        int componentCount = ReadCount(reader, "component");
        for (int i = 0; i < componentCount; i++)
            ReadComponent(reader, node);

        int childCount = ReadCount(reader, "child node");
        for (int i = 0; i < childCount; i++)
            ReadNode(reader, node);
        return node;
    }

    private static bool IsRuntimeOwnedVrEye(SceneNode node)
        => node.Transform is VREyeTransform &&
           (string.Equals(node.Name, "Left Eye", StringComparison.Ordinal) ||
            string.Equals(node.Name, "Right Eye", StringComparison.Ordinal));

    private static void WriteTransform(BinaryWriter writer, TransformBase transform)
    {
        switch (transform)
        {
            case RigidBodyTransform rigidBody:
                writer.Write((byte)TransformKind.RigidBody);
                WriteVector3(writer, rigidBody.Position);
                WriteQuaternion(writer, rigidBody.Rotation);
                writer.Write((int)rigidBody.InterpolationMode);
                return;
            case VRControllerTransform controller:
                writer.Write((byte)TransformKind.VrController);
                writer.Write(controller.LeftHand);
                return;
            case VRHeadsetTransform:
                writer.Write((byte)TransformKind.VrHeadset);
                return;
            case Transform standard:
                writer.Write((byte)TransformKind.Standard);
                WriteVector3(writer, standard.Translation);
                WriteQuaternion(writer, standard.Rotation);
                WriteVector3(writer, standard.Scale);
                return;
            default:
                throw new NotSupportedException(
                    $"MonkeyBall world cooking does not support transform '{transform.GetType().FullName}'.");
        }
    }

    private static TransformBase ReadTransform(BinaryReader reader)
        => (TransformKind)reader.ReadByte() switch
        {
            TransformKind.Standard => new Transform
            {
                Translation = ReadVector3(reader),
                Rotation = ReadQuaternion(reader),
                Scale = ReadVector3(reader),
            },
            TransformKind.RigidBody => new RigidBodyTransform
            {
                Position = ReadVector3(reader),
                Rotation = ReadQuaternion(reader),
                InterpolationMode = (RigidBodyTransform.EInterpolationMode)reader.ReadInt32(),
            },
            TransformKind.VrHeadset => new VRHeadsetTransform(),
            TransformKind.VrController => new VRControllerTransform
            {
                LeftHand = reader.ReadBoolean(),
            },
            var kind => throw new InvalidDataException($"Unknown MonkeyBall transform kind '{kind}'."),
        };

    private static void WriteComponent(BinaryWriter writer, XRComponent component)
    {
        switch (component)
        {
            case MonkeyBallGameComponent game:
                writer.Write((byte)ComponentKind.Game);
                WriteGameComponent(writer, game);
                break;
            case MonkeyBallPawnComponent:
                writer.Write((byte)ComponentKind.Pawn);
                break;
            case DynamicRigidBodyComponent rigidBody:
                writer.Write((byte)ComponentKind.DynamicRigidBody);
                WriteDynamicRigidBody(writer, rigidBody);
                break;
            case BoxMeshComponent boxMesh:
                writer.Write((byte)ComponentKind.BoxMesh);
                WriteBoxMesh(writer, boxMesh);
                break;
            case SphereMeshComponent sphereMesh:
                writer.Write((byte)ComponentKind.SphereMesh);
                WriteSphereMesh(writer, sphereMesh);
                break;
            case DebugDrawComponent debugDraw:
                writer.Write((byte)ComponentKind.DebugDraw);
                WriteDebugDrawComponent(writer, debugDraw);
                break;
            case CameraComponent camera:
                writer.Write((byte)ComponentKind.Camera);
                WriteCameraComponent(writer, camera);
                break;
            case VRHeadsetComponent:
                writer.Write((byte)ComponentKind.VrHeadset);
                break;
            case VRControllerModelComponent controllerModel:
                writer.Write((byte)ComponentKind.VrControllerModel);
                writer.Write(controllerModel.LeftHand);
                break;
            case VRTrackerCollectionComponent:
                writer.Write((byte)ComponentKind.VrTrackers);
                break;
            case DirectionalLightComponent directionalLight:
                writer.Write((byte)ComponentKind.DirectionalLight);
                WriteDirectionalLight(writer, directionalLight);
                break;
            default:
                throw new NotSupportedException(
                    $"MonkeyBall world cooking does not support component '{component.GetType().FullName}'.");
        }

        WriteString(writer, component.Name);
    }

    private static void ReadComponent(BinaryReader reader, SceneNode node)
    {
        ComponentKind kind = (ComponentKind)reader.ReadByte();
        XRComponent component = kind switch
        {
            ComponentKind.Game => ReadGameComponent(reader, node),
            ComponentKind.Pawn => node.AddComponent<MonkeyBallPawnComponent>(
                static () => new MonkeyBallPawnComponent())!,
            ComponentKind.DynamicRigidBody => ReadDynamicRigidBody(reader, node),
            ComponentKind.BoxMesh => ReadBoxMesh(reader, node),
            ComponentKind.SphereMesh => ReadSphereMesh(reader, node),
            ComponentKind.DebugDraw => ReadDebugDrawComponent(reader, node),
            ComponentKind.Camera => ReadCameraComponent(reader, node),
            ComponentKind.VrHeadset => node.AddComponent<VRHeadsetComponent>(
                static () => new VRHeadsetComponent())!,
            ComponentKind.VrControllerModel => ReadVrControllerModel(reader, node),
            ComponentKind.VrTrackers => node.AddComponent<VRTrackerCollectionComponent>(
                static () => new VRTrackerCollectionComponent())!,
            ComponentKind.DirectionalLight => ReadDirectionalLight(reader, node),
            _ => throw new InvalidDataException($"Unknown MonkeyBall component kind '{kind}'."),
        };
        component.Name = ReadString(reader);
    }

    private static void WriteGameComponent(BinaryWriter writer, MonkeyBallGameComponent game)
    {
        WriteString(writer, game.CourseNodeName);
        WriteString(writer, game.BallNodeName);
        WriteString(writer, game.DesktopCameraNodeName);
        WriteString(writer, game.HudNodeName);
        writer.Write(game.BallRadius);
        WriteVector2(writer, game.StartPosition);
        WriteVector2(writer, game.GoalPosition);
        writer.Write(game.GoalRadius);
        writer.Write(game.RoundDurationSeconds);
        writer.Write(game.MaxTiltDegrees);
        writer.Write(game.InitialLives);
        writer.Write(game.MaxBallSpeed);
        writer.Write(game.FallThresholdY);
        writer.Write(game.FallResetDelaySeconds);
        WriteVector3(writer, game.DesktopCameraOffset);
        writer.Write(game.DesktopCameraPitchDegrees);
        writer.Write(game.DesktopCameraYawResponse);
        writer.Write(game.CameraHeadingVelocityThreshold);
    }

    private static MonkeyBallGameComponent ReadGameComponent(BinaryReader reader, SceneNode node)
    {
        MonkeyBallGameComponent game = node.AddComponent<MonkeyBallGameComponent>(
            static () => new MonkeyBallGameComponent())!;
        game.CourseNodeName = ReadString(reader);
        game.BallNodeName = ReadString(reader);
        game.DesktopCameraNodeName = ReadString(reader);
        game.HudNodeName = ReadString(reader);
        game.BallRadius = reader.ReadSingle();
        game.StartPosition = ReadVector2(reader);
        game.GoalPosition = ReadVector2(reader);
        game.GoalRadius = reader.ReadSingle();
        game.RoundDurationSeconds = reader.ReadSingle();
        game.MaxTiltDegrees = reader.ReadSingle();
        game.InitialLives = reader.ReadInt32();
        game.MaxBallSpeed = reader.ReadSingle();
        game.FallThresholdY = reader.ReadSingle();
        game.FallResetDelaySeconds = reader.ReadSingle();
        game.DesktopCameraOffset = ReadVector3(reader);
        game.DesktopCameraPitchDegrees = reader.ReadSingle();
        game.DesktopCameraYawResponse = reader.ReadSingle();
        game.CameraHeadingVelocityThreshold = reader.ReadSingle();
        return game;
    }

    private static void WriteDynamicRigidBody(
        BinaryWriter writer,
        DynamicRigidBodyComponent body)
    {
        writer.Write(body.AutoCreateRigidBody);
        writer.Write(body.GravityEnabled);
        writer.Write(body.SimulationEnabled);
        writer.Write(body.DebugVisualization);
        writer.Write(body.SendSleepNotifies);
        writer.Write(body.CollisionGroup);
        WriteGroupsMask(writer, body.GroupsMask);
        writer.Write(body.DominanceGroup);
        WriteString(writer, body.ActorName);
        writer.Write((int)body.BodyFlags);
        writer.Write((int)body.LockFlags);
        writer.Write(body.Density);
        writer.Write(body.LinearDamping);
        writer.Write(body.AngularDamping);
        writer.Write(body.MaxLinearVelocity);
        writer.Write(body.MaxAngularVelocity);
        writer.Write(body.Mass);
        WriteVector3(writer, body.ShapeOffsetTranslation);
        WriteQuaternion(writer, body.ShapeOffsetRotation);
        WritePhysicsMaterial(writer, body.MaterialDefinition);
        WriteGeometry(writer, body.Geometry);

        writer.Write(body.ColliderShapes.Count);
        for (int i = 0; i < body.ColliderShapes.Count; i++)
            WriteColliderShape(writer, body.ColliderShapes[i]);
    }

    private static DynamicRigidBodyComponent ReadDynamicRigidBody(
        BinaryReader reader,
        SceneNode node)
    {
        DynamicRigidBodyComponent body = node.AddComponent<DynamicRigidBodyComponent>(
            static () => new DynamicRigidBodyComponent())!;
        body.AutoCreateRigidBody = reader.ReadBoolean();
        body.GravityEnabled = reader.ReadBoolean();
        body.SimulationEnabled = reader.ReadBoolean();
        body.DebugVisualization = reader.ReadBoolean();
        body.SendSleepNotifies = reader.ReadBoolean();
        body.CollisionGroup = reader.ReadUInt16();
        body.GroupsMask = ReadGroupsMask(reader);
        body.DominanceGroup = reader.ReadByte();
        body.ActorName = ReadString(reader);
        body.BodyFlags = (PhysicsRigidBodyFlags)reader.ReadInt32();
        body.LockFlags = (PhysicsLockFlags)reader.ReadInt32();
        body.Density = reader.ReadSingle();
        body.LinearDamping = reader.ReadSingle();
        body.AngularDamping = reader.ReadSingle();
        body.MaxLinearVelocity = reader.ReadSingle();
        body.MaxAngularVelocity = reader.ReadSingle();
        body.Mass = reader.ReadSingle();
        body.ShapeOffsetTranslation = ReadVector3(reader);
        body.ShapeOffsetRotation = ReadQuaternion(reader);
        body.MaterialDefinition = ReadPhysicsMaterial(reader);
        body.Geometry = ReadGeometry(reader);

        int colliderCount = ReadCount(reader, "collider");
        List<PhysicsColliderShape> colliders = new(colliderCount);
        for (int i = 0; i < colliderCount; i++)
            colliders.Add(ReadColliderShape(reader));
        body.ColliderShapes = colliders;
        return body;
    }

    private static void WriteColliderShape(
        BinaryWriter writer,
        PhysicsColliderShape shape)
    {
        writer.Write(shape.Enabled);
        WriteString(writer, shape.Name);
        WriteGeometry(writer, shape.Geometry);
        WritePhysicsMaterial(writer, shape.Material);
        WriteVector3(writer, shape.LocalPosition);
        WriteQuaternion(writer, shape.LocalRotation);
    }

    private static PhysicsColliderShape ReadColliderShape(BinaryReader reader)
        => new()
        {
            Enabled = reader.ReadBoolean(),
            Name = ReadString(reader),
            Geometry = ReadGeometry(reader),
            Material = ReadPhysicsMaterial(reader),
            LocalPosition = ReadVector3(reader),
            LocalRotation = ReadQuaternion(reader),
        };

    private static void WritePhysicsMaterial(
        BinaryWriter writer,
        PhysicsMaterialDefinition? material)
    {
        writer.Write(material is not null);
        if (material is null)
            return;

        writer.Write(material.StaticFriction);
        writer.Write(material.DynamicFriction);
        writer.Write(material.Restitution);
        writer.Write(material.Damping);
    }

    private static PhysicsMaterialDefinition? ReadPhysicsMaterial(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;

        return new PhysicsMaterialDefinition
        {
            StaticFriction = reader.ReadSingle(),
            DynamicFriction = reader.ReadSingle(),
            Restitution = reader.ReadSingle(),
            Damping = reader.ReadSingle(),
        };
    }

    private static void WriteGeometry(BinaryWriter writer, IPhysicsGeometry? geometry)
    {
        switch (geometry)
        {
            case null:
                writer.Write((byte)GeometryKind.None);
                return;
            case IPhysicsGeometry.Box box:
                writer.Write((byte)GeometryKind.Box);
                WriteVector3(writer, box.HalfExtents);
                return;
            case IPhysicsGeometry.Sphere sphere:
                writer.Write((byte)GeometryKind.Sphere);
                writer.Write(sphere.Radius);
                return;
            case IPhysicsGeometry.Capsule capsule:
                writer.Write((byte)GeometryKind.Capsule);
                writer.Write(capsule.Radius);
                writer.Write(capsule.HalfHeight);
                return;
            case IPhysicsGeometry.Plane plane:
                writer.Write((byte)GeometryKind.Plane);
                WriteVector3(writer, plane.PlaneData.Normal);
                writer.Write(plane.PlaneData.D);
                return;
            default:
                throw new NotSupportedException(
                    $"MonkeyBall world cooking does not support physics geometry '{geometry.GetType().FullName}'.");
        }
    }

    private static IPhysicsGeometry? ReadGeometry(BinaryReader reader)
        => (GeometryKind)reader.ReadByte() switch
        {
            GeometryKind.None => null,
            GeometryKind.Box => new IPhysicsGeometry.Box(ReadVector3(reader)),
            GeometryKind.Sphere => new IPhysicsGeometry.Sphere(reader.ReadSingle()),
            GeometryKind.Capsule => new IPhysicsGeometry.Capsule(
                reader.ReadSingle(),
                reader.ReadSingle()),
            GeometryKind.Plane => new IPhysicsGeometry.Plane
            {
                PlaneData = new Plane(ReadVector3(reader), reader.ReadSingle()),
            },
            var kind => throw new InvalidDataException(
                $"Unknown MonkeyBall physics geometry kind '{kind}'."),
        };

    private static void WriteBoxMesh(BinaryWriter writer, BoxMeshComponent component)
    {
        WriteVector3(writer, component.Box.Min);
        WriteVector3(writer, component.Box.Max);
        WriteLitMaterial(writer, component.Material);
    }

    private static BoxMeshComponent ReadBoxMesh(BinaryReader reader, SceneNode node)
    {
        Vector3 minimum = ReadVector3(reader);
        Vector3 maximum = ReadVector3(reader);
        BoxMeshComponent component = node.AddComponent<BoxMeshComponent>(
            static () => new BoxMeshComponent())!;
        component.Material = ReadLitMaterial(reader);
        component.Box = new AABB(minimum, maximum);
        return component;
    }

    private static void WriteSphereMesh(BinaryWriter writer, SphereMeshComponent component)
    {
        writer.Write(component.Radius);
        writer.Write(component.MeshPrecision);
        WriteLitMaterial(writer, component.Material);
    }

    private static SphereMeshComponent ReadSphereMesh(BinaryReader reader, SceneNode node)
    {
        float radius = reader.ReadSingle();
        uint precision = reader.ReadUInt32();
        SphereMeshComponent component = node.AddComponent<SphereMeshComponent>(
            static () => new SphereMeshComponent())!;
        component.Material = ReadLitMaterial(reader);
        component.Radius = radius;
        component.MeshPrecision = precision;
        return component;
    }

    private static void WriteLitMaterial(BinaryWriter writer, XRMaterial? material)
    {
        writer.Write(material is not null);
        if (material is null)
            return;

        WriteString(writer, material.Name);
        Vector3 baseColor = material.Parameter<ShaderVector3>("BaseColor")?.Value
            ?? Vector3.One;
        float opacity = material.Parameter<ShaderFloat>("Opacity")?.Value ?? 1.0f;
        WriteColor(writer, new ColorF4(baseColor.X, baseColor.Y, baseColor.Z, opacity));
        writer.Write(material.Parameter<ShaderFloat>("Specular")?.Value ?? 1.0f);
        writer.Write(material.Parameter<ShaderFloat>("Roughness")?.Value ?? 1.0f);
        writer.Write(material.Parameter<ShaderFloat>("Metallic")?.Value ?? 0.0f);
        writer.Write(material.RenderOptions.ExcludeFromGpuIndirect);
    }

    private static XRMaterial? ReadLitMaterial(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;

        string name = ReadString(reader);
        ColorF4 color = ReadColor(reader);
        float specular = reader.ReadSingle();
        float roughness = reader.ReadSingle();
        float metallic = reader.ReadSingle();
        bool excludeFromGpuIndirect = reader.ReadBoolean();
        XRMaterial material = XRMaterial.CreateLitColorMaterial(color, deferred: true);
        material.Name = name;
        material.Parameter<ShaderFloat>("Specular")!.Value = specular;
        material.Parameter<ShaderFloat>("Roughness")!.Value = roughness;
        material.Parameter<ShaderFloat>("Metallic")!.Value = metallic;
        material.RenderOptions.ExcludeFromGpuIndirect = excludeFromGpuIndirect;
        return material;
    }

    private static void WriteDebugDrawComponent(
        BinaryWriter writer,
        DebugDrawComponent debugDraw)
    {
        writer.Write(debugDraw.Shapes.Count);
        for (int i = 0; i < debugDraw.Shapes.Count; i++)
            WriteShape(writer, debugDraw.Shapes[i]);
    }

    private static DebugDrawComponent ReadDebugDrawComponent(
        BinaryReader reader,
        SceneNode node)
    {
        DebugDrawComponent debugDraw = node.AddComponent<DebugDrawComponent>(
            static () => new DebugDrawComponent())!;
        int shapeCount = ReadCount(reader, "debug shape");
        for (int i = 0; i < shapeCount; i++)
            debugDraw.AddShape(ReadShape(reader));
        return debugDraw;
    }

    private static void WriteShape(
        BinaryWriter writer,
        DebugDrawComponent.DebugShapeBase shape)
    {
        switch (shape)
        {
            case DebugDrawComponent.DebugDrawBox box:
                writer.Write((byte)ShapeKind.Box);
                WriteVector3(writer, box.HalfExtents);
                WriteVector3(writer, box.LocalOffset);
                writer.Write(box.DepthTested);
                break;
            case DebugDrawComponent.DebugDrawSphere sphere:
                writer.Write((byte)ShapeKind.Sphere);
                writer.Write(sphere.Radius);
                WriteVector3(writer, sphere.LocalOffset);
                break;
            case DebugDrawComponent.DebugDrawCylinder cylinder:
                writer.Write((byte)ShapeKind.Cylinder);
                writer.Write(cylinder.Radius);
                writer.Write(cylinder.HalfHeight);
                WriteVector3(writer, cylinder.LocalOffset);
                WriteVector3(writer, cylinder.LocalUpAxis);
                break;
            case DebugDrawComponent.DebugDrawLine line:
                writer.Write((byte)ShapeKind.Line);
                WriteVector3(writer, line.StartOffset);
                WriteVector3(writer, line.EndOffset);
                break;
            default:
                throw new NotSupportedException(
                    $"MonkeyBall world cooking does not support debug shape '{shape.GetType().FullName}'.");
        }

        WriteColor(writer, shape.Color);
        writer.Write(shape.Solid);
    }

    private static DebugDrawComponent.DebugShapeBase ReadShape(BinaryReader reader)
    {
        ShapeKind kind = (ShapeKind)reader.ReadByte();
        DebugDrawComponent.DebugShapeBase shape = kind switch
        {
            ShapeKind.Box => new DebugDrawComponent.DebugDrawBox
            {
                HalfExtents = ReadVector3(reader),
                LocalOffset = ReadVector3(reader),
                DepthTested = reader.ReadBoolean(),
            },
            ShapeKind.Sphere => new DebugDrawComponent.DebugDrawSphere
            {
                Radius = reader.ReadSingle(),
                LocalOffset = ReadVector3(reader),
            },
            ShapeKind.Cylinder => new DebugDrawComponent.DebugDrawCylinder
            {
                Radius = reader.ReadSingle(),
                HalfHeight = reader.ReadSingle(),
                LocalOffset = ReadVector3(reader),
                LocalUpAxis = ReadVector3(reader),
            },
            ShapeKind.Line => new DebugDrawComponent.DebugDrawLine
            {
                StartOffset = ReadVector3(reader),
                EndOffset = ReadVector3(reader),
            },
            _ => throw new InvalidDataException($"Unknown MonkeyBall debug shape kind '{kind}'."),
        };
        shape.Color = ReadColor(reader);
        shape.Solid = reader.ReadBoolean();
        return shape;
    }

    private static void WriteCameraComponent(BinaryWriter writer, CameraComponent camera)
    {
        XRPerspectiveCameraParameters perspective =
            camera.CameraParameters as XRPerspectiveCameraParameters
            ?? throw new NotSupportedException(
                "The MonkeyBall desktop camera must use perspective parameters.");
        writer.Write(perspective.HorizontalFieldOfView);
        writer.Write(perspective.NearZ);
        writer.Write(perspective.FarZ);
        writer.Write(camera.CullWithFrustum);
        writer.Write((int)camera.DirectionalShadowRenderingMode);
    }

    private static CameraComponent ReadCameraComponent(BinaryReader reader, SceneNode node)
    {
        float fieldOfView = reader.ReadSingle();
        float nearZ = reader.ReadSingle();
        float farZ = reader.ReadSingle();
        CameraComponent camera = node.AddComponent<CameraComponent>(
            static () => new CameraComponent())!;
        camera.CameraParameters = new XRPerspectiveCameraParameters(nearZ, farZ)
        {
            HorizontalFieldOfView = fieldOfView,
        };
        camera.CullWithFrustum = reader.ReadBoolean();
        camera.DirectionalShadowRenderingMode =
            (EDirectionalShadowRenderingMode)reader.ReadInt32();
        return camera;
    }

    private static VRControllerModelComponent ReadVrControllerModel(
        BinaryReader reader,
        SceneNode node)
    {
        VRControllerModelComponent component =
            node.AddComponent<VRControllerModelComponent>(
                static () => new VRControllerModelComponent())!;
        component.LeftHand = reader.ReadBoolean();
        return component;
    }

    private static void WriteDirectionalLight(
        BinaryWriter writer,
        DirectionalLightComponent light)
    {
        WriteColor(writer, light.Color);
        writer.Write(light.DiffuseIntensity);
        writer.Write(light.CastsShadows);
        writer.Write(light.UseShadowAtlas);
        writer.Write(light.EnableCascadedShadows);
        writer.Write(light.EnableContactShadows);
        WriteVector3(writer, light.Scale);
        writer.Write(light.ShadowMapResolutionWidth);
        writer.Write(light.ShadowMapResolutionHeight);
    }

    private static DirectionalLightComponent ReadDirectionalLight(
        BinaryReader reader,
        SceneNode node)
    {
        DirectionalLightComponent light = node.AddComponent<DirectionalLightComponent>(
            static () => new DirectionalLightComponent())!;
        light.Color = ReadColor3(reader);
        light.DiffuseIntensity = reader.ReadSingle();
        light.CastsShadows = reader.ReadBoolean();
        light.UseShadowAtlas = reader.ReadBoolean();
        light.EnableCascadedShadows = reader.ReadBoolean();
        light.EnableContactShadows = reader.ReadBoolean();
        light.Scale = ReadVector3(reader);
        light.SetShadowMapResolution(reader.ReadUInt32(), reader.ReadUInt32());
        return light;
    }

    private static void WriteGroupsMask(BinaryWriter writer, PhysicsGroupsMask mask)
    {
        writer.Write(mask.Word0);
        writer.Write(mask.Word1);
        writer.Write(mask.Word2);
        writer.Write(mask.Word3);
    }

    private static PhysicsGroupsMask ReadGroupsMask(BinaryReader reader)
        => new(
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32());

    private static void WriteString(BinaryWriter writer, string? value)
        => writer.Write(value ?? string.Empty);

    private static string ReadString(BinaryReader reader)
        => reader.ReadString();

    private static int ReadCount(BinaryReader reader, string kind)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaximumCollectionCount)
            throw new InvalidDataException($"Invalid MonkeyBall {kind} count {count}.");
        return count;
    }

    private static void WriteVector2(BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    private static Vector2 ReadVector2(BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle());

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

    private static Quaternion ReadQuaternion(BinaryReader reader)
        => new(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());

    private static void WriteColor(BinaryWriter writer, ColorF4 value)
    {
        writer.Write(value.R);
        writer.Write(value.G);
        writer.Write(value.B);
        writer.Write(value.A);
    }

    private static ColorF4 ReadColor(BinaryReader reader)
        => new(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());

    private static void WriteColor(BinaryWriter writer, ColorF3 value)
    {
        writer.Write(value.R);
        writer.Write(value.G);
        writer.Write(value.B);
    }

    private static ColorF3 ReadColor3(BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

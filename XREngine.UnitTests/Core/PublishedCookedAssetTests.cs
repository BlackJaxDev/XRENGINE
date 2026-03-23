using MemoryPack;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Animation;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class PublishedCookedAssetTests
{
    private string _tempRoot = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "PublishedCookedAssets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
        XRRuntimeEnvironment.ConfigurePublishedPaths(null);
    }

    [TearDown]
    public void TearDown()
    {
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
        XRRuntimeEnvironment.ConfigurePublishedPaths(null);

        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, true);
    }

    [Test]
    public void CookedAssetReader_RuntimeBinaryV1_RoundTrips_XRMesh()
    {
        XRMesh original = CreateSampleMesh();
        byte[] cooked = CreateRuntimeCookedAsset(original);

        XRMesh? clone = CookedAssetReader.LoadAsset<XRMesh>(cooked);

        clone.ShouldNotBeNull();
        AssertMeshesEquivalent(original, clone!);
    }

    [Test]
    public void CookedAssetReader_RuntimeBinaryV1_Rejects_UnregisteredAotType()
    {
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        XRMesh original = CreateSampleMesh();
        byte[] cooked = CreateRuntimeCookedAsset(original);

        Should.Throw<NotSupportedException>(() => CookedAssetReader.LoadAsset<XRMesh>(cooked));
    }

    [Test]
    public void CookedAssetReader_RuntimeBinaryV1_Uses_AotMetadataRegistry()
    {
        XRMesh original = CreateSampleMesh();
        string archivePath = CreateConfigArchive(new AotRuntimeMetadata
        {
            KnownTypeAssemblyQualifiedNames = [typeof(XRMesh).AssemblyQualifiedName!],
            PublishedRuntimeAssetTypeNames = [typeof(XRMesh).AssemblyQualifiedName!],
        });

        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        XRRuntimeEnvironment.ConfigurePublishedPaths(archivePath);

        XRMesh? clone = CookedAssetReader.LoadAsset<XRMesh>(CreateRuntimeCookedAsset(original));

        clone.ShouldNotBeNull();
        AssertMeshesEquivalent(original, clone!);
    }

    [Test]
    public void CookedAssetReader_RuntimeBinaryV1_RoundTrips_AnimationClip()
    {
        AnimationClip original = CreateSampleClip();

        AnimationClip? clone = CookedAssetReader.LoadAsset(CreateRuntimeCookedAsset(original), typeof(AnimationClip)) as AnimationClip;

        clone.ShouldNotBeNull();
        clone!.Name.ShouldBe(original.Name);
        clone.OriginalPath.ShouldBe(original.OriginalPath);
        clone.TraversalMethod.ShouldBe(original.TraversalMethod);
        clone.RootMember.ShouldNotBeNull();
        clone.RootMember!.Children.Count.ShouldBe(original.RootMember?.Children.Count ?? 0);
        clone.RootMember.Children[0].Children[0].MethodArguments.ShouldBe(original.RootMember!.Children[0].Children[0].MethodArguments);
    }

    [Test]
    public void CookedAssetReader_RuntimeBinaryV1_RoundTrips_AnimStateMachine_WithNestedMotion()
    {
        AnimStateMachine original = CreateSampleStateMachine();

        AnimStateMachine? clone = CookedAssetReader.LoadAsset(CreateRuntimeCookedAsset(original), typeof(AnimStateMachine)) as AnimStateMachine;

        clone.ShouldNotBeNull();
        clone!.Name.ShouldBe(original.Name);
        clone.Layers.Count.ShouldBe(1);
        clone.Layers[0].States.Count.ShouldBe(1);
        clone.Layers[0].States[0].Motion.ShouldBeOfType<BlendTree1D>();
        BlendTree1D clonedBlendTree = clone.Layers[0].States[0].Motion.ShouldBeOfType<BlendTree1D>();
        clonedBlendTree.Children.Count.ShouldBe(1);
        clonedBlendTree.Children[0].Motion.ShouldBeOfType<AnimationClip>();
    }

    private string CreateConfigArchive(AotRuntimeMetadata metadata)
    {
        string staging = Path.Combine(_tempRoot, "config");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(
            Path.Combine(staging, AotRuntimeMetadataStore.MetadataFileName),
            MemoryPackSerializer.Serialize(metadata));

        string archivePath = Path.Combine(_tempRoot, "config.archive");
        AssetPacker.Pack(staging, archivePath);
        return archivePath;
    }

    private static byte[] CreateRuntimeCookedAsset(XRAsset asset)
    {
        PublishedCookedAssetRegistry.TrySerialize(asset, out byte[] payload).ShouldBeTrue();
        CookedAssetBlob blob = new(
            CookedAssetTypeReference.Encode(asset.GetType()),
            CookedAssetFormat.RuntimeBinaryV1,
            payload);

        return MemoryPackSerializer.Serialize(blob);
    }

    private static XRMesh CreateSampleMesh()
    {
        XRMesh mesh = new(
        [
            new VertexTriangle(
                new Vertex(new Vector3(0.0f, 0.0f, 0.0f), Vector3.UnitZ, new Vector2(0.0f, 0.0f), new Vector4(1.0f, 0.0f, 0.0f, 1.0f)),
                new Vertex(new Vector3(1.0f, 0.0f, 0.0f), Vector3.UnitZ, new Vector2(1.0f, 0.0f), new Vector4(0.0f, 1.0f, 0.0f, 1.0f)),
                new Vertex(new Vector3(0.0f, 1.0f, 0.0f), Vector3.UnitZ, new Vector2(0.0f, 1.0f), new Vector4(0.0f, 0.0f, 1.0f, 1.0f)))
        ]);

        mesh.Name = "PublishedMesh";
        mesh.FilePath = "Assets/PublishedMesh.asset";
        return mesh;
    }

    private static AnimationClip CreateSampleClip()
    {
        PropAnimFloat animation = new(24, 24.0f, looped: true, useKeyframes: true)
        {
            Name = "HipRotationX"
        };
        animation.Keyframes.Add(
            new FloatKeyframe(0.0f, -0.5f, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(1.0f, 0.75f, 0.0f, EVectorInterpType.Linear));

        AnimationMember root = new("Root", EAnimationMemberType.Group);
        AnimationMember sceneNode = new("SceneNode", EAnimationMemberType.Property);
        AnimationMember findHips = new("FindDescendantByName", EAnimationMemberType.Method)
        {
            MethodArguments = ["Hips", StringComparison.InvariantCultureIgnoreCase],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        AnimationMember transform = new("Transform", EAnimationMemberType.Property);
        transform.Children.Add(new AnimationMember("QuaternionX", EAnimationMemberType.Property, animation));
        root.Children.Add(sceneNode);
        sceneNode.Children.Add(findHips);
        findHips.Children.Add(transform);

        return new AnimationClip(root)
        {
            Name = "Walk",
            OriginalPath = "Assets\\Walks\\Walk.anim",
            OriginalLastWriteTimeUtc = new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc),
            TraversalMethod = EAnimTreeTraversalMethod.BreadthFirst,
            Looped = true,
            ClipKind = EAnimationClipKind.UnityHumanoidMuscle,
            HasMuscleChannels = true,
            HasRootMotion = true,
            HasIKGoals = false,
            SampleRate = 24,
            LengthInSeconds = 1.0f
        };
    }

    private static AnimStateMachine CreateSampleStateMachine()
    {
        AnimationClip clip = CreateSampleClip();
        BlendTree1D blendTree = new()
        {
            Name = "Locomotion",
            ParameterName = "Speed",
            Children =
            [
                new BlendTree1D.Child
                {
                    Motion = clip,
                    Threshold = 0.5f,
                    Speed = 1.0f,
                    HumanoidMirror = false
                }
            ]
        };

        AnimStateMachine stateMachine = new()
        {
            Name = "Controller",
            AnimatePhysics = true,
            Variables =
            [
                new KeyValuePair<string, AnimVar>("Speed", new AnimFloat("Speed", 0.25f))
            ]
        };

        AnimState state = new(blendTree, "Locomotion")
        {
            StartSecond = 0.0f,
            EndSecond = 1.0f,
        };

        stateMachine.Layers =
        [
            new AnimLayer
            {
                States = [state],
                InitialStateIndex = 0,
            }
        ];

        return stateMachine;
    }

    private static void AssertMeshesEquivalent(XRMesh expected, XRMesh actual)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.FilePath.ShouldBe(expected.FilePath);
        actual.Type.ShouldBe(expected.Type);
        actual.VertexCount.ShouldBe(expected.VertexCount);
        actual.Buffers.Count.ShouldBe(expected.Buffers.Count);
        actual.Bounds.Min.ShouldBe(expected.Bounds.Min);
        actual.Bounds.Max.ShouldBe(expected.Bounds.Max);

        foreach (KeyValuePair<string, XRDataBuffer> kvp in (IEnumerable<KeyValuePair<string, XRDataBuffer>>)expected.Buffers)
        {
            actual.Buffers.ContainsKey(kvp.Key).ShouldBeTrue();
            XRDataBuffer expectedBuffer = kvp.Value;
            XRDataBuffer actualBuffer = actual.Buffers[kvp.Key];

            actualBuffer.AttributeName.ShouldBe(expectedBuffer.AttributeName);
            actualBuffer.Target.ShouldBe(expectedBuffer.Target);
            actualBuffer.ComponentType.ShouldBe(expectedBuffer.ComponentType);
            actualBuffer.ComponentCount.ShouldBe(expectedBuffer.ComponentCount);
            actualBuffer.ElementCount.ShouldBe(expectedBuffer.ElementCount);
            actualBuffer.Normalize.ShouldBe(expectedBuffer.Normalize);
            actualBuffer.Integral.ShouldBe(expectedBuffer.Integral);
            actualBuffer.PadEndingToVec4.ShouldBe(expectedBuffer.PadEndingToVec4);

            uint logicalByteLength = expectedBuffer.ElementCount * expectedBuffer.ElementSize;
            actualBuffer.GetRawBytes(logicalByteLength).ShouldBe(expectedBuffer.GetRawBytes(logicalByteLength));
        }
    }
}
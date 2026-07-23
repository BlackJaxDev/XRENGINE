using System.Numerics;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Animation;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class SnapshotDiagnosticsTests
{
    [Test]
    public void MeshAssetDiagnostics_EnumeratesStronglyTypedBuffers()
    {
        using XRMesh mesh = XRMesh.CreatePoints(Vector3.Zero);

        Assert.DoesNotThrow(() => SnapshotDiagnostics.LogAssetSerializationDecision(
            mesh,
            SnapshotAssetSerializationMode.Inline,
            "regression-test"));
        Assert.That(mesh.Buffers.Values, Is.Not.Empty);
    }

    [Test]
    public void SceneSnapshot_PreservesInlineAnimationClipTree()
    {
        var translationY = new PropAnimFloat(1.0f, looped: true, useKeyframes: true);
        translationY.Keyframes.Add(
            new FloatKeyframe(0.0f, 3.5f, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(1.0f, 4.5f, 0.0f, EVectorInterpType.Linear));

        var animationRoot = new AnimationMember("Root", EAnimationMemberType.Group);
        var sceneNodeMember = new AnimationMember("SceneNode", EAnimationMemberType.Property);
        var transformMember = new AnimationMember("Transform", EAnimationMemberType.Property);
        transformMember.Children.Add(new AnimationMember("TranslationY", EAnimationMemberType.Property, translationY));
        sceneNodeMember.Children.Add(transformMember);
        animationRoot.Children.Add(sceneNodeMember);

        var sceneNode = new SceneNode("Animated Node");
        var component = sceneNode.AddComponent<AnimationClipComponent>()!;
        component.Animation = new AnimationClip(animationRoot)
        {
            Name = "Inline Snapshot Animation",
            LengthInSeconds = 1.0f,
            Looped = true,
        };
        Assert.That(translationY.Keyframes.Last!.Second, Is.EqualTo(1.0f));

        byte[] directPayload = CookedBinarySerializer.Serialize(component.Animation);
        Assert.That(translationY.Keyframes.Last!.Second, Is.EqualTo(1.0f), "direct clip serialization mutated its source track");
        AnimationClip directClone = (AnimationClip)CookedBinarySerializer.Deserialize(
            typeof(AnimationClip),
            directPayload)!;
        Assert.That(directClone.GetAllAnimations(), Has.Count.EqualTo(1));
        AssertAnimationTrackPreserved(directClone, "direct clip");

        byte[]? snapshotClipPayload = SnapshotBinarySerializer.Serialize(component.Animation);
        Assert.That(translationY.Keyframes.Last!.Second, Is.EqualTo(1.0f), "snapshot clip serialization mutated its source track");
        AnimationClip snapshotClipClone = SnapshotBinarySerializer.Deserialize<AnimationClip>(
            snapshotClipPayload)!;
        Assert.That(snapshotClipClone.GetAllAnimations(), Has.Count.EqualTo(1));
        AssertAnimationTrackPreserved(snapshotClipClone, "snapshot clip");

        byte[] directComponentPayload = CookedBinarySerializer.Serialize(component);
        Assert.That(translationY.Keyframes.Last!.Second, Is.EqualTo(1.0f), "direct component serialization mutated its source track");
        var directComponentClone = (AnimationClipComponent)CookedBinarySerializer.Deserialize(
            typeof(AnimationClipComponent),
            directComponentPayload)!;
        Assert.That(directComponentClone.Animation!.GetAllAnimations(), Has.Count.EqualTo(1));
        AssertAnimationTrackPreserved(directComponentClone.Animation, "direct component");

        byte[]? snapshotComponentPayload = SnapshotBinarySerializer.Serialize(component);
        Assert.That(translationY.Keyframes.Last!.Second, Is.EqualTo(1.0f), "snapshot component serialization mutated its source track");
        AnimationClipComponent snapshotComponentClone =
            SnapshotBinarySerializer.Deserialize<AnimationClipComponent>(snapshotComponentPayload)!;
        Assert.That(snapshotComponentClone.Animation!.GetAllAnimations(), Has.Count.EqualTo(1));
        AssertAnimationTrackPreserved(snapshotComponentClone.Animation, "snapshot component");

        var scene = new XRScene("Snapshot Scene");
        scene.RootNodes.Add(sceneNode);

        byte[] directScenePayload = CookedBinarySerializer.Serialize(scene);
        Assert.That(translationY.Keyframes.Last!.Second, Is.EqualTo(1.0f), "direct scene serialization mutated its source track");
        var directSceneClone = (XRScene)CookedBinarySerializer.Deserialize(
            typeof(XRScene),
            directScenePayload)!;
        AnimationClip? directSceneClip = directSceneClone.RootNodes
            .Single()
            .GetComponent<AnimationClipComponent>()?
            .Animation;
        Assert.That(directSceneClip!.GetAllAnimations(), Has.Count.EqualTo(1));
        AssertAnimationTrackPreserved(directSceneClip, "direct scene");

        byte[]? payload = SnapshotBinarySerializer.Serialize(scene);
        Assert.That(translationY.Keyframes.Last!.Second, Is.EqualTo(1.0f), "snapshot scene serialization mutated its source track");
        XRScene? restoredScene = SnapshotBinarySerializer.Deserialize<XRScene>(payload);
        AnimationClip? restoredClip = restoredScene?
            .RootNodes
            .Single()
            .GetComponent<AnimationClipComponent>()?
            .Animation;

        Assert.That(restoredClip, Is.Not.Null);
        Assert.That(restoredClip!.RootMember, Is.Not.Null);
        Assert.That(restoredClip.RootMember!.Children, Has.Count.EqualTo(1));
        Assert.That(restoredClip.RootMember.ParentClip, Is.SameAs(restoredClip));
        Assert.That(restoredClip.RootMember.Children[0].Children, Has.Count.EqualTo(1));
        Assert.That(restoredClip.RootMember.Children[0].Children[0].Children, Has.Count.EqualTo(1));
        Assert.That(
            restoredClip.RootMember.Children[0].Children[0].Children[0].Animation,
            Is.TypeOf<PropAnimFloat>());
        Assert.That(restoredClip.GetAllAnimations(), Has.Count.EqualTo(1));

        var restoredTranslationY =
            (PropAnimFloat)restoredClip.RootMember.Children[0].Children[0].Children[0].Animation!;
        Assert.That(restoredTranslationY.Keyframes, Has.Count.EqualTo(2));
        Assert.That(restoredTranslationY.Keyframes.First!.Second, Is.EqualTo(0.0f));
        Assert.That(restoredTranslationY.Keyframes.Last!.Second, Is.EqualTo(1.0f));
        Assert.That(restoredTranslationY.Keyframes.First.OwningTrack, Is.SameAs(restoredTranslationY.Keyframes));
        Assert.That(restoredTranslationY.Keyframes.First.Next, Is.SameAs(restoredTranslationY.Keyframes.Last));
        Assert.That(restoredTranslationY.GetValue(0.5f), Is.EqualTo(4.0f).Within(0.0001f));

        SceneNode restoredNode = restoredScene!.RootNodes.Single();
        AnimationClipComponent restoredComponent =
            restoredNode.GetComponent<AnimationClipComponent>()!;
        restoredComponent.EvaluateAtTime(0.5f);

        Assert.That(
            ((Transform)restoredNode.Transform).TranslationY,
            Is.EqualTo(4.0f).Within(0.0001f));
    }

    [Test]
    public void SceneSnapshot_PreservesInlineModelMeshes()
    {
        XRMesh mesh = XRMesh.CreatePoints(Vector3.Zero);
        var sceneNode = new SceneNode("Model Node");
        var component = sceneNode.AddComponent<ModelComponent>()!;
        component.Model = new Model(new SubMesh(mesh, material: null));

        var scene = new XRScene("Snapshot Model Scene");
        scene.RootNodes.Add(sceneNode);

        byte[]? payload = SnapshotBinarySerializer.Serialize(scene);
        XRScene? restoredScene = SnapshotBinarySerializer.Deserialize<XRScene>(payload);
        ModelComponent? restoredComponent = restoredScene?
            .RootNodes
            .Single()
            .GetComponent<ModelComponent>();

        Assert.That(restoredComponent, Is.Not.Null);
        Assert.That(restoredComponent!.Model, Is.Not.Null);
        Assert.That(restoredComponent.Model!.Meshes, Has.Count.EqualTo(1));
        Assert.That(restoredComponent.Model.Meshes[0].LODs, Has.Count.EqualTo(1));
        Assert.That(restoredComponent.Model.Meshes[0].LODs.First().Mesh, Is.Not.Null);
    }

    [Test]
    public void SceneSnapshot_RebindsInlineSkinnedMeshBonesToRestoredHierarchy()
    {
        var rigNode = new SceneNode("Rig");
        var rootNode = new SceneNode(rigNode, "Root");
        var boneNode = new SceneNode(rootNode, "Bone");
        var visualNode = new SceneNode(rigNode, "Visual");
        Transform root = (Transform)rootNode.Transform;
        Transform bone = (Transform)boneNode.Transform;

        var rootWeights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>
        {
            [root] = (1.0f, Matrix4x4.Identity),
        };
        var boneWeights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>
        {
            [bone] = (1.0f, Matrix4x4.Identity),
        };
        var mesh = new XRMesh(
            [
                new Vertex(Vector3.Zero, rootWeights),
                new Vertex(Vector3.UnitX, boneWeights),
                new Vertex(Vector3.UnitY, boneWeights),
            ],
            new List<ushort> { 0, 1, 2 })
        {
            Name = "Snapshot Skinned Mesh",
        };
        mesh.RebuildSkinningBuffersFromVertices();

        ModelComponent component = visualNode.AddComponent<ModelComponent>()!;
        component.Model = new Model(new SubMesh(mesh, material: null));

        var scene = new XRScene("Snapshot Skinned Model Scene");
        scene.RootNodes.Add(rigNode);

        byte[]? payload = SnapshotBinarySerializer.Serialize(scene);
        XRScene restoredScene = SnapshotBinarySerializer.Deserialize<XRScene>(payload)!;
        SceneNode restoredRig = restoredScene.RootNodes.Single();
        SceneNode restoredRoot = restoredRig.GetChild(0)!;
        SceneNode restoredBone = restoredRoot.GetChild(0)!;
        ModelComponent restoredComponent = restoredRig
            .GetChild(1)!
            .GetComponent<ModelComponent>()!;
        XRMesh restoredSourceMesh = restoredComponent.Model!
            .Meshes
            .Single()
            .LODs
            .Single()
            .Mesh!;
        XRMesh restoredRuntimeMesh = restoredComponent
            .GetAllRenderersWhere(static _ => true)
            .Single()
            .Mesh!;

        Assert.That(restoredSourceMesh.HasSkinning, Is.True);
        Assert.That(restoredSourceMesh.UtilizedBones, Has.Length.EqualTo(2));
        Assert.That(restoredRuntimeMesh.HasSkinning, Is.True);
        Assert.That(restoredRuntimeMesh.UtilizedBones, Has.Length.EqualTo(2));
        Assert.That(restoredRuntimeMesh.UtilizedBones[0].tfm, Is.SameAs(restoredRoot.Transform));
        Assert.That(restoredRuntimeMesh.UtilizedBones[1].tfm, Is.SameAs(restoredBone.Transform));
    }

    [Test]
    public void SceneSnapshot_RebindsPhysicsChainSceneReferences()
    {
        var rigNode = new SceneNode("Rig");
        var rootNode = new SceneNode(rigNode, "Chain Root");
        var bone1Node = new SceneNode(rootNode, "Bone1");
        var bone2Node = new SceneNode(bone1Node, "Bone2");
        var colliderNode = new SceneNode(rigNode, "Collider");
        PhysicsChainSphereCollider collider = colliderNode.AddComponent<PhysicsChainSphereCollider>()!;

        PhysicsChainComponent chain = rootNode.AddComponent<PhysicsChainComponent>()!;
        chain.Root = (Transform)rootNode.Transform;
        chain.Roots = [(Transform)rootNode.Transform];
        chain.Exclusions = [bone2Node.Transform];
        chain.Colliders = [collider];
        chain.ReferenceObject = (Transform)bone1Node.Transform;
        chain.RootBone = rigNode.Transform;
        chain.EndLength = 0.0f;
        chain.EndOffset = Vector3.Zero;

        var scene = new XRScene("Snapshot Physics Chain Scene");
        scene.RootNodes.Add(rigNode);

        byte[]? payload = SnapshotBinarySerializer.Serialize(scene);
        XRScene restoredScene = SnapshotBinarySerializer.Deserialize<XRScene>(payload)!;
        SceneNode restoredRig = restoredScene.RootNodes.Single();
        SceneNode restoredRoot = restoredRig.GetChild(0)!;
        SceneNode restoredBone1 = restoredRoot.GetChild(0)!;
        SceneNode restoredBone2 = restoredBone1.GetChild(0)!;
        SceneNode restoredColliderNode = restoredRig.GetChild(1)!;
        PhysicsChainComponent restoredChain = restoredRoot.GetComponent<PhysicsChainComponent>()!;
        PhysicsChainSphereCollider restoredCollider =
            restoredColliderNode.GetComponent<PhysicsChainSphereCollider>()!;

        Assert.That(restoredChain.Root, Is.SameAs(restoredRoot.Transform));
        Assert.That(restoredChain.Roots, Has.Count.EqualTo(1));
        Assert.That(restoredChain.Roots![0], Is.SameAs(restoredRoot.Transform));
        Assert.That(restoredChain.Exclusions, Has.Count.EqualTo(1));
        Assert.That(restoredChain.Exclusions![0], Is.SameAs(restoredBone2.Transform));
        Assert.That(restoredChain.Colliders, Has.Count.EqualTo(1));
        Assert.That(restoredChain.Colliders![0], Is.SameAs(restoredCollider));
        Assert.That(restoredChain.ReferenceObject, Is.SameAs(restoredBone1.Transform));
        Assert.That(restoredChain.RootBone, Is.SameAs(restoredRig.Transform));

        restoredChain.SetupParticles();
        Assert.That(restoredChain.RuntimeParticleCount, Is.EqualTo(2));
    }

    [Test]
    [NonParallelizable]
    public void WorldSnapshot_PreservesCapturedRuntimeRootsAndRemovesLaterSpawns()
    {
        var sceneRoot = new SceneNode("Serialized Root");
        var scene = new XRScene("Snapshot Runtime Root Scene", sceneRoot);
        var world = new XRWorld("Snapshot Runtime Root World", scene);
        var worldInstance = new XRWorldInstance(world, new VisualScene3D(), new JitterScene());
        var editorRoot = new SceneNode("Editor Runtime Root");
        worldInstance.RootNodes.Add(editorRoot);
        XRWorldInstance.WorldInstances.Add(world, worldInstance);

        try
        {
            WorldStateSnapshot snapshot = WorldStateSnapshot.Capture(world)!;
            var spawnedRoot = new SceneNode("Play Spawned Root");
            worldInstance.RootNodes.Add(spawnedRoot);

            Assert.That(snapshot.CapturedRuntimeOnlyRootIds, Does.Contain(editorRoot.ID));
            Assert.That(snapshot.CapturedRuntimeOnlyRootIds, Does.Not.Contain(spawnedRoot.ID));
            Assert.That(snapshot.Restore(), Is.True);
            Assert.That(worldInstance.RootNodes, Does.Contain(editorRoot));
            Assert.That(worldInstance.RootNodes, Does.Not.Contain(spawnedRoot));
            Assert.That(editorRoot.IsDestroyed, Is.False);
            Assert.That(spawnedRoot.IsDestroyed, Is.True);
        }
        finally
        {
            XRWorldInstance.WorldInstances.Remove(world);
            worldInstance.TargetWorld = null;
        }
    }

    private static void AssertAnimationTrackPreserved(AnimationClip clip, string context)
    {
        var animation = (PropAnimFloat)clip.RootMember!.Children[0].Children[0].Children[0].Animation!;
        Assert.That(animation.LengthInSeconds, Is.EqualTo(1.0f), context);
        Assert.That(animation.Keyframes, Has.Count.EqualTo(2), context);
        Assert.That(animation.Keyframes.First!.Second, Is.EqualTo(0.0f), context);
        Assert.That(animation.Keyframes.Last!.Second, Is.EqualTo(1.0f), context);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using NUnit.Framework;
using XREngine;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using Assert = NUnit.Framework.Assert;

namespace XREngine.UnitTests.Prefabs;

[TestFixture]
public class SceneNodePrefabTests
{
    [Test]
    public void EnsurePrefabMetadata_AssignsIdsAndFlags()
    {
        Guid prefabId = Guid.NewGuid();
        SceneNode root = CreatePrefabTemplate();

        SceneNodePrefabUtility.EnsurePrefabMetadata(root, prefabId);

        SceneNode child = GetFirstChild(root);

        Assert.Multiple(() =>
        {
            Assert.That(root.Prefab, Is.Not.Null);
            Assert.That(root.Prefab!.PrefabAssetId, Is.EqualTo(prefabId));
            Assert.That(root.Prefab!.PrefabNodeId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(root.Prefab!.IsPrefabRoot, Is.True);

            Assert.That(child.Prefab, Is.Not.Null);
            Assert.That(child.Prefab!.PrefabAssetId, Is.EqualTo(prefabId));
            Assert.That(child.Prefab!.PrefabNodeId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(child.Prefab!.IsPrefabRoot, Is.False);
        });
    }

    [Test]
    public void BindInstanceToPrefab_AssignsAssetIdsAndPreservesNodeIds()
    {
        Guid prefabId = Guid.NewGuid();
        SceneNode template = CreatePrefabTemplate();
        SceneNodePrefabUtility.EnsurePrefabMetadata(template, prefabId);

        SceneNode instance = CloneViaSerializer(template);
        SceneNodePrefabUtility.BindInstanceToPrefab(instance, prefabId);

        SceneNode templateChild = GetFirstChild(template);
        SceneNode instanceChild = GetFirstChild(instance);

        Assert.That(instance.Prefab, Is.Not.Null);
        Assert.That(instance.Prefab!.PrefabAssetId, Is.EqualTo(prefabId));
        Assert.That(instance.Prefab!.IsPrefabRoot, Is.True);

        Assert.That(instanceChild.Prefab, Is.Not.Null);
        Assert.That(instanceChild.Prefab!.PrefabAssetId, Is.EqualTo(prefabId));
        Assert.That(instanceChild.Prefab!.IsPrefabRoot, Is.False);
        Assert.That(instanceChild.Prefab!.PrefabNodeId, Is.EqualTo(templateChild.Prefab!.PrefabNodeId));
    }

    [Test]
    [RequiresUnreferencedCode("Prefab override reflection is exercised by this test.")]
    public void ApplyOverrides_ReplaysRecordedChangesOnNewInstance()
    {
        Guid prefabId = Guid.NewGuid();
        SceneNode template = CreatePrefabTemplate();
        SceneNodePrefabUtility.EnsurePrefabMetadata(template, prefabId);

        SceneNode firstInstance = CloneViaSerializer(template);
        SceneNodePrefabUtility.BindInstanceToPrefab(firstInstance, prefabId);
        SceneNode firstChild = GetFirstChild(firstInstance);
        const string overriddenName = "Renamed Child";

        SceneNodePrefabUtility.RecordPropertyOverride(firstChild, nameof(SceneNode.Name), overriddenName);
        List<SceneNodePrefabNodeOverride> overrides = SceneNodePrefabUtility.ExtractOverrides(firstInstance);

        Assert.That(overrides, Has.Count.EqualTo(1));
        Assert.That(overrides[0].Properties.ContainsKey(nameof(SceneNode.Name)), Is.True);

        SceneNode secondInstance = CloneViaSerializer(template);
        SceneNodePrefabUtility.BindInstanceToPrefab(secondInstance, prefabId);
        SceneNode secondChild = GetFirstChild(secondInstance);

        SceneNodePrefabUtility.ApplyOverrides(secondInstance, overrides);

        Assert.That(secondChild.Name, Is.EqualTo(overriddenName));
    }

    [Test]
    public void SerializePrefabHierarchy_OmitsRedundantTransformAndReplicationFields()
    {
        SceneNode root = CreatePrefabTemplate();

        string yaml = AssetManager.Serializer.Serialize(root);

        Assert.Multiple(() =>
        {
            Assert.That(yaml, Does.Not.Contain("ScaleX:"));
            Assert.That(yaml, Does.Not.Contain("ScaleY:"));
            Assert.That(yaml, Does.Not.Contain("ScaleZ:"));
            Assert.That(yaml, Does.Not.Contain("TranslationX:"));
            Assert.That(yaml, Does.Not.Contain("TranslationY:"));
            Assert.That(yaml, Does.Not.Contain("TranslationZ:"));
            Assert.That(yaml, Does.Not.Contain("QuaternionX:"));
            Assert.That(yaml, Does.Not.Contain("QuaternionY:"));
            Assert.That(yaml, Does.Not.Contain("QuaternionZ:"));
            Assert.That(yaml, Does.Not.Contain("QuaternionW:"));
            Assert.That(yaml, Does.Not.Contain("ImmediateLocalMatrixRecalculation:"));
            Assert.That(yaml, Does.Not.Contain("TimeBetweenReplications:"));
            Assert.That(yaml, Does.Not.Contain("IsActiveSelf: true"));

            // Default transform values should not appear (nullable bridge omits them)
            Assert.That(yaml, Does.Not.Contain("Scale:"), "Default Scale (1,1,1) should be omitted");
            Assert.That(yaml, Does.Not.Contain("Translation:"), "Default Translation (0,0,0) should be omitted");
            Assert.That(yaml, Does.Not.Contain("Rotation:"), "Default Rotation (identity) should be omitted");
            Assert.That(yaml, Does.Not.Contain("IsPrefabRoot: false"), "IsPrefabRoot=false should be omitted");
        });
    }

    [Test]
    public void TransformRoundtrip_NonDefaultValues_SurviveSerialization()
    {
        var node = new SceneNode("TransformTest");
        var tfm = (Transform)node.Transform;
        tfm.Scale = new System.Numerics.Vector3(2f, 0.5f, 3f);
        tfm.Translation = new System.Numerics.Vector3(10f, -5f, 7.5f);
        tfm.Rotation = new System.Numerics.Quaternion(0.1f, 0.2f, 0.3f, 0.9f);

        string yaml = AssetManager.Serializer.Serialize(node);

        // Non-default values MUST appear
        Assert.That(yaml, Does.Contain("Scale:"), "Non-default Scale must appear");
        Assert.That(yaml, Does.Contain("Translation:"), "Non-default Translation must appear");
        Assert.That(yaml, Does.Contain("Rotation:"), "Non-default Rotation must appear");

        // Roundtrip deserialize
        var deserialized = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);
        var dtfm = (Transform)deserialized.Transform;

        Assert.Multiple(() =>
        {
            Assert.That(dtfm.Scale.X, Is.EqualTo(2f).Within(1e-5f));
            Assert.That(dtfm.Scale.Y, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(dtfm.Scale.Z, Is.EqualTo(3f).Within(1e-5f));
            Assert.That(dtfm.Translation.X, Is.EqualTo(10f).Within(1e-5f));
            Assert.That(dtfm.Translation.Y, Is.EqualTo(-5f).Within(1e-5f));
            Assert.That(dtfm.Translation.Z, Is.EqualTo(7.5f).Within(1e-5f));
            Assert.That(dtfm.Rotation.X, Is.EqualTo(0.1f).Within(1e-5f));
            Assert.That(dtfm.Rotation.Y, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(dtfm.Rotation.Z, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(dtfm.Rotation.W, Is.EqualTo(0.9f).Within(1e-5f));
        });
    }

    [Test]
    public void TransformSerialization_DefaultPropertyType_OmitsTransformDiscriminator()
    {
        SceneNode node = new("Root");

        string yaml = AssetManager.Serializer.Serialize(node);

        Assert.That(yaml, Does.Not.Contain("$type:"));
    }

    [Test]
    public void TransformSerialization_NonDefaultDerivedType_EmitsTransformDiscriminator()
    {
        SceneNode node = new("Root", new TransformNone());

        string yaml = AssetManager.Serializer.Serialize(node);

        Assert.That(yaml, Does.Contain("$type:"));
        Assert.That(yaml, Does.Contain("TransformNone"));
    }

    [Test]
    public void TransformRoundtrip_MissingTypeDiscriminators_DefaultsToTransformOnDeserialize()
    {
        SceneNode root = new("Root");
        ((Transform)root.Transform).Translation = new System.Numerics.Vector3(1.0f, 2.0f, 3.0f);

        SceneNode child = new("Child");
        child.Parent = root;
        ((Transform)child.Transform).Scale = new System.Numerics.Vector3(2.0f, 3.0f, 4.0f);

        string yaml = AssetManager.Serializer.Serialize(root);
        string yamlWithoutTransformTypes = string.Join(
            Environment.NewLine,
            yaml.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Where(static line => !line.TrimStart().StartsWith("$type:", StringComparison.Ordinal)));

        SceneNode deserialized = AssetManager.Deserializer.Deserialize<SceneNode>(yamlWithoutTransformTypes);
        SceneNode deserializedChild = GetFirstChild(deserialized);

        Assert.Multiple(() =>
        {
            Assert.That(deserialized.Transform, Is.TypeOf<Transform>());
            Assert.That(deserializedChild.Transform, Is.TypeOf<Transform>());
            Assert.That(((Transform)deserialized.Transform).Translation, Is.EqualTo(new System.Numerics.Vector3(1.0f, 2.0f, 3.0f)));
            Assert.That(((Transform)deserializedChild.Transform).Scale, Is.EqualTo(new System.Numerics.Vector3(2.0f, 3.0f, 4.0f)));
        });
    }

    [Test]
    public void PrefabAssetIdHoisting_OnlyRootEmitsId_ChildrenInheritOnDeserialize()
    {
        Guid prefabId = Guid.NewGuid();
        SceneNode root = CreatePrefabTemplate();
        SceneNodePrefabUtility.EnsurePrefabMetadata(root, prefabId);

        string yaml = AssetManager.Serializer.Serialize(root);

        // PrefabAssetId should appear exactly once (on the root)
        int count = yaml.Split("PrefabAssetId:").Length - 1;
        Assert.That(count, Is.EqualTo(1), "PrefabAssetId should appear exactly once in serialized YAML (on root only)");

        // Roundtrip: deserialize and verify all nodes have the correct PrefabAssetId
        var deserialized = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);
        SceneNode child = GetFirstChild(deserialized);
        SceneNode grandChild = GetFirstChild(child);

        Assert.Multiple(() =>
        {
            Assert.That(deserialized.Prefab?.PrefabAssetId, Is.EqualTo(prefabId), "Root should have PrefabAssetId after roundtrip");
            Assert.That(child.Prefab?.PrefabAssetId, Is.EqualTo(prefabId), "Child should inherit PrefabAssetId from root after roundtrip");
            Assert.That(grandChild.Prefab?.PrefabAssetId, Is.EqualTo(prefabId), "GrandChild should inherit PrefabAssetId from root after roundtrip");
        });
    }

    private static SceneNode CreatePrefabTemplate()
    {
        SceneNode root = new("PrefabRoot");
        SceneNode child = new("ChildNode");
        child.Parent = root;

        SceneNode grandChild = new("GrandChild");
        grandChild.Parent = child;

        return root;
    }

    private static SceneNode GetFirstChild(SceneNode node)
    {
        var child = node.Transform.Children
            .Select(t => t.SceneNode)
            .FirstOrDefault(n => n is not null);

        Assert.That(child, Is.Not.Null, "Expected template hierarchy to contain a child node.");
        return child!;
    }

    private static SceneNode CloneViaSerializer(SceneNode source)
        => SceneNodePrefabUtility.CloneHierarchy(source);
}

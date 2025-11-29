using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using NUnit.Framework;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
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

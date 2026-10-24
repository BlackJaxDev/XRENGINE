using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Prefabs;

[TestFixture, NonParallelizable]
public sealed class OwnedHierarchyCloneTests
{
    [TearDown]
    public void RestoreRuntimeMode() => XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);

    [Test]
    public void CanonicalYamlCloneHasOwnedStructureAndIndependentIdentities()
    {
        SceneNode template = new("Template");
        SceneNode child = new(template, "Child");
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.Null);
            SceneNode clone = result.Value!;
            Assert.That(clone.ID, Is.Not.EqualTo(template.ID));
            Assert.That(result.Ownership.Contains(clone), Is.True);
            Assert.That(result.Ownership.Contains(clone.Transform), Is.True);
            Assert.That(result.Ownership.Contains(clone.GetChild(0)!), Is.True);
            Assert.That(result.Ownership.Contains(template), Is.False);
            Assert.That(clone.GetChild(0)!.ID, Is.Not.EqualTo(child.ID));
            Assert.That(clone.World, Is.Null);
            Assert.That(clone.Parent, Is.Null);
        }
        finally { RetireTestAllocations(result.Ownership); template.Destroy(true); }
    }

    [Test]
    public void BoundedCloneRejectsBorrowedTransformBeforeConstructorAdoption()
    {
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        Guid transformId = template.Transform.ID;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(source => source);
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(template.Transform.SceneNode, Is.SameAs(template));
            Assert.That(template.Transform.ID, Is.EqualTo(transformId));
            Assert.That(result.Ownership.Contains(template.Transform), Is.False);
        }
        finally { RetireTestAllocations(result.Ownership); template.Destroy(true); }
    }

    [TestCase("Transform")]
    [TestCase("Children")]
    [TestCase("Components")]
    [TestCase("Parent")]
    [TestCase("SuppressedTransform")]
    [TestCase("ChildList")]
    public void CanonicalAdoptionRejectsBorrowedStructureBeforeChangingIt(string assignment)
    {
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        SceneNode borrowed = new("Borrowed");
        OwnedCloneComponentProbe component = borrowed.AddComponent(() => new OwnedCloneComponentProbe())!;
        Guid originalId = borrowed.Transform.ID;
        EventList<TransformBase> borrowedChildren = new() { ThreadSafe = false };
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            SceneNode owned = new("Owned");
            switch (assignment)
            {
                case "Transform": owned.SetTransform(borrowed.Transform); break;
                case "Children": owned.ChildNodesSerialized = [borrowed]; break;
                case "Components": owned.ComponentsSerialized = [component]; break;
                case "Parent": borrowed.Parent = owned; break;
                case "ChildList": owned.Transform.Children = borrowedChildren; break;
                case "SuppressedTransform":
                    using (XRBase.SuppressPropertyNotifications())
                        owned.SetTransform(borrowed.Transform, SceneNode.ETransformSetFlags.None);
                    break;
            }
            return new OwnedCloneTransformProbe();
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(borrowed.Parent, Is.Null);
            Assert.That(borrowed.Transform.SceneNode, Is.SameAs(borrowed));
            Assert.That(borrowed.Transform.ID, Is.EqualTo(originalId));
            Assert.That(component.SceneNode, Is.SameAs(borrowed));
            Assert.That(component.DestructionCount, Is.Zero);
            Assert.That(result.Ownership.Contains(borrowed), Is.False);
            Assert.That(borrowedChildren.ThreadSafe, Is.False);
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            borrowedChildren.Destroy(true);
            borrowed.Destroy(true);
            template.Destroy(true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void QueuedDestructionCannotBeAcceptedAsAClone(bool duringPublication)
    {
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        OwnedCloneTransformProbe? transform = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            transform = new();
            if (duringPublication)
                transform.Publishing = () => transform.Destroy();
            else
                transform.Destroy();
            return transform;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(transform!.IsDestroyQueued, Is.True);
            Assert.That(transform.IsDestroyed, Is.False);
            Assert.That(result.Ownership.Contains(transform), Is.True);
        }
        finally { RetireTestAllocations(result.Ownership); template.Destroy(true); }
    }

    [Test]
    public void NestedIndependentTransformDoesNotBecomeCloneOwned()
    {
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        ObjectConstructionResult<OwnedCloneTransformProbe>? independent = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            independent = XRObjectBase.ConstructOwned(_ => new OwnedCloneTransformProbe());
            return independent.Value!;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(independent!.Succeeded, Is.True);
            Assert.That(result.Ownership.Contains(independent.Value!), Is.False);
            Assert.That(independent.Value!.SceneNode, Is.Null);
            Assert.That(XRObjectBase.ObjectsCache[independent.Value.ID], Is.SameAs(independent.Value));
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            if (independent is not null) RetireTestAllocations(independent.Ownership);
            template.Destroy(true);
        }
    }

    [Test]
    public void FailureRetainsNestedAssetsWithoutRegeneratingOrDestroyingBorrowedAssets()
    {
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        TextFile borrowed = new();
        Guid borrowedId = borrowed.ID;
        TextFile? nested = null;
        InvalidOperationException failure = new("Expected clone failure.");
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            nested = new TextFile();
            throw failure;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.SameAs(failure));
            Assert.That(result.Ownership.Contains(nested!), Is.True);
            Assert.That(nested!.IsDestroyed, Is.False);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(nested.ID), Is.False);
            Assert.That(result.Ownership.Contains(borrowed), Is.False);
            Assert.That(borrowed.ID, Is.EqualTo(borrowedId));
            Assert.That(XRObjectBase.ObjectsCache[borrowedId], Is.SameAs(borrowed));
        }
        finally { RetireTestAllocations(result.Ownership); borrowed.Destroy(true); template.Destroy(true); }
    }

    [Test]
    public void FinalWriteRejectsAReentrantReplacementOwner()
    {
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        OwnedCloneTransformProbe? transform = null;
        SceneNode? replacement = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            transform = new();
            bool replacing = false;
            transform.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName != nameof(TransformBase.SceneNode) || replacing)
                    return;
                replacing = true;
                replacement = new("Replacement");
                replacement.SetTransform(transform, SceneNode.ETransformSetFlags.None);
            };
            return transform;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(transform!.SceneNode, Is.SameAs(replacement));
            Assert.That(replacement!.IsDestroyed, Is.False);
        }
        finally { RetireTestAllocations(result.Ownership); template.Destroy(true); }
    }

    [Test]
    public void PublicationGraphChangeFailsWithoutDestroyingEitherGraph()
    {
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        SceneNode foreign = new("Foreign");
        OwnedCloneTransformProbe? cloneTransform = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            cloneTransform = new();
            cloneTransform.Publishing = () => cloneTransform.Children.Add(foreign.Transform, false, false);
            return cloneTransform;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Value, Is.Not.Null);
            Assert.That(result.Value!.IsDestroyed, Is.False);
            Assert.That(foreign.IsDestroyed, Is.False);
            Assert.That(foreign.Parent, Is.Null);
            foreach (XRObjectBase allocation in result.Ownership.Objects)
                Assert.That(XRObjectBase.ObjectsCache.TryGetValue(allocation.ID, out XRObjectBase? cached) &&
                    ReferenceEquals(cached, allocation), Is.False);
        }
        finally
        {
            cloneTransform!.Publishing = null;
            cloneTransform.Children.Remove(foreign.Transform, false, false);
            RetireTestAllocations(result.Ownership);
            foreign.Destroy(true);
            template.Destroy(true);
        }
    }

    [Test]
    public void BoundedHandlerCannotStealANewTransformFromAnotherOwnedNode()
    {
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        SceneNode? other = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            OwnedCloneTransformProbe transform = new();
            other = new("Other owner", transform);
            return transform;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(other!.Transform.SceneNode, Is.SameAs(other));
            Assert.That(other.IsDestroyed, Is.False);
        }
        finally { RetireTestAllocations(result.Ownership); template.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void IdentityRegenerationLeavesNestedAndBorrowedAssetIdentitiesUnchanged(bool allocateAsset)
    {
        TextFile borrowed = new();
        SceneNode template = new("Template", new OwnedCloneTransformProbe { Asset = borrowed });
        TextFile? asset = null;
        Guid assetId = Guid.Empty;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(source =>
        {
            asset = allocateAsset ? new TextFile() : source.Asset;
            assetId = asset!.ID;
            return new OwnedCloneTransformProbe { Asset = asset };
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.Null);
            Assert.That(((OwnedCloneTransformProbe)result.Value!.Transform).Asset, Is.SameAs(asset));
            Assert.That(asset!.ID, Is.EqualTo(assetId));
            Assert.That(result.Ownership.Contains(asset), Is.EqualTo(allocateAsset));
            Assert.That(borrowed.IsDestroyed, Is.False);
        }
        finally { RetireTestAllocations(result.Ownership); borrowed.Destroy(true); template.Destroy(true); }
    }

    [Test]
    public void ForeignThreadCannotAdoptAnOwnedTransformDuringConstruction()
    {
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        SceneNode foreign = new("Foreign");
        Task? worker = null;
        Exception? rejection = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            OwnedCloneTransformProbe transform = new();
            worker = Task.Run(() =>
            {
                try { transform.Parent = foreign.Transform; }
                catch (Exception exception) { rejection = exception; }
            });
            if (!worker.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The clone adoption worker did not finish.");
            return transform;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            worker!.GetAwaiter().GetResult();
            Assert.That(result.Failure, Is.Null);
            Assert.That(rejection, Is.TypeOf<InvalidOperationException>());
            Assert.That(foreign.Transform.Children.Count, Is.Zero);
        }
        finally
        {
            worker?.GetAwaiter().GetResult();
            RetireTestAllocations(result.Ownership);
            foreign.Destroy(true);
            template.Destroy(true);
        }
    }

    private static void RetireTestAllocations(ObjectConstructionOwnership ownership)
    {
        // Each test removes its foreign links and disables callbacks before this fixture cleanup.
        foreach (XRObjectBase value in ownership.Objects)
            if (value is OwnedCloneTransformProbe probe)
                probe.Publishing = null;
        foreach (XRObjectBase value in ownership.Objects)
            if (value is SceneNode node && !node.IsDestroyed)
                node.Destroy(true);
        foreach (XRObjectBase value in ownership.Objects)
            if (!value.IsDestroyed)
                value.Destroy(true);
    }
}

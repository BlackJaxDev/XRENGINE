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
    public void GroupedCloneReturnsOrderedReadOnlyRootsWithOneRetirableLedger()
    {
        SceneNode first = new("First");
        _ = first.NewChild("First child");
        SceneNode second = new("Second");
        _ = second.NewChild("Second child");
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned([first, second]);
        OwnedHierarchyLifetime lifetime = new(result.Ownership, result.Failure);
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            IReadOnlyList<SceneNode> roots = result.Value!;
            Assert.Multiple(() =>
            {
                Assert.That(roots.Count, Is.EqualTo(2));
                Assert.That(roots[0].Name, Is.EqualTo("First"));
                Assert.That(roots[1].Name, Is.EqualTo("Second"));
                Assert.That(roots[0].ID, Is.Not.EqualTo(first.ID));
                Assert.That(roots[1].ID, Is.Not.EqualTo(second.ID));
                Assert.That(result.Ownership.Contains(roots[0]), Is.True);
                Assert.That(result.Ownership.Contains(roots[1]), Is.True);
                Assert.That(result.Ownership.Contains(first), Is.False);
                Assert.That(roots, Is.Not.TypeOf<SceneNode[]>());
            });
            Assert.Throws<NotSupportedException>(() => ((IList<SceneNode>)roots)[0] = first);
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
            Assert.That(roots[0].IsDestroyed, Is.True);
            Assert.That(roots[1].IsDestroyed, Is.True);
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void GroupedSourcesValidateBeforeAnyOwnedAllocation()
    {
        SceneNode first = new("First");
        SceneNode second = new("Second");
        int checks = 0;
        bool configured = false;
        try
        {
            Assert.Throws<InvalidOperationException>(() => SceneNodePrefabUtility.CloneHierarchiesOwned(
                [first, second], (_, _, _, _) => configured = true, () =>
                {
                    if (++checks == 4)
                        throw new InvalidOperationException("Second source changed.");
                }));
            Assert.That(configured, Is.False);
            Assert.That(first.IsDestroyed, Is.False);
            Assert.That(second.IsDestroyed, Is.False);
        }
        finally { first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void GroupedSourceReferencesAreCapturedBeforeValidationCallbacks()
    {
        SceneNode first = new("First");
        SceneNode second = new("Second");
        SceneNode replacement = new("Replacement");
        List<SceneNode> input = [first, second];
        int checks = 0;
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned(input, requireSources: () =>
            {
                if (++checks == 1) input[1] = replacement;
            });
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(result.Value![1].Name, Is.EqualTo("Second"));
            Assert.That(result.Ownership.Contains(replacement), Is.False);
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            replacement.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void LaterAotRootCannotRegenerateAnEarlierRootDuringItsFactory()
    {
        SceneNode first = new("First", new OwnedCloneTransformProbe());
        SceneNode second = new("Second", new OwnedCloneTransformProbe());
        OwnedCloneTransformProbe? firstCloneTransform = null;
        Exception? blocked = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(source =>
        {
            if (ReferenceEquals(source, first.Transform))
                return firstCloneTransform = new OwnedCloneTransformProbe();
            try { firstCloneTransform!.Generate(); }
            catch (Exception failure) { blocked = failure; }
            return new OwnedCloneTransformProbe();
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned([first, second]);
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(blocked, Is.TypeOf<InvalidOperationException>());
            Assert.That(firstCloneTransform!.SceneNode, Is.SameAs(result.Value![0]));
            Assert.That(result.Value[1].IsDestroyed, Is.False);
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void ConfigurationCannotRegenerateAnotherCapturedRoot()
    {
        SceneNode first = new("First");
        SceneNode second = new("Second");
        SceneNode? firstClone = null;
        Exception? blocked = null;
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned([first, second], (index, clone, _, validate) =>
            {
                if (index == 0) firstClone = clone;
                else
                {
                    try { firstClone!.Generate(); }
                    catch (Exception failure) { blocked = failure; }
                }
                validate();
            });
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(blocked, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Value![0], Is.SameAs(firstClone));
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void LaterInitializationCannotRegenerateEarlierRoot()
    {
        SceneNode first = new("First");
        SceneNode second = new("Second");
        _ = second.AddComponent(() => new OwnedCloneComponentProbe());
        SceneNode? firstClone = null;
        Exception? blocked = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterComponentCloneHandler<OwnedCloneComponentProbe>(
            (_, node) => node.AddComponent(() => new OwnedCloneComponentProbe()));
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned([first, second], (index, clone, _, _) =>
            {
                if (index == 0)
                    firstClone = clone;
                else
                    ((OwnedCloneComponentProbe)clone.Components[0]).PostDeserialize = () =>
                    {
                        try { firstClone!.Generate(); }
                        catch (Exception failure) { blocked = failure; }
                    };
            });
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(blocked, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Value![0], Is.SameAs(firstClone));
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void GroupedConfigurationFailureRetainsBothRootsAndOriginalError()
    {
        SceneNode first = new("First");
        SceneNode second = new("Second");
        SceneNode? firstClone = null;
        SceneNode? secondClone = null;
        InvalidOperationException failure = new("Second configuration failed.");
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned([first, second], (index, clone, _, _) =>
            {
                if (index == 0) firstClone = clone;
                else
                {
                    secondClone = clone;
                    throw failure;
                }
            });
        try
        {
            Assert.That(result.Failure, Is.SameAs(failure));
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Ownership.Contains(firstClone!), Is.True);
            Assert.That(result.Ownership.Contains(secondClone!), Is.True);
            Assert.That(firstClone!.IsDestroyed, Is.False);
            Assert.That(secondClone!.IsDestroyed, Is.False);
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void FinalSourceValidationCannotRegenerateACompletedPeer()
    {
        SceneNode first = new("First");
        SceneNode second = new("Second");
        SceneNode? firstClone = null;
        Guid beforeRegeneration = Guid.Empty;
        Exception? blocked = null;
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned([first, second],
                (index, clone, _, _) =>
                {
                    if (index != 0) return;
                    firstClone = clone;
                    beforeRegeneration = clone.ID;
                },
                () =>
                {
                    if (blocked is not null || firstClone is null || firstClone.ID == beforeRegeneration)
                        return;
                    try { firstClone.Generate(); }
                    catch (Exception failure) { blocked = failure; }
                });
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(blocked, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Value![0], Is.SameAs(firstClone));
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [TestCase("reparent")]
    [TestCase("children")]
    [TestCase("components")]
    [TestCase("list-lifetime")]
    public void ConfigurationCannotChangeAnotherRootsStructuralOwner(string change)
    {
        SceneNode first = new("First");
        _ = first.NewChild("Child");
        _ = first.AddComponent(() => new OwnedCloneComponentProbe());
        SceneNode second = new("Second");
        SceneNode? firstClone = null;
        Exception? blocked = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterComponentCloneHandler<OwnedCloneComponentProbe>(
            (_, node) => node.AddComponent(() => new OwnedCloneComponentProbe()));
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned([first, second], (index, clone, _, _) =>
            {
                if (index == 0)
                {
                    firstClone = clone;
                    return;
                }
                try
                {
                    switch (change)
                    {
                        case "reparent": firstClone!.Parent = clone; break;
                        case "children": firstClone!.Transform.Children = new(); break;
                        case "components": firstClone!.ComponentsSerialized = []; break;
                        case "list-lifetime": firstClone!.Transform.Children.Generate(); break;
                    }
                }
                catch (Exception failure) { blocked = failure; }
            });
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(blocked, Is.TypeOf<InvalidOperationException>());
            Assert.That(firstClone!.Parent, Is.Null);
            Assert.That(firstClone.Transform.Children, Has.Count.EqualTo(1));
            Assert.That(firstClone.Components, Has.Count.EqualTo(1));
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void LaterAotFactoryFailureRetainsEarlierRootAndPartialAllocation()
    {
        SceneNode first = new("First", new OwnedCloneTransformProbe());
        SceneNode second = new("Second", new OwnedCloneTransformProbe());
        OwnedCloneTransformProbe? earlierTransform = null;
        OwnedCloneTransformProbe? partial = null;
        InvalidOperationException failure = new("Later transform factory failed.");
        int destroying = 0;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(source =>
        {
            if (ReferenceEquals(source, first.Transform))
            {
                earlierTransform = new OwnedCloneTransformProbe();
                earlierTransform.Destroying += _ => { destroying++; return true; };
                return earlierTransform;
            }
            partial = new OwnedCloneTransformProbe();
            throw failure;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned([first, second]);
        try
        {
            Assert.That(result.Failure, Is.SameAs(failure));
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Ownership.Contains(earlierTransform!), Is.True);
            Assert.That(result.Ownership.Contains(earlierTransform!.SceneNode!), Is.True);
            Assert.That(result.Ownership.Contains(partial!), Is.True);
            Assert.That(destroying, Is.Zero);
            Assert.That(earlierTransform.IsDestroyed, Is.False);
            Assert.That(partial!.IsDestroyed, Is.False);
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void PublicationChangeToAnotherRootRollsBackExactCacheEntries()
    {
        SceneNode first = new("First", new OwnedCloneTransformProbe());
        SceneNode second = new("Second", new OwnedCloneTransformProbe());
        SceneNode? firstClone = null;
        SceneNode? secondClone = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(
            _ => new OwnedCloneTransformProbe());
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<IReadOnlyList<SceneNode>> result =
            SceneNodePrefabUtility.CloneHierarchiesOwned([first, second], (index, clone, _, _) =>
            {
                if (index == 0)
                {
                    firstClone = clone;
                    ((OwnedCloneTransformProbe)clone.Transform).Publishing = () => secondClone!.Parent = firstClone;
                }
                else secondClone = clone;
            });
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Ownership.Contains(firstClone!), Is.True);
            Assert.That(result.Ownership.Contains(secondClone!), Is.True);
            Assert.That(firstClone!.IsDestroyed, Is.False);
            Assert.That(secondClone!.IsDestroyed, Is.False);
            foreach (XRObjectBase allocation in result.Ownership.Objects)
                Assert.That(XRObjectBase.ObjectsCache.TryGetValue(allocation.ID, out XRObjectBase? cached) &&
                    ReferenceEquals(cached, allocation), Is.False);
        }
        finally
        {
            ((OwnedCloneTransformProbe)firstClone!.Transform).Publishing = null;
            secondClone!.Parent = null;
            RetireTestAllocations(result.Ownership);
            first.Destroy(true);
            second.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void ConfigurationRunsUnderConstructionOwnershipAndKeepsAdditionalAllocations()
    {
        SceneNode template = new("Template");
        TextFile? extra = null;
        bool configured = false;
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template,
            (clone, context, validate) =>
            {
                configured = true;
                context.RequireAllocation(clone);
                extra = new TextFile();
                clone.Name = "Configured";
                validate();
            });
        try
        {
            Assert.That(configured, Is.True);
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(result.Value!.Name, Is.EqualTo("Configured"));
            Assert.That(result.Ownership.Contains(extra!), Is.True);
            Assert.That(result.Ownership.HasSealedIdentities, Is.True);
        }
        finally { RetireTestAllocations(result.Ownership); template.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConfigurationCannotCompleteWithAnEndedEmptyStructuralList(bool components)
    {
        SceneNode template = new("Template");
        XRObjectBase? list = null;
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template,
            (clone, _, _) =>
            {
                list = components ? (XRObjectBase)clone.Components : clone.Transform.Children;
                list.Destroy(true);
            });
        try
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Ownership.Contains(list!), Is.True);
            Assert.That(list!.IsDestroyed, Is.True);
        }
        finally { list?.Generate(); RetireTestAllocations(result.Ownership); template.Destroy(true); }
    }

    [Test]
    public void SourceValidationRunsAfterSerializationBeforeOwnedConfiguration()
    {
        SceneNode template = new("Template");
        int checks = 0;
        bool configured = false;
        try
        {
            Assert.Throws<InvalidOperationException>(() => SceneNodePrefabUtility.CloneHierarchyOwned(template,
                (_, _, _) => configured = true,
                () =>
                {
                    if (++checks == 2) throw new InvalidOperationException("The serialized source changed.");
                }));
            Assert.That(checks, Is.EqualTo(2));
            Assert.That(configured, Is.False);
            Assert.That(template.IsDestroyed, Is.False);
        }
        finally { template.Destroy(true); }
    }

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

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void CloneCannotEnterAnExistingOrNewHiddenScene(bool createSceneInsideClone, bool duringPublication)
    {
        XRScene? hidden = createSceneInsideClone ? null : new() { IsVisible = false };
        SceneNode template = new("Template", new OwnedCloneTransformProbe());
        SceneNode? escaped = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            hidden ??= new() { IsVisible = false };
            OwnedCloneTransformProbe transform = new();
            if (duringPublication)
                transform.Publishing = () => hidden.SceneRoots.Add(escaped = transform.SceneNode!);
            else
                hidden.SceneRoots.Add(escaped = new("Unpublished root"));
            return transform;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(template);
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(hidden!.SceneRoots.Count, Is.Zero);
            Assert.That(escaped!.HasSceneRootOwner, Is.False);
            Assert.That(escaped.IsDestroyed, Is.False);
            Assert.That(result.Ownership.Contains(escaped), Is.True);
        }
        finally
        {
            RetireTestAllocations(result.Ownership);
            hidden?.Destroy(true);
            template.Destroy(true);
        }
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

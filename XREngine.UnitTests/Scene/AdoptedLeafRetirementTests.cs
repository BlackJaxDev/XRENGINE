using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.UnitTests.Prefabs;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class AdoptedLeafRetirementTests
{
    [Test]
    public void AdoptedLeafRetiresFromExactRuntimeWorldStore()
    {
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene());
        SceneNode parent = new(world, "Parent");
        try
        {
            SceneNode child = new(parent) { Name = "Child" };
            var transform = child.Transform;
            var handle = transform.HierarchyHandle;
            AdoptedLeafRetirementReceipt receipt = new(child, parent.Transform,
                Array.Empty<RuntimeComponentCreationReceipt>());
            transform.HierarchyStore.ShouldBeSameAs(world.TransformHierarchy);
            world.TransformHierarchy.IsAlive(handle).ShouldBeTrue();

            receipt.TryRetire().ShouldBeTrue(receipt.Failure?.ToString());

            child.IsDestroyed.ShouldBeTrue();
            transform.World.ShouldBeNull();
            transform.HierarchyStore.ShouldBeNull();
            world.TransformHierarchy.IsAlive(handle).ShouldBeFalse();
            parent.IsDestroyed.ShouldBeFalse();
            receipt.TryRetire().ShouldBeTrue();
        }
        finally { parent.Destroy(true); }
    }

    [Test]
    public void ForeignComponentRejectsAdmissionWithoutDestroyingTheNode()
    {
        SceneNode parent = new("Parent");
        try
        {
            SceneNode child = new(parent);
            AdoptedLeafRetirementReceipt receipt = new(child, parent.Transform,
                Array.Empty<RuntimeComponentCreationReceipt>());
            XRComponent foreign = child.AddComponent<OwnedRetirementComponentProbe>()!;

            receipt.TryRetire().ShouldBeFalse();
            receipt.TryRetire().ShouldBeFalse();
            child.IsDestroyed.ShouldBeFalse();
            foreign.IsDestroyed.ShouldBeFalse();
            receipt.Failure.ShouldNotBeNull();
        }
        finally { parent.Destroy(true); }
    }

    [Test]
    public void BorrowedParentNodeRejectsLifetimeChangesDuringChildDestruction()
    {
        SceneNode parent = new("Parent");
        try
        {
            SceneNode child = new(parent);
            AdoptedLeafRetirementReceipt receipt = new(child, parent.Transform,
                Array.Empty<RuntimeComponentCreationReceipt>());
            int calls = 0;
            child.Destroying += _ =>
            {
                calls++;
                Should.Throw<InvalidOperationException>(() => parent.Generate());
                Should.Throw<InvalidOperationException>(() => parent.Destroy(true));
                return true;
            };

            receipt.TryRetire().ShouldBeTrue(receipt.Failure?.ToString());

            calls.ShouldBe(1);
            parent.IsDestroyed.ShouldBeFalse();
            child.IsDestroyed.ShouldBeTrue();
        }
        finally { parent.Destroy(true); }
    }

    [Test]
    public void PostCommitExceptionKeepsFailedReceiptWithoutReplay()
    {
        SceneNode parent = new("Parent");
        try
        {
            SceneNode child = new(parent);
            AdoptedLeafRetirementReceipt receipt = new(child, parent.Transform,
                Array.Empty<RuntimeComponentCreationReceipt>());
            int calls = 0;
            child.Destroyed += _ => { calls++; throw new InvalidOperationException("Post commit."); };

            receipt.TryRetire().ShouldBeFalse();
            receipt.TryRetire().ShouldBeFalse();

            calls.ShouldBe(1);
            child.IsDestroyed.ShouldBeTrue();
            receipt.RetainedRetirement.ShouldNotBeNull();
            receipt.Failure.ShouldNotBeNull();
        }
        finally { parent.Destroy(true); }
    }

    [Test]
    public void SameParentRemoveAndReinsertRejectsTheOriginalReceipt()
    {
        SceneNode parent = new("Parent");
        try
        {
            SceneNode child = new(parent);
            AdoptedLeafRetirementReceipt receipt = new(child, parent.Transform,
                Array.Empty<RuntimeComponentCreationReceipt>());
            child.Parent = null;
            child.Parent = parent;

            receipt.TryRetire().ShouldBeFalse();
            child.IsDestroyed.ShouldBeFalse();
            child.Parent.ShouldBeSameAs(parent);
        }
        finally { parent.Destroy(true); }
    }

    [Test]
    public void SiblingInsertionDoesNotInvalidateTheOwnedOccurrence()
    {
        SceneNode parent = new("Parent");
        try
        {
            SceneNode child = new(parent);
            AdoptedLeafRetirementReceipt receipt = new(child, parent.Transform,
                Array.Empty<RuntimeComponentCreationReceipt>());
            SceneNode sibling = new(parent);

            receipt.TryRetire().ShouldBeTrue(receipt.Failure?.ToString());
            child.IsDestroyed.ShouldBeTrue();
            sibling.IsDestroyed.ShouldBeFalse();
        }
        finally { parent.Destroy(true); }
    }

    [Test]
    public void ExhaustedMembershipHistoryKeepsListChangesConsistentAndRejectsAdmission()
    {
        SceneNode parent = new("Parent");
        try
        {
            SceneNode child = new(parent);
            FieldInfo revision = typeof(XRWorldObjectBase).GetField("_hierarchyMembershipRevision",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            revision.SetValue(child.Transform, long.MaxValue - 1);

            child.Parent = null;
            child.Parent.ShouldBeNull();
            parent.Transform.Children.ShouldNotContain(child.Transform);
            child.Parent = parent;
            child.Parent.ShouldBeSameAs(parent);
            parent.Transform.Children.ShouldContain(child.Transform);
            child.Transform.HierarchyMembershipRevision.ShouldBe(long.MaxValue);
            Should.Throw<InvalidOperationException>(() => new AdoptedLeafRetirementReceipt(
                child, parent.Transform, Array.Empty<RuntimeComponentCreationReceipt>()));
        }
        finally { parent.Destroy(true); }
    }
}

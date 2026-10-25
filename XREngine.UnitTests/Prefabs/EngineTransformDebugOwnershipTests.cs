using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Prefabs;

[TestFixture, NonParallelizable]
public sealed class EngineTransformDebugOwnershipTests
{
    [Test]
    public void RealDebugHandleDoesNotEnterGroupedHierarchyOwnership()
    {
        IRuntimeTransformServices? previous = RuntimeTransformServices.Current;
        RuntimeTransformServices.Current = new EngineTransformDebugServiceProbe();
        SceneNode? first = null;
        SceneNode? second = null;
        ObjectConstructionResult<IReadOnlyList<SceneNode>>? result = null;
        try
        {
            first = new SceneNode("First");
            _ = first.NewChild("First child");
            second = new SceneNode("Second");
            _ = second.NewChild("Second child");
            result = SceneNodePrefabUtility.CloneHierarchiesOwned([first, second]);
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            OwnedHierarchyLifetime lifetime = new(result.Ownership, result.Failure);
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
            Assert.That(lifetime.State, Is.EqualTo(EOwnedHierarchyLifetimeState.Retired));
            Assert.That(result.Ownership.Objects.OfType<EventList<RenderCommand>>(),
                Is.Empty, "Debug render-command storage belongs to the render info.");
            Assert.That(result.Value!.All(root => root.IsDestroyed), Is.True);
        }
        finally
        {
            if (result is not null)
                foreach (XRObjectBase value in result.Ownership.Objects)
                    if (value is SceneNode node && !node.IsDestroyed) node.Destroy(true);
            first?.Destroy(true);
            second?.Destroy(true);
            RuntimeTransformServices.Current = previous;
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void FailedDebugFactoryDoesNotAbortItsOuterHierarchyBatch()
    {
        IRuntimeTransformServices? previous = RuntimeTransformServices.Current;
        var previousOverride = RenderInfo3D.ConstructorOverride;
        SceneNode source = new("Source");
        InvalidOperationException injected = new("Debug render info factory failed.");
        EventList<int>? independentList = null;
        ObjectConstructionResult<SceneNode>? result = null;
        try
        {
            IRuntimeTransformServices service = new EngineTransformDebugServiceProbe();
            result = XRObjectBase.ConstructOwned(_ =>
            {
                RuntimeTransformServices.Current = service;
                RenderInfo3D.ConstructorOverride = (_, _) =>
                {
                    independentList = new EventList<int>();
                    throw injected;
                };
                try
                {
                    Assert.That(Assert.Throws<InvalidOperationException>(() =>
                        service.CreateDebugHandle(source.Transform, () => { })),
                        Is.SameAs(injected));
                }
                finally
                {
                    RenderInfo3D.ConstructorOverride = previousOverride;
                    RuntimeTransformServices.Current = previous;
                }
                return new SceneNode("Owned");
            });
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(result.Ownership.Contains(independentList!), Is.False);
            OwnedHierarchyLifetime lifetime = new(result.Ownership, result.Failure);
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
        }
        finally
        {
            RuntimeTransformServices.Current = previous;
            RenderInfo3D.ConstructorOverride = previousOverride;
            if (result?.Value is { IsDestroyed: false } node) node.Destroy(true);
            independentList?.Destroy(true);
            source.Destroy(true);
        }
    }

    [Test]
    public void ForeignLifetimeChangeStillRejectsOwnedNonstructuralStorage()
    {
        EventList<int>? list = null;
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(_ =>
        {
            SceneNode root = new("Owned");
            list = new EventList<int>();
            return root;
        });
        OwnedHierarchyLifetime lifetime = new(result.Ownership, result.Failure);
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            list!.Generate();
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(lifetime.State, Is.EqualTo(EOwnedHierarchyLifetimeState.RecoveryRequired));
            Assert.That(lifetime.RetainedOwnership, Is.SameAs(result.Ownership));
            Assert.That(result.Value!.IsDestroyed, Is.False);
        }
        finally
        {
            result.Value?.Destroy(true);
            list?.Destroy(true);
        }
    }
}

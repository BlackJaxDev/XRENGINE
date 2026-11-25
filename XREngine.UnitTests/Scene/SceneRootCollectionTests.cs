using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class SceneRootCollectionTests
{
    private readonly List<XRScene> _scenes = [];
    private readonly List<SceneNode> _nodes = [];
    private IDisposable? _cacheSuppression;

    [SetUp]
    public void SetUp() => _cacheSuppression = XRObjectBase.SuppressObjectCacheRegistration();

    [TearDown]
    public void TearDown()
    {
        foreach (XRScene scene in _scenes)
        {
            scene.Destroying = null;
            scene.Destroyed = null;
            scene.Destroy(true);
        }
        foreach (SceneNode node in _nodes)
        {
            node.Destroy(true);
            node.Transform.Destroy(true);
        }
        _scenes.Clear();
        _nodes.Clear();
        _cacheSuppression?.Dispose();
    }

    [Test]
    public void DuplicateOccurrencesAndDualScenesKeepExactOwnerCounts()
    {
        XRScene first = Scene();
        XRScene second = Scene();
        SceneNode root = Node();
        first.SceneRoots.AddRange([root, root]);
        second.SceneRoots.Add(root);

        Assert.That(root.CaptureSceneRootMemberships(), Has.Length.EqualTo(2));
        Assert.That(Count(root, first), Is.EqualTo(2));
        Assert.That(first.SceneRoots.Remove(root), Is.True);
        Assert.That(Count(root, first), Is.EqualTo(1));
        first.SceneRoots.RemoveAt(0);
        Assert.That(Count(root, first), Is.Zero);
        Assert.That(Count(root, second), Is.EqualTo(1));
        second.SceneRoots.Clear();
        Assert.That(root.HasSceneRootOwner, Is.False);
    }

    [Test]
    public void IndexReplacementAndRemovalUseObjectIdentity()
    {
        XRScene scene = Scene();
        SceneNode first = Node();
        SceneNode second = Node();
        second.AdoptPersistentID(first.ID);
        scene.SceneRoots.AddRange([first, second, first]);
        Assert.That(scene.SceneRoots.IndexOf(second), Is.EqualTo(1));
        scene.SceneRoots[0] = second;
        Assert.That(Count(first, scene), Is.EqualTo(1));
        Assert.That(Count(second, scene), Is.EqualTo(2));
        scene.SceneRoots.Remove(first);
        Assert.That(scene.SceneRoots, Is.EqualTo(new[] { second, second }));
        Assert.That(first.HasSceneRootOwner, Is.False);
    }

    [Test]
    public void SerializationViewCannotMutateTheOwnedCollection()
    {
        XRScene scene = Scene();
        SceneNode first = Node();
        SceneNode second = Node();
        scene.SceneRoots.Add(first);
        var property = typeof(XRScene).GetProperty("RootNodes")!;
        List<SceneNode> view = (List<SceneNode>)property.GetValue(scene)!;
        view.Clear();
        view.Add(second);
        Assert.That(scene.SceneRoots.Single(), Is.SameAs(first));
        Assert.That(second.HasSceneRootOwner, Is.False);

        SceneRootCollection old = scene.SceneRoots;
        property.SetValue(scene, view);
        Assert.That(scene.SceneRoots.Single(), Is.SameAs(second));
        Assert.That(old.IsCurrent, Is.False);
        Assert.Throws<InvalidOperationException>(() => old.Add(first));
        Assert.That(first.HasSceneRootOwner, Is.False);
    }

    [Test]
    public void RangeMaterializationCannotCommitToAReplacedCollection()
    {
        XRScene scene = Scene();
        SceneNode first = Node();
        SceneNode replacement = Node();
        SceneNode pending = Node();
        scene.SceneRoots.Add(first);
        SceneRootCollection old = scene.SceneRoots;
        Assert.Throws<InvalidOperationException>(() => old.AddRange(ReentrantRange()));
        Assert.That(scene.SceneRoots.Single(), Is.SameAs(replacement));
        Assert.That(pending.HasSceneRootOwner, Is.False);

        IEnumerable<SceneNode> ReentrantRange()
        {
            scene.ReplaceSceneRoots([replacement]);
            yield return pending;
        }
    }

    [Test]
    public void InvalidRangeDoesNotPartiallyAcquireOwnership()
    {
        XRScene scene = Scene();
        SceneNode root = Node();
        Assert.Throws<ArgumentNullException>(() => scene.SceneRoots.AddRange([root, null!]));
        Assert.That(scene.SceneRoots.Count, Is.Zero);
        Assert.That(root.HasSceneRootOwner, Is.False);
    }

    [Test]
    public void ReplacementRechecksTheExactWrapperAfterCallbacks()
    {
        XRScene scene = Scene();
        SceneNode replacement = Node();
        SceneNode pending = Node();
        bool replacing = false;
        scene.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName != "RootNodes" || replacing)
                return;
            replacing = true;
            scene.ReplaceSceneRoots([replacement]);
        };
        Assert.Throws<InvalidOperationException>(() => scene.ReplaceSceneRoots([pending]));
        Assert.That(scene.SceneRoots.Single(), Is.SameAs(replacement));
        Assert.That(pending.HasSceneRootOwner, Is.False);
    }

    [Test]
    public void CompletedReplacementSurvivesAChangedObserverFailure()
    {
        XRScene scene = Scene();
        SceneNode before = Node();
        SceneNode after = Node();
        scene.SceneRoots.Add(before);
        scene.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == "RootNodes")
                throw new InvalidOperationException("Expected observer failure.");
        };
        Assert.Throws<InvalidOperationException>(() => scene.ReplaceSceneRoots([after]));
        Assert.That(scene.SceneRoots.Single(), Is.SameAs(after));
        Assert.That(before.HasSceneRootOwner, Is.False);
        Assert.That(Count(after, scene), Is.EqualTo(1));
    }

    [Test]
    public void TransferReleasesTheTemporarySceneAndPreservesDuplicateCounts()
    {
        XRScene target = Scene();
        XRScene donor = Scene();
        SceneNode old = Node();
        SceneNode root = Node();
        target.SceneRoots.Add(old);
        donor.SceneRoots.AddRange([root, root]);
        target.TakeSceneRootsFrom(donor);
        Assert.That(donor.SceneRoots.Count, Is.Zero);
        Assert.That(root.CaptureSceneRootMemberships(), Has.Length.EqualTo(1));
        Assert.That(Count(root, target), Is.EqualTo(2));
        Assert.That(old.HasSceneRootOwner, Is.False);
    }

    [Test]
    public void TransferRejectsAChangedDonorAfterDestinationCallbacks()
    {
        XRScene target = Scene();
        XRScene donor = Scene();
        SceneNode root = Node();
        SceneNode newer = Node();
        donor.SceneRoots.Add(root);
        target.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == "RootNodes")
                donor.SceneRoots.Add(newer);
        };
        Assert.Throws<InvalidOperationException>(() => target.TakeSceneRootsFrom(donor));
        Assert.That(target.SceneRoots.Count, Is.Zero);
        Assert.That(Count(root, donor), Is.EqualTo(1));
        Assert.That(Count(newer, donor), Is.EqualTo(1));
    }

    [Test]
    public void DestroyRejectsReentryAndRevivalUsesANewWrapper()
    {
        XRScene scene = Scene();
        SceneNode root = Node();
        scene.SceneRoots.AddRange([root, root]);
        SceneRootCollection old = scene.SceneRoots;
        scene.Destroying += _ =>
        {
            Assert.Throws<InvalidOperationException>(() => old.Clear());
            Assert.Throws<InvalidOperationException>(() => scene.ReplaceSceneRoots([]));
            Assert.Throws<InvalidOperationException>(scene.Generate);
            return true;
        };
        scene.Destroy(true);
        Assert.That(root.HasSceneRootOwner, Is.False);
        Assert.That(old.IsCurrent, Is.False);
        scene.Generate();
        Assert.That(scene.SceneRoots, Is.Not.SameAs(old));
        Assert.That(Count(root, scene), Is.EqualTo(2));
        Assert.Throws<InvalidOperationException>(() => old.Remove(root));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VetoOrFailedTeardownPreservesRootOwnership(bool throwAfterDestruction)
    {
        XRScene scene = Scene();
        SceneNode root = Node();
        scene.SceneRoots.Add(root);
        if (throwAfterDestruction)
        {
            scene.Destroyed += _ => throw new InvalidOperationException("Expected teardown failure.");
            Assert.Throws<InvalidOperationException>(() => scene.Destroy(true));
        }
        else
        {
            scene.Destroying += _ => false;
            scene.Destroy(true);
        }
        Assert.That(Count(root, scene), Is.EqualTo(1));
    }

    private XRScene Scene()
    {
        XRScene scene = new() { IsVisible = false };
        _scenes.Add(scene);
        return scene;
    }

    private SceneNode Node()
    {
        SceneNode node = new();
        _nodes.Add(node);
        return node;
    }

    private static int Count(SceneNode node, XRScene scene)
        => node.CaptureSceneRootMemberships().SingleOrDefault(link => ReferenceEquals(link.Scene, scene)).Count;
}

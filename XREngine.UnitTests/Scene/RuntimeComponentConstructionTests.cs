using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
[NonParallelizable]
public sealed class RuntimeComponentConstructionTests
{
    [Test]
    public void ExplicitFactory_BindsNodeBeforeDerivedConstructorAndPublishesAfterward()
    {
        SceneNode node = new("Constructor owner");
        XRComponent? created = null;
        void OnCreated(XRComponent component) => created = component;
        XRComponent.ComponentCreated += OnCreated;
        try
        {
            ConstructorProbe component = node.AddComponent(() => new ConstructorProbe())!;

            Assert.Multiple(() =>
            {
                Assert.That(component.NodeSeenInConstructor, Is.SameAs(node));
                Assert.That(component.TransformSeenInConstructor, Is.SameAs(node.Transform));
                Assert.That(component.TransformChangedAfterConstructor, Is.True);
                Assert.That(created, Is.SameAs(component));
                Assert.That(node.GetComponent<ConstructorProbe>(), Is.SameAs(component));
            });
        }
        finally
        {
            XRComponent.ComponentCreated -= OnCreated;
            node.Destroy(true);
        }
    }

    [Test]
    public void RegisteredFactories_NestedCreationKeepsEachNodeBoundToItsOwnComponent()
    {
        RuntimeComponentFactoryRegistry.Register(typeof(RegisteredInner), static () => new RegisteredInner());
        RuntimeComponentFactoryRegistry.Register(typeof(RegisteredOuter), static () => new RegisteredOuter());
        SceneNode node = new("Registered owner");
        try
        {
            RegisteredOuter outer = (RegisteredOuter)node.AddComponent(typeof(RegisteredOuter))!;

            Assert.Multiple(() =>
            {
                Assert.That(outer.NodeSeenInConstructor, Is.SameAs(node));
                Assert.That(outer.Inner, Is.Not.Null);
                Assert.That(outer.Inner!.NodeSeenInConstructor, Is.SameAs(node));
                Assert.That(node.GetComponent<RegisteredInner>(), Is.SameAs(outer.Inner));
                Assert.That(node.GetComponent<RegisteredOuter>(), Is.SameAs(outer));
            });
        }
        finally
        {
            node.Destroy(true);
        }
    }

    [Test]
    public void LegacyNestedInFieldInitializer_DoesNotConsumeOuterFactoryBinding()
    {
        SceneNode node = new("Initializer owner");
        LegacyNestedOwner.InitializerNode = node;
        try
        {
            XRComponent created = node.AddComponent<XRComponent>(static () => new LegacyNestedOwner())!;
            LegacyNestedOwner outer = (LegacyNestedOwner)created;

            Assert.Multiple(() =>
            {
                Assert.That(outer.SceneNode, Is.SameAs(node));
                Assert.That(outer.Inner.SceneNode, Is.SameAs(node));
                Assert.That(node.GetComponent<LegacyNestedComponent>(), Is.SameAs(outer.Inner));
                Assert.That(node.GetComponent<LegacyNestedOwner>(), Is.SameAs(outer));
            });
        }
        finally
        {
            LegacyNestedOwner.InitializerNode = null;
            node.Destroy(true);
        }
    }

    [Test]
    public void ThrowingConstructor_DoesNotLeaveGloballyDiscoverableComponent()
    {
        SceneNode node = new("Failed owner");
        ThrowingComponent.Constructed = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() => node.AddComponent(static () => new ThrowingComponent()));
            ThrowingComponent component = ThrowingComponent.Constructed!;

            Assert.Multiple(() =>
            {
                Assert.That(component, Is.Not.Null);
                Assert.That(component.IsDestroyed, Is.True);
                Assert.That(XRObjectBase.ObjectsCache.ContainsKey(component.ID), Is.False);
                Assert.That(node.GetComponent<ThrowingComponent>(), Is.Null);
            });
        }
        finally
        {
            ThrowingComponent.Constructed = null;
            node.Destroy(true);
        }
    }

    [Test]
    public void RegisteredFactoryReturningDifferentInstance_AbortsClaimedComponent()
    {
        SceneNode node = new("Wrong return owner");
        ForeignComponent foreign = new();
        WrongReturnComponent? claimed = null;
        RuntimeComponentFactoryRegistry.Register(typeof(WrongReturnComponent), () =>
        {
            claimed = new WrongReturnComponent();
            return foreign;
        });
        try
        {
            Assert.Throws<InvalidOperationException>(() => node.AddComponent(typeof(WrongReturnComponent)));

            Assert.Multiple(() =>
            {
                Assert.That(claimed, Is.Not.Null);
                Assert.That(claimed!.IsDestroyed, Is.True);
                Assert.That(XRObjectBase.ObjectsCache.ContainsKey(claimed.ID), Is.False);
                Assert.That(foreign.IsDestroyed, Is.False);
                Assert.That(node.GetComponent<WrongReturnComponent>(), Is.Null);
            });
        }
        finally
        {
            foreign.Destroy(true);
            node.Destroy(true);
        }
    }

    private sealed class ConstructorProbe : XRComponent
    {
        public SceneNode NodeSeenInConstructor { get; }
        public TransformBase TransformSeenInConstructor { get; }
        public bool TransformChangedAfterConstructor { get; private set; }

        public ConstructorProbe()
        {
            NodeSeenInConstructor = SceneNode;
            TransformSeenInConstructor = Transform;
        }

        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();
            TransformChangedAfterConstructor = true;
        }
    }

    private sealed class RegisteredInner : XRComponent
    {
        public SceneNode NodeSeenInConstructor { get; }
        public RegisteredInner() => NodeSeenInConstructor = SceneNode;
    }

    private sealed class RegisteredOuter : XRComponent
    {
        public SceneNode NodeSeenInConstructor { get; }
        public RegisteredInner? Inner { get; }
        public RegisteredOuter()
        {
            NodeSeenInConstructor = SceneNode;
            Inner = (RegisteredInner?)SceneNode.AddComponent(typeof(RegisteredInner));
        }
    }

    private sealed class LegacyNestedComponent : XRComponent;

    private sealed class LegacyNestedOwner : XRComponent
    {
        public static SceneNode? InitializerNode;
        public LegacyNestedComponent Inner { get; } = CreateNested();

        private static LegacyNestedComponent CreateNested()
            => InitializerNode!.AddComponent<LegacyNestedComponent>()!;
    }

    private sealed class ThrowingComponent : XRComponent
    {
        public static ThrowingComponent? Constructed;
        public ThrowingComponent()
        {
            Constructed = this;
            throw new InvalidOperationException("Construction failed after node binding.");
        }
    }

    private sealed class WrongReturnComponent : XRComponent;
    private sealed class ForeignComponent : XRComponent;
}

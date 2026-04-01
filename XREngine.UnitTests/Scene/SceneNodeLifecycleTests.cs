using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Rendering;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public class SceneNodeLifecycleTests
{
    private static readonly PropertyInfo s_worldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void AddComponent_AfterBeginPlay_InvokesComponentBeginPlay()
    {
        SceneNode node = new("LifecycleRoot");

        node.OnBeginPlay();

        LifecycleTrackingComponent component = node.AddComponent<LifecycleTrackingComponent>()!;

        Assert.Multiple(() =>
        {
            Assert.That(component, Is.Not.Null);
            Assert.That(component.BeginPlayCount, Is.EqualTo(1));
            Assert.That(component.EndPlayCount, Is.Zero);
        });
    }

    [Test]
    public void DetachAndReattachComponent_AfterBeginPlay_ReplaysPlayLifecycle()
    {
        SceneNode node = new("LifecycleRoot");
        LifecycleTrackingComponent component = node.AddComponent<LifecycleTrackingComponent>()!;

        node.OnBeginPlay();

        bool detached = node.DetachComponent(component);
        node.ReattachComponent(component);

        Assert.Multiple(() =>
        {
            Assert.That(detached, Is.True);
            Assert.That(component.BeginPlayCount, Is.EqualTo(2));
            Assert.That(component.EndPlayCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ParentingIntoBegunPlayNode_BeginsPlayForChildComponents()
    {
        SceneNode parent = new("Parent");
        SceneNode child = new("Child");
        LifecycleTrackingComponent component = child.AddComponent<LifecycleTrackingComponent>()!;

        parent.OnBeginPlay();
        child.Parent = parent;

        Assert.Multiple(() =>
        {
            Assert.That(child.HasBegunPlay, Is.True);
            Assert.That(component.BeginPlayCount, Is.EqualTo(1));
            Assert.That(component.EndPlayCount, Is.Zero);
        });
    }

    [Test]
    public void UnparentingFromBegunPlayNode_EndsPlayForChildComponents()
    {
        SceneNode parent = new("Parent");
        SceneNode child = new(parent, "Child");
        LifecycleTrackingComponent component = child.AddComponent<LifecycleTrackingComponent>()!;

        parent.OnBeginPlay();
        child.Parent = null;

        Assert.Multiple(() =>
        {
            Assert.That(child.HasBegunPlay, Is.False);
            Assert.That(component.BeginPlayCount, Is.EqualTo(1));
            Assert.That(component.EndPlayCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void AssigningAndClearingWorldOutsidePlay_TogglesExistingComponentActivation()
    {
        SceneNode node = new("LifecycleRoot");
        LifecycleTrackingComponent component = node.AddComponent<LifecycleTrackingComponent>()!;
        StubRuntimeWorldContext world = new(isPlaySessionActive: false);

        SetWorld(node, world);

        Assert.Multiple(() =>
        {
            Assert.That(component.ActivationCount, Is.EqualTo(1));
            Assert.That(component.DeactivationCount, Is.Zero);
        });

        SetWorld(node, null);

        Assert.Multiple(() =>
        {
            Assert.That(component.ActivationCount, Is.EqualTo(1));
            Assert.That(component.DeactivationCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void AssigningPlayWorld_DoesNotPreActivateExistingComponents()
    {
        SceneNode node = new("LifecycleRoot");
        LifecycleTrackingComponent component = node.AddComponent<LifecycleTrackingComponent>()!;
        StubRuntimeWorldContext world = new(isPlaySessionActive: true);

        SetWorld(node, world);

        Assert.That(component.ActivationCount, Is.Zero);

        node.OnActivated();

        Assert.That(component.ActivationCount, Is.EqualTo(1));
    }

    [Test]
    public void AddingRootNodeToEditWorld_RegistersChildDirectionalLightsImmediately()
    {
        XRWorldInstance world = new(new VisualScene3D(), new JitterScene());
        SceneNode root = new("Root");
        SceneNode lightNode = new(root, "DirectionalLight");
        DirectionalLightComponent light = lightNode.AddComponent<DirectionalLightComponent>()!;

        world.RootNodes.Add(root);

        Assert.That(world.Lights.DynamicDirectionalLights, Does.Contain(light));
    }

    [Test]
    public void ReactivatingDirectionalLight_RecreatesShadowMapAndReregistersIt()
    {
        XRWorldInstance world = new(new VisualScene3D(), new JitterScene());
        SceneNode root = new("Root");
        SceneNode lightNode = new(root, "DirectionalLight");
        DirectionalLightComponent light = lightNode.AddComponent<DirectionalLightComponent>()!;

        world.RootNodes.Add(root);

        var initialShadowMap = light.ShadowMap;

        Assert.That(initialShadowMap, Is.Not.Null);
        Assert.That(world.Lights.DynamicDirectionalLights, Does.Contain(light));

        light.IsActive = false;

        Assert.That(light.ShadowMap, Is.Null);
        Assert.That(world.Lights.DynamicDirectionalLights, Does.Not.Contain(light));

        light.IsActive = true;

        Assert.That(light.ShadowMap, Is.Not.Null);
        Assert.That(light.ShadowMap, Is.Not.SameAs(initialShadowMap));
        Assert.That(world.Lights.DynamicDirectionalLights, Does.Contain(light));
    }

    [Test]
    public void Constructor_UsesRuntimeSceneNodeService_DefaultTransformFactory()
    {
        IRuntimeSceneNodeServices previous = RuntimeSceneNodeServices.Current;
        TrackingSceneNodeServices services = new(() => new TrackingTransform(), static (object _, object _, out string? warning) =>
        {
            warning = null;
            return true;
        });

        try
        {
            RuntimeSceneNodeServices.Current = services;

            SceneNode node = new("FactoryRoot");

            Assert.Multiple(() =>
            {
                Assert.That(node.Transform, Is.TypeOf<TrackingTransform>());
                Assert.That(services.CreatedTransformCount, Is.EqualTo(1));
            });
        }
        finally
        {
            RuntimeSceneNodeServices.Current = previous;
        }
    }

    [Test]
    public void SetTransform_WhenRuntimeSceneNodeServiceRejectsAssignment_KeepsExistingTransform()
    {
        IRuntimeSceneNodeServices previous = RuntimeSceneNodeServices.Current;
        TrackingSceneNodeServices services = new(() => new Transform(), static (object _, object _, out string? warning) =>
        {
            warning = "Rejected for test.";
            return false;
        });

        try
        {
            RuntimeSceneNodeServices.Current = services;

            SceneNode node = new("ValidationRoot");
            TransformBase original = node.Transform;

            node.SetTransform(new TrackingTransform());

            Assert.That(node.Transform, Is.SameAs(original));
        }
        finally
        {
            RuntimeSceneNodeServices.Current = previous;
        }
    }

    private sealed class LifecycleTrackingComponent : XRComponent
    {
        public int ActivationCount { get; private set; }

        public int DeactivationCount { get; private set; }

        public int BeginPlayCount { get; private set; }

        public int EndPlayCount { get; private set; }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ActivationCount++;
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            DeactivationCount++;
        }

        protected override void OnBeginPlay()
        {
            base.OnBeginPlay();
            BeginPlayCount++;
        }

        protected override void OnEndPlay()
        {
            base.OnEndPlay();
            EndPlayCount++;
        }
    }

    private sealed class TrackingTransform : TransformBase
    {
        protected override System.Numerics.Matrix4x4 CreateLocalMatrix()
            => System.Numerics.Matrix4x4.Identity;
    }

    private sealed class TrackingSceneNodeServices(
        Func<object> createDefaultTransform,
        ValidateTransformAssignment validateTransformAssignment) : IRuntimeSceneNodeServices
    {
        public int CreatedTransformCount { get; private set; }

        public IDisposable? StartProfileScope(string scopeName)
            => null;

        public object CreateDefaultTransform()
        {
            CreatedTransformCount++;
            return createDefaultTransform();
        }

        public bool TryValidateTransformAssignment(object node, object transform, out string? warningMessage)
            => validateTransformAssignment(node, transform, out warningMessage);

        public void ApplyLayerToComponent(object node, object component, int layer)
        {
        }

        public void LogWarning(string message)
        {
        }
    }

    private sealed class StubRuntimeWorldContext(bool isPlaySessionActive) : IRuntimeWorldContext
    {
        public bool IsPlaySessionActive { get; } = isPlaySessionActive;

        public void RegisterTick(ETickGroup group, int order, WorldTick tick)
        {
        }

        public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
        {
        }

        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
        {
        }

        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
        {
        }
    }

    private static void SetWorld(SceneNode node, IRuntimeWorldContext? world)
        => s_worldProperty.SetValue(node, world);

    private delegate bool ValidateTransformAssignment(object node, object transform, out string? warningMessage);
}

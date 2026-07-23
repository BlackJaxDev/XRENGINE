using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine;
using XREngine.Components;
using XREngine.Components.Capture;
using XREngine.Components.Lights;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
[NonParallelizable]
public class SceneNodeLifecycleTests
{
    private static readonly PropertyInfo s_worldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private IRuntimeRenderingHostServices? _previousRenderingServices;
    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeRenderingHostServices.Current = new EngineRuntimeRenderingHostServices();
        RuntimeShaderServices.Current = new XREngine.UnitTests.Rendering.GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeRenderingHostServices.Current = _previousRenderingServices!;
        RuntimeShaderServices.Current = _previousShaderServices;
    }

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
    public void UnparentingFromEditWorldParent_KeepsChildInWorldAndActive()
    {
        SceneNode parent = new("Parent");
        SceneNode child = new(parent, "Child");
        LifecycleTrackingComponent component = child.AddComponent<LifecycleTrackingComponent>()!;
        StubRuntimeWorldContext world = new(isPlaySessionActive: false);

        SetWorld(parent, world);

        Assert.Multiple(() =>
        {
            Assert.That(child.World, Is.SameAs(world));
            Assert.That(component.ActivationCount, Is.EqualTo(1));
            Assert.That(component.DeactivationCount, Is.Zero);
        });

        child.Parent = null;

        Assert.Multiple(() =>
        {
            Assert.That(child.Parent, Is.Null);
            Assert.That(child.World, Is.SameAs(world));
            Assert.That(component.ActivationCount, Is.EqualTo(1));
            Assert.That(component.DeactivationCount, Is.Zero);
        });
    }

    [Test]
    public void Destroy_AfterUnparentingFromEditWorld_ClearsWorldForDetachedHierarchy()
    {
        SceneNode parent = new("Parent");
        SceneNode child = new(parent, "Child");
        SceneNode grandChild = new(child, "GrandChild");
        LifecycleTrackingComponent childComponent = child.AddComponent<LifecycleTrackingComponent>()!;
        LifecycleTrackingComponent grandChildComponent = grandChild.AddComponent<LifecycleTrackingComponent>()!;
        StubRuntimeWorldContext world = new(isPlaySessionActive: false);

        SetWorld(parent, world);

        parent.Transform.RemoveChild(child.Transform, EParentAssignmentMode.Immediate);
        child.Destroy();
        XRObjectBase.ProcessPendingDestructions();

        Assert.Multiple(() =>
        {
            Assert.That(child.Parent, Is.Null);
            Assert.That(child.IsDestroyed, Is.True);
            Assert.That(grandChild.IsDestroyed, Is.True);
            Assert.That(child.World, Is.Null);
            Assert.That(child.Transform.World, Is.Null);
            Assert.That(grandChild.World, Is.Null);
            Assert.That(grandChild.Transform.World, Is.Null);
            Assert.That(childComponent.DeactivationCount, Is.EqualTo(1));
            Assert.That(grandChildComponent.DeactivationCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Undo_DestroySceneNode_RestoresFreshChildHierarchy()
    {
        Undo.ClearHistory();

        try
        {
            SceneNode parent = new("Parent");
            SceneNode child = new(parent, "Child");
            _ = new SceneNode(child, "GrandChild");
            StubRuntimeWorldContext world = new(isPlaySessionActive: false);

            SetWorld(parent, world);
            Undo.TrackSceneNode(parent);
            Undo.ClearHistory();

            child.Destroy();
            XRObjectBase.ProcessPendingDestructions();

            Assert.Multiple(() =>
            {
                Assert.That(child.IsDestroyed, Is.True);
                Assert.That(parent.Transform.Children.Count, Is.Zero);
                Assert.That(Undo.CanUndo, Is.True);
            });

            Assert.That(Undo.TryUndo(), Is.True);

            SceneNode restoredChild = parent.Transform.Children.Single().SceneNode!;
            SceneNode restoredGrandChild = restoredChild.Transform.Children.Single().SceneNode!;
            Assert.Multiple(() =>
            {
                Assert.That(restoredChild, Is.Not.SameAs(child));
                Assert.That(restoredChild.Name, Is.EqualTo("Child"));
                Assert.That(restoredChild.World, Is.SameAs(world));
                Assert.That(restoredGrandChild.Name, Is.EqualTo("GrandChild"));
                Assert.That(restoredGrandChild.World, Is.SameAs(world));
            });

            Assert.That(Undo.TryRedo(), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(restoredChild.IsDestroyed, Is.True);
                Assert.That(restoredGrandChild.IsDestroyed, Is.True);
                Assert.That(parent.Transform.Children.Count, Is.Zero);
            });
        }
        finally
        {
            Undo.ClearHistory();
        }
    }

    [Test]
    public void Undo_DestroyRootSceneNode_RestoresSceneAndWorldRoots()
    {
        Undo.ClearHistory();

        try
        {
            SceneNode root = new("Root");
            _ = new SceneNode(root, "Child");
            XRScene scene = new("Scene", root);
            XRWorldInstance world = new(new XRWorld("World", scene));

            Undo.TrackScene(scene);
            Undo.ClearHistory();

            root.Destroy();
            XRObjectBase.ProcessPendingDestructions();

            Assert.Multiple(() =>
            {
                Assert.That(root.IsDestroyed, Is.True);
                Assert.That(scene.RootNodes, Is.Empty);
                Assert.That(world.RootNodes.Count, Is.Zero);
                Assert.That(Undo.CanUndo, Is.True);
            });

            Assert.That(Undo.TryUndo(), Is.True);

            SceneNode restoredRoot = scene.RootNodes.Single();
            SceneNode restoredChild = restoredRoot.Transform.Children.Single().SceneNode!;
            Assert.Multiple(() =>
            {
                Assert.That(restoredRoot, Is.Not.SameAs(root));
                Assert.That(restoredRoot.Name, Is.EqualTo("Root"));
                Assert.That(restoredRoot.World, Is.SameAs(world));
                Assert.That(restoredChild.Name, Is.EqualTo("Child"));
                Assert.That(restoredChild.World, Is.SameAs(world));
                Assert.That(world.RootNodes.Any(node => ReferenceEquals(node, restoredRoot)), Is.True);
            });

            Assert.That(Undo.TryRedo(), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(restoredRoot.IsDestroyed, Is.True);
                Assert.That(restoredChild.IsDestroyed, Is.True);
                Assert.That(scene.RootNodes, Is.Empty);
                Assert.That(world.RootNodes.Count, Is.Zero);
            });
        }
        finally
        {
            Undo.ClearHistory();
        }
    }

    [Test]
    public void Undo_DestroySceneNode_RestoresAssetReferencesWithoutCloningAssets()
    {
        Undo.ClearHistory();

        try
        {
            SceneNode parent = new("Parent");
            SceneNode child = new(parent, "Child");
            TestDestroyUndoAsset asset = new("SharedModelLikeAsset");
            DestroyUndoAssetReferenceComponent component = child.AddComponent<DestroyUndoAssetReferenceComponent>()!;
            component.Asset = asset;
            StubRuntimeWorldContext world = new(isPlaySessionActive: false);

            SetWorld(parent, world);
            Undo.TrackSceneNode(parent);
            Undo.ClearHistory();

            child.Destroy();
            XRObjectBase.ProcessPendingDestructions();

            Assert.Multiple(() =>
            {
                Assert.That(child.IsDestroyed, Is.True);
                Assert.That(parent.Transform.Children.Count, Is.Zero);
                Assert.That(Undo.CanUndo, Is.True);
            });

            Assert.That(Undo.TryUndo(), Is.True);

            SceneNode restoredChild = parent.Transform.Children.Single().SceneNode!;
            DestroyUndoAssetReferenceComponent? restoredComponent = restoredChild.GetComponent<DestroyUndoAssetReferenceComponent>();

            Assert.Multiple(() =>
            {
                Assert.That(restoredComponent, Is.Not.Null);
                Assert.That(restoredComponent!.Asset, Is.SameAs(asset));
                Assert.That(restoredChild.World, Is.SameAs(world));
            });
        }
        finally
        {
            Undo.ClearHistory();
        }
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
    public void ClearingPlayWorld_DeactivatesComponentBeforeWorldChanges()
    {
        SceneNode node = new("LifecycleRoot");
        LifecycleTrackingComponent component = node.AddComponent<LifecycleTrackingComponent>()!;
        StubRuntimeWorldContext world = new(isPlaySessionActive: true);

        SetWorld(node, world);
        node.OnActivated();
        SetWorld(node, null);

        Assert.Multiple(() =>
        {
            Assert.That(component.ActivationCount, Is.EqualTo(1));
            Assert.That(component.DeactivationCount, Is.EqualTo(1));
            Assert.That(component.WorldDuringLastDeactivation, Is.SameAs(world));
            Assert.That(component.World, Is.Null);
        });
    }

    [Test]
    public void DeactivatingTrackedParent_PreservesChildrenAndRecordsSingleUndo()
    {
        Undo.ClearHistory();

        try
        {
            SceneNode parent = new("Parent");
            SceneNode child = new(parent, "Child");
            SceneNode grandChild = new(child, "GrandChild");
            LifecycleTrackingComponent childComponent = child.AddComponent<LifecycleTrackingComponent>()!;
            StubRuntimeWorldContext world = new(isPlaySessionActive: false);

            SetWorld(parent, world);
            Undo.TrackSceneNode(parent);
            Undo.ClearHistory();

            using (Undo.TrackChange("Toggle Node Active", parent))
                parent.IsActiveSelf = false;

            IReadOnlyList<Undo.UndoEntry> undoEntries = Undo.PendingUndo;

            Assert.Multiple(() =>
            {
                Assert.That(parent.IsActiveSelf, Is.False);
                Assert.That(child.IsDestroyed, Is.False);
                Assert.That(grandChild.IsDestroyed, Is.False);
                Assert.That(child.Parent, Is.SameAs(parent));
                Assert.That(grandChild.Parent, Is.SameAs(child));
                Assert.That(parent.Transform.Children.Single().SceneNode, Is.SameAs(child));
                Assert.That(child.Transform.Children.Single().SceneNode, Is.SameAs(grandChild));
                Assert.That(childComponent.DeactivationCount, Is.EqualTo(1));
                Assert.That(undoEntries.Count, Is.EqualTo(1));
                Assert.That(undoEntries[0].Description, Is.EqualTo("Toggle Node Active"));
                Assert.That(undoEntries[0].Changes, Has.Count.EqualTo(1));
                Assert.That(undoEntries[0].Changes[0].TargetDisplayName, Is.EqualTo("Parent"));
                Assert.That(undoEntries[0].Changes[0].PropertyName, Is.EqualTo(nameof(SceneNode.IsActiveSelf)));
            });

            Assert.That(Undo.TryUndo(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(parent.IsActiveSelf, Is.True);
                Assert.That(child.Parent, Is.SameAs(parent));
                Assert.That(grandChild.Parent, Is.SameAs(child));
                Assert.That(child.IsDestroyed, Is.False);
                Assert.That(grandChild.IsDestroyed, Is.False);
            });

            Assert.That(Undo.TryRedo(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(parent.IsActiveSelf, Is.False);
                Assert.That(child.Parent, Is.SameAs(parent));
                Assert.That(grandChild.Parent, Is.SameAs(child));
                Assert.That(child.IsDestroyed, Is.False);
                Assert.That(grandChild.IsDestroyed, Is.False);
            });
        }
        finally
        {
            Undo.ClearHistory();
        }
    }

    [Test]
    public void DeactivatingLightProbeGridSpawnerParent_PreservesSpawnedChildrenAndRecordsSingleUndo()
    {
        Undo.ClearHistory();

        try
        {
            SceneNode parent = new("LightProbeGridParent");
            LightProbeGridSpawnerComponent spawner = parent.AddComponent<LightProbeGridSpawnerComponent>()!;
            SceneNode spawnedProbe = new(parent, "LightProbe[0,0,0]");
            AddSpawnedGridNode(spawner, spawnedProbe);
            StubRuntimeWorldContext world = new(isPlaySessionActive: false);

            SetWorld(parent, world);
            Undo.TrackSceneNode(parent);
            Undo.ClearHistory();

            using (Undo.TrackChange("Toggle Node Active", parent))
                parent.IsActiveSelf = false;

            XRObjectBase.ProcessPendingDestructions();
            IReadOnlyList<Undo.UndoEntry> undoEntries = Undo.PendingUndo;

            Assert.Multiple(() =>
            {
                Assert.That(parent.IsActiveSelf, Is.False);
                Assert.That(spawnedProbe.IsDestroyed, Is.False);
                Assert.That(spawnedProbe.Parent, Is.SameAs(parent));
                Assert.That(parent.Transform.Children.Single().SceneNode, Is.SameAs(spawnedProbe));
                Assert.That(undoEntries.Count, Is.EqualTo(1));
                Assert.That(undoEntries[0].Description, Is.EqualTo("Toggle Node Active"));
            });
        }
        finally
        {
            Undo.ClearHistory();
        }
    }

    [Test]
    public void NonDeactivatableNode_RejectsActiveSelfFalse()
    {
        SceneNode node = new("ProtectedEditorNode")
        {
            CanDeactivate = false,
        };
        int deactivatedCount = 0;
        node.Deactivated += _ => deactivatedCount++;

        node.IsActiveSelf = false;
        node.IsActiveInHierarchy = false;

        Assert.Multiple(() =>
        {
            Assert.That(node.IsActiveSelf, Is.True);
            Assert.That(deactivatedCount, Is.Zero);
        });

        node.CanDeactivate = true;
        node.IsActiveSelf = false;

        Assert.Multiple(() =>
        {
            Assert.That(node.IsActiveSelf, Is.False);
            Assert.That(deactivatedCount, Is.EqualTo(1));
        });
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
    public void ActiveDirectionalLight_RecreatesMissingRuntimeShadowCamera()
    {
        XRWorldInstance world = new(new VisualScene3D(), new JitterScene());
        SceneNode root = new("Root");
        SceneNode lightNode = new(root, "DirectionalLight");
        DirectionalLightComponent light = lightNode.AddComponent<DirectionalLightComponent>()!;

        world.RootNodes.Add(root);

        FieldInfo viewportField = typeof(XREngine.Components.Capture.Lights.Types.OneViewLightComponent)
            .GetField("_primaryShadowViewport", BindingFlags.Instance | BindingFlags.NonPublic)!;
        XRViewport viewport = (XRViewport)viewportField.GetValue(light)!;
        viewport.Camera = null;

        Assert.That(light.IsActiveInHierarchy, Is.True);
        Assert.That(light.ShadowCamera, Is.Not.Null);
        Assert.That(viewport.ActiveCamera, Is.SameAs(light.ShadowCamera));
    }

    [Test]
    public void DirectionalShadowCollection_RecreatesMissingRuntimeShadowCameraBeforeSubmission()
    {
        XRWorldInstance world = new(new VisualScene3D(), new JitterScene());
        SceneNode root = new("Root");
        SceneNode lightNode = new(root, "DirectionalLight");
        DirectionalLightComponent light = lightNode.AddComponent<DirectionalLightComponent>()!;
        light.EnableCascadedShadows = false;

        world.RootNodes.Add(root);

        FieldInfo viewportField = typeof(XREngine.Components.Capture.Lights.Types.OneViewLightComponent)
            .GetField("_primaryShadowViewport", BindingFlags.Instance | BindingFlags.NonPublic)!;
        XRViewport viewport = (XRViewport)viewportField.GetValue(light)!;
        viewport.Camera = null;

        light.CollectVisibleItems();

        Assert.Multiple(() =>
        {
            Assert.That(viewport.ActiveCamera, Is.Not.Null);
            Assert.That(viewport.ActiveCamera, Is.SameAs(light.ShadowCamera));
        });
    }

    [Test]
    public void ReactivatingDirectionalLight_ReplacesDisposedRuntimeShadowViewport()
    {
        XRWorldInstance world = new(new VisualScene3D(), new JitterScene());
        SceneNode root = new("Root");
        SceneNode lightNode = new(root, "DirectionalLight");
        DirectionalLightComponent light = lightNode.AddComponent<DirectionalLightComponent>()!;

        world.RootNodes.Add(root);

        FieldInfo viewportField = typeof(XREngine.Components.Capture.Lights.Types.OneViewLightComponent)
            .GetField("_primaryShadowViewport", BindingFlags.Instance | BindingFlags.NonPublic)!;
        XRViewport initialViewport = (XRViewport)viewportField.GetValue(light)!;
        Assert.That(initialViewport.ActiveCamera, Is.Not.Null);

        light.IsActive = false;

        Assert.Multiple(() =>
        {
            Assert.That(viewportField.GetValue(light), Is.Null);
            Assert.That(initialViewport.ActiveCamera, Is.Null);
        });

        light.IsActive = true;

        XRViewport replacementViewport = (XRViewport)viewportField.GetValue(light)!;
        Assert.Multiple(() =>
        {
            Assert.That(replacementViewport, Is.Not.SameAs(initialViewport));
            Assert.That(replacementViewport.ActiveCamera, Is.Not.Null);
            Assert.That(light.ShadowCamera, Is.SameAs(replacementViewport.ActiveCamera));
        });
    }

    [Test]
    public void DeactivatingDirectionalLight_WithdrawsCascadeViewportsBeforeDisposal()
    {
        XRWorldInstance world = new(new VisualScene3D(), new JitterScene());
        SceneNode root = new("Root");
        SceneNode lightNode = new(root, "DirectionalLight");
        DirectionalLightComponent light = lightNode.AddComponent<DirectionalLightComponent>()!;

        world.RootNodes.Add(root);

        FieldInfo stateField = typeof(DirectionalLightComponent)
            .GetField("_desktopCascadeState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object state = stateField.GetValue(light)!;
        FieldInfo viewportsField = state.GetType()
            .GetField("Viewports", BindingFlags.Instance | BindingFlags.Public)!;
        XRViewport[] initialViewports = (XRViewport[])viewportsField.GetValue(state)!;

        Assert.That(initialViewports, Is.Not.Empty);
        Assert.That(initialViewports, Has.All.Matches<XRViewport>(viewport => viewport.ActiveCamera is not null));

        light.IsActive = false;

        Assert.Multiple(() =>
        {
            Assert.That((XRViewport[])viewportsField.GetValue(state)!, Is.Empty);
            Assert.That(initialViewports, Has.All.Matches<XRViewport>(viewport => viewport.ActiveCamera is null));
        });
    }

    [Test]
    public void DestroyingEditorSceneRoot_RemovesItFromEditorAndRuntimeRootCollections()
    {
        XRWorldInstance world = new(new VisualScene3D(), new JitterScene());
        SceneNode root = new("EditorOnlyRoot");
        root.AddComponent<DirectionalLightComponent>();

        world.AddToEditorScene(root);

        Assert.Multiple(() =>
        {
            Assert.That(world.EditorScene.RootNodes, Does.Contain(root));
            Assert.That(world.RootNodes, Does.Contain(root));
            Assert.That(world.Lights.DynamicDirectionalLights.Count, Is.EqualTo(1));
        });

        root.Destroy();
        XREngine.Data.Core.XRObjectBase.ProcessPendingDestructions();

        Assert.Multiple(() =>
        {
            Assert.That(root.IsDestroyed, Is.True);
            Assert.That(world.EditorScene.RootNodes, Does.Not.Contain(root));
            Assert.That(world.RootNodes, Does.Not.Contain(root));
            Assert.That(world.Lights.DynamicDirectionalLights, Is.Empty);
        });
    }

    [Test]
    public void DestroyingLoadedSceneRoot_RemovesItFromSceneAndRuntimeRootCollections()
    {
        SceneNode root = new("SceneRoot");
        root.AddComponent<DirectionalLightComponent>();

        XRScene scene = new("RuntimeScene", root);
        XRWorld targetWorld = new("TestWorld", scene);
        XRWorldInstance world = new(targetWorld, new VisualScene3D(), new JitterScene());

        Assert.Multiple(() =>
        {
            Assert.That(scene.RootNodes, Does.Contain(root));
            Assert.That(world.RootNodes, Does.Contain(root));
            Assert.That(world.Lights.DynamicDirectionalLights.Count, Is.EqualTo(1));
        });

        root.Destroy();
        XREngine.Data.Core.XRObjectBase.ProcessPendingDestructions();

        Assert.Multiple(() =>
        {
            Assert.That(root.IsDestroyed, Is.True);
            Assert.That(scene.RootNodes, Does.Not.Contain(root));
            Assert.That(world.RootNodes, Does.Not.Contain(root));
            Assert.That(world.Lights.DynamicDirectionalLights, Is.Empty);
        });
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

        public IRuntimeWorldContext? WorldDuringLastDeactivation { get; private set; }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ActivationCount++;
        }

        protected override void OnComponentDeactivated()
        {
            WorldDuringLastDeactivation = World;
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

    private sealed class DestroyUndoAssetReferenceComponent : XRComponent
    {
        public XRAsset? Asset { get; set; }
    }

    private sealed class TestDestroyUndoAsset(string name) : XRAsset(name)
    {
    }

    private static void AddSpawnedGridNode(LightProbeGridSpawnerComponent spawner, SceneNode node)
    {
        FieldInfo field = typeof(LightProbeGridSpawnerComponent).GetField(
            "_spawnedNodes",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((List<SceneNode>)field.GetValue(spawner)!).Add(node);
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

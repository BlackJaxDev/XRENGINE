using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Rendering.Picking;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

public enum EWorldPlayState
{
    Stopped,
    Playing,
    Paused,
}

public sealed class RuntimeRenderWorld
{
    public string? Name { get; set; }
    public RuntimeWorldSettings Settings { get; set; } = new();
}

public sealed class RuntimeRenderWorldInstance : XRObjectBase, IRuntimeRenderWorld, IRuntimeRenderInfo3DRegistrationTarget
{
    public RuntimeRenderWorldInstance()
    {
        RootNodes = new RootNodeCollection(this, OnRootNodeDestroying);
        VisualScene = new VisualScene3D();
        Lights = new Lights3DCollection(this);
    }

    public RuntimeRenderWorld? TargetWorld { get; set; }
    public object? TargetWorldObject => TargetWorld;
    public string? TargetWorldName => TargetWorld?.Name;
    public object? GameModeObject => GameMode;
    public IRuntimeAmbientSettings? AmbientSettings => Settings;
    public RuntimeWorldSettings Settings => TargetWorld?.Settings ?? _settings;
    public EWorldPlayState PlayState { get; set; } = EWorldPlayState.Stopped;
    public bool IsPlaySessionActive => PlayState is EWorldPlayState.Playing or EWorldPlayState.Paused;
    public RootNodeCollection RootNodes { get; }
    IReadOnlyList<SceneNode> IRuntimeRenderWorld.RootNodes => RootNodes;
    public VisualScene3D VisualScene { get; }
    public Lights3DCollection Lights { get; }
    public EventList<CameraComponent> FramebufferCameras { get; } = [];
    public RuntimePhysicsDebugScene PhysicsScene { get; } = new();
    public object? GameMode { get; set; }

    public bool IsInEditorScene(SceneNode? node) => false;

    private readonly RuntimeWorldSettings _settings = new();
    private readonly HashSet<RuntimeWorldObjectBase> _dirtyWorldObjects = [];
    private readonly Dictionary<RuntimeWorldObjectBase, Matrix4x4> _dirtyWorldMatrices = [];
    private readonly Dictionary<ETickGroup, SortedDictionary<int, List<WorldTick>>> _registeredTicks = [];

    public ColorF3 GetEffectiveAmbientColor()
        => Settings.AmbientLightColor * Settings.AmbientLightIntensity;

    public void GlobalPreRender()
    {
        VisualScene.GlobalSwapBuffers();
        Lights.SwapBuffers();
    }

    public void GlobalPostRender()
    {
    }

    public void DebugRenderPhysics()
        => PhysicsScene.DebugRender();

    public void OnRootNodeDestroying(SceneNode node)
    {
        RemoveFromWorld(node);
        RootNodes.RemoveDuringNodeDestroy(node);
    }

    public void AddToWorld(RuntimeWorldObjectBase item)
    {
        if (item is IRenderable renderable)
        {
            foreach (RenderInfo renderInfo in renderable.RenderedObjects)
            {
                if (renderInfo is RenderInfo3D renderInfo3D)
                    VisualScene.AddRenderable(renderInfo3D);
            }
        }
    }

    public void RemoveFromWorld(RuntimeWorldObjectBase item)
    {
        if (item is IRenderable renderable)
        {
            foreach (RenderInfo renderInfo in renderable.RenderedObjects)
            {
                if (renderInfo is RenderInfo3D renderInfo3D)
                    VisualScene.RemoveRenderable(renderInfo3D);
            }
        }
    }

    void IRuntimeRenderInfo3DRegistrationTarget.AddRenderable3D(IRuntimeRenderInfo3DRegistrationItem renderable)
    {
        if (renderable is RenderInfo3D renderInfo)
            VisualScene.AddRenderable(renderInfo);
    }

    void IRuntimeRenderInfo3DRegistrationTarget.RemoveRenderable3D(IRuntimeRenderInfo3DRegistrationItem renderable)
    {
        if (renderable is RenderInfo3D renderInfo)
            VisualScene.RemoveRenderable(renderInfo);
    }

    public void RegisterTick(ETickGroup group, int order, WorldTick callback)
    {
        if (!_registeredTicks.TryGetValue(group, out SortedDictionary<int, List<WorldTick>>? orderedTicks))
            _registeredTicks[group] = orderedTicks = [];

        if (!orderedTicks.TryGetValue(order, out List<WorldTick>? callbacks))
            orderedTicks[order] = callbacks = [];

        if (!callbacks.Contains(callback))
            callbacks.Add(callback);
    }

    public void UnregisterTick(ETickGroup group, int order, WorldTick callback)
    {
        if (!_registeredTicks.TryGetValue(group, out SortedDictionary<int, List<WorldTick>>? orderedTicks)
            || !orderedTicks.TryGetValue(order, out List<WorldTick>? callbacks))
            return;

        callbacks.Remove(callback);
        if (callbacks.Count == 0)
            orderedTicks.Remove(order);
        if (orderedTicks.Count == 0)
            _registeredTicks.Remove(group);
    }

    public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
        => _dirtyWorldObjects.Add(worldObject);

    public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
        => _dirtyWorldMatrices[worldObject] = worldMatrix;

    public void RaycastOctreeAsync(
        CameraComponent cameraComponent,
        Vector2 normalizedScreenPoint,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedResults,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback,
        ERaycastHitMode hitMode = ERaycastHitMode.Faces,
        bool useUnjitteredProjection = false)
        => RaycastOctreeAsync(
            cameraComponent.Camera.GetWorldSegment(normalizedScreenPoint, useUnjitteredProjection),
            orderedResults,
            finishedCallback,
            hitMode);

    public void RaycastOctreeAsync(
        Segment worldSegment,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedResults,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback,
        ERaycastHitMode hitMode = ERaycastHitMode.Faces)
        => VisualScene.RaycastAsync(worldSegment, orderedResults, (_, _) => (null, null), finishedCallback);
}

public sealed class RuntimePhysicsDebugScene
{
    public void DebugRender()
    {
    }
}
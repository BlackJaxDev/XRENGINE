using XREngine.Rendering;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Physics;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Bootstrap-owned composition root for one live world. This is deliberately a
/// coordinator, not another world interface: nodes retain <see cref="RuntimeWorld"/>
/// and windows retain <see cref="IRuntimeRenderWorld"/>.
/// </summary>
public sealed class RuntimeWorldHost : IDisposable
{
    private bool _physicsInitialized;
    private bool _visualSceneInitialized;
    private bool _timeCallbacksLinked;
    private bool _disposed;
    private XRWorld? _subscribedWorld;

    internal RuntimeWorldHost(AbstractPhysicsScene physicsScene, VisualScene3D visualScene)
    {
        // Rendering must be attached before the target scenes enter Core. Scene
        // loading assigns world context and can activate renderable components
        // synchronously, so constructing Core with the target already assigned
        // would make those initial registrations race the renderer capability.
        CoreWorld = new RuntimeWorld(physicsScene ?? throw new ArgumentNullException(nameof(physicsScene)));
        RenderWorld = new RuntimeWorldRenderer(CoreWorld, visualScene ?? throw new ArgumentNullException(nameof(visualScene)));
        RenderWorld.BindWorldState(
            () => CoreWorld.TargetWorld,
            () => CoreWorld.TargetWorld?.Name,
            () => CoreWorld.GameMode,
            () => CoreWorld.RootNodes);
        RenderWorld.GpuMeshBvhPickingEnabled = Engine.EditorPreferences.GpuMeshBvhClickPickEnabled;
        try
        {
            RuntimeWorldHostCompositionServices.Compose(this);
        }
        catch
        {
            try
            {
                CoreWorld.Dispose();
            }
            finally
            {
                RenderWorld.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Assigns the initial world only after the host and Core registry entries
    /// can be published. The callback runs after identity assignment and before
    /// scene loading, closing re-entrant lookup gaps in activation callbacks.
    /// </summary>
    internal void Initialize(XRWorld targetWorld, Action? afterTargetAssigned = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(targetWorld);
        if (CoreWorld.TargetWorld is not null)
            throw new InvalidOperationException("The world host is already initialized.");

        CoreWorld.RetargetWorld(
            targetWorld,
            afterTargetAssigned: () =>
            {
                afterTargetAssigned?.Invoke();
                RenderWorld.BindSettings(targetWorld.Settings);
                ApplyPhysicsSettings(targetWorld.Settings);
                SubscribeToWorldSettings(targetWorld);
            });
    }

    public RuntimeWorld CoreWorld { get; }
    public RuntimeWorldRenderer RenderWorld { get; }
    public XRWorld TargetWorld => CoreWorld.TargetWorld
        ?? throw new InvalidOperationException("The world host no longer has a target world.");

    /// <summary>Starts the visual and physics backends before activating Core roots.</summary>
    public async Task BeginPlayAsync()
    {
        ThrowIfDisposed();
        if (CoreWorld.IsPlaySessionActive)
            return;

        await CoreWorld.BeginPlayAsync(
            beforeNodeActivation: () =>
            {
                RenderWorld.BindSettings(TargetWorld.Settings);
                ApplyPhysicsSettings(TargetWorld.Settings);
                RenderWorld.Lights.RebuildCachesFromWorld();
                RenderWorld.VisualScene.Initialize();
                _visualSceneInitialized = true;
                Engine.InvokePhysicsThreadTask(CoreWorld.PhysicsScene.Initialize);
                _physicsInitialized = true;
                RenderWorld.VisualScene.GenericRenderTree.Swap();
                return Task.CompletedTask;
            },
            afterNodeActivation: () =>
            {
                if (CoreWorld.PhysicsEnabled)
                {
                    Engine.InvokePhysicsThreadTask(CoreWorld.PhysicsScene.OnEnterPlayMode);
                    Engine.InvokePhysicsThreadTask(CoreWorld.CapturePhysicsResetInitialPoses);
                }

                LinkTimeCallbacks();
                return Task.CompletedTask;
            },
            childRecalculationLoopType: Engine.EffectiveSettings.RecalcChildMatricesLoopType);
    }

    /// <summary>Returns a composed world to editor operation without a game mode or simulation.</summary>
    public Task BeginEditModeAsync()
    {
        ThrowIfDisposed();
        if (CoreWorld.IsPlaySessionActive)
            EndPlay();

        CoreWorld.PhysicsEnabled = false;
        CoreWorld.GameMode = null;
        return BeginPlayAsync();
    }

    /// <summary>Ends Core callbacks before tearing down backend-specific resources.</summary>
    public void EndPlay()
    {
        if (_disposed || !CoreWorld.IsPlaySessionActive)
            return;

        UnlinkTimeCallbacks();
        CoreWorld.EndPlay(
            afterNodeDeactivation: () =>
            {
                TearDownBackends();
                RenderWorld.ResetPhysicsDebugRenderer();
            },
            afterPersistentRootReactivation: RenderWorld.Lights.RebuildCachesFromWorld);
    }

    /// <summary>Retargets this composed host while preserving its runtime identity.</summary>
    internal void Retarget(XRWorld targetWorld, Action? afterTargetAssigned = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(targetWorld);
        if (ReferenceEquals(CoreWorld.TargetWorld, targetWorld))
            return;

        CoreWorld.RetargetWorld(
            targetWorld,
            afterTargetAssigned: () =>
            {
                afterTargetAssigned?.Invoke();
                RenderWorld.BindSettings(targetWorld.Settings);
                ApplyPhysicsSettings(targetWorld.Settings);
                SubscribeToWorldSettings(targetWorld);
            });
        RenderWorld.Lights.RebuildCachesFromWorld();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        EndPlay();
        TearDownBackends();
        SubscribeToWorldSettings(null);
        // Core unloads roots while rendering registration is still available,
        // allowing active components to detach deterministically.
        CoreWorld.Dispose();
        RenderWorld.Dispose();
        _disposed = true;
    }

    private void ApplyPhysicsSettings(WorldSettings settings)
        => CoreWorld.PhysicsScene.Gravity = settings.Gravity;

    private void SubscribeToWorldSettings(XRWorld? world)
    {
        if (ReferenceEquals(_subscribedWorld, world))
            return;

        if (_subscribedWorld is not null)
            _subscribedWorld.Settings.PropertyChanged -= OnWorldSettingsChanged;
        _subscribedWorld = world;
        if (_subscribedWorld is not null)
            _subscribedWorld.Settings.PropertyChanged += OnWorldSettingsChanged;
    }

    private void OnWorldSettingsChanged(object? sender, IXRPropertyChangedEventArgs args)
    {
        if (sender is not WorldSettings settings)
            return;

        switch (args.PropertyName)
        {
            case nameof(WorldSettings.Gravity):
                ApplyPhysicsSettings(settings);
                break;
            case nameof(WorldSettings.Bounds):
                RenderWorld.VisualScene.SetBounds(settings.Bounds);
                break;
        }
    }

    private void LinkTimeCallbacks()
    {
        if (_timeCallbacksLinked)
            return;

        Engine.Time.Timer.UpdateFrame += CoreWorld.Update;
        Engine.Time.Timer.PostUpdateFrame += ProcessDirtyTransforms;
        Engine.Time.Timer.FixedUpdate += CoreWorld.FixedUpdate;
        Engine.Time.Timer.SwapBuffers += RenderWorld.GlobalSwapBuffers;
        Engine.Time.Timer.PreCollectVisible += RenderWorld.GlobalPreCollectVisible;
        Engine.Time.Timer.CollectVisible += RenderWorld.GlobalCollectVisible;
        _timeCallbacksLinked = true;
    }

    private void UnlinkTimeCallbacks()
    {
        if (!_timeCallbacksLinked)
            return;

        Engine.Time.Timer.UpdateFrame -= CoreWorld.Update;
        Engine.Time.Timer.PostUpdateFrame -= ProcessDirtyTransforms;
        Engine.Time.Timer.FixedUpdate -= CoreWorld.FixedUpdate;
        Engine.Time.Timer.SwapBuffers -= RenderWorld.GlobalSwapBuffers;
        Engine.Time.Timer.PreCollectVisible -= RenderWorld.GlobalPreCollectVisible;
        Engine.Time.Timer.CollectVisible -= RenderWorld.GlobalCollectVisible;
        _timeCallbacksLinked = false;
    }

    private void ProcessDirtyTransforms()
        => CoreWorld.ProcessDirtyTransforms(Engine.EffectiveSettings.RecalcChildMatricesLoopType);

    private void TearDownBackends()
    {
        if (_physicsInitialized)
        {
            Engine.InvokePhysicsThreadTask(CoreWorld.PhysicsScene.Destroy);
            _physicsInitialized = false;
        }

        if (_visualSceneInitialized)
        {
            RenderWorld.VisualScene.Destroy();
            _visualSceneInitialized = false;
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}

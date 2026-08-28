using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Composes one simulation world without a visual scene, window, or renderer service.</summary>
internal sealed class HeadlessRuntimeWorldHost : IDisposable
{
    private bool _physicsInitialized;
    private bool _timeCallbacksLinked;
    private bool _disposed;
    private XRWorld? _subscribedWorld;

    public HeadlessRuntimeWorldHost()
        => CoreWorld = new RuntimeWorld(new JitterScene());

    public RuntimeWorld CoreWorld { get; }

    private XRWorld TargetWorld => CoreWorld.TargetWorld
        ?? throw new InvalidOperationException("The headless world host has no target world.");

    public void Initialize(XRWorld targetWorld, Action? afterTargetAssigned = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(targetWorld);
        CoreWorld.RetargetWorld(targetWorld, () =>
        {
            afterTargetAssigned?.Invoke();
            ApplyPhysicsSettings(targetWorld.Settings);
            SubscribeToWorldSettings(targetWorld);
        });
    }

    public async Task BeginPlayAsync()
    {
        ThrowIfDisposed();
        if (CoreWorld.IsPlaySessionActive)
            return;

        await CoreWorld.BeginPlayAsync(
            beforeNodeActivation: () =>
            {
                ApplyPhysicsSettings(TargetWorld.Settings);
                Engine.InvokePhysicsThreadTask(CoreWorld.PhysicsScene.Initialize);
                _physicsInitialized = true;
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

    public Task BeginEditModeAsync()
    {
        ThrowIfDisposed();
        if (CoreWorld.IsPlaySessionActive)
            EndPlay();
        CoreWorld.PhysicsEnabled = false;
        CoreWorld.GameMode = null;
        return BeginPlayAsync();
    }

    public void EndPlay()
    {
        if (_disposed || !CoreWorld.IsPlaySessionActive)
            return;

        UnlinkTimeCallbacks();
        CoreWorld.EndPlay(afterNodeDeactivation: TearDownPhysics);
    }

    public void Retarget(XRWorld targetWorld, Action? afterTargetAssigned = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(targetWorld);
        CoreWorld.RetargetWorld(targetWorld, () =>
        {
            afterTargetAssigned?.Invoke();
            ApplyPhysicsSettings(targetWorld.Settings);
            SubscribeToWorldSettings(targetWorld);
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        EndPlay();
        TearDownPhysics();
        SubscribeToWorldSettings(null);
        CoreWorld.Dispose();
        _disposed = true;
    }

    private void LinkTimeCallbacks()
    {
        if (_timeCallbacksLinked)
            return;
        Engine.Time.Timer.UpdateFrame += CoreWorld.Update;
        Engine.Time.Timer.PostUpdateFrame += ProcessDirtyTransforms;
        Engine.Time.Timer.FixedUpdate += CoreWorld.FixedUpdate;
        _timeCallbacksLinked = true;
    }

    private void UnlinkTimeCallbacks()
    {
        if (!_timeCallbacksLinked)
            return;
        Engine.Time.Timer.UpdateFrame -= CoreWorld.Update;
        Engine.Time.Timer.PostUpdateFrame -= ProcessDirtyTransforms;
        Engine.Time.Timer.FixedUpdate -= CoreWorld.FixedUpdate;
        _timeCallbacksLinked = false;
    }

    private void ProcessDirtyTransforms()
        => CoreWorld.ProcessDirtyTransforms(Engine.EffectiveSettings.RecalcChildMatricesLoopType);

    private void TearDownPhysics()
    {
        if (!_physicsInitialized)
            return;
        Engine.InvokePhysicsThreadTask(CoreWorld.PhysicsScene.Destroy);
        _physicsInitialized = false;
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
        if (sender is WorldSettings settings && args.PropertyName == nameof(WorldSettings.Gravity))
            ApplyPhysicsSettings(settings);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}

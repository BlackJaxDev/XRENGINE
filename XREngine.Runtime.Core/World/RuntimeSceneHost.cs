using XREngine.Data.Runtime.AotParity;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine;

/// <summary>
/// Owns an explicitly composed scene and advances it one step on the caller's
/// thread. It creates no window, native subsystem, worker, or timing loop.
/// </summary>
public sealed class RuntimeSceneHost : IRuntimeWorldContext, IDisposable
{
    private readonly RuntimeWorldLifecycle _lifecycle;
    private bool _disposed;
    private bool _disposing;
    private bool _disposeRequested;
    private bool _advancing;
    private int _lifecycleVersion;

    public RuntimeSceneHost()
    {
        _lifecycle = new RuntimeWorldLifecycle(this, OnRootDestroying);
    }

    public RootNodeCollection RootNodes => _lifecycle.RootNodes;
    public bool IsPlaySessionActive => _lifecycle.IsPlaySessionActive;
    public float DeltaSeconds { get; private set; }
    public long StepCount { get; private set; }

    /// <summary>Starts the existing node/component lifecycle exactly once until stopped.</summary>
    public void Start()
    {
        using var parityScope = AotParityDiagnostics.EnterSynchronousPlayerPath(EAotParityPlayerPathKind.PlayMode);
        ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
        if (_lifecycle.PlayState != RuntimeWorldPlayState.Stopped)
            return;

        int version = ++_lifecycleVersion;
        _lifecycle.PlayState = RuntimeWorldPlayState.BeginningPlay;
        try
        {
            RefreshTransforms();
            if (_lifecycleVersion != version || _lifecycle.PlayState != RuntimeWorldPlayState.BeginningPlay)
                return;
            SceneNode[] roots = [.. RootNodes];
            foreach (SceneNode node in roots)
            {
                if (_lifecycleVersion != version || _lifecycle.PlayState != RuntimeWorldPlayState.BeginningPlay)
                    return;
                if (!ReferenceEquals(node.World, this))
                    continue;
                node.OnBeginPlay();
            }

            foreach (SceneNode node in roots)
            {
                if (_lifecycleVersion != version || _lifecycle.PlayState != RuntimeWorldPlayState.BeginningPlay)
                    return;
                if (!ReferenceEquals(node.World, this))
                    continue;
                if (node.IsActiveSelf)
                    node.OnActivated();
            }

            if (_lifecycleVersion != version || _lifecycle.PlayState != RuntimeWorldPlayState.BeginningPlay)
                return;
            _lifecycle.PlayState = RuntimeWorldPlayState.Playing;
            RefreshTransforms();
            SwapBuffers();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Runs one bounded caller-scheduled simulation step; stopped hosts do no work.</summary>
    public void Advance(float deltaSeconds) => Advance(deltaSeconds, publishRenderBuffers: true);

    /// <summary>Advances simulation, optionally leaving render-buffer publication to the frame owner.</summary>
    public void Advance(float deltaSeconds, bool publishRenderBuffers)
    {
        ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (_lifecycle.PlayState != RuntimeWorldPlayState.Playing)
            return;
        if (_advancing)
            throw new InvalidOperationException("Scene advancement cannot be reentered.");

        _advancing = true;
        int version = _lifecycleVersion;
        DeltaSeconds = deltaSeconds;
        try
        {
            _lifecycle.TickGroup(ETickGroup.PrePhysics);
            if (!CanContinueAdvance(version))
                return;
            _lifecycle.TickGroup(ETickGroup.DuringPhysics);
            if (!CanContinueAdvance(version))
                return;
            _lifecycle.TickGroup(ETickGroup.PostPhysics);
            if (!CanContinueAdvance(version))
                return;
            _lifecycle.TickGroup(ETickGroup.Normal);
            if (!CanContinueAdvance(version))
                return;
            _lifecycle.TickGroup(ETickGroup.Late);
            if (!CanContinueAdvance(version))
                return;
            RefreshTransforms();
            if (CanContinueAdvance(version))
            {
                if (publishRenderBuffers)
                    PublishRenderBuffers();
                StepCount++;
            }
        }
        finally
        {
            _advancing = false;
        }
    }

    public void Stop()
    {
        if (_lifecycle.PlayState is RuntimeWorldPlayState.Stopped or RuntimeWorldPlayState.EndingPlay)
            return;

        _lifecycleVersion++;
        _lifecycle.PlayState = RuntimeWorldPlayState.EndingPlay;
        try
        {
            SceneNode[] roots = [.. RootNodes];
            foreach (SceneNode node in roots)
            {
                if (ReferenceEquals(node.World, this) && node.HasBegunPlay)
                    node.OnEndPlay();
            }

            roots = [.. RootNodes];
            foreach (SceneNode node in roots)
                if (ReferenceEquals(node.World, this) && node.IsActiveSelf)
                    node.OnDeactivated();
        }
        finally
        {
            _lifecycle.PlayState = RuntimeWorldPlayState.Stopped;
            DeltaSeconds = 0;
            if (_disposeRequested)
            {
                _disposeRequested = false;
                Dispose();
            }
        }
    }

    public void RegisterTick(ETickGroup group, int order, WorldTick tick)
        => _lifecycle.RegisterTick(group, order, tick);

    public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
        => _lifecycle.UnregisterTick(group, order, tick);

    private bool CanContinueAdvance(int version)
        => _lifecycleVersion == version && _lifecycle.PlayState == RuntimeWorldPlayState.Playing && !_disposed;

    // This host traverses every root at the end of each step. It does not need
    // the desktop world's separately scheduled dirty-queue collection stage.
    public void AddDirtyRuntimeObject(XRWorldObjectBase worldObject)
        => ArgumentNullException.ThrowIfNull(worldObject);

    public void EnqueueRuntimeWorldMatrixChange(XRWorldObjectBase worldObject, Matrix4x4 worldMatrix)
    {
        // World matrices remain simulation-owned until the caller publishes a frame.
        ArgumentNullException.ThrowIfNull(worldObject);
    }

    private void RefreshTransforms()
    {
        for (int index = 0; index < RootNodes.Count; index++)
            RootNodes[index].Transform.RecalculateMatrixHierarchyImmediate(setRenderMatrixNow: false);
    }

    /// <summary>Publishes the completed simulation hierarchy to the engine's render matrices.</summary>
    public void SwapBuffers()
    {
        ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
        if (_advancing)
            throw new InvalidOperationException("Render buffers cannot be published during a simulation step.");
        RefreshTransforms();
        PublishRenderBuffers();
    }

    private void PublishRenderBuffers()
    {
        for (int index = 0; index < RootNodes.Count; index++)
            PublishTransform(RootNodes[index].Transform);
    }

    private static void PublishTransform(TransformBase transform)
    {
        Matrix4x4 matrix = transform.WorldMatrix;
        if (transform.ShouldEnqueueRenderMatrix(matrix))
            transform.SetRenderMatrixImmediate(matrix);
        for (int index = 0; index < transform.ChildCount; index++)
            PublishTransform(transform[index]);
    }

    private void OnRootDestroying(SceneNode node)
        => RootNodes.RemoveDuringNodeDestroy(node);

    public void Dispose()
    {
        if (_disposed || _disposing)
            return;
        if (_lifecycle.PlayState == RuntimeWorldPlayState.EndingPlay)
        {
            // Finish callbacks before destruction can invoke node cleanup again.
            _disposeRequested = true;
            return;
        }

        _disposing = true;
        try
        {
            Stop();
            while (RootNodes.Count != 0)
            {
                SceneNode node = RootNodes[RootNodes.Count - 1];
                RootNodes.Remove(node);
                node.Destroy(true);
            }
            _disposed = true;
        }
        finally
        {
            _disposing = false;
        }
    }
}

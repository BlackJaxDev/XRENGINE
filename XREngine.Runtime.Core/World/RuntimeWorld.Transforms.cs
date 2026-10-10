using XREngine.Data.Runtime.AotParity;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Execution;
using XREngine.Scene.Transforms;

namespace XREngine;

public sealed partial class RuntimeWorld
{
    /// <summary>Dense, runtime-only hierarchy matrices and publication state.</summary>
    public TransformHierarchyStore TransformHierarchy { get; } = new();

    /// <summary>Runs the ordinary and late Core tick groups for one update.</summary>
    public void Update()
    {
        ThrowIfDisposed();
        bool playing = PlayState == RuntimeWorldPlayState.Playing;
        RuntimeWorldTickTelemetry.UpdateCalled(playing);
        if (!playing)
            return;
        TickGroup(ETickGroup.Normal);
        TickGroup(ETickGroup.Late);
    }

    /// <summary>Queues a transform for parent-before-child recalculation.</summary>
    public void AddDirtyTransform(TransformBase transform)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(transform);
        if (transform.ForceManualRecalc)
            return;

        TransformHierarchy.MarkDirty(transform.HierarchyHandle, transform.IsLocalMatrixDirty);
    }

    /// <summary>Processes dirty subtree ranges before rendering and dispatches callbacks after propagation.</summary>
    public void ProcessDirtyTransforms(ELoopType loopType)
    {
        ThrowIfDisposed();
        if (loopType != ELoopType.Sequential && (RuntimeWorkScheduler.IsCallerThread || OperatingSystem.IsBrowser()))
            throw new InvalidOperationException("Caller-thread worlds require sequential transform recalculation.");
        using var parityScope = IsPlaySessionActive
            ? AotParityDiagnostics.EnterSynchronousPlayerPath(EAotParityPlayerPathKind.PlayMode) : default;
        TransformHierarchy.Process(loopType);
    }
}

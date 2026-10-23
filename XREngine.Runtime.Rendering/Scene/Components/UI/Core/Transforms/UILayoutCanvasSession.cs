using System.Buffers;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Bounds one canvas to one running input and one replaceable pending input.</summary>
internal sealed class UILayoutCanvasSession
{
    private const int MaxWorkers = 2;
    private static int _activeWorkers;

    private readonly UICanvasTransform _canvas;
    private UILayoutSnapshot? _runningInput;
    private UILayoutPageLease? _runningLease;
    private List<UITransform>? _runningTargets;
    private Task<UILayoutOutputPageView>? _runningTask;
    private CancellationTokenSource? _runningCancellation;
    private bool _runningAbandoned;
    private bool _capturing;
    private bool _captureReentered;
    private bool _dispatching;
    private bool _pumping;
    private UILayoutSnapshot? _pendingInput;
    private UILayoutPageLease? _pendingLease;
    private List<UITransform>? _pendingTargets;
    private long _epoch;
    private long _runningEpoch;
    private UILayoutSnapshot? _lastAcceptedInput;
    private List<UITransform>? _lastAcceptedTargets;
    private UILayoutOutputPageView? _lastAcceptedOutput;
    private uint _blockedMeasureVersion;
    private uint _blockedArrangeVersion;
    private bool _blocked;
    private bool _forceFullCapture;
    private readonly List<UIElementChange> _drainedChanges = [];

    public UILayoutCanvasSession(UICanvasTransform canvas) => _canvas = canvas;

    /// <summary>Capture one owner-thread input and replace the prior unscheduled input.</summary>
    public bool Request()
    {
        if (_capturing)
        {
            _captureReentered = true;
            return false;
        }
        if (_dispatching || _pumping)
            return false;
        if (_canvas.IsNestedCanvas)
            return false;
        if (!_canvas.IsLayoutInvalidated)
        {
            if (TryDrainJournal(out UIChangeBatch idleChanges))
                _forceFullCapture |= idleChanges.RequiresFullResync;
            return false;
        }
        if (_blocked && _canvas.LayoutMeasureVersion == _blockedMeasureVersion &&
            _canvas.LayoutArrangeVersion == _blockedArrangeVersion)
            return false;
        _blocked = false;
        UILayoutPageLease? lease = UILayoutPageLease.TryAcquire();
        if (lease is null)
        {
            if (_pendingInput is null)
                return false;
            _pendingLease!.RetireOwner();
            _pendingInput = null;
            _pendingTargets = null;
            _pendingLease = null;
            lease = UILayoutPageLease.TryAcquire();
            if (lease is null)
                return false;
        }
        if (!TryDrainJournal(out UIChangeBatch changes))
        {
            lease.RetireOwner();
            return false;
        }

        long captureEpoch = _epoch;
        UILayoutSnapshot input;
        List<UITransform> targets = [];
        List<UITransform>? reusedTargets = null;
        _capturing = true;
        _captureReentered = false;
        try
        {
            UILayoutSnapshot? previous = _pendingInput ?? _runningInput ?? _lastAcceptedInput;
            IReadOnlyList<UITransform>? previousTargets = _pendingTargets ?? _runningTargets ?? _lastAcceptedTargets;
            reusedTargets = previousTargets as List<UITransform>;
            input = _forceFullCapture || changes.RequiresFullResync || previous is null || previousTargets is null
                ? UILayoutSnapshot.Capture(_canvas, targets)
                : UILayoutSnapshot.CaptureIncremental(_canvas, previous, previousTargets, targets,
                    _drainedChanges, reuseTargets: reusedTargets is not null);
        }
        catch (Exception ex)
        {
            _forceFullCapture = true;
            lease.RetireOwner();
            if (captureEpoch == _epoch)
                Block(ex);
            return false;
        }
        finally
        {
            _capturing = false;
        }

        if (targets.Count == 0 && reusedTargets is not null)
            targets = reusedTargets;

        if (captureEpoch != _epoch || _captureReentered || _canvas.IsNestedCanvas ||
            !DependenciesMatch(_canvas, input, targets))
        {
            _forceFullCapture = true;
            lease.RetireOwner();
            _canvas.SetLayoutInvalidated(true);
            return false;
        }

        if (_pendingInput is not null)
            _pendingLease!.RetireOwner();
        _pendingInput = null;
        _pendingTargets = null;
        _pendingLease = lease;
        _pendingInput = input;
        _pendingTargets = targets;
        _forceFullCapture = false;
        _canvas.SetLayoutInvalidated(false);
        TryStart();
        return true;
    }

    private bool TryDrainJournal(out UIChangeBatch batch)
    {
        _drainedChanges.Clear();
        UIChangeJournal journal = _canvas.ChangeJournal;
        if (journal.TryDrain(Span<UIElementChange>.Empty, out batch))
            return true;
        UIElementChange[] buffer = ArrayPool<UIElementChange>.Shared.Rent(Math.Max(1, batch.Count));
        try
        {
            if (!journal.TryDrain(buffer, out batch))
                return false;
            for (int i = 0; i < batch.Count; i++)
                _drainedChanges.Add(buffer[i]);
            return true;
        }
        finally
        {
            ArrayPool<UIElementChange>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>Accept a finished task only when every captured layout dependency still matches.</summary>
    public bool Pump()
    {
        if (_pumping || _dispatching || _capturing)
            return false;
        Task<UILayoutOutputPageView>? task = _runningTask;
        if (task is null || !task.IsCompleted)
        {
            TryStart();
            return false;
        }

        // Cancellation can abandon the owner input while its task remains a
        // one-running-per-canvas sentinel until worker completion.
        if (_runningInput is null)
        {
            Interlocked.CompareExchange(ref _runningTask, null, task);
            TryStart();
            return false;
        }

        UILayoutSnapshot input = _runningInput!;
        List<UITransform> targets = _runningTargets!;
        UILayoutPageLease lease = _runningLease!;
        long epoch = _runningEpoch;
        _pumping = true;
        _runningTask = null;
        _runningInput = null;
        _runningTargets = null;
        _runningLease = null;
        _runningCancellation?.Dispose();
        _runningCancellation = null;
        _canvas.SetUpdatingLayout(false);

        bool accepted = false;
        try
        {
            _canvas.RaiseLayoutingFinished();
            if (task.IsCompletedSuccessfully && epoch == _epoch && DependenciesMatch(_canvas, input, targets))
            {
                UILayoutOutputPageView output = task.Result;
                bool applied;
                try
                {
                    applied = Apply(input, targets, output, epoch);
                }
                catch (Exception ex)
                {
                    Block(ex);
                    applied = false;
                }
                if (applied && epoch == _epoch && !_canvas.IsLayoutInvalidated &&
                    DependenciesMatch(_canvas, input, targets))
                {
                    _canvas.PublishLayoutValues(input.Identities, output);
                    _lastAcceptedInput = input;
                    _lastAcceptedTargets = targets;
                    _lastAcceptedOutput = output;
                    accepted = true;
                }
                else
                    _canvas.SetLayoutInvalidated(true);
            }
            else
            {
                if (task.IsFaulted && task.Exception!.GetBaseException() is not OperationCanceledException)
                    Block(task.Exception!.GetBaseException());
                _canvas.SetLayoutInvalidated(true);
            }
        }
        catch (Exception ex)
        {
            Block(ex);
            _canvas.SetLayoutInvalidated(true);
        }
        finally
        {
            lease.RetireOwner();
            _pumping = false;
        }

        if (epoch != _epoch)
            return false;
        if (_canvas.IsLayoutInvalidated)
            Request();
        else
            TryStart();
        return accepted;
    }

    /// <summary>Reject any result for the old canvas lifetime; a running task keeps its page.</summary>
    public void Cancel()
    {
        _epoch++;
        _canvas.SetLayoutInvalidated(true);
        _lastAcceptedInput = null;
        _lastAcceptedTargets = null;
        _lastAcceptedOutput = null;
        _canvas.ClearLayoutValues();
        if (_pendingInput is not null)
        {
            _pendingLease!.RetireOwner();
            _pendingInput = null;
            _pendingTargets = null;
            _pendingLease = null;
        }
        if (_runningTask is null && _runningLease is not null)
        {
            _runningLease.RetireOwner();
            _runningLease = null;
            _runningInput = null;
            _runningTargets = null;
            _runningCancellation?.Cancel();
            _canvas.SetUpdatingLayout(false);
            return;
        }
        if (_runningTask is not { } task || _runningAbandoned)
            return;

        _runningAbandoned = true;
        _runningLease?.RetireOwner();
        _runningLease = null;
        _runningInput = null;
        _runningTargets = null;
        CancellationTokenSource? cancellation = _runningCancellation;
        _runningCancellation = null;
        _canvas.SetUpdatingLayout(false);
        cancellation?.Cancel();
        if (task.IsCompleted)
        {
            _ = task.Exception;
            Interlocked.CompareExchange(ref _runningTask, null, task);
            cancellation?.Dispose();
            return;
        }
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            Interlocked.CompareExchange(ref _runningTask, null, task);
            cancellation?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Checks the root dependency token and captured dirty branches.</summary>
    internal static bool DependenciesMatch(UICanvasTransform canvas, UILayoutSnapshot input, List<UITransform> targets)
    {
        BoundingRectangleF currentRootConstraint = new(Vector2.Zero,
            new Vector2(canvas.Width ?? float.PositiveInfinity, canvas.Height ?? float.PositiveInfinity));
        if (canvas.IsNestedCanvas || targets.Count != input.Nodes.Length ||
            !currentRootConstraint.Equals(input.RootBounds) ||
            FontGlyphSet.GlobalLayoutMetricsGeneration != input.FontGeneration ||
            canvas.ElementIdentity != input.Identities[0] ||
            canvas.LayoutMeasureVersion != input.MeasureVersions[0] ||
            canvas.LayoutArrangeVersion != input.ArrangeVersions[0])
            return false;

        foreach (int i in input.ValidationIndices)
        {
            UITransform target = targets[i];
            if (target.ElementIdentity != input.Identities[i] ||
                target.LayoutMeasureVersion != input.MeasureVersions[i] ||
                target.LayoutArrangeVersion != input.ArrangeVersions[i])
                return false;
            UIChangeRevisions before = input.Revisions[i];
            UIChangeRevisions now = target.ChangeRevisions;
            if (before.Measure != now.Measure || before.Arrange != now.Arrange ||
                before.Transform != now.Transform || before.Topology != now.Topology || before.Text != now.Text)
                return false;
            if (input.Nodes[i].TextInput is { } textInput &&
                !(target.SceneNode?.GetComponent<UITextComponent>()?.CaptureLayoutInput()?.Equals(textInput) ?? false))
                return false;
            int parent = input.Nodes[i].Parent;
            if (parent < 0 ? !ReferenceEquals(target, canvas) : !ReferenceEquals(target.Parent, targets[parent]))
                return false;
            UILayoutNodeInput node = input.Nodes[i];
            int currentUIChildren = 0;
            for (int c = 0; c < target.Children.Count; c++)
                if (target.Children[c] is UITransform child)
                {
                    if (currentUIChildren >= node.ChildCount ||
                        !ReferenceEquals(child, targets[input.Children[node.FirstChild + currentUIChildren].NodeIndex]))
                        return false;
                    currentUIChildren++;
                }
            if (currentUIChildren != node.ChildCount)
                return false;
        }
        return true;
    }

    private bool Apply(UILayoutSnapshot input, List<UITransform> targets, UILayoutOutputPageView outputs, long epoch)
    {
        bool textApplied = true;
        for (int page = 0; page < outputs.ChangedPages.Length; page++)
        {
            if (!outputs.ChangedPages[page])
                continue;
            int end = Math.Min((page + 1) * UILayoutOutputPageView.PageSize, targets.Count);
            for (int i = page * UILayoutOutputPageView.PageSize; i < end; i++)
            {
                if (epoch != _epoch || _canvas.IsLayoutInvalidated)
                    return false;
                UITransform transform = targets[i];
                Vector2 placement = outputs[i].PlacementTranslation;
                switch (transform.PlacementInfo)
                {
                    case UIListTransform.UIListChildPlacementInfo list:
                        if ((transform.Parent as UIListTransform)?.DisplayHorizontal == true)
                        {
                            list.BottomOrLeftOffset = placement.X;
                            placement.X = 0;
                        }
                        else
                        {
                            list.BottomOrLeftOffset = placement.Y;
                            placement.Y = 0;
                        }
                        break;
                    case UIDualSplitTransform.UISplitChildPlacementInfo split:
                        if (split.Vertical)
                        {
                            split.Offset = placement.Y;
                            placement.Y = 0;
                        }
                        else
                        {
                            split.Offset = placement.X;
                            placement.X = 0;
                        }
                        break;
                    case UIMultiSplitTransform.UISplitChildPlacementInfo split:
                        if (split.Vertical)
                        {
                            split.Offset = placement.Y;
                            placement.Y = 0;
                        }
                        else
                        {
                            split.Offset = placement.X;
                            placement.X = 0;
                        }
                        break;
                    case UIGridTransform.UIGridChildPlacementInfo grid:
                        grid.ApplyLayoutOffset(placement);
                        placement = Vector2.Zero;
                        break;
                    case UIDockingPlacementInfo dock:
                        dock.BottomLeft = placement;
                        placement = Vector2.Zero;
                        break;
                }
                transform.ApplyCalculatedLayout(outputs[i], placement);
                if (outputs[i].TextResult is { } textResult &&
                    transform.SceneNode?.GetComponent<UITextComponent>() is { } text)
                    textApplied &= text.ApplyLayoutResult(textResult, outputs[i].ActualSize);
            }
        }
        foreach (int i in input.OwnerSyncIndices)
        {
            if (epoch != _epoch || _canvas.IsLayoutInvalidated)
                return false;
            if (targets[i] is UIListTransform list && list.VirtualProvider is not null)
                list.SynchronizeVirtualRows(list.DisplayHorizontal ? outputs[i].ActualSize.X : outputs[i].ActualSize.Y);
        }
        foreach (int i in input.OwnerSyncIndices)
        {
            if (epoch != _epoch || _canvas.IsLayoutInvalidated)
                return false;
            if (targets[i] is UIScrollableTransform scroll)
                scroll.SynchronizeScroll(outputs[i].ActualSize - new Vector2(scroll.Padding.X + scroll.Padding.Z,
                    scroll.Padding.Y + scroll.Padding.W));
        }
        return textApplied;
    }

    private void TryStart()
    {
        if (_dispatching || _pumping || _capturing || _runningTask is not null || _pendingInput is null)
            return;
        if (_canvas.IsNestedCanvas || !DependenciesMatch(_canvas, _pendingInput, _pendingTargets!))
        {
            _pendingLease!.RetireOwner();
            _pendingInput = null;
            _pendingLease = null;
            _pendingTargets = null;
            _canvas.SetLayoutInvalidated(true);
            return;
        }
        if (!TryAcquireWorker())
            return;

        UILayoutSnapshot input = _pendingInput;
        UILayoutPageLease lease = _pendingLease!;
        UILayoutSnapshot? previous = _lastAcceptedInput;
        UILayoutOutputPageView? previousOutput = _lastAcceptedOutput;
        _runningInput = input;
        _runningLease = lease;
        _runningTargets = _pendingTargets;
        _runningEpoch = _epoch;
        _runningAbandoned = false;
        _runningCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _runningCancellation.Token;
        _pendingInput = null;
        _pendingLease = null;
        _pendingTargets = null;
        _canvas.SetUpdatingLayout(true);
        _dispatching = true;
        try
        {
            _canvas.RaiseLayoutingStarted();
            if (_runningEpoch != _epoch || _canvas.IsLayoutInvalidated || _canvas.IsNestedCanvas)
                throw new OperationCanceledException("Canvas layout was canceled before worker dispatch.");
            lease.StartWorker();
            _runningTask = StartWorkerTask(input, previous, previousOutput, cancellation, lease);
        }
        catch (Exception ex)
        {
            _runningInput = null;
            _runningLease = null;
            _runningTargets = null;
            _runningCancellation?.Dispose();
            _runningCancellation = null;
            _canvas.SetUpdatingLayout(false);
            _canvas.SetLayoutInvalidated(true);
            lease.FinishWorker();
            lease.RetireOwner();
            Interlocked.Decrement(ref _activeWorkers);
            if (ex is not OperationCanceledException)
                Block(ex);
        }
        finally
        {
            _dispatching = false;
        }
    }

    // Keep the task closure out of TryStart. Idle pumps must not allocate a
    // compiler-generated capture object before the pending-work guard returns.
    private static Task<UILayoutOutputPageView> StartWorkerTask(UILayoutSnapshot input,
        UILayoutSnapshot? previous, UILayoutOutputPageView? previousOutput,
        CancellationToken cancellation, UILayoutPageLease lease)
        => Task.Run(() =>
        {
            try
            {
                return UILayoutValueKernel.RunPaged(input, previous, previousOutput, cancellation);
            }
            finally
            {
                lease.FinishWorker();
                Interlocked.Decrement(ref _activeWorkers);
            }
        });

    private void Block(Exception error)
    {
        _blocked = true;
        _blockedMeasureVersion = _canvas.LayoutMeasureVersion;
        _blockedArrangeVersion = _canvas.LayoutArrangeVersion;
        Debug.UIException(error);
    }

    private static bool TryAcquireWorker()
    {
        while (true)
        {
            int current = Volatile.Read(ref _activeWorkers);
            if (current >= MaxWorkers)
                return false;
            if (Interlocked.CompareExchange(ref _activeWorkers, current + 1, current) == current)
                return true;
        }
    }
}

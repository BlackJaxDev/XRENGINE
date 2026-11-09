using System.Diagnostics;
using System.Runtime.CompilerServices;
using XREngine.Rendering;

namespace XREngine;

/// <summary>
/// Services and drains asynchronous render work on the render owner thread. It holds
/// two preallocated consumer slots. A slot stays occupied while its consumer has
/// building or submitted CPU work, pending output, or an unfinished drain. The
/// service calls owner-thread callbacks only; it does not interpret backend handles.
/// A drain request from another thread is generation-checked, so an older
/// acknowledgement cannot satisfy a newer request. A drain request can name one
/// renderer. Then the owner drains only that renderer's consumers, and admission
/// closes only for that renderer. Renderer identity is the renderer instance, so a
/// replacement renderer is a new identity. A drain that the owner thread requests
/// inside an owner operation (a consumer callback, or a backend preparation or
/// recording scope from <see cref="EnterOwnerOperation"/>) waits until the
/// outermost operation returns, so the drain never resets a workspace in use.
/// </summary>
public sealed class RuntimeAsynchronousRenderWorkService
{
    public const int ConsumerCapacity = 2;

    /// <summary>Longest wait slice while unresolved asynchronous work exists.</summary>
    public static readonly long ServiceSliceTicks = Math.Max(1L, Stopwatch.Frequency / 1000L);

    private static RuntimeAsynchronousRenderWorkService? s_installed;

    private readonly IRuntimeAsynchronousRenderWorkConsumer?[] _consumers =
        new IRuntimeAsynchronousRenderWorkConsumer?[ConsumerCapacity];
    private readonly object _sync = new();
    private int _ownerThreadId;
    private int _admissionClosed;
    private long _drainRequestGeneration;
    private long _drainAcknowledgedGeneration;
    private int _drainAcknowledgedSucceeded;
    // Owner-thread operation nesting depth. Only the owner thread reads or writes it.
    private int _ownerOperationDepth;
    // Scope of the pending drain request. Null drains every consumer. Two pending
    // requests for different renderers merge into one drain of every consumer.
    private AbstractRenderer? _pendingDrainRenderer;
    private bool _pendingDrainIsScoped;
    // Renderers whose teardown closed admission. Weak keys, so a destroyed renderer
    // does not stay alive.
    private readonly ConditionalWeakTable<AbstractRenderer, object> _closedRenderers = [];

    /// <summary>The service installed by the render thread host, or null on hosts without it.</summary>
    public static RuntimeAsynchronousRenderWorkService? Installed => Volatile.Read(ref s_installed);

    internal static void Install(RuntimeAsynchronousRenderWorkService service)
        => Volatile.Write(ref s_installed, service);

    public bool IsAdmissionOpen => Volatile.Read(ref _admissionClosed) == 0;

    /// <summary>True when admission is open globally and for <paramref name="renderer"/>.</summary>
    public bool IsAdmissionOpenFor(AbstractRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return IsAdmissionOpen && !_closedRenderers.TryGetValue(renderer, out _);
    }

    public int OwnerThreadId => Volatile.Read(ref _ownerThreadId);

    /// <summary>True when any registered consumer holds unresolved work or a drain is pending.</summary>
    public bool HasUnresolvedWork
    {
        get
        {
            if (Volatile.Read(ref _drainRequestGeneration) != Volatile.Read(ref _drainAcknowledgedGeneration))
                return true;

            for (int index = 0; index < _consumers.Length; index++)
            {
                if (Volatile.Read(ref _consumers[index]) is { HasUnresolvedWork: true })
                    return true;
            }

            return false;
        }
    }

    /// <summary>Earliest deadline among registered consumers, or <see cref="long.MaxValue"/>.</summary>
    public long NextDeadlineTicks
    {
        get
        {
            long deadline = long.MaxValue;
            for (int index = 0; index < _consumers.Length; index++)
            {
                if (Volatile.Read(ref _consumers[index]) is { } consumer)
                    deadline = Math.Min(deadline, consumer.NextDeadlineTicks);
            }

            return deadline;
        }
    }

    /// <summary>True on the owner thread while an owner operation or consumer callback is active.</summary>
    public bool IsInsideOwnerOperation => IsOwnerThread() && _ownerOperationDepth > 0;

    /// <summary>
    /// Marks owner-thread preparation or recording that a drain must not interrupt.
    /// A drain request inside the scope runs when the outermost scope ends. On other
    /// threads the scope does nothing.
    /// </summary>
    public OwnerOperationScope EnterOwnerOperation()
    {
        if (!IsOwnerThread())
            return default;

        _ownerOperationDepth++;
        return new OwnerOperationScope(this);
    }

    /// <summary>Ends an owner operation. Dispose it once, on the owner thread.</summary>
    public readonly struct OwnerOperationScope : IDisposable
    {
        private readonly RuntimeAsynchronousRenderWorkService? _service;

        internal OwnerOperationScope(RuntimeAsynchronousRenderWorkService service)
            => _service = service;

        public void Dispose()
            => _service?.ExitOwnerOperation();
    }

    private void ExitOwnerOperation()
    {
        if (_ownerOperationDepth <= 0)
            throw new InvalidOperationException("An owner operation ended more than once.");

        _ownerOperationDepth--;
        if (_ownerOperationDepth == 0 && IsDrainPending)
            Service();
    }

    private bool IsDrainPending
        => Volatile.Read(ref _drainRequestGeneration) != Volatile.Read(ref _drainAcknowledgedGeneration);

    /// <summary>Binds the render owner thread. The render loop calls this when it starts.</summary>
    public void BindOwnerThread(int managedThreadId)
        => Volatile.Write(ref _ownerThreadId, managedThreadId);

    /// <summary>
    /// Takes one of the two consumer slots. Fails when admission is closed or both
    /// slots are taken; the caller then holds its output with a reason.
    /// </summary>
    public bool TryRegister(IRuntimeAsynchronousRenderWorkConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_sync)
        {
            if (!IsAdmissionOpenFor(consumer.Renderer))
                return false;

            int free = -1;
            for (int index = 0; index < _consumers.Length; index++)
            {
                IRuntimeAsynchronousRenderWorkConsumer? current = _consumers[index];
                if (ReferenceEquals(current, consumer))
                    return true;
                if (current is null && free < 0)
                    free = index;
            }

            if (free < 0)
                return false;

            Volatile.Write(ref _consumers[free], consumer);
            return true;
        }
    }

    /// <summary>
    /// Returns a consumer slot. A consumer unregisters only after discard or complete
    /// hand-off of its output to backend retirement.
    /// </summary>
    public void Unregister(IRuntimeAsynchronousRenderWorkConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_sync)
        {
            for (int index = 0; index < _consumers.Length; index++)
            {
                if (ReferenceEquals(_consumers[index], consumer))
                {
                    if (consumer.HasUnresolvedWork)
                        throw new InvalidOperationException("A consumer cannot leave its slot while it holds unresolved work.");

                    Volatile.Write(ref _consumers[index], null);
                }
            }
        }
    }

    /// <summary>
    /// Owner-thread service pass. It checks consumer completion and deadlines and
    /// performs a pending drain request. It never waits for workers or the GPU.
    /// Inside an owner operation it does nothing; the operation's end services it.
    /// </summary>
    public void Service()
    {
        if (!IsOwnerThread() || _ownerOperationDepth > 0)
            return;

        _ownerOperationDepth++;
        try
        {
            if (!IsDrainPending)
            {
                for (int index = 0; index < _consumers.Length; index++)
                {
                    if (Volatile.Read(ref _consumers[index]) is { HasUnresolvedWork: true } consumer)
                        consumer.ServiceAsynchronousWork();
                }
            }

            // A callback above can request a drain; perform it after the callbacks return.
            if (IsDrainPending)
                PerformPendingDrain();
        }
        finally
        {
            _ownerOperationDepth--;
        }
    }

    private void PerformPendingDrain()
    {
        long requested;
        AbstractRenderer? scope;
        lock (_sync)
        {
            // Read the scope and generation together so that a request that
            // arrives after this point keeps its own scope.
            requested = _drainRequestGeneration;
            scope = _pendingDrainIsScoped ? _pendingDrainRenderer : null;
            _pendingDrainRenderer = null;
            _pendingDrainIsScoped = false;
        }

        bool drained = DrainConsumers(scope);
        Volatile.Write(ref _drainAcknowledgedSucceeded, drained ? 1 : 0);
        Volatile.Write(ref _drainAcknowledgedGeneration, requested);
    }

    /// <summary>Closes admission without a drain request. New consumers cannot register.</summary>
    public void CloseAdmission()
        => Interlocked.Exchange(ref _admissionClosed, 1);

    /// <summary>Closes admission for one renderer. Other renderers can still register consumers.</summary>
    public void CloseAdmission(AbstractRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        lock (_sync)
            _closedRenderers.AddOrUpdate(renderer, renderer);
    }

    /// <summary>
    /// Requests a drain of every consumer and, by default, closes admission first.
    /// The owner thread performs it at its next service pass; a request from the
    /// owner thread drains at once. Returns the request generation to wait for.
    /// </summary>
    public long RequestDrain(bool closeAdmission = true)
    {
        if (closeAdmission)
            Interlocked.Exchange(ref _admissionClosed, 1);
        return PublishDrainRequest(null);
    }

    /// <summary>
    /// Requests a drain of the consumers of <paramref name="renderer"/> only and, by
    /// default, closes admission for that renderer first. Work of other renderers
    /// keeps running. Returns the request generation to wait for.
    /// </summary>
    public long RequestDrain(AbstractRenderer renderer, bool closeAdmission = true)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (closeAdmission)
            CloseAdmission(renderer);
        return PublishDrainRequest(renderer);
    }

    /// <summary>True when any consumer of <paramref name="renderer"/> holds unresolved work.</summary>
    public bool HasUnresolvedWorkFor(AbstractRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (Volatile.Read(ref _drainRequestGeneration) != Volatile.Read(ref _drainAcknowledgedGeneration))
            return true;

        for (int index = 0; index < _consumers.Length; index++)
        {
            if (Volatile.Read(ref _consumers[index]) is { HasUnresolvedWork: true } consumer &&
                ReferenceEquals(consumer.Renderer, renderer))
            {
                return true;
            }
        }

        return false;
    }

    private long PublishDrainRequest(AbstractRenderer? renderer)
    {
        long generation;
        lock (_sync)
        {
            bool pending = _drainRequestGeneration != Volatile.Read(ref _drainAcknowledgedGeneration);
            if (!pending)
            {
                _pendingDrainIsScoped = renderer is not null;
                _pendingDrainRenderer = renderer;
            }
            else if (!_pendingDrainIsScoped || !ReferenceEquals(_pendingDrainRenderer, renderer))
            {
                // A different scope is already pending: drain every consumer.
                _pendingDrainIsScoped = false;
                _pendingDrainRenderer = null;
            }

            generation = Interlocked.Increment(ref _drainRequestGeneration);
        }

        if (IsOwnerThread())
            Service();
        return generation;
    }

    /// <summary>
    /// Reads the acknowledgement for <paramref name="requestGeneration"/>. Returns false
    /// while the owner has not acknowledged that generation or a newer one.
    /// </summary>
    public bool TryGetDrainAcknowledgement(long requestGeneration, out bool succeeded)
    {
        long acknowledged = Volatile.Read(ref _drainAcknowledgedGeneration);
        if (acknowledged < requestGeneration)
        {
            succeeded = false;
            return false;
        }

        succeeded = Volatile.Read(ref _drainAcknowledgedSucceeded) != 0;
        return true;
    }

    /// <summary>
    /// Waits without locks for the owner to acknowledge a drain request. Returns false
    /// on timeout or when the drain failed; ordinary renderer destruction must then stop.
    /// </summary>
    public bool WaitForDrainAcknowledgement(long requestGeneration, TimeSpan timeout)
    {
        if (IsInsideOwnerOperation && !TryGetDrainAcknowledgement(requestGeneration, out _))
        {
            // The drain runs only after the active owner operation returns, so a wait
            // here cannot finish. The caller must defer destruction.
            Debug.RenderingWarning(
                "[AsyncRenderWork] A drain was requested inside an active owner operation. It runs when the operation returns; destruction must wait.");
            return false;
        }

        long deadline = Stopwatch.GetTimestamp() + Math.Max(1L, (long)(timeout.TotalSeconds * Stopwatch.Frequency));
        while (true)
        {
            if (TryGetDrainAcknowledgement(requestGeneration, out bool succeeded))
                return succeeded;
            if (Stopwatch.GetTimestamp() >= deadline)
                return false;

            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Owner-thread drain when the render loop exits, normally or by failure. A pending
    /// request is acknowledged with the drain result. Thread exit alone is not success.
    /// </summary>
    public bool DrainOnOwnerExit()
    {
        Interlocked.Exchange(ref _admissionClosed, 1);
        bool drained = DrainConsumers(null);
        long requested;
        lock (_sync)
        {
            requested = _drainRequestGeneration;
            _pendingDrainRenderer = null;
            _pendingDrainIsScoped = false;
        }

        Volatile.Write(ref _drainAcknowledgedSucceeded, drained ? 1 : 0);
        Volatile.Write(ref _drainAcknowledgedGeneration, requested);
        return drained;
    }

    private bool DrainConsumers(AbstractRenderer? scope)
    {
        bool drained = true;
        for (int index = 0; index < _consumers.Length; index++)
        {
            if (Volatile.Read(ref _consumers[index]) is not { } consumer)
                continue;
            if (scope is not null && !ReferenceEquals(consumer.Renderer, scope))
                continue;

            try
            {
                if (!consumer.DrainAsynchronousWork())
                    drained = false;
            }
            catch (Exception ex)
            {
                drained = false;
                Debug.LogException(ex, "[AsyncRenderWork] Consumer drain failed; its resources stay retained.");
            }
        }

        return drained;
    }

    private bool IsOwnerThread()
    {
        int owner = Volatile.Read(ref _ownerThreadId);
        return owner != 0 && owner == Environment.CurrentManagedThreadId;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine
{
    public enum JobPriority
    {
        Lowest = 0,
        Low = 1,
        Normal = 2,
        High = 3,
        Highest = 4,
    }

    public enum JobAffinity
    {
        Any = 0,
        RenderThread = 1,
        MainThread = RenderThread,
        AppThread = 2,
        CollectVisibleSwap = 3,
        Remote = 4,
    }

    public abstract class Job
    {
        private const int StateCreated = 0;
        private const int StateRunning = 1;
        private const int StateCompleting = 2;
        private const int StateFaulting = 3;
        private const int StateCompleted = 4;

        private readonly object _lifecycleLock = new();
        private readonly Guid _id = Guid.NewGuid();
        private Stack<IEnumerator>? _executionStack;
        private Task? _pendingTask;
        private TaskCompletionSource<bool>? _completionSource;
        private int _state;
        private int _isFaulted;
        private int _isCanceled;
        private float _progress;
        private Exception? _exception;
        private CancellationTokenSource? _cts;
        private TaskCompletionSource<bool>? _shutdownCancellationCompletion;
        private Task? _shutdownFinalizationTask;
        private TaskCompletionSource<bool>? _terminalNotificationCompletion;
        private Exception? _terminalNotificationException;
        private int _pendingContextNotifications;
        private int _terminalCallbackState;
        private int _shutdownCancellationState;
        private int _shutdownCancellationTrackingClaim;
        private int _shutdownManagerFinalizationClaim;
        private CancellationTokenRegistration _externalCancellation;
        private bool _hasExternalCancellation;
        private int _starvationLogged;

        internal long LastEnqueuedTimestamp;
        internal long FirstEnqueuedTimestamp;
        private int _usesQueueSlot;
        internal bool UsesQueueSlot
        {
            get => Volatile.Read(ref _usesQueueSlot) != 0;
            set => Volatile.Write(ref _usesQueueSlot, value ? 1 : 0);
        }

        public JobPriority Priority { get; internal set; } = JobPriority.Normal;
        public JobAffinity Affinity { get; internal set; } = JobAffinity.Any;
        public RenderThreadJobKind RenderThreadKind { get; internal set; } = RenderThreadJobKind.Unknown;

        protected Job()
        {
            CallbackContext = SynchronizationContext.Current;
        }

        public Guid Id => _id;
        public float Progress => Volatile.Read(ref _progress);
        public bool IsRunning => Volatile.Read(ref _state) == StateRunning;
        public bool IsCompleted => Volatile.Read(ref _state) == StateCompleted;
        public bool IsFaulted => Volatile.Read(ref _isFaulted) == 1;
        public bool IsCanceled => Volatile.Read(ref _isCanceled) == 1;
        public bool IsCancellationRequested
            => Volatile.Read(ref _isCanceled) != 0 || CancellationToken.IsCancellationRequested;
        public Exception? Exception => _exception;
        public object? Result { get; private set; }
        public object? Payload { get; private set; }
        public SynchronizationContext? CallbackContext { get; set; }
        public CancellationToken CancellationToken => _cts?.Token ?? CancellationToken.None;
        internal Task? PendingTask => _pendingTask;
        internal Task TerminalNotificationTask
            => _terminalNotificationCompletion?.Task ?? Task.CompletedTask;
        public JobHandle Handle { get; internal set; }
        internal bool StarvationWarningEmitted => _starvationLogged == 1;

        public event Action<Job, float>? ProgressChanged;
        public event Action<Job, float, object?>? ProgressWithPayload;
        public event Action<Job>? Completed;
        public event Action<Job>? Canceled;
        public event Action<Job, Exception>? Faulted;

        internal virtual string GetProfilerLabel()
            => GetType().Name;

        public abstract IEnumerable Process();

        protected void SetResult(object? result)
        {
            Result = result;
        }

        protected void SetPayload(object? payload)
        {
            Payload = payload;
        }

        internal bool TryStart()
            => TryStartCore(createExecutionStack: true);

        internal bool TryStartForShutdownRejection()
            => TryStartCore(createExecutionStack: false);

        private bool TryStartCore(bool createExecutionStack)
        {
            if (Interlocked.CompareExchange(ref _state, StateRunning, StateCreated) != StateCreated)
                return false;

            Stack<IEnumerator>? executionStack = createExecutionStack ? new Stack<IEnumerator>() : null;
            CancellationTokenSource cancellationSource;
            bool cancelBeforeFactory;
            lock (_lifecycleLock)
            {
                _cts?.Dispose();
                cancellationSource = new CancellationTokenSource();
                _cts = cancellationSource;
                if (_hasExternalCancellation)
                {
                    _externalCancellation.Dispose();
                    _externalCancellation = default;
                    _hasExternalCancellation = false;
                }
                _executionStack = executionStack;
                _pendingTask = null;
                _shutdownCancellationCompletion = null;
                _shutdownFinalizationTask = null;
                _terminalNotificationCompletion =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _terminalNotificationException = null;
                Volatile.Write(ref _pendingContextNotifications, 0);
                Volatile.Write(ref _terminalCallbackState, 0);
                Volatile.Write(ref _shutdownCancellationState, 0);
                Volatile.Write(ref _shutdownCancellationTrackingClaim, 0);
                Volatile.Write(ref _shutdownManagerFinalizationClaim, 0);
                _completionSource = null;
                Handle = default;
                Volatile.Write(ref _usesQueueSlot, 0);
                LastEnqueuedTimestamp = 0;
                FirstEnqueuedTimestamp = 0;
                _starvationLogged = 0;
                Result = null;
                Payload = null;
                _exception = null;
                Volatile.Write(ref _progress, 0f);
                Volatile.Write(ref _isFaulted, 0);

                // Jobs are single-use. Preserve a cancellation requested before or
                // concurrently with startup, and reflect it into the newly-owned CTS.
                // A later Cancel() observes this source and cancels it directly.
                cancelBeforeFactory = Volatile.Read(ref _isCanceled) != 0;
            }

            try
            {
                // Cancellation can invoke arbitrary user registrations. Never run
                // those callbacks while holding the lifecycle lock, and include
                // callback faults in the same terminal startup-failure path.
                if (cancelBeforeFactory)
                    cancellationSource.Cancel();

                if (!createExecutionStack)
                    return true;

                // Process() and GetEnumerator() are user-extensible. Initialize all
                // lifecycle and notification tracking before invoking either, and do
                // not hold the lifecycle lock while arbitrary factory code runs.
                IEnumerable routine = Process() ??
                    throw new InvalidOperationException("Job routine cannot be null.");
                IEnumerator enumerator = routine.GetEnumerator() ??
                    throw new InvalidOperationException("Job routine enumerator cannot be null.");

                lock (_lifecycleLock)
                    executionStack!.Push(enumerator);
            }
            catch (Exception exception)
            {
                MarkStartFaulted(exception);
                throw;
            }

            return true;
        }

        private void MarkStartFaulted(Exception exception)
        {
            Exception terminalException = exception;
            try
            {
                CleanupExecutionState();
            }
            catch (Exception cleanupException)
            {
                terminalException = new AggregateException(
                    "Job startup and terminal cleanup both faulted.",
                    exception,
                    cleanupException);
            }

            _exception = terminalException;
            Interlocked.Exchange(ref _isFaulted, 1);
            Interlocked.Exchange(ref _state, StateCompleted);

            // Startup failed synchronously before manager ownership/publication. No
            // terminal user callback is dispatched, but any progress notification
            // posted by the eager factory still owns terminal tracking until it runs.
            Volatile.Write(ref _terminalCallbackState, 1);
            TryCompleteTerminalNotification();
        }

        internal bool TryClearQueueSlot()
            => Interlocked.Exchange(ref _usesQueueSlot, 0) != 0;

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _isCanceled, 1) == 1)
                return;

            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Requests cancellation without running token callbacks on the shutdown
        /// caller. The owning execution path must still reach terminal cleanup.
        /// </summary>
        internal Task RequestCancellationForShutdown()
        {
            TaskCompletionSource<bool> completion;
            CancellationTokenSource? source = null;
            bool initiate = false;
            lock (_lifecycleLock)
            {
                completion = _shutdownCancellationCompletion ??=
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (Volatile.Read(ref _shutdownCancellationState) == 0)
                {
                    Volatile.Write(ref _shutdownCancellationState, 1);
                    source = _cts;
                    initiate = true;
                }
            }

            Volatile.Write(ref _isCanceled, 1);
            if (!initiate)
                return completion.Task;

            try
            {
                if (source is null)
                {
                    QueueShutdownCancellationCompletion(completion, exception: null);
                    return completion.Task;
                }

                Task cancellation = source.CancelAsync();
                QueueShutdownCancellationObservation(cancellation, completion);
            }
            catch (ObjectDisposedException)
            {
                QueueShutdownCancellationCompletion(completion, exception: null);
            }
            catch (Exception exception)
            {
                QueueShutdownCancellationCompletion(completion, exception);
            }

            return completion.Task;
        }

        /// <summary>
        /// Asynchronously settles shutdown-canceled work. The cancellation
        /// completion source runs continuations asynchronously, so a token
        /// callback cannot re-enter and wait on its own cancellation operation.
        /// Call only from the manager's owned queue pump or an inactive job's
        /// execution/pending-task continuation, never the lifecycle caller.
        /// </summary>
        internal Task CompleteCancellationForShutdownAsync()
        {
            Task cancellation = RequestCancellationForShutdown();
            TaskCompletionSource<bool> completion;
            lock (_lifecycleLock)
            {
                if (_shutdownFinalizationTask is not null)
                    return _shutdownFinalizationTask;

                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdownFinalizationTask = completion.Task;
            }

            _ = CompleteCancellationForShutdownCoreAsync(cancellation, completion);
            return completion.Task;
        }

        internal bool TryClaimShutdownCancellationTracking()
            => Interlocked.CompareExchange(ref _shutdownCancellationTrackingClaim, 1, 0) == 0;

        internal bool TryClaimShutdownManagerFinalization()
            => Interlocked.CompareExchange(ref _shutdownManagerFinalizationClaim, 1, 0) == 0;

        private async Task CompleteCancellationForShutdownCoreAsync(
            Task cancellation,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                await cancellation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogShutdownCancellationFault(exception);
            }

            try
            {
                Task terminalNotification;
                int previousState = Interlocked.CompareExchange(
                    ref _state,
                    StateCompleted,
                    StateRunning);
                if (previousState != StateRunning)
                {
                    // A fault owner publishes its payload before StateCompleted and
                    // owns the terminal notification. Shutdown must observe that
                    // publication instead of replacing it with cancellation.
                    terminalNotification = TerminalNotificationTask;
                }
                else
                {
                    try
                    {
                        CleanupExecutionState();
                    }
                    catch
                    {
                        // Terminal shutdown cannot make the job executable again. A
                        // registration-disposal race is contained by the manager's
                        // tracked finalizer and must not strand the completion handle.
                    }

                    terminalNotification = InvokeCanceled();
                    _completionSource?.TrySetCanceled(new CancellationToken(canceled: true));
                }

                try
                {
                    await terminalNotification.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogShutdownCancellationFault(exception);
                }

                // A concurrent fault owner may have deferred CTS disposal while
                // CancelAsync was active. Terminal notification implies that owner
                // has published StateCompleted, so shutdown can now release the
                // remaining cancellation resources safely.
                try
                {
                    lock (_lifecycleLock)
                        DisposeCancellationResourcesUnderLock();
                }
                catch (Exception exception)
                {
                    LogShutdownCancellationFault(exception);
                }
            }
            finally
            {
                completion.TrySetResult(true);
            }
        }

        private void QueueShutdownCancellationObservation(
            Task cancellation,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                _ = cancellation.ContinueWith(
                    completed =>
                    {
                        Exception? exception = completed.IsFaulted
                            ? completed.Exception?.GetBaseException()
                            : completed.IsCanceled
                                ? new TaskCanceledException(completed)
                                : null;
                        FinishShutdownCancellationRequest(completion, exception);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
            catch (Exception dispatchException)
            {
                Environment.FailFast(
                    "Unable to dispatch shutdown cancellation observation without running cleanup on the lifecycle caller.",
                    dispatchException);
            }
        }

        private void QueueShutdownCancellationCompletion(
            TaskCompletionSource<bool> completion,
            Exception? exception)
        {
            try
            {
                _ = Task.Factory.StartNew(
                    () => FinishShutdownCancellationRequest(completion, exception),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
            catch (Exception dispatchException)
            {
                Environment.FailFast(
                    "Unable to dispatch shutdown cancellation completion without running cleanup on the lifecycle caller.",
                    dispatchException);
            }
        }

        private void FinishShutdownCancellationRequest(
            TaskCompletionSource<bool> completion,
            Exception? exception)
        {
            if (exception is not null)
                LogShutdownCancellationFault(exception);

            Volatile.Write(ref _shutdownCancellationState, 2);
            int state = Volatile.Read(ref _state);
            if (state is StateCompleting or StateFaulting or StateCompleted)
            {
                try
                {
                    lock (_lifecycleLock)
                        DisposeCancellationResourcesUnderLock();
                }
                catch (Exception cleanupException)
                {
                    LogShutdownCancellationFault(cleanupException);
                }
            }

            completion.TrySetResult(true);
        }

        internal void LinkCancellationToken(CancellationToken token)
        {
            if (!token.CanBeCanceled)
                return;

            if (token.IsCancellationRequested)
            {
                Cancel();
                return;
            }

            var registration = token.Register(static state => ((Job)state!).Cancel(), this);

            lock (_lifecycleLock)
            {
                if (_hasExternalCancellation)
                    _externalCancellation.Dispose();

                _externalCancellation = registration;
                _hasExternalCancellation = true;
            }
        }

        internal void AttachCompletionSource(TaskCompletionSource<bool> completionSource)
        {
            _completionSource = completionSource;
            Handle = new JobHandle(_id, completionSource.Task, this);
        }

        internal void MarkQueued(long timestamp)
        {
            LastEnqueuedTimestamp = timestamp;
            if (FirstEnqueuedTimestamp == 0)
                FirstEnqueuedTimestamp = timestamp;
            Interlocked.Exchange(ref _starvationLogged, 0);
        }

        internal bool TryMarkStarvationLogged()
            => Interlocked.Exchange(ref _starvationLogged, 1) == 0;

        internal JobStepResult Step()
        {
            if (!IsRunning)
                return JobStepResult.Idle;

            if (IsCancellationRequested)
                return CancelInternal();

            if (_pendingTask is { } waitingTask)
            {
                if (!waitingTask.IsCompleted)
                    return JobStepResult.Waiting;

                var taskResult = HandleCompletedTask(waitingTask);
                _pendingTask = null;
                if (taskResult != JobStepResult.Progressed)
                    return taskResult;

                if (IsCancellationRequested)
                    return CancelInternal();
            }

            Stack<IEnumerator>? stack;
            lock (_lifecycleLock)
            {
                stack = _executionStack;
            }

            if (stack is null || stack.Count == 0)
                return CompleteInternal();

            while (stack.Count > 0)
            {
                var iterator = stack.Peek();
                bool moved;
                try
                {
                    moved = iterator.MoveNext();
                }
                catch (Exception ex)
                {
                    return FailInternal(ex);
                }

                if (!moved)
                {
                    stack.Pop();
                    continue;
                }

                var yielded = iterator.Current;

                if (yielded is null)
                    return JobStepResult.Progressed;

                if (yielded is IEnumerator nestedEnum)
                {
                    stack.Push(nestedEnum);
                    continue;
                }

                if (yielded is IEnumerable nestedEnumerable)
                {
                    stack.Push(nestedEnumerable.GetEnumerator());
                    continue;
                }

                return HandleYield(yielded);
            }

            return CompleteInternal();
        }

        protected virtual JobStepResult HandleYield(object yielded)
        {
            switch (yielded)
            {
                case JobProgress progress:
                    UpdateProgress(progress.Value, progress.Payload, true);
                    return JobStepResult.Progressed;
                case float f:
                    UpdateProgress(f);
                    return JobStepResult.Progressed;
                case double d:
                    UpdateProgress((float)d);
                    return JobStepResult.Progressed;
                case Task task:
                    return AttachTask(task);
                case ValueTask valueTask:
                    return AttachTask(valueTask.AsTask());
                case Func<Task> taskFactory:
                {
                    var task = taskFactory();
                    return AttachTask(task ?? throw new InvalidOperationException("Task factory returned null."));
                }
                case WaitForNextDispatch:
                    return JobStepResult.Idle;
                case Action action:
                    action();
                    return JobStepResult.Progressed;
                default:
                    SetPayload(yielded);
                    return JobStepResult.Progressed;
            }
        }

        private JobStepResult AttachTask(Task task)
        {
            if (!task.IsCompleted)
            {
                _pendingTask = task;
                return JobStepResult.Waiting;
            }

            return HandleCompletedTask(task);
        }

        private JobStepResult HandleCompletedTask(Task task)
        {
            if (task.IsCanceled)
                return CancelInternal();

            if (task.IsFaulted)
            {
                Exception? aggregate = task.Exception;
                var ex = aggregate?.GetBaseException() ?? aggregate ?? new InvalidOperationException("Job task faulted without an exception.");
                return FailInternal(ex);
            }

            return JobStepResult.Progressed;
        }

        private JobStepResult CompleteInternal(bool setCompletion = true)
        {
            if (Interlocked.CompareExchange(ref _state, StateCompleting, StateRunning) != StateRunning)
                return JobStepResult.Completed;

            try
            {
                CleanupExecutionState();
            }
            catch (Exception cleanupException)
            {
                _exception = cleanupException;
                Interlocked.Exchange(ref _isFaulted, 1);
                Volatile.Write(ref _state, StateCompleted);
                _ = InvokeFaulted(cleanupException);
                _completionSource?.TrySetException(cleanupException);
                return JobStepResult.Completed;
            }

            try
            {
                UpdateProgress(1f);
            }
            catch (Exception notificationException)
            {
                LogTerminalNotificationFault("final progress", notificationException);
            }
            Volatile.Write(ref _state, StateCompleted);
            _ = InvokeCompletion();
            if (setCompletion)
                _completionSource?.TrySetResult(true);
            return JobStepResult.Completed;
        }

        private void CleanupExecutionState()
        {
            lock (_lifecycleLock)
            {
                _executionStack?.Clear();
                _executionStack = null;
                _pendingTask = null;

                if (Volatile.Read(ref _shutdownCancellationState) != 1)
                    DisposeCancellationResourcesUnderLock();
            }
        }

        private JobStepResult CancelInternal()
        {
            Interlocked.Exchange(ref _isCanceled, 1);
            if (Volatile.Read(ref _shutdownCancellationState) == 0)
            {
                try
                {
                    _cts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            else
            {
                Task? cancellation;
                lock (_lifecycleLock)
                    cancellation = _shutdownCancellationCompletion?.Task;

                if (cancellation is { IsCompleted: false })
                {
                    _pendingTask = cancellation;
                    return JobStepResult.Waiting;
                }
            }

            if (Interlocked.CompareExchange(ref _state, StateCompleted, StateRunning) != StateRunning)
                return JobStepResult.Completed;

            try
            {
                CleanupExecutionState();
            }
            catch (Exception cleanupException)
            {
                LogTerminalNotificationFault("cancellation cleanup", cleanupException);
            }
            try
            {
                _ = InvokeCanceled();
            }
            finally
            {
                _completionSource?.TrySetCanceled(CancellationToken);
            }
            return JobStepResult.Completed;
        }

        private void DisposeCancellationResourcesUnderLock()
        {
            if (_hasExternalCancellation)
            {
                _externalCancellation.Dispose();
                _externalCancellation = default;
                _hasExternalCancellation = false;
            }

            _cts?.Dispose();
            _cts = null;
        }

        private void LogShutdownCancellationFault(Exception exception)
        {
            try
            {
                JobManager.LogMessage?.Invoke(
                    $"Job '{GetProfilerLabel()}' [{Id}] shutdown cancellation callback faulted: {exception}");
            }
            catch
            {
                // Diagnostics must not compromise terminal ownership cleanup.
            }
        }

        private JobStepResult FailInternal(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _state, StateFaulting, StateRunning) != StateRunning)
                return JobStepResult.Completed;

            Exception terminalException = exception;
            try
            {
                CleanupExecutionState();
            }
            catch (Exception cleanupException)
            {
                terminalException = new AggregateException(
                    "Job execution and terminal cleanup both faulted.",
                    exception,
                    cleanupException);
            }

            _exception = terminalException;
            Interlocked.Exchange(ref _isFaulted, 1);
            Volatile.Write(ref _state, StateCompleted);
            _ = InvokeFaulted(terminalException);
            _completionSource?.TrySetException(terminalException);
            return JobStepResult.Completed;
        }

        internal void Fail(Exception exception)
            => _ = FailInternal(exception);

        protected void UpdateProgress(float progress, object? payload = null, bool setPayload = false)
        {
            float clamped = Math.Clamp(progress, 0f, 1f);
            Volatile.Write(ref _progress, clamped);
            if (setPayload)
                Payload = payload;

            Action<Job, float>? progressChanged = ProgressChanged;
            Action<Job, float, object?>? progressWithPayload = ProgressWithPayload;
            if (progressChanged is null && progressWithPayload is null)
                return;

            SynchronizationContext? context = CallbackContext;
            if (context != null)
            {
                Interlocked.Increment(ref _pendingContextNotifications);
                int completionClaimed = 0;

                void CompletePendingNotification()
                {
                    if (Interlocked.Exchange(ref completionClaimed, 1) != 0)
                        return;

                    Interlocked.Decrement(ref _pendingContextNotifications);
                    TryCompleteTerminalNotification();
                }

                try
                {
                    context.Post(_ =>
                    {
                        try
                        {
                            progressChanged?.Invoke(this, clamped);
                            progressWithPayload?.Invoke(this, clamped, Payload);
                        }
                        finally
                        {
                            CompletePendingNotification();
                        }
                    }, null);
                }
                catch
                {
                    // A custom SynchronizationContext may dispatch inline and then
                    // throw. Claim completion exactly once across both paths.
                    CompletePendingNotification();
                    throw;
                }
                return;
            }

            progressChanged?.Invoke(this, clamped);
            progressWithPayload?.Invoke(this, clamped, Payload);
        }

        private Task InvokeCompletion()
            => DispatchTerminalNotification(
                Completed is { } completed ? () => completed(this) : null,
                "completion");

        private Task InvokeCanceled()
            => DispatchTerminalNotification(
                Canceled is { } canceled ? () => canceled(this) : null,
                "cancellation");

        private Task InvokeFaulted(Exception exception)
            => DispatchTerminalNotification(
                Faulted is { } faulted ? () => faulted(this, exception) : null,
                "fault");

        private Task DispatchTerminalNotification(Action? callback, string kind)
        {
            TaskCompletionSource<bool>? completion = _terminalNotificationCompletion;
            if (completion is null)
                return Task.CompletedTask;

            SynchronizationContext? context = CallbackContext;
            if (context is null || callback is null)
            {
                CompleteTerminalNotification(callback, kind);
                return completion.Task;
            }

            try
            {
                context.Post(
                    _ => CompleteTerminalNotification(callback, kind),
                    null);
            }
            catch (Exception exception)
            {
                LogTerminalNotificationFault(kind, exception);
                _terminalNotificationException = exception;
                Volatile.Write(ref _terminalCallbackState, 2);
                TryCompleteTerminalNotification();
            }

            return completion.Task;
        }

        private void CompleteTerminalNotification(
            Action? callback,
            string kind)
        {
            try
            {
                callback?.Invoke();
                Volatile.Write(ref _terminalCallbackState, 1);
            }
            catch (Exception exception)
            {
                LogTerminalNotificationFault(kind, exception);
                _terminalNotificationException = exception;
                Volatile.Write(ref _terminalCallbackState, 2);
            }

            TryCompleteTerminalNotification();
        }

        private void TryCompleteTerminalNotification()
        {
            if (Volatile.Read(ref _terminalCallbackState) == 0 ||
                Volatile.Read(ref _pendingContextNotifications) != 0)
                return;

            TaskCompletionSource<bool>? completion = _terminalNotificationCompletion;
            if (completion is null)
                return;

            Exception? exception = _terminalNotificationException;
            if (exception is null)
                completion.TrySetResult(true);
            else
                completion.TrySetException(exception);
        }

        private void LogTerminalNotificationFault(string kind, Exception exception)
        {
            try
            {
                JobManager.LogMessage?.Invoke(
                    $"Job '{GetProfilerLabel()}' [{Id}] {kind} notification faulted: {exception}");
            }
            catch
            {
                // Diagnostics must not compromise terminal ownership cleanup.
            }
        }
    }
}

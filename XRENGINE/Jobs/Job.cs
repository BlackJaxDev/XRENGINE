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
        MainThread = 1,
        CollectVisibleSwap = 2,
        Remote = 3,
    }

    public abstract class Job
    {
        private const int StateCreated = 0;
        private const int StateRunning = 1;
        private const int StateCompleted = 2;

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
        private CancellationTokenRegistration _externalCancellation;
        private bool _hasExternalCancellation;
        private int _starvationLogged;

        internal long LastEnqueuedTimestamp;
        internal long FirstEnqueuedTimestamp;
        internal bool UsesQueueSlot;

        public JobPriority Priority { get; internal set; } = JobPriority.Normal;
        public JobAffinity Affinity { get; internal set; } = JobAffinity.Any;

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
        public bool IsCancellationRequested => CancellationToken.IsCancellationRequested;
        public Exception? Exception => _exception;
        public object? Result { get; private set; }
        public object? Payload { get; private set; }
        public SynchronizationContext? CallbackContext { get; set; }
        public CancellationToken CancellationToken => _cts?.Token ?? CancellationToken.None;
        internal Task? PendingTask => _pendingTask;
        public JobHandle Handle { get; internal set; }
        internal bool StarvationWarningEmitted => _starvationLogged == 1;

        public event Action<Job, float>? ProgressChanged;
        public event Action<Job, float, object?>? ProgressWithPayload;
        public event Action<Job>? Completed;
        public event Action<Job>? Canceled;
        public event Action<Job, Exception>? Faulted;

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
        {
            if (Interlocked.CompareExchange(ref _state, StateRunning, StateCreated) != StateCreated)
                return false;

            lock (_lifecycleLock)
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                if (_hasExternalCancellation)
                {
                    _externalCancellation.Dispose();
                    _externalCancellation = default;
                    _hasExternalCancellation = false;
                }
                _executionStack = new Stack<IEnumerator>();
                var routine = Process() ?? throw new InvalidOperationException("Job routine cannot be null.");
                _executionStack.Push(routine.GetEnumerator());
                _pendingTask = null;
                _completionSource = null;
                Handle = default;
                UsesQueueSlot = false;
                LastEnqueuedTimestamp = 0;
                FirstEnqueuedTimestamp = 0;
                _starvationLogged = 0;
                Result = null;
                Payload = null;
                _exception = null;
                Volatile.Write(ref _progress, 0f);
                Volatile.Write(ref _isFaulted, 0);
                Volatile.Write(ref _isCanceled, 0);
            }
            return true;
        }

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
            if (Interlocked.Exchange(ref _state, StateCompleted) == StateCompleted)
                return JobStepResult.Completed;

            CleanupExecutionState();

            UpdateProgress(1f);
            InvokeCompletion();
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
            }

            if (_hasExternalCancellation)
            {
                _externalCancellation.Dispose();
                _externalCancellation = default;
                _hasExternalCancellation = false;
            }

            _cts?.Dispose();
            _cts = null;
        }

        private JobStepResult CancelInternal()
        {
            Interlocked.Exchange(ref _isCanceled, 1);
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            var result = CompleteInternal(false);
            InvokeCancellation();
            _completionSource?.TrySetCanceled();
            return result;
        }

        private JobStepResult FailInternal(Exception ex)
        {
            _exception = ex;
            Interlocked.Exchange(ref _isFaulted, 1);
            var result = CompleteInternal(false);
            InvokeFault(ex);
            _completionSource?.TrySetException(ex);
            return result;
        }

        internal JobStepResult Fail(Exception ex)
            => FailInternal(ex);

        private void UpdateProgress(float value, object? payload = null, bool persistPayload = false)
        {
            var clamped = value < 0f ? 0f : value > 1f ? 1f : value;
            var previous = Volatile.Read(ref _progress);
            if (clamped < previous)
                clamped = previous;

            if (persistPayload)
                SetPayload(payload);
            else if (payload != null)
                SetPayload(payload);

            if (clamped.Equals(previous) && payload is null)
                return;

            Volatile.Write(ref _progress, clamped);

            var handler = ProgressChanged;
            if (handler != null)
            {
                var capture = clamped;
                PostCallback(() => handler(this, capture));
            }

            var handlerWithPayload = ProgressWithPayload;
            if (handlerWithPayload != null)
            {
                var capture = clamped;
                var payloadCapture = payload ?? Payload;
                PostCallback(() => handlerWithPayload(this, capture, payloadCapture));
            }
        }

        private void InvokeCompletion()
        {
            var handler = Completed;
            if (handler is null)
                return;

            PostCallback(() => handler(this));
        }

        private void InvokeCancellation()
        {
            var handler = Canceled;
            if (handler is null)
                return;

            PostCallback(() => handler(this));
        }

        private void InvokeFault(Exception ex)
        {
            var handler = Faulted;
            if (handler is null)
                return;

            PostCallback(() => handler(this, ex));
        }

        private void PostCallback(Action action)
        {
            var context = CallbackContext;
            if (context != null)
            {
                context.Post(static s => ((Action)s!).Invoke(), action);
                return;
            }

            action();
        }
    }
}

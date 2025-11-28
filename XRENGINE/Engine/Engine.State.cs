using Extensions;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Input;
using XREngine.Native;

namespace XREngine
{
    public abstract class Job
    {
        private const int StateCreated = 0;
        private const int StateRunning = 1;
        private const int StateCompleted = 2;

        private readonly object _lifecycleLock = new();
        private readonly Guid _id = Guid.NewGuid();
        private Stack<IEnumerator>? _executionStack;
        private Task? _pendingTask;
        private int _state;
        private int _isFaulted;
        private int _isCanceled;
        private float _progress;
        private Exception? _exception;
        private CancellationTokenSource? _cts;
        private CancellationTokenRegistration _externalCancellation;
        private bool _hasExternalCancellation;

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

        private JobStepResult CompleteInternal()
        {
            if (Interlocked.Exchange(ref _state, StateCompleted) == StateCompleted)
                return JobStepResult.Completed;

            CleanupExecutionState();

            UpdateProgress(1f);
            InvokeCompletion();
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

            var result = CompleteInternal();
            InvokeCancellation();
            return result;
        }

        private JobStepResult FailInternal(Exception ex)
        {
            _exception = ex;
            Interlocked.Exchange(ref _isFaulted, 1);
            var result = CompleteInternal();
            InvokeFault(ex);
            return result;
        }

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

    public readonly struct JobProgress
    {
        public JobProgress(float value, object? payload = null)
        {
            Value = value;
            Payload = payload;
        }

        public float Value { get; }
        public object? Payload { get; }

        public static JobProgress FromRange(float completed, float total, object? payload = null)
        {
            var progress = total <= 0f ? 1f : completed / total;
            return new JobProgress(progress, payload);
        }
    }

    public enum JobStepResult
    {
        Idle,
        Waiting,
        Progressed,
        Completed,
    }

    public sealed class EnumeratorJob : Job
    {
        private readonly Func<IEnumerable> _routineFactory;

        public EnumeratorJob(
            IEnumerable routine,
            Action<float>? onProgress = null,
            Action? onCompleted = null,
            Action<Exception>? onError = null,
            Action? onCanceled = null,
            Action<float, object?>? onProgressWithPayload = null)
        {
            _routineFactory = routine is null ? throw new ArgumentNullException(nameof(routine)) : () => routine;
            HookCallbacks(onProgress, onCompleted, onError, onCanceled, onProgressWithPayload);
        }

        public EnumeratorJob(
            Func<IEnumerable> routineFactory,
            Action<float>? onProgress = null,
            Action? onCompleted = null,
            Action<Exception>? onError = null,
            Action? onCanceled = null,
            Action<float, object?>? onProgressWithPayload = null)
        {
            _routineFactory = routineFactory ?? throw new ArgumentNullException(nameof(routineFactory));
            HookCallbacks(onProgress, onCompleted, onError, onCanceled, onProgressWithPayload);
        }

        public override IEnumerable Process()
            => _routineFactory();

        private void HookCallbacks(
            Action<float>? progress,
            Action? completed,
            Action<Exception>? error,
            Action? canceled,
            Action<float, object?>? progressWithPayload)
        {
            if (progress != null)
                ProgressChanged += (_, value) => progress(value);
            if (progressWithPayload != null)
                ProgressWithPayload += (_, value, payload) => progressWithPayload(value, payload);
            if (completed != null)
                Completed += _ => completed();
            if (canceled != null)
                Canceled += _ => canceled();
            if (error != null)
                Faulted += (_, ex) => error(ex);
        }
    }

    public class JobManager
    {
        private readonly ConcurrentQueue<Job> _pending = new();
        private readonly List<Job> _active = new();
        private readonly object _activeLock = new();

        public IReadOnlyCollection<Job> Active
        {
            get
            {
                lock (_activeLock)
                    return _active.ToArray();
            }
        }

        public void Schedule(Job job)
            => Schedule(job, CancellationToken.None);

        public void Schedule(Job job, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(job);

            if (!job.TryStart())
                throw new InvalidOperationException("Job has already been scheduled or completed.");

            job.LinkCancellationToken(cancellationToken);
            _pending.Enqueue(job);
        }

        public EnumeratorJob Schedule(
            IEnumerable routine,
            Action<float>? progress = null,
            Action? completed = null,
            Action<Exception>? error = null,
            Action? canceled = null,
            Action<float, object?>? progressWithPayload = null,
            CancellationToken cancellationToken = default)
        {
            var job = new EnumeratorJob(routine, progress, completed, error, canceled, progressWithPayload);
            Schedule(job, cancellationToken);
            return job;
        }

        public EnumeratorJob Schedule(
            Func<IEnumerable> routineFactory,
            Action<float>? progress = null,
            Action? completed = null,
            Action<Exception>? error = null,
            Action? canceled = null,
            Action<float, object?>? progressWithPayload = null,
            CancellationToken cancellationToken = default)
        {
            var job = new EnumeratorJob(routineFactory, progress, completed, error, canceled, progressWithPayload);
            Schedule(job, cancellationToken);
            return job;
        }

        public bool Cancel(Job job)
        {
            if (job is null)
                throw new ArgumentNullException(nameof(job));

            job.Cancel();
            return true;
        }

        public bool Cancel(Guid jobId)
        {
            Job? target = null;

            lock (_activeLock)
            {
                foreach (var job in _active)
                {
                    if (job.Id == jobId)
                    {
                        target = job;
                        break;
                    }
                }
            }

            if (target != null)
            {
                target.Cancel();
                return true;
            }

            foreach (var pending in _pending)
            {
                if (pending.Id == jobId)
                {
                    pending.Cancel();
                    return true;
                }
            }

            return false;
        }

        public bool Process()
        {
            var didWork = false;

            while (_pending.TryDequeue(out var pending))
            {
                lock (_activeLock)
                {
                    _active.Add(pending);
                }
                didWork = true;
            }

            Job[] snapshot;
            lock (_activeLock)
            {
                if (_active.Count == 0)
                    return didWork;

                snapshot = _active.ToArray();
            }

            foreach (var job in snapshot)
            {
                var result = job.Step();
                if (result == JobStepResult.Completed)
                {
                    lock (_activeLock)
                    {
                        _active.Remove(job);
                    }
                    didWork = true;
                }
                else if (result == JobStepResult.Progressed)
                {
                    didWork = true;
                }
            }

            return didWork;
        }
    }
    public static partial class Engine
    {
        /// <summary>
        /// Whether the engine is running in editor mode (as opposed to standalone game).
        /// This is set at startup and does not change during runtime.
        /// </summary>
        public static bool IsEditor { get; internal set; } = true;
        
        /// <summary>
        /// Whether the game is currently playing (simulation running).
        /// Delegates to PlayMode.IsPlaying for consistency.
        /// </summary>
        public static bool IsPlaying => PlayMode.IsPlaying;
        
        public static JobManager Jobs { get; } = new JobManager();

        public static GameState LoadOrGenerateGameState(
            Func<GameState>? generateFactory = null,
            string assetName = "state.asset",
            bool allowLoading = true)
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new GameState(), assetName, allowLoading);

        public static GameStartupSettings LoadOrGenerateGameSettings(
            Func<GameStartupSettings>? generateFactory = null,
            string assetName = "startup.asset",
            bool allowLoading = true)
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? GenerateGameSettings(), assetName, allowLoading);

        public static T LoadOrGenerateGameState<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
            Func<T>? generateFactory = null,
            string assetName = "state.asset",
            bool allowLoading = true) where T : GameState, new()
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new T(), assetName, allowLoading);

        public static T LoadOrGenerateGameSettings<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
            Func<T>? generateFactory = null,
            string assetName = "startup.asset",
            bool allowLoading = true) where T : GameStartupSettings, new()
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new T(), assetName, allowLoading);

        public static T LoadOrGenerateAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
            Func<T>? generateFactory,
            string assetName,
            bool allowLoading,
            params string[] folderNames) where T : XRAsset, new()
        {
            T? asset = null;
            if (allowLoading)
            {
                asset = Assets.LoadGameAsset<T>([.. folderNames, assetName]);
                if (asset != null)
                    return asset;
            }
            asset = generateFactory?.Invoke() ?? Activator.CreateInstance<T>();
            asset.Name = assetName;
            /*Task.Run(() => */Assets.SaveGameAssetTo(asset, folderNames)/*)*/;
            return asset;
        }

        private static GameStartupSettings GenerateGameSettings()
        {
            int w = 1920;
            int h = 1080;
            float updateHz = 90.0f;
            float renderHz = 90.0f;
            float fixedHz = 45.0f;

            int primaryX = NativeMethods.GetSystemMetrics(0);
            int primaryY = NativeMethods.GetSystemMetrics(1);

            return new GameStartupSettings()
            {
                StartupWindows =
                [
                    new()
                    {
                        WindowTitle = "FREAK ENGINE",
                        TargetWorld = new Scene.XRWorld(),
                        WindowState = EWindowState.Windowed,
                        X = primaryX / 2 - w / 2,
                        Y = primaryY / 2 - h / 2,
                        Width = w,
                        Height = h,
                    }
                ],
                OutputVerbosity = EOutputVerbosity.Verbose,
                DefaultUserSettings = new UserSettings()
                {
                    TargetFramesPerSecond = renderHz,
                    VSync = EVSyncMode.Off,
                },
                TargetUpdatesPerSecond = updateHz,
                FixedFramesPerSecond = fixedHz,
            };
        }

        public static class State
        {
            /// <summary>
            /// Called when a local player is first created.
            /// </summary>
            public static event Action<LocalPlayerController>? LocalPlayerAdded;
            /// <summary>
            /// Called when a local player is removed.
            /// </summary>
            public static event Action<LocalPlayerController>? LocalPlayerRemoved;

            //Only up to 4 local players, because we only support up to 4 players split screen, realistically. If that.
            public static LocalPlayerController?[] LocalPlayers { get; } = new LocalPlayerController[4];

            public static bool RemoveLocalPlayer(ELocalPlayerIndex index)
            {
                var player = LocalPlayers[(int)index];
                if (player is null)
                    return false;

                LocalPlayers[(int)index] = null;
                LocalPlayerRemoved?.Invoke(player);
                player.Destroy();
                return true;
            }

            /// <summary>
            /// Retrieves or creates a local player controller for the given index.
            /// </summary>
            /// <param name="index">Player slot to fetch.</param>
            /// <param name="controllerTypeOverride">Optional controller type to force for this request.</param>
            /// <returns>The resolved local player controller.</returns>
            public static LocalPlayerController GetOrCreateLocalPlayer(ELocalPlayerIndex index, Type? controllerTypeOverride = null)
            {
                var existing = LocalPlayers[(int)index];
                var desiredType = ResolveLocalPlayerControllerType(controllerTypeOverride);

                if (existing is not null)
                {
                    if (desiredType.IsInstanceOfType(existing))
                        return existing;

                    RemoveLocalPlayer(index);
                }

                return AddLocalPlayer(index, desiredType);
            }

            /// <summary>
            /// This property returns the main player, which is the first player and should always exist.
            /// </summary>
            public static LocalPlayerController MainPlayer => GetOrCreateLocalPlayer(ELocalPlayerIndex.One);

            private static LocalPlayerController AddLocalPlayer(ELocalPlayerIndex index, Type controllerType)
            {
                var player = InstantiateLocalPlayerController(controllerType, index);
                LocalPlayers[(int)index] = player;
                LocalPlayerAdded?.Invoke(player);
                return player;
            }

            private static Type ResolveLocalPlayerControllerType(Type? controllerTypeOverride)
            {
                if (controllerTypeOverride is not null)
                    return controllerTypeOverride;

                if (Engine.PlayMode.ActiveGameMode?.DefaultPlayerControllerClass is Type gameModePreferred)
                    return gameModePreferred;

                return typeof(LocalPlayerController);
            }

            private static LocalPlayerController InstantiateLocalPlayerController(Type controllerType, ELocalPlayerIndex index)
            {
                if (!typeof(LocalPlayerController).IsAssignableFrom(controllerType))
                    throw new ArgumentException($"Controller type {controllerType.FullName} must inherit from LocalPlayerController", nameof(controllerType));

                LocalPlayerController? player;
                var ctorWithIndex = controllerType.GetConstructor(new[] { typeof(ELocalPlayerIndex) });
                if (ctorWithIndex is not null)
                    player = ctorWithIndex.Invoke(new object[] { index }) as LocalPlayerController;
                else
                    player = Activator.CreateInstance(controllerType) as LocalPlayerController;

                if (player is null)
                    throw new InvalidOperationException($"Failed to instantiate controller of type {controllerType.FullName}");

                player.LocalPlayerIndex = index;
                return player;
            }

            /// <summary>
            /// Gets the local player controller for the given index, if it exists.
            /// </summary>
            /// <param name="index"></param>
            /// <returns></returns>
            public static LocalPlayerController? GetLocalPlayer(ELocalPlayerIndex index)
                => LocalPlayers.TryGet((int)index);

            /// <summary>
            /// All remote players that are connected to this server, this p2p client, or the server this client is connected to.
            /// </summary>
            public static List<RemotePlayerController> RemotePlayers { get; } = [];
        }
    }
}

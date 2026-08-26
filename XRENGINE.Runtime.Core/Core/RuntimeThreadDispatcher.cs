using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace XREngine;

/// <summary>
/// Owns runtime update/physics thread identity and their bounded dispatch queues.
/// Render-thread scheduling remains owned by the rendering host.
/// </summary>
public static class RuntimeThreadDispatcher
{
    private static readonly ConcurrentQueue<Action> UpdateWork = new();
    private static readonly ConcurrentQueue<Action> PhysicsWork = new();
    private static int _updateThreadId;
    private static int _physicsThreadId;
    private static int _frameSwapThreadId;

    public static int UpdateThreadId => Volatile.Read(ref _updateThreadId);
    public static int PhysicsThreadId => Volatile.Read(ref _physicsThreadId);
    public static bool IsUpdateThread => Environment.CurrentManagedThreadId == UpdateThreadId;
    public static bool IsPhysicsThread => Environment.CurrentManagedThreadId == PhysicsThreadId;
    public static bool IsFrameSwapThread =>
        Environment.CurrentManagedThreadId == Volatile.Read(ref _frameSwapThreadId);

    public static void AssignUpdateThread(int threadId)
        => Volatile.Write(ref _updateThreadId, threadId);

    public static void AssignPhysicsThread(int threadId)
        => Volatile.Write(ref _physicsThreadId, threadId);

    public static void SetFrameSwapThreadActive(bool active)
        => Volatile.Write(
            ref _frameSwapThreadId,
            active ? Environment.CurrentManagedThreadId : 0);

    public static void EnqueueUpdate(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        UpdateWork.Enqueue(action);
    }

    public static void EnqueuePhysics(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PhysicsWork.Enqueue(action);
    }

    public static int ProcessUpdate(int maxTasks, Action<Exception>? exceptionHandler = null)
        => Process(UpdateWork, maxTasks, exceptionHandler);

    public static int ProcessPhysics(int maxTasks, Action<Exception>? exceptionHandler = null)
        => Process(PhysicsWork, maxTasks, exceptionHandler);

    public static void InvokePhysics(
        Action action,
        bool physicsLoopRunning,
        Action<Exception>? exceptionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsPhysicsThread || !physicsLoopRunning)
        {
            action();
            return;
        }

        ExceptionDispatchInfo? exception = null;
        using ManualResetEventSlim completed = new(false);
        EnqueuePhysics(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exceptionHandler?.Invoke(ex);
                exception = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                completed.Set();
            }
        });

        completed.Wait();
        exception?.Throw();
    }

    private static int Process(
        ConcurrentQueue<Action> queue,
        int maxTasks,
        Action<Exception>? exceptionHandler)
    {
        int processed = 0;
        while (processed < Math.Max(0, maxTasks) && queue.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exceptionHandler?.Invoke(ex);
            }
            ++processed;
        }

        return processed;
    }
}

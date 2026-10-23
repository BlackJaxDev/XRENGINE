using System.Threading;

namespace XREngine.Rendering.UI;

/// <summary>Owns one bounded input/output working page until worker and owner are finished.</summary>
internal sealed class UILayoutPageLease
{
    private const int MaxPages = 8;
    private const long MaxWorkingBytes = 128L * 1024 * 1024;
    private const int WorkerDone = 1;
    private const int OwnerRetired = 2;
    private const int Released = 4;
    private static int _retainedPages;
    private static long _retainedWorkingBytes;
    private int _state = WorkerDone;

    private UILayoutPageLease() { }

    internal static int RetainedPages => Volatile.Read(ref _retainedPages);
    internal static long RetainedWorkingBytes => Volatile.Read(ref _retainedWorkingBytes);
    internal bool IsReleased => (Volatile.Read(ref _state) & Released) != 0;

    internal static UILayoutPageLease? TryAcquire()
    {
        while (true)
        {
            int count = Volatile.Read(ref _retainedPages);
            if (count >= MaxPages)
                return null;
            if (Interlocked.CompareExchange(ref _retainedPages, count + 1, count) != count)
                continue;
            if (TryReserveBytes())
                return new UILayoutPageLease();
            Interlocked.Decrement(ref _retainedPages);
            return null;
        }
    }

    private static bool TryReserveBytes()
    {
        while (true)
        {
            long bytes = Volatile.Read(ref _retainedWorkingBytes);
            if (UILayoutSnapshot.MaxWorkingBytes > MaxWorkingBytes - bytes)
                return false;
            if (Interlocked.CompareExchange(ref _retainedWorkingBytes,
                bytes + UILayoutSnapshot.MaxWorkingBytes, bytes) == bytes)
                return true;
        }
    }

    /// <summary>Transfers a pending page into a worker before Task.Run.</summary>
    internal void StartWorker()
    {
        int state = Volatile.Read(ref _state);
        if (state != WorkerDone || Interlocked.CompareExchange(ref _state, 0, WorkerDone) != WorkerDone)
            throw new InvalidOperationException("The layout page cannot start a second worker.");
    }

    /// <summary>The worker has stopped reading the input page.</summary>
    internal void FinishWorker()
    {
        Interlocked.Or(ref _state, WorkerDone);
        TryRelease();
    }

    /// <summary>The owner has accepted, replaced, or canceled the page.</summary>
    internal void RetireOwner()
    {
        Interlocked.Or(ref _state, OwnerRetired);
        TryRelease();
    }

    private void TryRelease()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if ((state & (WorkerDone | OwnerRetired)) != (WorkerDone | OwnerRetired) ||
                (state & Released) != 0)
                return;
            if (Interlocked.CompareExchange(ref _state, state | Released, state) != state)
                continue;
            Interlocked.Add(ref _retainedWorkingBytes, -UILayoutSnapshot.MaxWorkingBytes);
            Interlocked.Decrement(ref _retainedPages);
            return;
        }
    }
}

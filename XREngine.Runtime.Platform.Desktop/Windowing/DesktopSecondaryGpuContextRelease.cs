namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Routes secondary window release to its captured native owner.</summary>
internal sealed class DesktopSecondaryGpuContextRelease(
    int nativeOwnerThreadId,
    int renderOwnerThreadId,
    Action<Action> enqueueRenderTask,
    Action<Action> enqueueWindowTask,
    Func<bool> tryDispose)
{
    public void Release(Action<bool> completed)
    {
        void DisposeOnOwner()
        {
            // A stopped or replaced mailbox must not authorize native work on another thread.
            if (Environment.CurrentManagedThreadId != nativeOwnerThreadId)
            {
                completed(false);
                return;
            }
            completed(tryDispose());
        }

        if (Environment.CurrentManagedThreadId == nativeOwnerThreadId)
            DisposeOnOwner();
        else if (nativeOwnerThreadId == renderOwnerThreadId)
            enqueueRenderTask(DisposeOnOwner);
        else
            enqueueWindowTask(DisposeOnOwner);
    }
}

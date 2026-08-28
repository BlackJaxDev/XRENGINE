namespace XREngine;

internal sealed class EngineRuntimeThreadServices : IRuntimeThreadServices
{
    public bool InvokeOnAppThread(Action action, string? reason = null, bool executeNowIfAlreadyAppThread = false)
        => Engine.InvokeOnAppThread(action, reason ?? "runtime app-thread dispatch", executeNowIfAlreadyAppThread);

    public void EnqueueUpdateThread(Action action)
        => Engine.EnqueueUpdateThreadTask(action);

    public void EnqueuePhysicsThread(Action action)
        => Engine.EnqueuePhysicsThreadTask(action);
}

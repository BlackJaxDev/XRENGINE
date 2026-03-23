using System;
using System.Collections.Generic;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapStartupWork
{
    private static readonly object s_deferredWorkLock = new();
    private static List<Action>? s_deferredWorkActions;

    public static bool ShouldDeferNonEssentialStartupWork
        => RuntimeBootstrapState.DeferNonEssentialStartupWorkUntilStartupCompletes;

    public static bool TryQueueDeferredWork(Action action, string debugMessage)
    {
        if (!ShouldDeferNonEssentialStartupWork)
            return false;

        lock (s_deferredWorkLock)
        {
            s_deferredWorkActions ??= [];
            s_deferredWorkActions.Add(action);
        }

        Debug.Out(debugMessage);
        return true;
    }

    public static void FlushDeferredWork()
    {
        List<Action>? deferredActions;
        lock (s_deferredWorkLock)
        {
            deferredActions = s_deferredWorkActions;
            s_deferredWorkActions = null;
            RuntimeBootstrapState.DeferNonEssentialStartupWorkUntilStartupCompletes = false;
        }

        if (deferredActions is null || deferredActions.Count == 0)
            return;

        foreach (Action deferredAction in deferredActions)
        {
            try
            {
                deferredAction();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "[BootstrapStartupWork] Deferred startup work failed.");
            }
        }
    }

    public static void ResetDeferredWork()
    {
        lock (s_deferredWorkLock)
        {
            s_deferredWorkActions = null;
            RuntimeBootstrapState.DeferNonEssentialStartupWorkUntilStartupCompletes = false;
        }
    }
}
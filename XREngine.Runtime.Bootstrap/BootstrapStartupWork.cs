using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapStartupWork
{
    private readonly record struct DeferredStartupWorkItem(Action Action, string DebugMessage);

    private static readonly object s_deferredWorkLock = new();
    private static List<DeferredStartupWorkItem>? s_deferredWorkActions;

    public static bool ShouldDeferNonEssentialStartupWork
        => RuntimeBootstrapState.DeferNonEssentialStartupWorkUntilStartupCompletes;

    public static int PendingDeferredWorkCount
    {
        get
        {
            lock (s_deferredWorkLock)
                return s_deferredWorkActions?.Count ?? 0;
        }
    }

    public static bool TryQueueDeferredWork(Action action, string debugMessage)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!ShouldDeferNonEssentialStartupWork)
            return false;

        lock (s_deferredWorkLock)
        {
            s_deferredWorkActions ??= [];
            s_deferredWorkActions.Add(new DeferredStartupWorkItem(action, NormalizeDebugMessage(debugMessage)));
        }

        Debug.Out(debugMessage);
        return true;
    }

    public static void FlushDeferredWork()
    {
        List<DeferredStartupWorkItem>? deferredActions;
        lock (s_deferredWorkLock)
        {
            deferredActions = s_deferredWorkActions;
            s_deferredWorkActions = null;
            RuntimeBootstrapState.DeferNonEssentialStartupWorkUntilStartupCompletes = false;
        }

        if (deferredActions is null || deferredActions.Count == 0)
            return;

        Debug.Out($"[BootstrapStartupWork] Flushing {deferredActions.Count} deferred startup work item(s).");

        for (int i = 0; i < deferredActions.Count; i++)
        {
            DeferredStartupWorkItem deferredAction = deferredActions[i];
            Stopwatch stopwatch = Stopwatch.StartNew();
            Debug.Out($"[BootstrapStartupWork] Starting {i + 1}/{deferredActions.Count}: {deferredAction.DebugMessage}");

            try
            {
                deferredAction.Action();
                stopwatch.Stop();
                Debug.Out($"[BootstrapStartupWork] Completed {i + 1}/{deferredActions.Count} in {stopwatch.Elapsed.TotalMilliseconds:F0} ms: {deferredAction.DebugMessage}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Debug.LogException(ex, $"[BootstrapStartupWork] Deferred startup work failed after {stopwatch.Elapsed.TotalMilliseconds:F0} ms: {deferredAction.DebugMessage}");
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

    private static string NormalizeDebugMessage(string debugMessage)
        => string.IsNullOrWhiteSpace(debugMessage)
            ? "<unnamed deferred startup work>"
            : debugMessage.Trim();
}

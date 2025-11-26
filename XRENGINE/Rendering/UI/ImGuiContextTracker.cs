using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.UI;

internal static class ImGuiContextTracker
{
    private static readonly HashSet<IntPtr> ActiveContexts = [];
    private static readonly object ContextLock = new();

    public static void Register(IntPtr context)
    {
        if (context == IntPtr.Zero)
            return;

        lock (ContextLock)
            ActiveContexts.Add(context);
    }

    public static void Unregister(IntPtr context)
    {
        if (context == IntPtr.Zero)
            return;

        lock (ContextLock)
            ActiveContexts.Remove(context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlive(IntPtr context)
    {
        if (context == IntPtr.Zero)
            return false;

        lock (ContextLock)
            return ActiveContexts.Contains(context);
    }
}

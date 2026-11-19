using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares display selection without opening or reading a display.</summary>
public class Display
{
    private Display()
    {
    }

    /// <summary>Rejects editor display lookup until the worker has display state.</summary>
    public static int activeEditorGameViewTarget
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Display.get_activeEditorGameViewTarget()");
}

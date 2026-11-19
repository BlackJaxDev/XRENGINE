using XREngine.UnityScriptHost.Execution;

namespace UnityEditor;

/// <summary>Declares editor lifecycle APIs. A host scheduler must supply their behavior.</summary>
public static class EditorApplication
{
    public delegate void CallbackFunction();

    public static bool isPlaying
    {
        get => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorApplication.get_isPlaying()");
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorApplication.set_isPlaying(System.Boolean)");
    }

    public static event CallbackFunction delayCall
    {
        add => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorApplication.add_delayCall(UnityEditor.EditorApplication.CallbackFunction)");
        remove => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorApplication.remove_delayCall(UnityEditor.EditorApplication.CallbackFunction)");
    }
}

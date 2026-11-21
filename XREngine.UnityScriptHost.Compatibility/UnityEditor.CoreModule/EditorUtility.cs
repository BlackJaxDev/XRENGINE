using XREngine.UnityScriptHost.Execution;
using Object = UnityEngine.Object;

namespace UnityEditor;

/// <summary>Declares editor operations that require an asset workspace or presentation service.</summary>
public static class EditorUtility
{
    public static void SetDirty(Object target) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorUtility.SetDirty(UnityEngine.Object)");
    public static bool IsPersistent(Object target) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorUtility.IsPersistent(UnityEngine.Object)");
    public static void CopySerialized(Object source, Object destination) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorUtility.CopySerialized(UnityEngine.Object,UnityEngine.Object)");
    public static void DisplayProgressBar(string title, string info, float progress) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorUtility.DisplayProgressBar(System.String,System.String,System.Single)");
    public static bool DisplayCancelableProgressBar(string title, string info, float progress) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorUtility.DisplayCancelableProgressBar(System.String,System.String,System.Single)");
    public static void ClearProgressBar() => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.EditorUtility.ClearProgressBar()");
}

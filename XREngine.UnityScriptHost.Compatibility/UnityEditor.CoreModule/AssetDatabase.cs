using XREngine.UnityScriptHost.Execution;
using Object = UnityEngine.Object;

namespace UnityEditor;

/// <summary>Declares asset APIs. Reads and writes fail until an asset workspace implements them.</summary>
public static class AssetDatabase
{
    public static string GetAssetPath(Object assetObject) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.GetAssetPath(UnityEngine.Object)");
    public static string GUIDToAssetPath(string guid) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.GUIDToAssetPath(System.String)");
    public static string AssetPathToGUID(string path) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.AssetPathToGUID(System.String)");
    public static T LoadAssetAtPath<T>(string assetPath) where T : Object => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.LoadAssetAtPath<T>(System.String)");
    public static Object LoadAssetAtPath(string assetPath, Type type) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.LoadAssetAtPath(System.String,System.Type)");
    public static Object[] LoadAllAssetsAtPath(string assetPath) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.LoadAllAssetsAtPath(System.String)");
    public static string[] FindAssets(string filter) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.FindAssets(System.String)");
    public static string[] FindAssets(string filter, string[] searchInFolders) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.FindAssets(System.String,System.String[])");
    public static void CreateAsset(Object asset, string path) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.CreateAsset(UnityEngine.Object,System.String)");
    public static void AddObjectToAsset(Object objectToAdd, Object assetObject) => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.AddObjectToAsset(UnityEngine.Object,UnityEngine.Object)");
    public static void SaveAssets() => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.SaveAssets()");
    public static void Refresh() => throw UnityCompatibilityRuntime.Unsupported("UnityEditor.AssetDatabase.Refresh()");
}

using System.Reflection;
using System.Runtime.ExceptionServices;

namespace XREngine.Scene;

/// <summary>
/// Invokes Unity asset import support owned by the editor without introducing an
/// engine-to-editor project reference.
/// </summary>
internal sealed class UnityEditorImportBridge : IRuntimeSceneImportServices
{
    public UnityEditorImportBridge()
    {
    }

    private const string ImporterTypeName =
        "XREngine.Scene.Importers.UnitySceneImporter, XREngine.Editor";

    private static readonly Lazy<ImporterMethods> Methods = new(ResolveMethods);

    public static SceneNode[] ImportScene(string filePath)
        => Invoke<SceneNode[]>(Methods.Value.ImportScene, filePath);

    IReadOnlyList<SceneNode> IRuntimeSceneImportServices.ImportScene(string filePath)
        => ImportScene(filePath);

    public static SceneNode ImportPrefab(string filePath)
        => Invoke<SceneNode>(Methods.Value.ImportPrefab, filePath);

    private static ImporterMethods ResolveMethods()
    {
        Type importerType = Type.GetType(ImporterTypeName, throwOnError: false)
            ?? throw new NotSupportedException(
                "Unity scene and prefab import requires XREngine.Editor. " +
                "Install and load the editor assembly before importing Unity-authored assets.");

        return new ImporterMethods(
            ResolveMethod(importerType, "Import"),
            ResolveMethod(importerType, "ImportPrefab"));
    }

    private static MethodInfo ResolveMethod(Type importerType, string methodName)
        => importerType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(string)],
                modifiers: null)
            ?? throw new MissingMethodException(importerType.FullName, methodName);

    private static TResult Invoke<TResult>(MethodInfo method, string filePath)
    {
        try
        {
            object? value = method.Invoke(null, [filePath]);
            return value is TResult result
                ? result
                : throw new InvalidOperationException(
                    $"Unity editor importer '{method.Name}' returned an unexpected result.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private sealed record ImporterMethods(MethodInfo ImportScene, MethodInfo ImportPrefab);
}

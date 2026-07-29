using System.Reflection;
using System.Runtime.ExceptionServices;
using XREngine.Scene.Prefabs;

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
        => ImportPrefabConversion(filePath).RootNode
            ?? throw new InvalidDataException($"Unity prefab importer returned no root for '{filePath}'.");

    public static UnityPrefabConversionResult ImportPrefabConversion(string filePath)
        => Invoke<UnityPrefabConversionResult>(Methods.Value.ImportPrefabConversion, filePath);

    public static UnityPrefabConversionResult ImportPrefabConversion(
        string filePath,
        string? outputDestination,
        string? explicitProjectOrAssetsRoot)
        => Invoke<UnityPrefabConversionResult>(
            Methods.Value.ImportPrefabConversionWithOptions,
            filePath,
            outputDestination,
            explicitProjectOrAssetsRoot);

    private static ImporterMethods ResolveMethods()
    {
        Type importerType = Type.GetType(ImporterTypeName, throwOnError: false)
            ?? throw new NotSupportedException(
                "Unity scene and prefab import requires XREngine.Editor. " +
                "Install and load the editor assembly before importing Unity-authored assets.");

        return new ImporterMethods(
            ResolveMethod(importerType, "Import"),
            ResolveMethod(importerType, "ImportPrefab"),
            ResolveMethod(importerType, "ImportPrefabWithManifest"),
            ResolveMethod(
                importerType,
                "ImportPrefabWithManifest",
                typeof(string),
                typeof(string),
                typeof(string)));
    }

    private static MethodInfo ResolveMethod(Type importerType, string methodName)
        => ResolveMethod(importerType, methodName, typeof(string));

    private static MethodInfo ResolveMethod(Type importerType, string methodName, params Type[] parameterTypes)
        => importerType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: parameterTypes,
                modifiers: null)
            ?? throw new MissingMethodException(importerType.FullName, methodName);

    private static TResult Invoke<TResult>(MethodInfo method, string filePath)
        => Invoke<TResult>(method, [filePath]);

    private static TResult Invoke<TResult>(
        MethodInfo method,
        string filePath,
        string? outputDestination,
        string? explicitProjectOrAssetsRoot)
        => Invoke<TResult>(method, [filePath, outputDestination, explicitProjectOrAssetsRoot]);

    private static TResult Invoke<TResult>(MethodInfo method, object?[] arguments)
    {
        try
        {
            object? value = method.Invoke(null, arguments);
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

    private sealed record ImporterMethods(
        MethodInfo ImportScene,
        MethodInfo ImportPrefab,
        MethodInfo ImportPrefabConversion,
        MethodInfo ImportPrefabConversionWithOptions);
}

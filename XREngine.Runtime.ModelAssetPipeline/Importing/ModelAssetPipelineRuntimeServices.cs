using System.Collections;
using XREngine.Execution;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Rendering.Models;

/// <summary>Connects the model importer to renderer-neutral engine scheduling and asset roots.</summary>
internal sealed class ModelAssetPipelineRuntimeModelImportServices(
    AssetManager assets,
    IRuntimeModelImportServices fallback) : IRuntimeModelImportServices
{
    public int WorkerCount => RuntimeWorkScheduler.Jobs.WorkerCount;
    public bool ProcessMeshesAsynchronously => fallback.ProcessMeshesAsynchronously;
    public FbxImportBackend PreferredFbxBackend => fallback.PreferredFbxBackend;
    public GltfImportBackend PreferredGltfBackend => fallback.PreferredGltfBackend;
    public string? ProjectAssetsRoot => assets.GameAssetsPath;
    public string? EngineAssetsRoot => assets.EngineAssetsPath;

    public EnumeratorJob Schedule(
        Func<IEnumerable> routineFactory,
        Action<float>? progress = null,
        Action? completed = null,
        Action<Exception>? error = null,
        Action? canceled = null,
        Action<float, object?>? progressWithPayload = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Normal)
        => RuntimeWorkScheduler.Jobs.Schedule(
            routineFactory,
            progress,
            completed,
            error,
            canceled,
            progressWithPayload,
            cancellationToken,
            priority);

    public void EnqueueAppThread(Action action, string reason)
    {
        if (!RuntimeThreadServices.Current.InvokeOnAppThread(action, reason))
            action();
    }

    public IDisposable? StartProfileScope(string scopeName)
        => RuntimeRenderingHostServices.Profiling.StartProfileScope(scopeName);
}

/// <summary>ModelAssetPipeline implementation of the renderer-owned hierarchy loading contract.</summary>
internal sealed class ModelAssetPipelineRuntimeModelSceneLoadingServices : IRuntimeModelSceneLoadingServices
{
    public async Task<SceneNode?> LoadAsync(
        string sourcePath,
        SceneNode parent,
        CancellationToken cancellationToken = default)
    {
        (SceneNode? rootNode, _, _) = await ModelAssetImporter.ImportAsync(
            sourcePath,
            ModelImportSteps.Triangulate |
            ModelImportSteps.GenerateSmoothNormals |
            ModelImportSteps.CalculateTangentSpace |
            ModelImportSteps.JoinIdenticalVertices |
            ModelImportSteps.ImproveCacheLocality,
            onCompleted: null,
            materialFactory: null,
            parent,
            scaleConversion: 1.0f,
            zUp: false,
            batchSubmeshAddsDuringAsyncImport: false,
            cancellationToken: cancellationToken);
        return rootNode;
    }
}

using System.Collections;
using XREngine.Rendering.Models;

namespace XREngine;

internal sealed class EngineRuntimeModelImportServices : IRuntimeModelImportServices
{
    public int WorkerCount => Engine.Jobs.WorkerCount;
    public bool ProcessMeshesAsynchronously => RuntimeEngine.Rendering.Settings.ProcessMeshImportsAsynchronously;
    public FbxImportBackend PreferredFbxBackend => Engine.EditorPreferences?.FbxImporterBackend ?? FbxImportBackend.Auto;
    public GltfImportBackend PreferredGltfBackend => Engine.EditorPreferences?.GltfImporterBackend ?? GltfImportBackend.Auto;
    public string? ProjectAssetsRoot => Engine.Assets.GameAssetsPath;
    public string? EngineAssetsRoot => Engine.Assets.EngineAssetsPath;

    public EnumeratorJob Schedule(
        Func<IEnumerable> routineFactory,
        Action<float>? progress = null,
        Action? completed = null,
        Action<Exception>? error = null,
        Action? canceled = null,
        Action<float, object?>? progressWithPayload = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Normal)
        => Engine.Jobs.Schedule(
            routineFactory,
            progress,
            completed,
            error,
            canceled,
            progressWithPayload,
            cancellationToken,
            priority);

    public void EnqueueAppThread(Action action, string reason)
        => Engine.EnqueueAppThreadTask(action, reason);

    public IDisposable? StartProfileScope(string scopeName)
        => Engine.Profiler.Start(scopeName);
}

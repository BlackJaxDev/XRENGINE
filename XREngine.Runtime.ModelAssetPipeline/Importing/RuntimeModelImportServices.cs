using System.Collections;
using XREngine.Rendering.Models;

namespace XREngine;

/// <summary>
/// Host policy consumed by model importers without coupling the modeling bridge to the
/// legacy engine facade or editor preferences.
/// </summary>
public interface IRuntimeModelImportServices
{
    int WorkerCount { get; }
    bool ProcessMeshesAsynchronously { get; }
    FbxImportBackend PreferredFbxBackend { get; }
    GltfImportBackend PreferredGltfBackend { get; }
    string? ProjectAssetsRoot { get; }
    string? EngineAssetsRoot { get; }

    EnumeratorJob Schedule(
        Func<IEnumerable> routineFactory,
        Action<float>? progress = null,
        Action? completed = null,
        Action<Exception>? error = null,
        Action? canceled = null,
        Action<float, object?>? progressWithPayload = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Normal);

    void EnqueueAppThread(Action action, string reason);
    IDisposable? StartProfileScope(string scopeName);
}

/// <summary>Process-wide model-import host boundary configured by application composition.</summary>
public static class RuntimeModelImportServices
{
    private static IRuntimeModelImportServices _current = new DefaultRuntimeModelImportServices();

    public static IRuntimeModelImportServices Current => Volatile.Read(ref _current);

    public static IDisposable Install(IRuntimeModelImportServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IRuntimeModelImportServices previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    private sealed class InstallationLease(
        IRuntimeModelImportServices installed,
        IRuntimeModelImportServices previous) : IDisposable
    {
        private IRuntimeModelImportServices? _installed = installed;

        public void Dispose()
        {
            IRuntimeModelImportServices? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }

    private sealed class DefaultRuntimeModelImportServices : IRuntimeModelImportServices
    {
        private readonly JobManager _jobs = new();

        public int WorkerCount => _jobs.WorkerCount;
        public bool ProcessMeshesAsynchronously => true;
        public FbxImportBackend PreferredFbxBackend => FbxImportBackend.Auto;
        public GltfImportBackend PreferredGltfBackend => GltfImportBackend.Auto;
        public string? ProjectAssetsRoot => null;
        public string? EngineAssetsRoot => null;

        public EnumeratorJob Schedule(
            Func<IEnumerable> routineFactory,
            Action<float>? progress = null,
            Action? completed = null,
            Action<Exception>? error = null,
            Action? canceled = null,
            Action<float, object?>? progressWithPayload = null,
            CancellationToken cancellationToken = default,
            JobPriority priority = JobPriority.Normal)
            => _jobs.Schedule(routineFactory, progress, completed, error, canceled, progressWithPayload, cancellationToken, priority);

        public void EnqueueAppThread(Action action, string reason)
        {
            if (!RuntimeThreadServices.Current.InvokeOnAppThread(action, reason))
                action();
        }

        public IDisposable? StartProfileScope(string scopeName) => null;
    }
}

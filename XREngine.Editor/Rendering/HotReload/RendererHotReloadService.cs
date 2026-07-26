using XREngine.Rendering;

namespace XREngine.Editor.HotReload;

/// <summary>
/// Editor-facing build/load/replacement orchestrator. Build and staging finish before the
/// active renderer is disturbed.
/// </summary>
public sealed class RendererHotReloadService : IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string _repositoryRoot;
    private readonly RendererBackendBuildService _buildService;
    private readonly RendererBackendModuleLoader _moduleLoader = new();
    private LoadedRendererBackendGeneration? _activeGeneration;
    private string? _lastCandidateManifestPath;
    private string? _lastGoodManifestPath;
    private bool _unloadLeakBlocksFurtherReload;
    private RendererBackendBuildResult? _lastBuild;
    private RendererBackendSourceWatcher? _sourceWatcher;
    private RendererBackendId _watchedBackend;
    private int _watchedDebounceMilliseconds;
    private Task _pendingUnloadVerification = Task.CompletedTask;

    public static RendererHotReloadService Current { get; } = new(Environment.CurrentDirectory);

    public RendererHotReloadService(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _buildService = new(_repositoryRoot);
    }

    public RendererBackendBuildResult? LastBuild => _lastBuild;
    public string? LastCandidateManifestPath => _lastCandidateManifestPath;
    public string? LastGoodManifestPath => _lastGoodManifestPath;
    public bool UnloadLeakBlocksFurtherReload => _unloadLeakBlocksFurtherReload;
    public string? ActiveManifestPath => _activeGeneration?.ManifestPath;
    public string? ActiveLoadContextName => _activeGeneration?.LoadContextName;
    public string? ActiveBuildHash => _activeGeneration?.Manifest.BuildHash;
    public bool AutomaticReloadEnabled => _sourceWatcher is not null;

    public RendererReloadSnapshot Snapshot => RendererReplacementCoordinator.Current.Snapshot;

    public int ReloadShaders()
        => ShaderHotReload.ReloadAll();

    public void ConfigureAutomaticReload(
        bool enabled,
        RendererBackendId backendId,
        int debounceMilliseconds)
    {
        int boundedDebounce = Math.Clamp(debounceMilliseconds, 100, 10000);
        if (enabled &&
            _sourceWatcher is not null &&
            _watchedBackend == backendId &&
            _watchedDebounceMilliseconds == boundedDebounce)
        {
            return;
        }

        _sourceWatcher?.Dispose();
        _sourceWatcher = null;
        _watchedBackend = backendId;
        _watchedDebounceMilliseconds = boundedDebounce;
        if (!enabled)
            return;

        string projectDirectory = Path.Combine(
            _repositoryRoot,
            backendId == RendererBackendId.OpenGL
                ? "XREngine.Runtime.Rendering.OpenGL"
                : backendId == RendererBackendId.Vulkan
                    ? "XREngine.Runtime.Rendering.Vulkan"
                    : throw new ArgumentOutOfRangeException(nameof(backendId)));
        _sourceWatcher = new(
            projectDirectory,
            backendId,
            boundedDebounce,
            changedBackend => BuildAndReloadAsync(changedBackend));
    }

    public async Task<RendererReplacementResult> BuildAndReloadAsync(
        RendererBackendId backendId,
        string configuration = "Debug",
        TimeSpan? firstFrameTimeout = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pendingUnloadVerification.ConfigureAwait(false);
            if (_unloadLeakBlocksFurtherReload)
            {
                string error =
                    "A previous collectible renderer context remains alive. Further backend activation is blocked until retaining callbacks, tasks, types, or native registrations are released.";
                RendererReplacementCoordinator.Current.ReportExternalFailure(
                    backendId,
                    Snapshot.Generation,
                    RendererReloadFailureKind.Unload,
                    error);
                return new(
                    false,
                    RuntimeRenderingHostServices.Factories.RendererBackends.GetRequired(backendId),
                    RendererReloadFailureKind.Unload,
                    error);
            }

            long pendingGeneration = Math.Max(1, Snapshot.Generation + 1);
            RendererReplacementCoordinator.Current.ReportBuildPending(
                backendId,
                pendingGeneration,
                $"Building {backendId} renderer backend.");
            RendererBackendBuildResult build = await _buildService.BuildAsync(
                backendId,
                configuration,
                cancellationToken).ConfigureAwait(false);
            _lastBuild = build;
            if (!build.Succeeded || build.ManifestPath is null)
            {
                RendererReloadFailureKind failureKind = build.Cancelled
                    ? RendererReloadFailureKind.Cancelled
                    : RendererReloadFailureKind.Build;
                string error = BuildFailureDescription(build);
                RendererReplacementCoordinator.Current.ReportExternalFailure(
                    backendId,
                    build.Generation,
                    failureKind,
                    error);
                return new(
                    false,
                    RuntimeRenderingHostServices.Factories.RendererBackends.GetRequired(backendId),
                    failureKind,
                    error);
            }

            _lastCandidateManifestPath = build.ManifestPath;
            return await ActivateManifestCoreAsync(
                build.ManifestPath,
                firstFrameTimeout ?? TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RendererReplacementResult> RestartCurrentGenerationAsync(
        RendererBackendId backendId,
        TimeSpan? firstFrameTimeout = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RendererReplacementCoordinator.Current.RestartCurrentGenerationAsync(
                backendId,
                firstFrameTimeout ?? TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RendererReplacementResult> RestartCurrentGenerationWithOpenXrSessionAsync(
        RendererBackendId backendId,
        TimeSpan? firstFrameTimeout = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RendererReplacementCoordinator.Current
                .ReplaceWithOpenXrSessionRestartAsync(
                    backendId,
                    candidate: null,
                    firstFrameTimeout ?? TimeSpan.FromSeconds(15),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<RendererReplacementResult> RetryCandidateAsync(
        TimeSpan? firstFrameTimeout = null,
        CancellationToken cancellationToken = default)
        => ActivateManifestAsync(_lastCandidateManifestPath, firstFrameTimeout, cancellationToken);

    public Task<RendererReplacementResult> RollBackAsync(
        TimeSpan? firstFrameTimeout = null,
        CancellationToken cancellationToken = default)
        => ActivateManifestAsync(_lastGoodManifestPath, firstFrameTimeout, cancellationToken);

    public void CancelPendingBuild()
        => _buildService.CancelPendingBuild();

    public void Dispose()
    {
        _sourceWatcher?.Dispose();
        _buildService.Dispose();
        _operationGate.Dispose();
    }

    private async Task<RendererReplacementResult> ActivateManifestAsync(
        string? manifestPath,
        TimeSpan? firstFrameTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new InvalidOperationException("No staged renderer generation is available for this action.");

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ActivateManifestCoreAsync(
                manifestPath,
                firstFrameTimeout ?? TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<RendererReplacementResult> ActivateManifestCoreAsync(
        string manifestPath,
        TimeSpan firstFrameTimeout,
        CancellationToken cancellationToken)
    {
        LoadedRendererBackendGeneration candidate;
        try
        {
            candidate = _moduleLoader.Load(manifestPath);
        }
        catch (Exception ex)
        {
            RendererBackendGenerationManifest manifest = ReadManifestForFailure(manifestPath);
            RendererReplacementCoordinator.Current.ReportExternalFailure(
                manifest.GetBackendId(),
                manifest.Generation,
                RendererReloadFailureKind.ModuleValidation,
                ex.ToString());
            return new(
                false,
                RuntimeRenderingHostServices.Factories.RendererBackends.GetRequired(manifest.GetBackendId()),
                RendererReloadFailureKind.ModuleValidation,
                ex.ToString());
        }

        RendererReplacementResult result = await RendererReplacementCoordinator.Current.ReplaceAsync(
            candidate.Registration.Metadata.Id,
            candidate.Registration,
            firstFrameTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            WeakReference rejectedContext = candidate.BeginUnload();
            RendererBackendModuleLoader.VerifyUnloaded(rejectedContext);
            return result;
        }

        LoadedRendererBackendGeneration? previous = Interlocked.Exchange(
            ref _activeGeneration,
            candidate);
        if (previous is not null)
        {
            _lastGoodManifestPath = previous.ManifestPath;
            RendererBackendId previousBackendId = previous.Manifest.GetBackendId();
            long previousGeneration = previous.Manifest.Generation;
            WeakReference previousContext = previous.BeginUnload();
            previous = null;
            // Verification cannot run synchronously in this continuation: the completed
            // coordinator/MCP async state machines can legally retain their local
            // registration until the request stack unwinds. The next activation awaits
            // this bounded verifier, so a genuine leak still blocks further generations.
            _pendingUnloadVerification = VerifyPreviousGenerationAfterRequestUnwindsAsync(
                previousContext,
                previousBackendId,
                previousGeneration);
        }

        return result;
    }

    private async Task VerifyPreviousGenerationAfterRequestUnwindsAsync(
        WeakReference contextReference,
        RendererBackendId backendId,
        long generation)
    {
        const int unloadVerificationAttempts = 200;
        for (int attempt = 0; attempt < unloadVerificationAttempts; attempt++)
        {
            await Task.Delay(50).ConfigureAwait(false);
            if (!RendererReloadFailureInjection.IsEnabled(
                    RendererReloadInjectedFailure.UnloadLeak) &&
                RendererBackendModuleLoader.VerifyUnloaded(contextReference, 1))
            {
                return;
            }
        }

        _unloadLeakBlocksFurtherReload = true;
        RendererReplacementCoordinator.Current.ReportUnloadLeak(
            backendId,
            generation,
            $"Collectible load context for renderer generation {generation} is still alive after a 10-second cooperative unload window and diagnostic GC cycles.");
    }

    private static string BuildFailureDescription(RendererBackendBuildResult build)
    {
        RendererBackendBuildDiagnostic? firstError = build.Diagnostics.FirstOrDefault(
            diagnostic => diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        if (firstError is not null)
        {
            string location = firstError.File is null
                ? string.Empty
                : $" {firstError.File}({firstError.Line},{firstError.Column})";
            return $"{firstError.Code}:{location} {firstError.Message}";
        }

        return build.Cancelled
            ? "The superseded renderer build was cancelled before teardown."
            : "The renderer backend build failed. See the renderer-development diagnostics for MSBuild output.";
    }

    private static RendererBackendGenerationManifest ReadManifestForFailure(string manifestPath)
        => System.Text.Json.JsonSerializer.Deserialize<RendererBackendGenerationManifest>(
            File.ReadAllText(manifestPath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Renderer manifest '{manifestPath}' is invalid.");
}

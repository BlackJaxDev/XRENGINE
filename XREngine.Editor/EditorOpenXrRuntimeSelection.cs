using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using XREngine.Runtime.Bootstrap;

namespace XREngine.Editor;

/// <summary>Resolves and starts the selected OpenXR runtime without creating OpenXR objects.</summary>
internal static class EditorOpenXrRuntimeSelection
{
    private const string ActiveRuntimeRegistryPath = @"SOFTWARE\Khronos\OpenXR\1";
    private const string SteamVrManifestName = "steamxr_win64.json";
    private const string SteamVrMonitorRelativePath = "bin/win64/vrmonitor.exe";
    private static readonly TimeSpan ServiceStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ServicePollInterval = TimeSpan.FromMilliseconds(300);

    public static async Task<EditorOpenXrRuntimePreparation> PrepareAsync(
        EditorOpenXrRuntimeChoice choice,
        UnitTestingWorldSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return choice switch
        {
            EditorOpenXrRuntimeChoice.Monado => await PrepareMonadoAsync(settings, cancellationToken).ConfigureAwait(false),
            EditorOpenXrRuntimeChoice.SteamVR => await PrepareSteamVrAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unsupported editor OpenXR runtime choice."),
        };
    }

    private static async Task<EditorOpenXrRuntimePreparation> PrepareMonadoAsync(
        UnitTestingWorldSettings settings,
        CancellationToken cancellationToken)
    {
        string manifestPath = await Task.Run(
            () => ResolveMonadoManifest(settings),
            cancellationToken).ConfigureAwait(false);

        Func<string, bool> ensureService = reason =>
            UnitTestingWorldSettingsStore.TryEnsureMonadoServiceForRuntimeSelection(settings, manifestPath, reason);
        bool serviceReady = await Task.Run(() => ensureService("editor OpenXR runtime selection"), cancellationToken)
            .ConfigureAwait(false);
        if (!serviceReady)
            throw new InvalidOperationException($"Could not start the selected Monado runtime service for manifest '{manifestPath}'.");

        return new EditorOpenXrRuntimePreparation(
            manifestPath,
            ensureService,
            RecommendedDimensionsRequireServiceRestart: true);
    }

    private static async Task<EditorOpenXrRuntimePreparation> PrepareSteamVrAsync(CancellationToken cancellationToken)
    {
        string manifestPath = await Task.Run(ResolveSteamVrManifest, cancellationToken).ConfigureAwait(false);
        string? monitorPath = FindSteamVrMonitorPath(manifestPath);
        if (monitorPath is null)
            throw new FileNotFoundException($"SteamVR manifest was found, but no matching vrmonitor.exe was found in its runtime roots.", manifestPath);

        bool serviceReady = await Task.Run(
            () => EnsureSteamVrRunning(monitorPath, "editor OpenXR runtime selection", cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (!serviceReady)
            throw new InvalidOperationException($"SteamVR did not start from '{monitorPath}'.");

        return new EditorOpenXrRuntimePreparation(
            manifestPath,
            RuntimeServiceEnsurer: null,
            RecommendedDimensionsRequireServiceRestart: false);
    }

    private static string ResolveMonadoManifest(UnitTestingWorldSettings settings)
    {
        string? configured = settings.VR.OpenXrRuntimeJson;
        if (!string.IsNullOrWhiteSpace(configured)
            && UnitTestingWorldSettingsStore.TryResolveOpenXrRuntimeManifest(
                configured,
                out string resolvedConfigured,
                out string? configuredName,
                out _,
                out _)
            && LooksLikeMonado(resolvedConfigured, configuredName))
        {
            return resolvedConfigured;
        }

        if (UnitTestingWorldSettingsStore.TryResolveMonadoOpenXrRuntimeManifest(
            out string detected,
            out _,
            out string? error))
        {
            return detected;
        }

        throw new InvalidOperationException(
            $"Could not resolve a valid Monado OpenXR runtime manifest. {error ?? "Configure VR.OpenXrRuntimeJson or install Monado."}");
    }

    private static string ResolveSteamVrManifest()
    {
        IEnumerable<string> candidates = EnumerateActiveRuntimeManifests()
            .Concat(EnumerateSteamVrManifestsFromOpenVrPaths())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string candidate in candidates)
        {
            if (!UnitTestingWorldSettingsStore.TryResolveOpenXrRuntimeManifest(
                    candidate,
                    out string resolved,
                    out string? runtimeName,
                    out string? libraryPath,
                    out _))
                continue;

            if (LooksLikeSteamVr(resolved, runtimeName, libraryPath))
                return resolved;
        }

        throw new InvalidOperationException(
            "Could not resolve a valid SteamVR OpenXR manifest. Checked the registered ActiveRuntime and SteamVR runtime roots in LocalAppData/openvr/openvrpaths.vrpath.");
    }

    private static IEnumerable<string> EnumerateActiveRuntimeManifests()
    {
        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using RegistryKey? key = hive.OpenSubKey(ActiveRuntimeRegistryPath);
            if (key?.GetValue("ActiveRuntime") is string activeRuntime && !string.IsNullOrWhiteSpace(activeRuntime))
                yield return activeRuntime;
        }
    }

    private static IEnumerable<string> EnumerateSteamVrManifestsFromOpenVrPaths()
    {
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            yield break;

        string pathsFile = Path.Combine(localAppData, "openvr", "openvrpaths.vrpath");
        if (!File.Exists(pathsFile))
            yield break;

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(pathsFile));
        if (!document.RootElement.TryGetProperty("runtime", out JsonElement runtimes)
            || runtimes.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (JsonElement runtime in runtimes.EnumerateArray())
        {
            if (runtime.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(runtime.GetString()))
                continue;

            string root = runtime.GetString()!;
            yield return Path.Combine(root, SteamVrManifestName);
            yield return Path.Combine(root, "resources", "openxr", SteamVrManifestName);
        }
    }

    private static string? FindSteamVrMonitorPath(string manifestPath)
    {
        string? directory = Path.GetDirectoryName(manifestPath);
        for (int parentDepth = 0; directory is not null && parentDepth < 4; parentDepth++)
        {
            string candidate = Path.Combine(directory, SteamVrMonitorRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static bool EnsureSteamVrRunning(string monitorPath, string reason, CancellationToken cancellationToken)
    {
        if (IsMonitorRunning(monitorPath))
            return true;

        string? workingDirectory = Path.GetDirectoryName(monitorPath);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = monitorPath,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            XREngine.Debug.LogWarning($"[OpenXRRuntimeSelection] Failed to start SteamVR from '{monitorPath}' ({reason}): {ex.Message}");
            return false;
        }

        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < ServiceStartTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsMonitorRunning(monitorPath))
                return true;

            Thread.Sleep(ServicePollInterval);
        }

        XREngine.Debug.LogWarning($"[OpenXRRuntimeSelection] Timed out waiting for SteamVR vrmonitor at '{monitorPath}' ({reason}).");
        return false;
    }

    private static bool IsMonitorRunning(string monitorPath)
    {
        string expectedPath = Path.GetFullPath(monitorPath);
        foreach (Process process in Process.GetProcessesByName("vrmonitor"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited
                        && string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty), expectedPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Process metadata can disappear during SteamVR shutdown.
                }
            }
        }

        return false;
    }

    private static bool LooksLikeMonado(string manifestPath, string? runtimeName)
        => (!string.IsNullOrWhiteSpace(runtimeName) && runtimeName.Contains("Monado", StringComparison.OrdinalIgnoreCase))
        || Path.GetFileName(manifestPath).Contains("monado", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSteamVr(string manifestPath, string? runtimeName, string? libraryPath)
        => (!string.IsNullOrWhiteSpace(runtimeName) && runtimeName.Contains("SteamVR", StringComparison.OrdinalIgnoreCase))
        || Path.GetFileName(manifestPath).Equals(SteamVrManifestName, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(libraryPath)
            && (libraryPath.Contains("steamvr", StringComparison.OrdinalIgnoreCase)
                || libraryPath.Contains("steamxr_win64", StringComparison.OrdinalIgnoreCase)
                || libraryPath.Contains("vrclient_x64", StringComparison.OrdinalIgnoreCase)));
}

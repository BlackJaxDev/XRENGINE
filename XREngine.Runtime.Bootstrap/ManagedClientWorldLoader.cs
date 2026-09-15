using System.Text.Json;
using XREngine.ControlPlane;
using XREngine.Networking;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Loads the exact verified managed-client world before networking opens a socket.</summary>
public static class ManagedClientWorldLoader
{
    public const string ConfigurationEnvironmentVariable = XREngineEnvironmentVariables.ManagedClientConfigFile;
    /// <summary>Marks a launcher-owned configuration file for deletion after it has been consumed.</summary>
    public const string DeleteConfigurationAfterReadEnvironmentVariable = "XRE_MANAGED_CLIENT_CONFIG_DELETE_AFTER_READ";

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConfigurationEnvironmentVariable));
    public static bool WasApplied { get; private set; }

    public static XRWorld? TryLoadFromEnvironment(GameStartupSettings settings)
        => TryLoadFromEnvironment(settings, CancellationToken.None, progress: null);

    /// <summary>Stages a verified immutable package into the client cache before loading its world.</summary>
    public static XRWorld? TryLoadFromEnvironment(
        GameStartupSettings settings,
        CancellationToken cancellationToken,
        IProgress<WorldPackageStagingProgress>? progress)
    {
        string? path = Environment.GetEnvironmentVariable(ConfigurationEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fullPath = Path.GetFullPath(path);
        bool deleteAfterRead = string.Equals(
            Environment.GetEnvironmentVariable(DeleteConfigurationAfterReadEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        ManagedClientLaunch launch;
        try
        {
            launch = JsonSerializer.Deserialize(File.ReadAllText(fullPath), XreControlPlaneJsonContext.Default.ManagedClientLaunch)
                ?? throw new InvalidOperationException("Managed client configuration was empty.");
        }
        finally
        {
            if (deleteAfterRead)
                DeleteOneShotConfiguration(fullPath);
        }

        return Load(launch, settings, cancellationToken, progress);
    }

    /// <summary>
    /// Stages and loads a verified managed package supplied by a trusted local launcher or in-process service client.
    /// The launch object is consumed in memory; callers must not persist or log its admission secret.
    /// </summary>
    public static XRWorld Load(
        ManagedClientLaunch launch,
        GameStartupSettings settings,
        CancellationToken cancellationToken = default,
        IProgress<WorldPackageStagingProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(settings);
        if (launch.ContractVersion != 1 || string.IsNullOrWhiteSpace(launch.PackageRootPath) || string.IsNullOrWhiteSpace(launch.CacheRootPath) || string.IsNullOrWhiteSpace(launch.WorldEntryPoint))
            throw new InvalidOperationException("Managed client configuration is incomplete.");
        if (launch.WorldPackage.SchemaVersion != 1
            || !string.Equals(launch.GameBootstrapId, "world-v1", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(launch.WorldPackage.GameBootstrapId) && !string.Equals(launch.GameBootstrapId, launch.WorldPackage.GameBootstrapId, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Managed client package requests an unsupported content schema or game bootstrap.");
        }

        WorldPackageVerificationResult verification = WorldPackageManifestBuilder.Verify(launch.WorldPackage, launch.PackageRootPath, cancellationToken, requireAssetContentHashMatch: true);
        if (!verification.Success)
            throw new InvalidOperationException("Managed client package verification failed.");
        if (!string.Equals(launch.BuildVersion, launch.WorldPackage.Asset.RequiredBuildVersion, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(launch.WorldPackage.WorldEntryPoint) && !string.Equals(launch.WorldEntryPoint, launch.WorldPackage.WorldEntryPoint, StringComparison.Ordinal)
            || launch.Handoff.WorldAsset is null
            || !launch.Handoff.WorldAsset.IsSameAssetAs(launch.WorldPackage.Asset))
        {
            throw new InvalidOperationException("Managed client handoff does not bind the verified package to the requested build and world.");
        }

        string cacheKey = WorldAssetIdentity.NormalizeHash(launch.WorldPackage.ManifestHash);
        if (string.IsNullOrWhiteSpace(cacheKey))
            throw new InvalidOperationException("Managed client package is missing its manifest identity.");
        string root = WorldPackageManifestBuilder.StageVerified(
            launch.WorldPackage,
            Path.Combine(Path.GetFullPath(launch.CacheRootPath), cacheKey),
            launch.PackageRootPath,
            cancellationToken,
            progress,
            requireAssetContentHashMatch: true);
        string entry = Path.GetFullPath(Path.Combine(root, launch.WorldEntryPoint));
        if (!entry.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(entry), ".asset", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(entry))
        {
            throw new InvalidOperationException("Managed client world entry point must be a verified native .asset file inside the package root.");
        }

        RealtimeJoinHandoff.ApplyToSettings(settings, launch.Handoff);
        // This launch was verified and supplied in memory; ambient process variables from an
        // earlier launcher must not replace its authenticated endpoint or player identity.
        settings.IgnoreEnvironmentRealtimeHandoffs = true;
        // Managed launches do not reserve a shared client port. Each local client gets an OS-selected UDP port.
        settings.UdpClientRecievePort = 0;
        XRWorld world = Engine.Assets.Load<XRWorld>(entry, bypassJobThread: true)
            ?? throw new InvalidOperationException("Managed client world entry point did not load an XRWorld.");
        WorldAssetIdentityProvider.RegisterVerifiedIdentity(world, launch.WorldPackage.Asset);
        WorldAssetIdentityProvider.RegisterVerifiedAssetPaths(world, launch.WorldPackage.Files.Select(static file => file.RelativePath));
        WasApplied = true;
        return world;
    }

    private static void DeleteOneShotConfiguration(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The launch remains fail-closed: loading can continue, but avoid surfacing
            // the credential-bearing path or configuration contents in diagnostics.
            Debug.NetworkingWarning("Managed client one-shot launch configuration could not be removed.");
        }
    }
}

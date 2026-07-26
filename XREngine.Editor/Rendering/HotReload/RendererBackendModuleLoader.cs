using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using XREngine.Rendering;

namespace XREngine.Editor.HotReload;

public sealed class RendererBackendModuleLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public LoadedRendererBackendGeneration Load(string manifestPath)
    {
        RendererReloadFailureInjection.ThrowIfEnabled(
            RendererReloadInjectedFailure.ModuleValidation,
            "module validation");
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        string generationDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new RendererBackendModuleValidationException("The generation manifest has no directory.");

        RendererBackendGenerationManifest manifest = JsonSerializer.Deserialize<RendererBackendGenerationManifest>(
            File.ReadAllText(fullManifestPath),
            JsonOptions) ?? throw new RendererBackendModuleValidationException(
                $"Manifest '{fullManifestPath}' is empty or invalid.");

        ValidateManifest(manifest, generationDirectory);
        string entryAssemblyPath = Path.Combine(generationDirectory, manifest.EntryAssembly);
        RendererBackendLoadContext context = new(entryAssemblyPath, manifest.Generation);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(entryAssemblyPath);
            Type entryPointType = assembly.GetType(
                manifest.EntryPointType,
                throwOnError: true,
                ignoreCase: false) ?? throw new RendererBackendModuleValidationException(
                $"Entry point '{manifest.EntryPointType}' was not found.");
            if (!typeof(IRendererBackendModule).IsAssignableFrom(entryPointType) ||
                entryPointType.IsAbstract ||
                entryPointType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new RendererBackendModuleValidationException(
                    $"Entry point '{entryPointType.FullName}' must be a public, non-abstract, parameterless {nameof(IRendererBackendModule)}.");
            }

            IRendererBackendModule module =
                (IRendererBackendModule)(Activator.CreateInstance(entryPointType)
                    ?? throw new RendererBackendModuleValidationException(
                        $"Entry point '{entryPointType.FullName}' returned null."));
            ValidateModuleMetadata(module.Metadata, manifest);

            RendererBackendMetadata generationMetadata = new(
                module.Metadata.Id,
                module.Metadata.GraphicsApi,
                module.Metadata.DisplayName,
                module.Metadata.Version,
                module.Metadata.Capabilities,
                module.Metadata.ReloadLimitations,
                module.Metadata.ReloadLimitationDescription,
                module.Metadata.AbiVersion,
                manifest.Generation,
                manifest.BuildHash,
                manifest.TargetFramework,
                manifest.ProcessArchitecture,
                manifest.EntryPointType);
            RendererBackendRegistration registration = new(
                generationMetadata,
                module.Factory,
                module);
            return new(
                manifest,
                fullManifestPath,
                context,
                module,
                registration);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    public static bool VerifyUnloaded(
        WeakReference contextReference,
        int garbageCollectionCycles = 3)
    {
        for (int cycle = 0; cycle < Math.Max(1, garbageCollectionCycles) && contextReference.IsAlive; cycle++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        return !contextReference.IsAlive;
    }

    private static void ValidateManifest(
        RendererBackendGenerationManifest manifest,
        string generationDirectory)
    {
        if (manifest.AbiVersion != RendererBackendAbi.CurrentVersion)
        {
            throw new RendererBackendModuleValidationException(
                $"Renderer module ABI {manifest.AbiVersion} is incompatible with host ABI {RendererBackendAbi.CurrentVersion}.");
        }

        if (manifest.Generation <= 0)
            throw new RendererBackendModuleValidationException("Renderer module generation must be positive.");
        if (manifest.ProcessArchitecture != RuntimeInformation.ProcessArchitecture)
        {
            throw new RendererBackendModuleValidationException(
                $"Renderer module architecture {manifest.ProcessArchitecture} does not match process architecture {RuntimeInformation.ProcessArchitecture}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            Path.IsPathRooted(manifest.EntryAssembly))
        {
            throw new RendererBackendModuleValidationException(
                "Renderer module entry assembly must be a relative file name.");
        }

        foreach ((string relativePath, string expectedHash) in manifest.FileHashes)
        {
            if (Path.IsPathRooted(relativePath))
                throw new RendererBackendModuleValidationException($"Manifest file '{relativePath}' is rooted.");
            if (RendererBackendLoadContext.IsSharedAssemblyName(relativePath))
            {
                throw new RendererBackendModuleValidationException(
                    $"Generation contains duplicate stable contract assembly '{relativePath}'.");
            }

            string filePath = Path.GetFullPath(Path.Combine(generationDirectory, relativePath));
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(generationDirectory)) +
                Path.DirectorySeparatorChar;
            if (!filePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new RendererBackendModuleValidationException($"Manifest file '{relativePath}' escapes its generation directory.");
            if (!File.Exists(filePath))
                throw new RendererBackendModuleValidationException($"Required module file '{relativePath}' is missing.");

            string actualHash = ComputeFileHash(filePath);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new RendererBackendModuleValidationException(
                    $"Module file hash mismatch for '{relativePath}'. Expected {expectedHash}, got {actualHash}.");
            }
        }

        if (!manifest.FileHashes.ContainsKey(manifest.EntryAssembly))
            throw new RendererBackendModuleValidationException("The entry assembly is not covered by the generation hash manifest.");
    }

    private static void ValidateModuleMetadata(
        RendererBackendMetadata metadata,
        RendererBackendGenerationManifest manifest)
    {
        if (metadata.Id != manifest.GetBackendId())
            throw new RendererBackendModuleValidationException(
                $"Module backend ID '{metadata.Id}' does not match manifest backend ID '{manifest.BackendId}'.");
        if (metadata.AbiVersion != manifest.AbiVersion)
            throw new RendererBackendModuleValidationException(
                $"Module ABI {metadata.AbiVersion} does not match manifest ABI {manifest.AbiVersion}.");
        if (!string.Equals(metadata.EntryPointTypeName, manifest.EntryPointType, StringComparison.Ordinal))
            throw new RendererBackendModuleValidationException(
                $"Module entry point metadata '{metadata.EntryPointTypeName}' does not match manifest '{manifest.EntryPointType}'.");
    }

    internal static string ComputeFileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
            .ToLowerInvariant();
    }
}

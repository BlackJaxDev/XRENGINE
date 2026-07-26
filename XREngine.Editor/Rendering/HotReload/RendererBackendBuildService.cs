using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XREngine.Rendering;

namespace XREngine.Editor.HotReload;

/// <summary>
/// Builds one renderer leaf project, stages a complete immutable generation, and publishes
/// its manifest only after every file has been copied and hashed.
/// </summary>
public sealed partial class RendererBackendBuildService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly HashSet<string> StagedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll",
        ".pdb",
        ".json",
        ".bin",
        ".dat",
        ".txt",
        ".license",
    };

    private readonly object _sync = new();
    private readonly string _repositoryRoot;
    private readonly string _generationRoot;
    private CancellationTokenSource? _activeBuildCancellation;
    private long _nextGeneration;

    public RendererBackendBuildService(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _generationRoot = Path.Combine(_repositoryRoot, "Build", "RendererHotReload");
        Directory.CreateDirectory(_generationRoot);
        _nextGeneration = DiscoverHighestGeneration(_generationRoot);
    }

    public int RetainedGenerationCount { get; set; } = 3;

    public async Task<RendererBackendBuildResult> BuildAsync(
        RendererBackendId backendId,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        long generation = Interlocked.Increment(ref _nextGeneration);
        CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _activeBuildCancellation?.Cancel();
            _activeBuildCancellation?.Dispose();
            _activeBuildCancellation = linkedCancellation;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        string projectPath = ResolveProjectPath(backendId);
        string buildOutput = Path.Combine(
            _generationRoot,
            "build",
            backendId.Value,
            generation.ToString());
        string buildParent = Path.GetDirectoryName(buildOutput)!;
        string generationParent = Path.Combine(_generationRoot, "generations", backendId.Value);
        string partialDirectory = Path.Combine(
            generationParent,
            $"{generation}.{Guid.NewGuid():N}.partial");
        string finalDirectory = Path.Combine(generationParent, generation.ToString());
        Directory.CreateDirectory(buildOutput);
        Directory.CreateDirectory(generationParent);
        PruneAbandonedBuildDirectories(buildParent, generation);
        PruneAbandonedPartialDirectories(generationParent);

        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(buildOutput);
        startInfo.ArgumentList.Add("-v:minimal");
        startInfo.ArgumentList.Add("-p:XREngineUseExistingNativeBridges=true");

        StringBuilder output = new();
        List<RendererBackendBuildDiagnostic> diagnostics = [];
        try
        {
            RendererReloadFailureInjection.ThrowIfEnabled(
                RendererReloadInjectedFailure.BackendBuild,
                "backend build");
            using Process process = new()
            {
                StartInfo = startInfo,
            };
            process.Start();
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
                linkedCancellation.Token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(
                linkedCancellation.Token);
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            string[] capturedOutput = await Task.WhenAll(standardOutput, standardError)
                .ConfigureAwait(false);
            CaptureOutput(capturedOutput[0], output, diagnostics);
            CaptureOutput(capturedOutput[1], output, diagnostics);
            if (process.ExitCode != 0)
            {
                return new(
                    false,
                    backendId,
                    generation,
                    null,
                    output.ToString(),
                    diagnostics,
                    stopwatch.Elapsed);
            }

            linkedCancellation.Token.ThrowIfCancellationRequested();
            StageGeneration(
                backendId,
                generation,
                buildOutput,
                partialDirectory,
                finalDirectory);
            string manifestPath = PublishManifest(backendId, generation, finalDirectory);
            PruneExpiredGenerations(generationParent, generation);
            return new(
                true,
                backendId,
                generation,
                manifestPath,
                output.ToString(),
                diagnostics,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return new(
                false,
                backendId,
                generation,
                null,
                output.ToString(),
                diagnostics,
                stopwatch.Elapsed,
                Cancelled: true);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new("error", "HOTRELOAD", ex.Message, null, null, null));
            return new(
                false,
                backendId,
                generation,
                null,
                output.AppendLine(ex.ToString()).ToString(),
                diagnostics,
                stopwatch.Elapsed);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeBuildCancellation, linkedCancellation))
                    _activeBuildCancellation = null;
            }

            linkedCancellation.Dispose();
            TryDeleteDirectory(partialDirectory);
            TryDeleteDirectory(buildOutput);
        }
    }

    public void CancelPendingBuild()
    {
        lock (_sync)
            _activeBuildCancellation?.Cancel();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _activeBuildCancellation?.Cancel();
            _activeBuildCancellation?.Dispose();
            _activeBuildCancellation = null;
        }
    }

    private void StageGeneration(
        RendererBackendId backendId,
        long generation,
        string buildOutput,
        string partialDirectory,
        string finalDirectory)
    {
        RendererReloadFailureInjection.ThrowIfEnabled(
            RendererReloadInjectedFailure.ShadowCopy,
            "backend shadow copy");
        if (Directory.Exists(partialDirectory))
            Directory.Delete(partialDirectory, recursive: true);
        if (Directory.Exists(finalDirectory))
            Directory.Delete(finalDirectory, recursive: true);
        Directory.CreateDirectory(partialDirectory);

        string entryAssembly = GetEntryAssemblyName(backendId);
        foreach (string sourcePath in Directory.EnumerateFiles(buildOutput, "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(sourcePath);
            if (!StagedExtensions.Contains(extension))
                continue;

            string relativePath = Path.GetRelativePath(buildOutput, sourcePath);
            string fileName = Path.GetFileName(relativePath);
            if (RendererBackendLoadContext.IsSharedAssemblyName(fileName))
                continue;

            string destinationPath = Path.Combine(partialDirectory, relativePath);
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (destinationDirectory is not null)
                Directory.CreateDirectory(destinationDirectory);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        if (!File.Exists(Path.Combine(partialDirectory, entryAssembly)))
        {
            throw new FileNotFoundException(
                $"Backend build did not produce expected entry assembly '{entryAssembly}'.",
                entryAssembly);
        }

        MoveDirectoryWithRetry(partialDirectory, finalDirectory);
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        const int maximumAttempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(50);
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void PruneAbandonedPartialDirectories(string generationParent)
    {
        foreach (string path in Directory.EnumerateDirectories(
                     generationParent,
                     "*.partial",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // A prior antivirus/indexer handle can briefly outlive a failed publish.
                // The unique next staging name avoids collision; a later build retries.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above. Never fail the new build because stale staging cleanup
                // is temporarily unavailable.
            }
        }
    }

    private static void PruneAbandonedBuildDirectories(string buildParent, long activeGeneration)
    {
        if (!Directory.Exists(buildParent))
            return;

        foreach (string path in Directory.EnumerateDirectories(
                     buildParent,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (long.TryParse(Path.GetFileName(path), out long generation) &&
                generation != activeGeneration)
            {
                TryDeleteDirectory(path);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Antivirus/indexer handles may briefly outlive dotnet build. The next build's
            // abandoned-output pruning gets another opportunity without invalidating a
            // generation that was already published atomically.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above. Failure to reclaim disposable output is diagnostic, not a
            // reason to roll back an already accepted backend generation.
        }
    }

    private static string PublishManifest(
        RendererBackendId backendId,
        long generation,
        string finalDirectory)
    {
        string entryAssembly = GetEntryAssemblyName(backendId);
        string entryPoint = GetEntryPointTypeName(backendId);
        Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(finalDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(finalDirectory, path);
            hashes.Add(relativePath, RendererBackendModuleLoader.ComputeFileHash(path));
        }

        string buildHash = hashes[entryAssembly];
        RendererBackendGenerationManifest manifest = new()
        {
            BackendId = backendId.Value,
            Generation = generation,
            AbiVersion = RendererBackendAbi.CurrentVersion,
            TargetFramework = "net10.0-windows7.0",
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture,
            EntryAssembly = entryAssembly,
            EntryPointType = entryPoint,
            BuildHash = buildHash,
            CreatedAt = DateTimeOffset.UtcNow,
            FileHashes = hashes,
        };

        string manifestPath = Path.Combine(finalDirectory, "renderer-backend-generation.json");
        string temporaryManifestPath = manifestPath + ".tmp";
        File.WriteAllText(temporaryManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temporaryManifestPath, manifestPath);
        return manifestPath;
    }

    private void PruneExpiredGenerations(string generationParent, long activeGeneration)
    {
        int retained = Math.Clamp(RetainedGenerationCount, 2, 16);
        DirectoryInfo[] generations = new DirectoryInfo(generationParent)
            .EnumerateDirectories()
            .Where(directory => long.TryParse(directory.Name, out _))
            .OrderByDescending(directory => long.Parse(directory.Name))
            .ToArray();
        for (int i = retained; i < generations.Length; i++)
        {
            if (long.TryParse(generations[i].Name, out long generation) &&
                generation != activeGeneration)
            {
                generations[i].Delete(recursive: true);
            }
        }
    }

    private string ResolveProjectPath(RendererBackendId backendId)
    {
        string projectDirectory = backendId == RendererBackendId.OpenGL
            ? "XREngine.Runtime.Rendering.OpenGL"
            : backendId == RendererBackendId.Vulkan
                ? "XREngine.Runtime.Rendering.Vulkan"
                : throw new ArgumentOutOfRangeException(nameof(backendId), backendId, "Only OpenGL and Vulkan support editor backend rebuilds.");
        return Path.Combine(_repositoryRoot, projectDirectory, projectDirectory + ".csproj");
    }

    private static string GetEntryAssemblyName(RendererBackendId backendId)
        => backendId == RendererBackendId.OpenGL
            ? "XREngine.Runtime.Rendering.OpenGL.dll"
            : backendId == RendererBackendId.Vulkan
                ? "XREngine.Runtime.Rendering.Vulkan.dll"
                : throw new ArgumentOutOfRangeException(nameof(backendId));

    private static string GetEntryPointTypeName(RendererBackendId backendId)
        => backendId == RendererBackendId.OpenGL
            ? "XREngine.Rendering.OpenGL.OpenGlRendererBackendModuleEntry"
            : backendId == RendererBackendId.Vulkan
                ? "XREngine.Rendering.Vulkan.VulkanRendererBackendModuleEntry"
                : throw new ArgumentOutOfRangeException(nameof(backendId));

    private static void CaptureLine(
        string? line,
        StringBuilder output,
        List<RendererBackendBuildDiagnostic> diagnostics)
    {
        if (line is null)
            return;

        lock (output)
            output.AppendLine(line);
        Match match = BuildDiagnosticRegex().Match(line);
        if (!match.Success)
            return;

        int? parsedLine = int.TryParse(match.Groups["line"].Value, out int lineNumber)
            ? lineNumber
            : null;
        int? parsedColumn = int.TryParse(match.Groups["column"].Value, out int columnNumber)
            ? columnNumber
            : null;
        lock (diagnostics)
        {
            diagnostics.Add(new(
                match.Groups["severity"].Value,
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim(),
                EmptyToNull(match.Groups["file"].Value),
                parsedLine,
                parsedColumn));
        }
    }

    private static void CaptureOutput(
        string text,
        StringBuilder output,
        List<RendererBackendBuildDiagnostic> diagnostics)
    {
        using StringReader reader = new(text);
        while (reader.ReadLine() is { } line)
            CaptureLine(line, output, diagnostics);
    }

    private static string? EmptyToNull(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static long DiscoverHighestGeneration(string root)
    {
        string generationRoot = Path.Combine(root, "generations");
        if (!Directory.Exists(generationRoot))
            return 0;

        long highest = 0;
        foreach (string path in Directory.EnumerateDirectories(generationRoot, "*", SearchOption.AllDirectories))
        {
            if (long.TryParse(Path.GetFileName(path), out long generation))
                highest = Math.Max(highest, generation);
        }

        return highest;
    }

    [GeneratedRegex(
        @"^(?:(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*)?(?<severity>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<message>.+?)(?:\s+\[.+\])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildDiagnosticRegex();
}

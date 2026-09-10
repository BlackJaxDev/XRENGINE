using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Invokes a locally installed Slang compiler for direct Vulkan SPIR-V output.
/// </summary>
internal static class SlangVulkanShaderCompiler
{
    private static readonly SemaphoreSlim CompileGate = new(2, 2);
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(45);
    private static readonly Regex DiagnosticPattern = new(
        @"^(?<path>[^\r\n]+?)(?:\((?<line>\d+)(?:,\d+)?\)|:(?<line>\d+)(?::\d+)?):\s*(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    internal static async Task<ShaderCompileResult> CompileAsync(
        ShaderCompileRequest request,
        CancellationToken cancellationToken)
    {
        request = request with
        {
            Includes = request.Includes?.ToArray(), Defines = request.Defines?.ToArray(),
            RequiredCapabilities = request.RequiredCapabilities?.ToArray(),
        };
        ValidateRequest(request);
        await CompileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The gate includes toolchain probes as well as compilation processes.
            string compilerPath = ResolveCompilerPath();
            string compilerIdentity = await GetCompilerIdentityAsync(compilerPath, cancellationToken).ConfigureAwait(false);
            string lookupKey = BuildLookupKey(request, compilerIdentity);
            if (SlangVulkanShaderCache.TryRead(lookupKey, compilerIdentity, out ShaderCompileResult cached))
                return cached;

            return await CompileMissAsync(request, compilerPath, compilerIdentity, lookupKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompileGate.Release();
        }
    }

    private static async Task<ShaderCompileResult> CompileMissAsync(
        ShaderCompileRequest request,
        string compilerPath,
        string compilerIdentity,
        string lookupKey,
        CancellationToken cancellationToken)
    {
        string jobDirectory = Path.Combine(SlangVulkanShaderCache.GetRootPath(), "compile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);
        string sourcePath = Path.Combine(jobDirectory, string.IsNullOrWhiteSpace(request.SourcePath) ? "source.slang" : Path.GetFileName(request.SourcePath));
        string spirvPath = Path.Combine(jobDirectory, "shader.spv");
        string reflectionPath = Path.Combine(jobDirectory, "reflection.json");
        string dependencyPath = Path.Combine(jobDirectory, "dependencies.d");
        try
        {
            string authoredPath = (request.SourcePath ?? "<memory.slang>").Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal);
            // A native source-map directive preserves __FILE__ and source spans
            // while the private job copy retains the caller's unsaved source text.
            string mappedSource = $"#line 1 \"{authoredPath}\"\n" + request.Source;
            await File.WriteAllTextAsync(sourcePath, mappedSource, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> includeDirectories = ResolveIncludeDirectories(request);
            List<ShaderCompileDependency> before = CaptureDeclaredDependencies(request);
            if (BuildLookupKey(request, compilerIdentity) != lookupKey)
                throw new InvalidOperationException("Slang include search graph changed before compilation; retry the request.");
            Stopwatch stopwatch = Stopwatch.StartNew();
            ProcessOutput output = await RunCompilerAsync(
                compilerPath,
                sourcePath,
                spirvPath,
                reflectionPath,
                dependencyPath,
                request,
                includeDirectories,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            string diagnosticText = output.Combined.Replace(sourcePath, request.SourcePath ?? "<memory.slang>", StringComparison.OrdinalIgnoreCase);
            if (output.ExitCode != 0)
                throw new ShaderCompilationException($"Slang compilation failed: {diagnosticText.Trim()}", ParseDiagnostics(diagnosticText));
            if (!File.Exists(spirvPath) || new FileInfo(spirvPath).Length == 0)
                throw new InvalidOperationException("Slang completed without producing direct SPIR-V output.");
            if (!File.Exists(reflectionPath))
                throw new InvalidOperationException("Slang completed without producing reflection JSON.");

            byte[] spirv = await File.ReadAllBytesAsync(spirvPath, cancellationToken).ConfigureAwait(false);
            VulkanShaderCompiler.ValidateModuleWhenRequested(request.SourcePath ?? "SlangShader", spirv);
            IReadOnlyList<ShaderCompileDependency> dependencies = CaptureDependenciesAfterCompile(
                request,
                dependencyPath,
                before, sourcePath);
            if (BuildLookupKey(request, compilerIdentity) != lookupKey ||
                await GetCompilerIdentityAsync(compilerPath, cancellationToken).ConfigureAwait(false) != compilerIdentity)
                throw new InvalidOperationException("Slang compiler or include search graph changed during compilation; the artifact was rejected.");
            string reflectionJson = await File.ReadAllTextAsync(reflectionPath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ShaderCompileDiagnostic> diagnostics = ParseDiagnostics(diagnosticText);
            string artifactIdentity = BuildArtifactIdentity(request, compilerIdentity, dependencies);
            ShaderCompileResult result = new(
                spirv,
                request.EntryPoint,
                artifactIdentity,
                compilerIdentity,
                reflectionJson,
                dependencies,
                diagnostics,
                LoadedFromCache: false,
                stopwatch.Elapsed);
            SlangVulkanShaderCache.Write(lookupKey, result);
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(jobDirectory))
                    Directory.Delete(jobDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<ProcessOutput> RunCompilerAsync(
        string compilerPath,
        string sourcePath,
        string spirvPath,
        string reflectionPath,
        string dependencyPath,
        ShaderCompileRequest request,
        IReadOnlyList<string> includeDirectories,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(compilerPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sourcePath)!,
        };
        startInfo.ArgumentList.Add("-lang");
        startInfo.ArgumentList.Add("slang");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-target");
        startInfo.ArgumentList.Add("spirv");
        startInfo.ArgumentList.Add("-profile");
        startInfo.ArgumentList.Add("spirv_1_6");
        startInfo.ArgumentList.Add("-emit-spirv-directly");
        startInfo.ArgumentList.Add("-fvk-use-entrypoint-name");
        startInfo.ArgumentList.Add("-reflection-json");
        startInfo.ArgumentList.Add(reflectionPath);
        startInfo.ArgumentList.Add("-depfile");
        startInfo.ArgumentList.Add(dependencyPath);
        startInfo.ArgumentList.Add("-restrictive-capability-check");
        startInfo.ArgumentList.Add(request.MatrixLayout == ShaderMatrixLayout.RowMajor ? "-matrix-layout-row-major" : "-matrix-layout-column-major");
        startInfo.ArgumentList.Add("-entry");
        startInfo.ArgumentList.Add(request.EntryPoint);
        startInfo.ArgumentList.Add("-stage");
        startInfo.ArgumentList.Add(ToSlangStage(request.Stage));
        foreach (string includeDirectory in includeDirectories)
        {
            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add(includeDirectory);
        }
        foreach (string define in request.Defines ?? [])
        {
            startInfo.ArgumentList.Add("-D");
            startInfo.ArgumentList.Add(define);
        }
        foreach (string capability in request.RequiredCapabilities ?? [])
        {
            startInfo.ArgumentList.Add("-capability");
            startInfo.ArgumentList.Add(capability);
        }
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(spirvPath);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("slangc did not start.");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CompileTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"slangc exceeded the {CompileTimeout.TotalSeconds:F0}-second shader compile timeout.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        string standardOutput = await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        return new ProcessOutput(process.ExitCode, standardOutput, standardError);
    }

    private static void ValidateRequest(ShaderCompileRequest request)
    {
        if (request.Language != ShaderSourceLanguage.Slang)
            throw new ArgumentException("The Slang compiler accepts only Slang requests.", nameof(request));
        if (request.Target != ShaderCompileTarget.Vulkan14Spirv16)
            throw new NotSupportedException($"Slang target '{request.Target}' is not supported by the Vulkan compiler.");
        if (string.IsNullOrWhiteSpace(request.Source))
            throw new ArgumentException("Shader source is empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.EntryPoint))
            throw new ArgumentException("An entry point is required.", nameof(request));
    }

    private static string ResolveCompilerPath()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("XRE_SLANGC");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return RequireExistingCompiler(explicitPath, "XRE_SLANGC");

        string? sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (!string.IsNullOrWhiteSpace(sdk))
        {
            string sdkCompiler = Path.Combine(sdk, "Bin", "slangc.exe");
            if (File.Exists(sdkCompiler))
                return sdkCompiler;
        }

        string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string segment in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(segment, "slangc.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Slang compilation requires slangc 2026.8. Set XRE_SLANGC, install it in VULKAN_SDK\\Bin, or add it to PATH.");
    }

    private static string RequireExistingCompiler(string path, string source)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"{source} points to a missing slangc executable.", fullPath);
        return fullPath;
    }

    private static async Task<string> GetCompilerIdentityAsync(string compilerPath, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(compilerPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-version");
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("slangc did not start for version inspection.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CompileTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        string version = ((await outputTask.ConfigureAwait(false)) + (await errorTask.ConfigureAwait(false))).Trim();
        if (process.ExitCode != 0 || !Regex.IsMatch(version, @"(?<![\d.])2026\.8(?![\d.])"))
            throw new InvalidOperationException($"slangc must be pinned to 2026.8; found '{version}'.");

        string directory = Path.GetDirectoryName(compilerPath)!;
        StringBuilder identity = new(Path.GetFullPath(compilerPath));
        identity.Append('|').Append(version);
        // SDK distributions keep standard modules outside the compiler DLL. They
        // participate in identity just like the executable and loaded Slang DLLs.
        IEnumerable<string> compilerFiles = Directory.EnumerateFiles(directory, "slang*")
            .Where(static path => Path.GetExtension(path) is ".exe" or ".dll" or ".slang")
            .Concat(Directory.EnumerateDirectories(directory, "slang-standard-module*")
                .SelectMany(static path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)))
            .Append(compilerPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in compilerFiles)
        {
            identity.Append('|').Append(Path.GetRelativePath(directory, filePath)).Append('=')
                .Append(SlangVulkanShaderCache.Hash(File.ReadAllBytes(filePath)));
        }
        return identity.ToString();
    }

    private static IReadOnlyList<string> ResolveIncludeDirectories(ShaderCompileRequest request)
    {
        List<string> directories = [];
        if (!string.IsNullOrWhiteSpace(request.SourcePath))
        {
            string? sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(request.SourcePath));
            if (!string.IsNullOrWhiteSpace(sourceDirectory))
                directories.Add(sourceDirectory);
        }
        foreach (string include in request.Includes ?? [])
            directories.Add(Path.GetFullPath(include));
        return directories;
    }

    private static List<ShaderCompileDependency> CaptureDeclaredDependencies(ShaderCompileRequest request)
    {
        // Snapshot candidate include/module inputs before compilation, including
        // shadowing candidates earlier in the search path. A newly created file
        // must invalidate a cache hit even when old depfile paths still exist.
        SortedSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in ResolveIncludeDirectories(request))
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Slang include directory does not exist: {directory}");
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                if (Path.GetExtension(path).ToLowerInvariant() is ".slang" or ".slangh" or ".slang-module" or ".h" or ".hlsl" or ".glslinc")
                    paths.Add(Path.GetFullPath(path));
        }
        if (!string.IsNullOrWhiteSpace(request.SourcePath) && File.Exists(request.SourcePath))
            paths.Add(Path.GetFullPath(request.SourcePath));
        return paths.Select(Snapshot).ToList();
    }

    private static IReadOnlyList<ShaderCompileDependency> CaptureDependenciesAfterCompile(
        ShaderCompileRequest request,
        string dependencyPath,
        List<ShaderCompileDependency> before, string temporarySourcePath)
    {
        Dictionary<string, ShaderCompileDependency> dependencies = new(StringComparer.OrdinalIgnoreCase);
        foreach (ShaderCompileDependency dependency in before)
        {
            if (!File.Exists(dependency.Path) ||
                !string.Equals(SlangVulkanShaderCache.Hash(File.ReadAllBytes(dependency.Path)), dependency.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException($"Shader dependency '{dependency.Path}' changed while slangc was compiling.");
            dependencies[dependency.Path] = dependency;
        }
        if (!File.Exists(dependencyPath))
            throw new InvalidOperationException("Slang completed without its required dependency manifest.");
        if (File.Exists(dependencyPath))
        {
            foreach (string path in ParseDependencyFile(File.ReadAllText(dependencyPath)))
            {
                string fullPath = Path.GetFullPath(path);
                if (string.Equals(fullPath, temporarySourcePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!dependencies.ContainsKey(fullPath))
                    throw new InvalidOperationException($"Slang dependency '{fullPath}' was not in the precompile snapshot. Put source imports under a declared include root using a supported source extension.");
            }
        }
        return dependencies.Values.OrderBy(static x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> ParseDependencyFile(string text)
    {
        string flattened = text.Replace("\\\r\n", " ", StringComparison.Ordinal).Replace("\\\n", " ", StringComparison.Ordinal);
        int colon = flattened.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
            yield break;
        string dependencyList = flattened[(colon + 2)..];
        StringBuilder token = new();
        for (int index = 0; index < dependencyList.Length; index++)
        {
            char ch = dependencyList[index];
            if (ch == '\\' && index + 1 < dependencyList.Length)
                token.Append(dependencyList[++index]);
            else if (char.IsWhiteSpace(ch))
            {
                if (token.Length == 0)
                    continue;
                yield return token.ToString();
                token.Clear();
            }
            else
                token.Append(ch);
        }
        if (token.Length > 0)
            yield return token.ToString();
    }

    private static ShaderCompileDependency Snapshot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return new ShaderCompileDependency(fullPath, SlangVulkanShaderCache.Hash(File.ReadAllBytes(fullPath)));
    }

    private static string BuildLookupKey(ShaderCompileRequest request, string compilerIdentity)
        => "slang-" + HashText(BuildRequestIdentity(request, compilerIdentity))[..24];

    private static string BuildArtifactIdentity(
        ShaderCompileRequest request,
        string compilerIdentity,
        IReadOnlyList<ShaderCompileDependency> dependencies)
    {
        StringBuilder builder = new(BuildRequestIdentity(request, compilerIdentity));
        foreach (ShaderCompileDependency dependency in dependencies)
            builder.Append("Dependency=").Append(dependency.Path).Append('|').Append(dependency.Sha256).Append('\n');
        return "SLANGVK-" + HashText(builder.ToString())[..24];
    }

    private static string BuildRequestIdentity(ShaderCompileRequest request, string compilerIdentity)
    {
        StringBuilder builder = new();
        builder.Append("Frontend=xre-native-slang-v2;direct-spirv;restrictive-capabilities;preserve-entry;native-source-map\n");
        builder.Append("Language=").Append(request.Language).Append('\n');
        builder.Append("Target=").Append(request.Target).Append('\n');
        builder.Append("Stage=").Append(request.Stage).Append('\n');
        builder.Append("Entry=").Append(request.EntryPoint).Append('\n');
        builder.Append("Matrix=").Append(request.MatrixLayout).Append('\n');
        builder.Append("SemanticSchema=").Append(request.SemanticSchemaIdentity).Append('\n');
        builder.Append("Compiler=").Append(compilerIdentity).Append('\n');
        builder.Append("Source=").Append(HashText(request.Source)).Append('\n');
        if (!string.IsNullOrWhiteSpace(request.SourcePath))
        {
            builder.Append("SourcePath=").Append(Path.GetFullPath(request.SourcePath)).Append('\n');
            builder.Append("SourceDirectory=").Append(Path.GetDirectoryName(Path.GetFullPath(request.SourcePath))).Append('\n');
        }
        AppendOrdered(builder, "Include", ResolveIncludeDirectories(request));
        AppendOrdered(builder, "Define", request.Defines);
        AppendOrdered(builder, "Capability", request.RequiredCapabilities);
        foreach (ShaderCompileDependency candidate in CaptureDeclaredDependencies(request))
            builder.Append("Input=").Append(candidate.Path).Append('|').Append(candidate.Sha256).Append('\n');
        return builder.ToString();
    }

    private static void AppendOrdered(StringBuilder builder, string name, IReadOnlyList<string>? values)
    {
        if (values is null)
            return;
        foreach (string value in values)
            builder.Append(name).Append('=').Append(value).Append('\n');
    }

    private static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IReadOnlyList<ShaderCompileDiagnostic> ParseDiagnostics(string text)
    {
        List<ShaderCompileDiagnostic> diagnostics = [];
        // Slang 2026.8 uses a diagnostic header followed by an arrow source span.
        string message = string.Empty;
        string? severity = null;
        using StringReader reader = new(text);
        while (reader.ReadLine() is string lineText)
        {
            string trimmed = lineText.Trim();
            if (trimmed.StartsWith("error[", StringComparison.Ordinal) || trimmed.StartsWith("warning[", StringComparison.Ordinal))
            {
                message = trimmed;
                severity = trimmed.StartsWith("error", StringComparison.Ordinal) ? "error" : "warning";
            }
            Match location = Regex.Match(trimmed, @"^-->\s+(?<path>.+):(?<line>\d+):(?<column>\d+)$", RegexOptions.CultureInvariant);
            if (location.Success)
                diagnostics.Add(new ShaderCompileDiagnostic(location.Groups["path"].Value,
                    int.Parse(location.Groups["line"].Value), message,
                    int.Parse(location.Groups["column"].Value), severity));
        }
        if (diagnostics.Count != 0)
            return diagnostics;
        foreach (Match match in DiagnosticPattern.Matches(text))
        {
            int? line = int.TryParse(match.Groups["line"].Value, out int parsed) ? parsed : null;
            diagnostics.Add(new ShaderCompileDiagnostic(match.Groups["path"].Value, line, match.Groups["message"].Value));
        }
        return diagnostics;
    }

    private static string ToSlangStage(EShaderType stage)
        => stage switch
        {
            EShaderType.Vertex => "vertex",
            EShaderType.Fragment => "fragment",
            EShaderType.Geometry => "geometry",
            EShaderType.TessControl => "hull",
            EShaderType.TessEvaluation => "domain",
            EShaderType.Compute => "compute",
            EShaderType.Task => "amplification",
            EShaderType.Mesh => "mesh",
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
        };

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private readonly record struct ProcessOutput(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Combined => StandardOutput + Environment.NewLine + StandardError;
    }
}

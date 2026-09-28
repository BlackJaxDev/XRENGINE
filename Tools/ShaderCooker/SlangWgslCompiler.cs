using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XREngine.Tools.ShaderCooker;

/// <summary>Compiles a source-root-contained Slang program to WGSL with a locally installed Slang 2026.8.</summary>
internal static class SlangWgslCompiler
{
    private const int MaxSourceBytes = 1024 * 1024;
    private const int MaxReflectionBytes = 64 * 1024;
    private const int MaxDepfileBytes = 512 * 1024;
    private const int MaxDiagnosticCharacters = 64 * 1024;
    private const int MaxInputFiles = 4096;
    private const long MaxInputBytes = 64L * 1024 * 1024;
    private const int MaxToolchainFiles = 128;
    private const long MaxToolchainBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Compiles the fixed browser mesh vertex and fragment entry points as one WGSL module.</summary>
    internal static async Task<SlangWgslOutput> CompileAsync(
        string sourceRoot,
        string sourcePath,
        IReadOnlyList<string> includes,
        IReadOnlyList<string> defines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(includes);
        ArgumentNullException.ThrowIfNull(defines);

        string root = Path.GetFullPath(sourceRoot);
        RequireDirectory(root);
        string source = ContainedPath(root, sourcePath);
        if (!source.EndsWith(".slang", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Slang source must have a .slang extension.", nameof(sourcePath));
        RequireFile(source);

        List<string> includePaths = new(includes.Count);
        foreach (string include in includes)
        {
            string directory = ContainedPath(root, include, allowRoot: true);
            RequireDirectory(directory);
            includePaths.Add(directory);
        }
        foreach (string define in defines)
        {
            if (string.IsNullOrWhiteSpace(define) || define.Length > 256 ||
                define.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("Slang defines must be nonempty single-line values of at most 256 characters.", nameof(defines));
        }

        string compiler = ResolveCompiler();
        string identity = await CompilerIdentityAsync(compiler, cancellationToken).ConfigureAwait(false);
        SortedDictionary<string, string> before = await SnapshotAsync(root, cancellationToken).ConfigureAwait(false);
        string relativeSource = Relative(root, source);
        if (!before.ContainsKey(relativeSource))
            throw new InvalidOperationException("Slang source is absent from the dependency snapshot.");

        string job = Path.Combine(Path.GetTempPath(), "xr-shader-cooker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            string wgsl = Path.Combine(job, "shader.wgsl");
            string reflection = Path.Combine(job, "reflection.json");
            string depfile = Path.Combine(job, "dependencies.d");
            ProcessStartInfo start = new(compiler)
            {
                WorkingDirectory = Path.GetDirectoryName(source)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[]
            {
                "-lang", "slang", source, "-target", "wgsl", "-whole-program",
                "-entry", "vertexMain", "-stage", "vertex",
                "-entry", "fragmentMain", "-stage", "fragment",
                "-matrix-layout-column-major", "-restrictive-capability-check",
                "-reflection-json", reflection, "-depfile", depfile,
            })
                start.ArgumentList.Add(argument);
            foreach (string include in includePaths)
            {
                start.ArgumentList.Add("-I");
                start.ArgumentList.Add(include);
            }
            foreach (string define in defines)
            {
                start.ArgumentList.Add("-D");
                start.ArgumentList.Add(define);
            }
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add(wgsl);

            (int exitCode, string diagnostics) = await RunAsync(start, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException($"Slang WGSL compilation failed (exit {exitCode}): {diagnostics.Trim()}");
            string output = await ReadUtf8Async(wgsl, MaxSourceBytes, cancellationToken).ConfigureAwait(false);
            string reflectionJson = await ReadUtf8Async(reflection, MaxReflectionBytes, cancellationToken).ConfigureAwait(false);
            using (JsonDocument parsed = JsonDocument.Parse(reflectionJson))
            {
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Slang reflection must be a JSON object.");
            }
            string dependenciesText = await ReadUtf8Async(depfile, MaxDepfileBytes, cancellationToken).ConfigureAwait(false);
            HashSet<string> dependencies = ParseDepfile(dependenciesText, Path.GetDirectoryName(source)!);
            if (dependencies.Count == 0)
                throw new InvalidDataException("Slang produced an empty dependency file.");
            foreach (string dependency in dependencies)
            {
                if (PathEquals(dependency, source))
                    continue;
                string relative = Relative(root, dependency);
                RequireFile(dependency);
                if (!before.ContainsKey(relative))
                    throw new InvalidOperationException($"Slang dependency '{relative}' was not in the precompile snapshot.");
            }
            SortedDictionary<string, string> after = await SnapshotAsync(root, cancellationToken).ConfigureAwait(false);
            if (!before.SequenceEqual(after))
                throw new InvalidOperationException("Slang source or an include-shadowing candidate changed during compilation; retry.");
            if (!string.Equals(identity, await CompilerIdentityAsync(compiler, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
                throw new InvalidOperationException("The Slang installation changed during compilation; retry.");
            return new SlangWgslOutput(output, identity, before, reflectionJson);
        }
        finally
        {
            try { Directory.Delete(job, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string ResolveCompiler()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("XRE_SLANGC");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return ExistingCompiler(explicitPath);
        string executable = OperatingSystem.IsWindows() ? "slangc.exe" : "slangc";
        string? sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (!string.IsNullOrWhiteSpace(sdk))
        {
            string candidate = Path.Combine(sdk, "Bin", executable);
            if (File.Exists(candidate))
                return ExistingCompiler(candidate);
        }
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
                return ExistingCompiler(candidate);
        }
        throw new FileNotFoundException("Slang 2026.8 is required. Set XRE_SLANGC, VULKAN_SDK/Bin, or PATH to an existing slangc installation.");
    }

    private static string ExistingCompiler(string path)
    {
        string full = Path.GetFullPath(path);
        RequireFile(full);
        return full;
    }

    private static async Task<string> CompilerIdentityAsync(string compiler, CancellationToken cancellationToken)
    {
        ProcessStartInfo versionStart = new(compiler)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        versionStart.ArgumentList.Add("-version");
        (int exitCode, string version) = await RunAsync(versionStart, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0 || !Regex.IsMatch(version, @"(?<![\d.])2026\.8(?![\d.])", RegexOptions.CultureInvariant))
            throw new InvalidOperationException($"slangc must be pinned to 2026.8; reported '{version.Trim()}'.");

        string directory = Path.GetDirectoryName(compiler)!;
        SortedSet<string> files = new(StringComparer.Ordinal);
        files.Add(compiler);
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if ((name.StartsWith("slang", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("libslang", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("gfx", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("libgfx", StringComparison.OrdinalIgnoreCase)) &&
                (Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".so" or ".dylib" or ".exe" or ".slang" or ".slang-module" ||
                 name.Contains(".so.", StringComparison.OrdinalIgnoreCase)))
                files.Add(path);
        }
        foreach (string path in Directory.EnumerateDirectories(directory, "slang-standard-module*", SearchOption.TopDirectoryOnly))
        {
            Stack<string> pending = new();
            pending.Push(path);
            int directoryCount = 0;
            while (pending.Count > 0)
            {
                string moduleDirectory = pending.Pop();
                RejectReparse(moduleDirectory);
                if (++directoryCount > MaxToolchainFiles)
                    throw new InvalidDataException("The Slang installation exceeds the toolchain directory limit.");
                foreach (string entry in Directory.EnumerateFileSystemEntries(moduleDirectory))
                {
                    RejectReparse(entry);
                    if (Directory.Exists(entry)) pending.Push(entry);
                    else files.Add(entry);
                    if (files.Count > MaxToolchainFiles)
                        throw new InvalidDataException("The Slang installation exceeds the toolchain file limit.");
                }
            }
        }
        if (files.Count > MaxToolchainFiles)
            throw new InvalidDataException("The Slang installation exceeds the toolchain file limit.");
        long total = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, version.Trim());
        foreach (string path in files)
        {
            RejectReparse(path);
            FileInfo info = new(path);
            total = checked(total + info.Length);
            if (total > MaxToolchainBytes)
                throw new InvalidDataException("The Slang installation exceeds the toolchain size limit.");
            AppendHash(hash, Path.GetRelativePath(directory, path).Replace('\\', '/'));
            hash.AppendData(await HashFileAsync(path, info.Length, cancellationToken).ConfigureAwait(false));
        }
        return "slang/2026.8/" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHash(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(new byte[] { 0 });
    }

    private static async Task<byte[]> HashFileAsync(string path, long expectedLength, CancellationToken token)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            length = checked(length + read);
            if (length > expectedLength)
                throw new InvalidOperationException("A Slang input grew while its identity was being computed.");
            hash.AppendData(buffer.AsSpan(0, read));
        }
        if (length != expectedLength)
            throw new InvalidOperationException("A Slang input changed while its identity was being computed.");
        return hash.GetHashAndReset();
    }

    private static async Task<SortedDictionary<string, string>> SnapshotAsync(string root, CancellationToken token)
    {
        SortedDictionary<string, string> result = new(StringComparer.Ordinal);
        Stack<string> directories = new();
        directories.Push(root);
        int directoryCount = 0;
        long total = 0;
        while (directories.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = directories.Pop();
            RejectReparse(directory);
            if (++directoryCount > MaxInputFiles)
                throw new InvalidDataException("Slang source tree exceeds the directory limit.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparse(entry);
                if (Directory.Exists(entry))
                {
                    directories.Push(entry);
                    continue;
                }
                if (!IsSourceCandidate(entry))
                    continue;
                if (result.Count >= MaxInputFiles)
                    throw new InvalidDataException("Slang source tree exceeds the input file limit.");
                FileInfo info = new(entry);
                total = checked(total + info.Length);
                if (total > MaxInputBytes || info.Length > MaxSourceBytes)
                    throw new InvalidDataException("Slang source tree exceeds its input size limit.");
                string digest = Convert.ToHexString(await HashFileAsync(entry, info.Length, token).ConfigureAwait(false)).ToLowerInvariant();
                result.Add(Relative(root, entry), digest);
            }
        }
        return result;
    }

    private static bool IsSourceCandidate(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".slang" or ".slangh" or ".slang-module" or ".h" or ".hlsl" or ".glslinc";

    private static HashSet<string> ParseDepfile(string text, string workingDirectory)
    {
        // Make-style escaping permits spaces and continuations. The first unescaped
        // colon followed by whitespace separates the output target from inputs.
        int start = -1;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == ':' && char.IsWhiteSpace(text[i + 1]) &&
                (i == 0 || text[i - 1] != '\\'))
            {
                start = i + 1;
                break;
            }
        }
        if (start < 0)
            throw new InvalidDataException("Slang dependency file has no target separator.");
        HashSet<string> result = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        StringBuilder token = new();
        for (int i = start; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next == '\r' && i + 2 < text.Length && text[i + 2] == '\n') { i += 2; continue; }
                if (next == '\n') { i++; continue; }
                if (char.IsWhiteSpace(next) || next is '\\' or '#' or ':') { token.Append(next); i++; continue; }
            }
            if (char.IsWhiteSpace(ch))
            {
                if (token.Length > 0)
                {
                    result.Add(Path.GetFullPath(token.ToString(), workingDirectory));
                    token.Clear();
                }
            }
            else token.Append(ch);
        }
        if (token.Length > 0)
            result.Add(Path.GetFullPath(token.ToString(), workingDirectory));
        return result;
    }

    private static async Task<(int ExitCode, string Diagnostics)> RunAsync(ProcessStartInfo start, CancellationToken token)
    {
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("slangc failed to start.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        Task<string> stdout = ReadLimitedAsync(process.StandardOutput, MaxDiagnosticCharacters, timeout.Token);
        Task<string> stderr = ReadLimitedAsync(process.StandardError, MaxDiagnosticCharacters, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).ConfigureAwait(false);
            return (process.ExitCode, stdout.Result + Environment.NewLine + stderr.Result);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            catch (OperationCanceledException) { }
            if (token.IsCancellationRequested)
                throw new OperationCanceledException(token);
            if (timeout.IsCancellationRequested)
                throw new TimeoutException("slangc exceeded the 45-second timeout.");
            throw;
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxCharacters, CancellationToken token)
    {
        StringBuilder text = new();
        char[] buffer = new char[4096];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
                return text.ToString();
            if (text.Length + read > maxCharacters)
                throw new InvalidDataException("slangc diagnostics exceed the output limit.");
            text.Append(buffer, 0, read);
        }
    }

    private static async Task<string> ReadUtf8Async(string path, int maxBytes, CancellationToken token)
    {
        await using FileStream stream = File.OpenRead(path);
        if (stream.Length == 0 || stream.Length > maxBytes)
            throw new InvalidDataException($"Slang output '{Path.GetFileName(path)}' is empty or exceeds its size limit.");
        byte[] content = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(content, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Slang output grew during reading.");
        string decoded = StrictUtf8.GetString(content);
        if (decoded.IndexOf('\0') >= 0 || decoded[0] == '\uFEFF')
            throw new InvalidDataException("Slang output contains a BOM or NUL character.");
        return decoded;
    }

    private static string ContainedPath(string root, string path, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A nonempty source path is required.", nameof(path));
        string full = Path.GetFullPath(path, root);
        if (!allowRoot || !PathEquals(root, full))
            _ = Relative(root, full);
        return full;
    }

    private static string Relative(string root, string full)
    {
        string relative = Path.GetRelativePath(root, full);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Slang source and dependencies must remain inside the source root.");
        return relative.Replace('\\', '/');
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RequireDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Slang source directory does not exist: {path}");
        RejectPathComponents(path);
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Slang input or compiler does not exist.", path);
        RejectPathComponents(path);
    }

    private static void RejectPathComponents(string path)
    {
        string current = Path.GetPathRoot(path)!;
        foreach (string part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            RejectReparse(current);
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Slang input or toolchain path is a link or reparse point: {path}");
    }
}

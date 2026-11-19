using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XREngine.UnityScriptHost.Admission;
using XREngine.UnityScriptHost.Contracts;

namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Compiles captured source with the installed SDK compiler and explicit metadata references.</summary>
public sealed partial class UnityScriptCompiler
{
    private const long MaxSourceBytes = 128 * 1024 * 1024;
    private const long MaxReferenceBytes = 64 * 1024 * 1024;
    private const long MaxCompilationBytes = 512 * 1024 * 1024;
    private readonly List<UnityCompilerInput> _inputs = [];
    private readonly List<UnityCompilerDiagnostic> _diagnostics = [];
    private readonly List<UnityCompilationArtifact> _artifacts = [];
    private readonly UnityConversionCoverageBuilder _coverage = new(UnityConversionAssessmentStage.Compilation);
    private long _capturedBytes;
    private bool _used;
    private bool _diagnosticsOnly;

    /// <summary>Writes compiler inputs and a result into a new output directory. Package projects never run.</summary>
    public Task<UnityCompilationResult> CompileAsync(string projectRoot, UnitySourceInventory inventory,
        UnityAssemblyGraph graph, UnityCompilationProfile profile, string profileDirectory, string outputDirectory,
        CancellationToken cancellationToken = default)
        => CompileCoreAsync(projectRoot, inventory, graph, profile, profileDirectory, outputDirectory, false, cancellationToken);

    /// <summary>Collects source errors with available references. This operation never accepts compiler artifacts.</summary>
    public Task<UnityCompilationResult> DiagnoseAsync(string projectRoot, UnitySourceInventory inventory,
        UnityAssemblyGraph graph, UnityCompilationProfile profile, string profileDirectory, string outputDirectory,
        CancellationToken cancellationToken = default)
        => CompileCoreAsync(projectRoot, inventory, graph, profile, profileDirectory, outputDirectory, true, cancellationToken);

    private async Task<UnityCompilationResult> CompileCoreAsync(string projectRoot, UnitySourceInventory inventory,
        UnityAssemblyGraph graph, UnityCompilationProfile profile, string profileDirectory, string outputDirectory,
        bool diagnosticsOnly, CancellationToken cancellationToken)
    {
        if (_used)
            throw new InvalidOperationException("Create a new compiler for each request.");
        _used = true;
        _diagnosticsOnly = diagnosticsOnly;
        string sourceRoot = Path.GetFullPath(projectRoot);
        string output = Path.GetFullPath(outputDirectory);
        ValidateDestination(sourceRoot, output);
        Directory.CreateDirectory(output);
        bool succeeded = false;
        try
        {
            ValidateCompilerProfile(profile);
            if (!graph.Succeeded)
            {
                foreach (UnityAssemblyGraphDiagnostic diagnostic in graph.Diagnostics.Where(static diagnostic => diagnostic.IsError))
                    AddDiagnostic(diagnostic.Code, diagnostic.Assembly, diagnostic.Message, true, diagnostic.Path);
                if (!diagnosticsOnly)
                    return CreateResult(inventory, profile, false);
            }
            if (graph.SourceContentSha256 != inventory.SourceContentSha256 || graph.ProfileName != profile.Name ||
                graph.ProfileSha256 != profile.ComputeSha256())
                throw new InvalidDataException("The graph does not match the source inventory and profile.");
            if (graph.Assemblies.Length == 0)
                throw new InvalidDataException("No source assemblies were selected.");

            var nodes = graph.Assemblies.ToDictionary(static node => node.Name, StringComparer.Ordinal);
            if (nodes.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != nodes.Count)
                throw new InvalidDataException("Source assembly identities collide on case-insensitive filesystems.");
            if (graph.CompilationOrder.Length != nodes.Count || graph.CompilationOrder.Distinct(StringComparer.Ordinal).Count() != nodes.Count)
                throw new InvalidDataException("The graph compilation order is incomplete or duplicated.");
            foreach (UnityAssemblyNode node in nodes.Values)
                ValidateAssemblyName(node.Name);

            string dotnetRoot = GetDotnetRoot();
            string sdkDirectory = Path.Combine(dotnetRoot, "sdk", profile.SdkVersion, "Roslyn", "bincore");
            string compiler = Path.Combine(sdkDirectory, "csc.dll");
            UnitySourceInventoryBuilder.EnsureNoReparsePoints(compiler);
            foreach (string toolFile in Directory.EnumerateFiles(sdkDirectory).Order(StringComparer.Ordinal))
                RecordInput("Compiler", Path.GetFileName(toolFile), ReadBounded(toolFile, MaxReferenceBytes));

            Dictionary<string, string> references = CaptureReferences(sourceRoot, profileDirectory, output, dotnetRoot, profile, inventory);
            foreach (string name in nodes.Keys.Concat(profile.SourceReferenceAssemblies))
                if (references.Keys.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Source assembly '{name}' conflicts with an approved reference identity.");
            foreach (UnityAssemblyNode node in nodes.Values)
                foreach (string required in node.ExternalReferences)
                    if (!HasReference(references, required))
                        AddDiagnostic("UNITYHOST3001", node.Name, CreateMissingReferenceMessage(required, inventory), true);
            if (!diagnosticsOnly && _diagnostics.Any(static diagnostic => diagnostic.IsError))
                return CreateResult(inventory, profile, false);

            var files = inventory.Entries.ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
            var capturedSources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (UnityAssemblyNode node in nodes.Values)
                foreach (string path in node.SourcePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (capturedSources.ContainsKey(path))
                        continue;
                    if (!files.TryGetValue(path, out UnitySourceEntry? entry) || entry.Kind != "Script")
                        throw new InvalidDataException($"Source is not an inventoried C# file: '{path}'.");
                    string source = ResolveSourcePath(sourceRoot, path);
                    byte[] bytes = ReadBounded(source, MaxSourceBytes);
                    string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    if (bytes.LongLength != entry.Length || !string.Equals(digest, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Source changed since inspection: '{path}'.");
                    string relative = "source/" + path;
                    RecordInput("Source", path, bytes);
                    WriteCapturedFile(output, relative, bytes);
                    capturedSources.Add(path, relative);
                }

            Directory.CreateDirectory(Path.Combine(output, "assemblies"));
            Directory.CreateDirectory(Path.Combine(output, "responses"));
            Directory.CreateDirectory(Path.Combine(output, "logs"));
            var compiled = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in graph.CompilationOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!nodes.TryGetValue(name, out UnityAssemblyNode? node))
                    throw new InvalidDataException("The compilation order names an unknown assembly.");
                if (node.SourcePaths.Length == 0)
                    throw new InvalidDataException($"Assembly '{name}' has no source files.");

                string assemblyPath = "assemblies/" + name + ".dll";
                var arguments = CreateCompilerArguments(output, node, profile, references, compiled, capturedSources, assemblyPath);
                string responsePath = Path.Combine(output, "responses", name + ".rsp");
                string response = string.Join(Environment.NewLine, arguments);
                File.WriteAllText(responsePath, response, new UTF8Encoding(false));
                RecordInput("Response", name, Encoding.UTF8.GetBytes(response.Replace(output, "/unity-build", StringComparison.Ordinal)));
                int exitCode = await RunCompilerAsync(dotnetRoot, compiler, responsePath, output, name, cancellationToken).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    AddDiagnostic("UNITYHOST3002", name, $"C# compilation failed with exit code {exitCode}. See logs/{name}.txt for source diagnostics.", true);
                    if (!diagnosticsOnly)
                        return CreateResult(inventory, profile, false);
                    continue;
                }

                string fullAssembly = Path.Combine(output, assemblyPath);
                byte[] assemblyBytes = ReadBounded(fullAssembly, MaxReferenceBytes);
                if (ReadAssemblyName(assemblyBytes) != name)
                    throw new InvalidDataException("The compiler output assembly identity does not match its graph node.");
                RejectSigningInputs(assemblyBytes);
                _artifacts.Add(new(name, assemblyPath, Convert.ToHexStringLower(SHA256.HashData(assemblyBytes)), assemblyBytes.LongLength));
                compiled.Add(name, assemblyPath);
            }
            succeeded = !diagnosticsOnly;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or
            InvalidOperationException or UnauthorizedAccessException or BadImageFormatException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            AddDiagnostic("UNITYHOST3000", string.Empty, exception.Message, true);
        }
        return CreateResult(inventory, profile, succeeded);
    }

    private UnityCompilationResult CreateResult(UnitySourceInventory inventory, UnityCompilationProfile profile, bool succeeded)
    {
        byte[] identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Profile = profile,
            DiagnosticsOnly = _diagnosticsOnly,
            inventory.SourceContentSha256,
            Runtime = Environment.Version.ToString(),
            Inputs = _inputs,
        });
        return new UnityCompilationResult
        {
            Succeeded = succeeded && !_diagnostics.Any(static diagnostic => diagnostic.IsError),
            DiagnosticsOnly = _diagnosticsOnly,
            SourceContentSha256 = inventory.SourceContentSha256,
            CompilationInputSha256 = Convert.ToHexStringLower(SHA256.HashData(identity)),
            ProfileName = profile.Name,
            CompilerRuntimeVersion = Environment.Version.ToString(),
            Artifacts = succeeded ? [.. _artifacts] : [],
            Inputs = [.. _inputs],
            Diagnostics = [.. _diagnostics],
            ImportedAssemblies = [.. _importedAssemblies],
            ConversionCoverage = _coverage.Build(),
        };
    }

    private void AddDiagnostic(string code, string assembly, string message, bool isError, string? sourcePath = null)
    {
        if (isError)
            _coverage.Add(new UnityConversionFeature
            {
                FeatureId = code,
                Status = UnityConversionFeatureStatus.AuthoringBlocked,
                Assembly = assembly,
                SourcePath = sourcePath,
                Functionality = "Compile the selected authoring source before producing a native conversion input.",
                Reason = message
            });
        if (_diagnostics.Count < 4096)
            _diagnostics.Add(new(code, assembly, message.Length > 8192 ? message[..8192] : message, isError));
    }

    private void RecordInput(string kind, string identity, byte[] bytes)
    {
        _capturedBytes += bytes.LongLength;
        if (_capturedBytes > MaxCompilationBytes)
            throw new InvalidDataException("Compilation inputs exceed the byte limit.");
        _inputs.Add(new(kind, identity, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength));
    }

    private static byte[] ReadBounded(string path, long limit)
    {
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit)
            throw new InvalidDataException("A compiler input exceeds its byte limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("A compiler input changed during capture.");
        return bytes;
    }

    private static string ReadAssemblyName(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new InvalidDataException("A reference is not a managed metadata assembly.");
        MetadataReader reader = pe.GetMetadataReader();
        if (!reader.IsAssembly)
            throw new InvalidDataException("A reference is not a managed assembly.");
        return reader.GetString(reader.GetAssemblyDefinition().Name);
    }

    private static void RejectSigningInputs(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        if (!reader.GetAssemblyDefinition().PublicKey.IsNil)
            throw new InvalidDataException("Assembly signing inputs are not supported by the compilation profile.");
    }

    private static string ResolveSourcePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(static part => part is "" or "." or "..") || relative.Length > 2048)
            throw new InvalidDataException("Source paths must be bounded project-relative paths.");
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void WriteCapturedFile(string output, string relative, byte[] bytes)
    {
        string path = ResolveSourcePath(output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    private static void ValidateAssemblyName(string name)
    {
        if (name is null || !Regex.IsMatch(name, @"\A[A-Za-z0-9_][A-Za-z0-9_. -]{0,199}\z", RegexOptions.CultureInvariant) ||
            name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidDataException("An assembly name is not supported as a bounded output identity.");
    }

    private static void ValidateCompilerProfile(UnityCompilationProfile profile)
    {
        if (profile.SchemaVersion != UnityCompilationProfile.CurrentSchemaVersion || string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidDataException("The compilation profile schema or identity is invalid.");
        foreach (string version in new[] { profile.SdkVersion, profile.ReferencePackVersion })
            if (version is null || version.Length > 64 || !Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Compiler and reference-pack versions must be exact numeric versions.");
        if (profile.LanguageVersion is null || !Regex.IsMatch(profile.LanguageVersion, @"\A[0-9]{1,2}(\.0)?\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The language version must be an explicit numeric version.");
        if (profile.References is null || profile.References.Length > 1024)
            throw new InvalidDataException("The reference count exceeds the supported profile limit.");
        if (profile.SourceReferenceAssemblies is null || profile.SourceReferenceAssemblies.Length > 1024 ||
            profile.SourceReferenceAssemblies.Any(static name => string.IsNullOrWhiteSpace(name) || name.Length > 200))
            throw new InvalidDataException("Source reference selections contain invalid or unbounded names.");
    }

    private static void ValidateDestination(string sourceRoot, string output)
    {
        string parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Output parent is missing.");
        string sourceIdentity = UnitySourcePath.GetDirectoryIdentity(sourceRoot);
        string outputIdentity = UnitySourcePath.GetDirectoryIdentity(parent);
        if (outputIdentity.Equals(sourceIdentity, StringComparison.OrdinalIgnoreCase) ||
            outputIdentity.StartsWith(sourceIdentity + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Compiler output must be outside the source project.");
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Compiler output already exists. Choose a new directory.");
        if (output.IndexOfAny(['"', '\r', '\n', ',', '=']) >= 0)
            throw new InvalidDataException("The compiler output path contains unsupported response-file or path-map characters.");
    }
}

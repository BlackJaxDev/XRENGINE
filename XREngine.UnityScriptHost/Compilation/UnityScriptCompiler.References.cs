using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using XREngine.UnityScriptHost.Admission;
using XREngine.UnityScriptHost.Analysis;

namespace XREngine.UnityScriptHost.Compilation;

public sealed partial class UnityScriptCompiler
{
    private readonly HashSet<string> _frameworkReferences = new(StringComparer.Ordinal);
    private readonly List<UnityManagedAssemblyInfo> _importedAssemblies = [];
    private long _importedMetadataCharacters;
    private int _importedMetadataEntries;

    private static string GetDotnetRoot()
        => Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar))?
            .Parent?.Parent?.FullName ?? throw new InvalidDataException("The installed .NET root was not found.");

    private Dictionary<string, string> CaptureReferences(string sourceRoot, string profileDirectory,
        string output, string dotnetRoot, UnityCompilationProfile profile, UnitySourceInventory inventory)
    {
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string pack = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref", profile.ReferencePackVersion, "ref", "net10.0");
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(pack);
        foreach (string path in Directory.EnumerateFiles(pack, "*.dll").Order(StringComparer.Ordinal))
        {
            byte[] bytes = ReadBounded(path, MaxReferenceBytes);
            string name = ReadAssemblyName(bytes);
            Capture(name, bytes, "Framework");
            _frameworkReferences.Add(name);
        }
        if (_frameworkReferences.Count == 0)
            throw new InvalidDataException("The selected framework reference pack is empty.");

        string sourceIdentity = UnitySourcePath.GetDirectoryIdentity(sourceRoot);
        foreach (UnityCompilationReference reference in profile.References)
        {
            if (reference is null || !reference.HasValidRole() ||
                string.IsNullOrWhiteSpace(reference.Path) || reference.Path.Length > 2048 ||
                reference.Sha256 is null || !Regex.IsMatch(reference.Sha256, "\\A[0-9a-fA-F]{64}\\z"))
                throw new InvalidDataException("Approved references need a path, exact hash, identity, and supported role.");
            ValidateAssemblyName(reference.AssemblyName);
            if (reference.Role == "Imported")
            {
                byte[] importedBytes = CaptureImportedReference(sourceRoot, inventory, reference);
                UnityManagedAssemblyInfo info = UnityManagedAssemblyInspector.Inspect(importedBytes);
                if (info.Name != reference.AssemblyName)
                    throw new InvalidDataException("An imported reference does not match its selected assembly identity.");
                Capture(info.Name, importedBytes, reference.Role);
                AddImportedMetadata(info with
                {
                    SourcePath = reference.Path,
                    Sha256 = reference.Sha256.ToLowerInvariant(),
                    Length = importedBytes.LongLength,
                });
                continue;
            }
            string path = Path.GetFullPath(reference.Path, profileDirectory);
            UnitySourceInventoryBuilder.EnsureNoReparsePoints(path);
            string parentIdentity = UnitySourcePath.GetDirectoryIdentity(Path.GetDirectoryName(path)!);
            if (parentIdentity.Equals(sourceIdentity, StringComparison.OrdinalIgnoreCase) ||
                parentIdentity.StartsWith(sourceIdentity + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An inventoried package DLL requires the explicit Imported metadata role.");
            byte[] bytes = ReadBounded(path, MaxReferenceBytes);
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase) ||
                ReadAssemblyName(bytes) != reference.AssemblyName)
                throw new InvalidDataException("A compatibility reference does not match its approved hash and assembly identity.");
            Capture(reference.AssemblyName, bytes, reference.Role);
        }
        return references;

        void Capture(string name, byte[] bytes, string kind)
        {
            ValidateAssemblyName(name);
            string relative = "references/" + name + ".dll";
            if (!fileIdentities.Add(name) || !references.TryAdd(name, relative))
                throw new InvalidDataException($"Reference identity is duplicated: '{name}'.");
            RecordInput(kind, name, bytes);
            WriteCapturedFile(output, relative, bytes);
        }
    }

    private void AddImportedMetadata(UnityManagedAssemblyInfo info)
    {
        long characters = info.Name.Length + info.Version.Length + info.Culture.Length + info.PublicKeyToken.Length +
            info.SourcePath.Length + info.Sha256.Length;
        foreach (UnityManagedAssemblyReference reference in info.References)
            characters += reference.Name.Length + reference.Version.Length + reference.Culture.Length + reference.PublicKeyToken.Length;
        foreach (string name in info.ModuleReferences)
            characters += name.Length;
        _importedMetadataCharacters += characters;
        _importedMetadataEntries += 1 + info.References.Length + info.ModuleReferences.Length;
        if (_importedMetadataCharacters > 8 * 1024 * 1024 || _importedMetadataEntries > 32_768)
            throw new InvalidDataException("Imported metadata reports exceed the total text or entry limit.");
        _importedAssemblies.Add(info);
    }

    private static byte[] CaptureImportedReference(string sourceRoot, UnitySourceInventory inventory,
        UnityCompilationReference reference)
    {
        string relative = reference.Path;
        if (!(relative.StartsWith("Assets/", StringComparison.Ordinal) || relative.StartsWith("Packages/", StringComparison.Ordinal)) ||
            !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(static part => part is "" or "." or ".."))
            throw new InvalidDataException("Imported metadata paths must name a project-relative DLL under Assets or Packages.");

        UnitySourceEntry? entry = inventory.Entries.FirstOrDefault(entry => entry.Path == relative);
        if (entry is null || entry.Kind != "Executable" ||
            !string.Equals(entry.Sha256, reference.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An imported reference must match an inventoried executable and its selected SHA-256.");

        byte[] bytes = ReadBounded(ResolveSourcePath(sourceRoot, relative), MaxReferenceBytes);
        if (bytes.LongLength != entry.Length ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An imported DLL changed since inspection.");
        return bytes;
    }

    private static string ReferenceName(string value)
        => value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    private static bool HasReference(Dictionary<string, string> references, string required)
        => references.ContainsKey(ReferenceName(required));

    private List<string> CreateCompilerArguments(string output, UnityAssemblyNode node, UnityCompilationProfile profile,
        Dictionary<string, string> references, Dictionary<string, string> compiled,
        Dictionary<string, string> sources, string assemblyPath)
    {
        var arguments = new List<string>
        {
            "/nologo", "/nostdlib+", "/target:library", "/deterministic+", "/parallel-", "/utf8output",
            "/optimize+", "/langversion:" + profile.LanguageVersion,
            node.AllowUnsafeCode ? "/unsafe+" : "/unsafe-",
            "/pathmap:" + Quote(output + "=/unity-build"), "/out:" + Quote(assemblyPath),
        };
        foreach (string symbol in node.Defines)
        {
            if (!Regex.IsMatch(symbol, @"\A[A-Za-z_][A-Za-z0-9_]{0,2047}\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("A compiler define is not a supported identifier.");
            arguments.Add("/define:" + symbol);
        }
        var names = new SortedSet<string>(_frameworkReferences, StringComparer.Ordinal);
        foreach (string external in node.ExternalReferences)
            names.Add(ReferenceName(external));
        foreach (string name in names)
        {
            if (references.TryGetValue(name, out string? referencePath))
                arguments.Add("/reference:" + Quote(referencePath));
            else if (!_diagnosticsOnly)
                throw new InvalidDataException("A required metadata reference is absent.");
        }
        foreach (string name in node.References)
        {
            if (!compiled.TryGetValue(name, out string? path))
            {
                if (!_diagnosticsOnly)
                    throw new InvalidDataException("A graph dependency has not compiled before its consumer.");
                AddDiagnostic("UNITYHOST3004", node.Name, $"Dependency '{name}' has no compiler output. Source diagnostics may contain dependent errors.", true);
                continue;
            }
            arguments.Add("/reference:" + Quote(path));
        }
        foreach (string source in node.SourcePaths)
            arguments.Add(Quote(sources[source]));
        return arguments;
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new InvalidDataException("A compiler path contains unsupported response-file characters.");
        return "\"" + value + "\"";
    }
}

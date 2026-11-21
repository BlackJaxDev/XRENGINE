using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using XREngine.UnityScriptHost.Admission;

namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Maps inspected scripts to Unity source assemblies for a selected target.</summary>
public sealed partial class UnityAssemblyGraphBuilder
{
    private const int MaxGraphDiagnostics = 4096;
    private static readonly Regex GuidPattern = new(@"(?im)^guid:\s*([0-9a-f]{32})\s*$", RegexOptions.CultureInvariant);
    private readonly List<UnityAssemblyGraphDiagnostic> _diagnostics = [];

    /// <summary>Builds a dependency graph from inspected metadata and verified GUID files.</summary>
    public UnityAssemblyGraph Build(string projectRoot, UnitySourceInventory inventory, UnityCompilationProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(profile);
        if (_diagnostics.Count != 0)
            throw new InvalidOperationException("Create a new graph builder for each build.");
        if (inventory.SchemaVersion != UnitySourceInventory.CurrentSchemaVersion ||
            profile.SchemaVersion != UnityCompilationProfile.CurrentSchemaVersion)
            throw new InvalidDataException("The inventory or profile schema version is not supported.");
        if (inventory.Entries is null || inventory.AssemblyDefinitions is null ||
            inventory.AssemblyReferences is null || inventory.Packages is null ||
            profile.Defines is null || profile.RootAssemblies is null || profile.References is null ||
            profile.SourceReferenceAssemblies is null || profile.SourceReferenceAssemblies.Length > 1024 ||
            inventory.Entries.Length > 100_000 || inventory.AssemblyDefinitions.Length > 4096 ||
            inventory.AssemblyReferences.Length > 4096 || inventory.Packages.Length > 4096 ||
            profile.Defines.Length > 4096 || profile.RootAssemblies.Length > 4096 ||
            profile.References.Length > 4096)
            throw new InvalidDataException("The assembly graph input exceeds its item limit.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 2048 ||
            string.IsNullOrWhiteSpace(profile.TargetPlatform) || profile.TargetPlatform.Length > 128 ||
            profile.UnityVersion is null || profile.UnityVersion.Length > 128 ||
            profile.SdkVersion is null || profile.SdkVersion.Length > 128 ||
            profile.ReferencePackVersion is null || profile.ReferencePackVersion.Length > 128 ||
            profile.LanguageVersion is null || profile.LanguageVersion.Length > 128)
            throw new InvalidDataException("The compilation profile needs a bounded name and target platform.");
        if (profile.Defines.Any(symbol => symbol is null || !ValidSymbol(symbol)) ||
            profile.RootAssemblies.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 2048) ||
            profile.References.Any(reference => reference is null ||
                string.IsNullOrWhiteSpace(reference.AssemblyName) || reference.AssemblyName.Length > 2048 ||
                reference.Path is null || reference.Path.Length > 2048 ||
                reference.Sha256 is null || reference.Sha256.Length > 128 ||
                reference.Role is null || reference.Role.Length > 128 ||
                !reference.HasValidRole()))
            throw new InvalidDataException("The compilation profile contains an invalid or unbounded value.");

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var entries = inventory.Entries.ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        UnityAssemblyDefinition[] graphDefinitions = [.. inventory.AssemblyDefinitions.Where(
            static definition => !IgnoreSourceDirectory(definition.Path))];
        UnityAssemblyReference[] graphReferences = [.. inventory.AssemblyReferences.Where(
            static reference => !IgnoreSourceDirectory(reference.Path))];
        var definitions = new Dictionary<string, UnityAssemblyDefinition>(StringComparer.Ordinal);
        foreach (UnityAssemblyDefinition definition in graphDefinitions)
        {
            ValidateDefinitionOptions(root, definition, entries);
            if (!definitions.TryAdd(definition.Name, definition))
                Add("UNITYHOST2001", definition.Name, definition.Path, "Assembly names must be unique.");
            if (definition.Name is "Assembly-CSharp" or "Assembly-CSharp-Editor" or
                "Assembly-CSharp-firstpass" or "Assembly-CSharp-Editor-firstpass")
                Add("UNITYHOST2014", definition.Name, definition.Path, "An assembly definition cannot use a predefined assembly name.");
        }

        var guidNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (UnityAssemblyDefinition definition in graphDefinitions)
        {
            string? guid = ReadVerifiedGuid(root, definition.Path + ".meta", entries, definition.Name);
            if (guid is not null && !guidNames.TryAdd(guid, definition.Name))
                Add("UNITYHOST2002", definition.Name, definition.Path, "Assembly definition GUID is not unique.");
        }

        var referenceByDirectory = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (UnityAssemblyReference reference in graphReferences)
        {
            string name = ResolveReference(reference.Reference, definitions, guidNames);
            if (name.Length == 0)
                Add("UNITYHOST2003", string.Empty, reference.Path, $"Assembly reference '{reference.Reference}' does not name an inspected assembly.");
            string directory = Parent(reference.Path);
            if (!referenceByDirectory.TryAdd(directory, name))
                Add("UNITYHOST2004", name, reference.Path, "A directory has more than one assembly reference.");
            if (graphDefinitions.Any(definition => Parent(definition.Path) == directory))
                Add("UNITYHOST2015", name, reference.Path, "An assembly definition and reference cannot own the same directory.");
        }

        var selected = new Dictionary<string, NodeState>(StringComparer.Ordinal);
        var baseDefines = new HashSet<string>(profile.Defines, StringComparer.Ordinal);
        bool optionalAssemblyReferences = SupportsOptionalAssemblyReferences(profile.UnityVersion);
        foreach (UnityAssemblyDefinition definition in definitions.Values)
        {
            if (!Eligible(definition, profile, inventory, baseDefines, out string[] defines))
                continue;
            selected.Add(definition.Name, new NodeState(definition.Name, definition.Path, definition, defines));
        }

        foreach (UnitySourceEntry entry in inventory.Entries)
        {
            if (entry.Kind != "Script" || IgnoreSourceDirectory(entry.Path))
                continue;
            string? owner = FindOwner(entry.Path, graphDefinitions, referenceByDirectory);
            if (owner is not null)
            {
                if (selected.TryGetValue(owner, out NodeState? node))
                    node.Sources.Add(entry.Path);
                continue;
            }
            if (entry.Path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                Add("UNITYHOST2005", string.Empty, entry.Path,
                    "Package scripts without an assembly definition or reference have no supported assembly policy.",
                    profile.RootAssemblies.Length == 0);
                continue;
            }
            if (!entry.Path.StartsWith("Assets/", StringComparison.Ordinal))
                continue;
            string defaultName = DefaultAssembly(entry.Path, profile.TargetPlatform);
            if (defaultName.Length == 0)
                continue;
            if (!selected.TryGetValue(defaultName, out NodeState? defaultNode))
            {
                defaultNode = new NodeState(defaultName, string.Empty, null, [.. baseDefines]);
                selected.Add(defaultName, defaultNode);
            }
            defaultNode.Sources.Add(entry.Path);
        }

        Dictionary<string, string> sourceReferences = ValidateSourceReferences(profile, selected);
        foreach (NodeState node in selected.Values)
        {
            if (node.Definition is not UnityAssemblyDefinition definition)
                continue;
            foreach (string declared in definition.References)
            {
                if (string.IsNullOrWhiteSpace(declared))
                {
                    Add("UNITYHOST2006", node.Name, node.DefinitionPath, "Assembly dependency has no name or GUID.");
                    continue;
                }
                string name = ResolveReference(declared, definitions, guidNames);
                if (name.Length != 0 && selected.ContainsKey(name))
                    node.Dependencies.Add(name);
                else if (name.Length == 0 && !declared.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase) &&
                    profile.References.Any(reference => reference.AssemblyName == declared))
                    node.External.Add(declared);
                else if (optionalAssemblyReferences && name.Length == 0 &&
                    !declared.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                    Add("UNITYHOST2019", node.Name, node.DefinitionPath,
                        $"Optional assembly dependency '{declared}' is absent from the inspected project.", false);
                else
                    Add("UNITYHOST2006", node.Name, node.DefinitionPath,
                        $"Assembly dependency '{declared}' is unresolved or excluded for this target.");
            }
            if (definition.OverrideReferences)
                foreach (string external in definition.PrecompiledReferences)
                    AddPrecompiledReference(node, external, sourceReferences, selected);
        }

        // Unity adds auto-referenced custom assemblies to predefined assemblies only.
        foreach (NodeState node in selected.Values)
        {
            if (node.Definition is not null)
                continue;
            foreach (NodeState custom in selected.Values)
                if (custom.Definition is UnityAssemblyDefinition customDefinition &&
                    customDefinition.AutoReferenced && !IsTestAssembly(customDefinition) &&
                    (IsEditor(node, profile.TargetPlatform) || !IsEditor(custom, profile.TargetPlatform)))
                    node.Dependencies.Add(custom.Name);
            if (node.Name is "Assembly-CSharp" or "Assembly-CSharp-Editor")
            {
                AddPredefinedDependency(node, selected, "Assembly-CSharp-firstpass");
                if (node.Name == "Assembly-CSharp-Editor")
                {
                    AddPredefinedDependency(node, selected, "Assembly-CSharp");
                    AddPredefinedDependency(node, selected, "Assembly-CSharp-Editor-firstpass");
                }
            }
            else if (node.Name == "Assembly-CSharp-Editor-firstpass")
                AddPredefinedDependency(node, selected, "Assembly-CSharp-firstpass");
        }

        foreach (NodeState node in selected.Values)
        {
            if (node.Definition is not null && IsTestAssembly(node.Definition))
            {
                node.External.Add("nunit.framework.dll");
                node.External.Add("UnityEngine.TestRunner.dll");
            }
            foreach (UnityCompilationReference reference in profile.References)
            {
                if (reference.Role == "Platform")
                {
                    node.External.Add(reference.AssemblyName);
                    continue;
                }
                if (reference.Role is "Compatibility" or "Imported" && node.Definition?.OverrideReferences != true)
                    node.External.Add(reference.AssemblyName);
                if (node.Definition?.NoEngineReferences == true)
                    continue;
                if (reference.Role == "UnityEngine" ||
                    (reference.Role == "UnityEditor" && profile.TargetPlatform == "Editor"))
                    node.External.Add(reference.AssemblyName);
            }
            if (node.Definition?.NoEngineReferences == true)
                continue;
            node.External.Add("UnityEngine");
            if (profile.TargetPlatform == "Editor")
                node.External.Add("UnityEditor");
        }

        var active = new HashSet<string>(StringComparer.Ordinal);
        if (profile.RootAssemblies.Length == 0)
            active.UnionWith(selected.Keys);
        else
            foreach (string name in profile.RootAssemblies)
                AddClosure(name, selected, active, 0);

        var ordered = new List<string>();
        var visit = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in active.Order(StringComparer.Ordinal))
            Visit(name, selected, active, visit, ordered, 0);

        UnityAssemblyNode[] nodes = [.. ordered.Select(name =>
        {
            NodeState state = selected[name];
            return new UnityAssemblyNode
            {
                Name = name,
                DefinitionPath = state.DefinitionPath,
                SourcePaths = [.. state.Sources.Order(StringComparer.Ordinal)],
                References = [.. state.Dependencies.Where(active.Contains).Order(StringComparer.Ordinal)],
                ExternalReferences = [.. state.External.Order(StringComparer.Ordinal)],
                Defines = [.. state.Defines.Order(StringComparer.Ordinal)],
                AllowUnsafeCode = state.Definition?.AllowUnsafeCode ?? false,
                IsEditorOnly = IsEditor(state, profile.TargetPlatform),
                IsTestAssembly = state.Definition is not null && IsTestAssembly(state.Definition),
                NoEngineReferences = state.Definition?.NoEngineReferences ?? false,
            };
        })];
        return new UnityAssemblyGraph
        {
            SourceContentSha256 = inventory.SourceContentSha256,
            ProfileName = profile.Name,
            ProfileSha256 = profile.ComputeSha256(),
            CompilationOrder = [.. ordered],
            Assemblies = nodes,
            Diagnostics = [.. _diagnostics.Where(diagnostic => diagnostic.Code is not ("UNITYHOST2006" or "UNITYHOST2019" or "UNITYHOST2021" or "UNITYHOST2022") ||
                active.Contains(diagnostic.Assembly))],
        };
    }

    private static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static bool Under(string path, string directory)
        => directory.Length != 0 && path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static bool IgnoreSourceDirectory(string path)
        => UnitySourcePath.IsIgnoredCompilationPath(path);

    private static string? FindOwner(string path, UnityAssemblyDefinition[] definitions, Dictionary<string, string> references)
    {
        string? owner = null;
        int depth = -1;
        foreach (UnityAssemblyDefinition definition in definitions)
        {
            string directory = Parent(definition.Path);
            if (Under(path, directory) && directory.Length > depth)
            {
                owner = definition.Name;
                depth = directory.Length;
            }
        }
        foreach ((string directory, string name) in references)
            if (Under(path, directory) && directory.Length > depth)
            {
                owner = name;
                depth = directory.Length;
            }
        return owner;
    }

    private static string ResolveReference(string value, Dictionary<string, UnityAssemblyDefinition> definitions,
        Dictionary<string, string> guidNames)
    {
        if (value.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            return guidNames.GetValueOrDefault(value[5..]) ?? string.Empty;
        return definitions.ContainsKey(value) ? value : string.Empty;
    }

    private static bool SupportsOptionalAssemblyReferences(string unityVersion)
    {
        Match release = Regex.Match(unityVersion, @"^(\d{4})\.(\d{1,2})\.\d+[abfp]\d+$", RegexOptions.CultureInvariant);
        if (!release.Success || !int.TryParse(release.Groups[1].Value, out int year) ||
            !int.TryParse(release.Groups[2].Value, out int minor))
            return false;
        return year > 2019 || year == 2019 && minor >= 1;
    }

    private bool Eligible(UnityAssemblyDefinition definition, UnityCompilationProfile profile,
        UnitySourceInventory inventory, HashSet<string> baseDefines, out string[] defines)
    {
        var symbols = new HashSet<string>(baseDefines, StringComparer.Ordinal);
        if (IsTestAssembly(definition) && !profile.IncludeTestAssemblies)
        {
            defines = [.. symbols];
            return false;
        }
        if (definition.IncludePlatforms.Length != 0 && definition.ExcludePlatforms.Length != 0)
            Add("UNITYHOST2010", definition.Name, definition.Path, "Include and exclude platforms cannot both have values.");
        foreach (UnityAssemblyVersionDefine rule in definition.VersionDefines)
        {
            if (!ValidSymbol(rule.Define) || !TryParseRange(rule.Expression, out VersionRange range))
            {
                Add("UNITYHOST2011", definition.Name, definition.Path, $"Version define '{rule.Name}' has an unsupported symbol or version expression.");
                continue;
            }
            string? version = rule.Name == "Unity" ? profile.UnityVersion :
                inventory.Packages.FirstOrDefault(package => package.IsEmbeddedPackage && package.Name == rule.Name)?.Version;
            if (string.IsNullOrEmpty(version))
                continue;
            if (rule.Expression.Length == 0)
            {
                symbols.Add(rule.Define);
                continue;
            }
            string versionText = version;
            if (rule.Name == "Unity")
            {
                Match release = Regex.Match(version, @"^(\d+\.\d+\.\d+)f\d+$", RegexOptions.CultureInvariant);
                if (release.Success)
                    versionText = release.Groups[1].Value;
            }
            if (!TryParseVersion(versionText, out VersionNumber number))
            {
                Add("UNITYHOST2012", definition.Name, definition.Path, $"Version '{version}' for '{rule.Name}' is not supported.");
                continue;
            }
            if (range.Contains(number))
                symbols.Add(rule.Define);
        }
        defines = [.. symbols];
        if (definition.IncludePlatforms.Length != 0 &&
            !definition.IncludePlatforms.Contains(profile.TargetPlatform, StringComparer.OrdinalIgnoreCase))
            return false;
        if (definition.ExcludePlatforms.Contains(profile.TargetPlatform, StringComparer.OrdinalIgnoreCase))
            return false;
        foreach (string constraint in definition.DefineConstraints)
        {
            string[] alternatives = constraint.Split("||", StringSplitOptions.TrimEntries);
            if (alternatives.Length == 0)
            {
                Add("UNITYHOST2013", definition.Name, definition.Path, "A define constraint is empty.");
                return false;
            }
            bool met = false;
            foreach (string alternative in alternatives)
            {
                bool negated = alternative.StartsWith('!');
                string symbol = negated ? alternative[1..] : alternative;
                if (!ValidSymbol(symbol))
                {
                    Add("UNITYHOST2013", definition.Name, definition.Path, $"Define constraint '{constraint}' has unsupported syntax.");
                    return false;
                }
                met |= negated ? !symbols.Contains(symbol) : symbols.Contains(symbol);
            }
            if (!met)
                return false;
        }
        return true;
    }

    private static bool ValidSymbol(string value)
        => value.Length is > 0 and <= 2048 && (char.IsLetter(value[0]) || value[0] == '_') &&
            value.All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static bool IsTestAssembly(UnityAssemblyDefinition definition)
        => definition.OptionalUnityReferences.Contains("TestAssemblies", StringComparer.Ordinal);

    private static bool TryParseRange(string expression, out VersionRange range)
    {
        range = default;
        if (expression is null || expression.Length > 2048 || expression.Any(char.IsWhiteSpace))
            return false;
        string text = expression;
        if (text.Length == 0)
            return true;
        if (TryParseVersion(text, out VersionNumber exact))
        {
            range = new(exact, null, true, false);
            return true;
        }
        if (text.Length >= 3 && text[0] == '[' && text[^1] == ']' &&
            TryParseVersion(text[1..^1], out exact))
        {
            range = new(exact, exact, true, true);
            return true;
        }
        if (text.Length < 3 || text[0] is not ('[' or '(') || text[^1] is not (']' or ')'))
            return false;
        string body = text[1..^1];
        int separator = body.IndexOf(',');
        if (separator < 0 || body.LastIndexOf(',') != separator)
            return false;
        string low = body[..separator].Trim();
        string high = body[(separator + 1)..].Trim();
        if (low.Length == 0 && high.Length == 0)
            return false;
        VersionNumber? lower = null;
        VersionNumber? upper = null;
        if (low.Length != 0)
        {
            if (!TryParseVersion(low, out VersionNumber parsed))
                return false;
            lower = parsed;
        }
        if (high.Length != 0)
        {
            if (!TryParseVersion(high, out VersionNumber parsed))
                return false;
            upper = parsed;
        }
        range = new(lower, upper, text[0] == '[', text[^1] == ']');
        return true;
    }

    private static bool TryParseVersion(string text, out VersionNumber version)
    {
        version = default;
        Match match = Regex.Match(text,
            @"^(\d{1,9})(?:\.(\d{1,9}))?(?:\.(\d{1,9}))?(?:[-.]([0-9A-Za-z][0-9A-Za-z.-]{0,63}))?$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int major) ||
            !int.TryParse(match.Groups[2].Success ? match.Groups[2].Value : "0", out int minor) ||
            !int.TryParse(match.Groups[3].Success ? match.Groups[3].Value : "0", out int patch))
            return false;
        version = new(major, minor, patch, match.Groups[4].Value);
        return true;
    }

    private readonly record struct VersionNumber(int Major, int Minor, int Patch, string Suffix) : IComparable<VersionNumber>
    {
        public int CompareTo(VersionNumber other)
        {
            int result = Major.CompareTo(other.Major);
            if (result != 0)
                return result;
            result = Minor.CompareTo(other.Minor);
            if (result != 0)
                return result;
            result = Patch.CompareTo(other.Patch);
            if (result != 0)
                return result;
            if (Suffix.Length == 0)
                return other.Suffix.Length == 0 ? 0 : 1;
            if (other.Suffix.Length == 0)
                return -1;
            string[] parts = Suffix.Split('.');
            string[] otherParts = other.Suffix.Split('.');
            for (int index = 0; index < Math.Min(parts.Length, otherParts.Length); index++)
            {
                bool leftNumeric = int.TryParse(parts[index], out int left);
                bool rightNumeric = int.TryParse(otherParts[index], out int right);
                if (leftNumeric && rightNumeric)
                    result = left.CompareTo(right);
                else if (leftNumeric != rightNumeric)
                    result = leftNumeric ? -1 : 1;
                else
                    result = StringComparer.Ordinal.Compare(parts[index], otherParts[index]);
                if (result != 0)
                    return result;
            }
            return parts.Length.CompareTo(otherParts.Length);
        }
    }

    private readonly record struct VersionRange(VersionNumber? Lower, VersionNumber? Upper,
        bool IncludeLower, bool IncludeUpper)
    {
        public bool Contains(VersionNumber value)
        {
            if (Lower is VersionNumber lower && (value.CompareTo(lower) < 0 ||
                (!IncludeLower && value.CompareTo(lower) == 0)))
                return false;
            if (Upper is VersionNumber upper && (value.CompareTo(upper) > 0 ||
                (!IncludeUpper && value.CompareTo(upper) == 0)))
                return false;
            return true;
        }
    }

    private string? ReadVerifiedGuid(string root, string path, Dictionary<string, UnitySourceEntry> entries, string assembly)
    {
        if (!entries.TryGetValue(path, out UnitySourceEntry? entry))
            return null;
        if (entry.Kind != "Metadata" || entry.Length > 4 * 1024 * 1024)
        {
            Add("UNITYHOST2007", assembly, path, "Assembly GUID metadata is not a bounded metadata file.");
            return null;
        }
        byte[] bytes = ReadVerifiedMetadata(root, path, entry);
        Match match = GuidPattern.Match(System.Text.Encoding.UTF8.GetString(bytes));
        return match.Success ? match.Groups[1].Value : null;
    }

    private void ValidateDefinitionOptions(string root, UnityAssemblyDefinition definition,
        Dictionary<string, UnitySourceEntry> entries)
    {
        if (!entries.TryGetValue(definition.Path, out UnitySourceEntry? entry) || entry.Kind != "AssemblyDefinition")
            throw new InvalidDataException($"Assembly definition is absent from the inventory: '{definition.Path}'.");
        byte[] bytes = ReadVerifiedMetadata(root, definition.Path, entry);
        int bomLength = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(bomLength));
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name == "optionalUnityReferences")
                continue;
            if (property.Name is "name" or "rootNamespace" or "references" or "precompiledReferences" or
                "includePlatforms" or "excludePlatforms" or "defineConstraints" or "versionDefines" or
                "overrideReferences" or "allowUnsafeCode" or "autoReferenced" or "noEngineReferences" or "useGUIDs")
                continue;
            Add("UNITYHOST2016", definition.Name, definition.Path, $"Assembly option '{property.Name}' has no supported graph policy.");
        }
        foreach (string reference in definition.OptionalUnityReferences)
            if (reference != "TestAssemblies")
                Add("UNITYHOST2018", definition.Name, definition.Path,
                    $"Optional Unity reference '{reference}' has no supported graph policy.");
    }

    private static byte[] ReadVerifiedMetadata(string root, string path, UnitySourceEntry entry)
    {
        if (entry.Length > 4 * 1024 * 1024)
            throw new InvalidDataException($"Assembly metadata exceeds the graph limit: '{path}'.");
        string fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Assembly metadata path leaves the project.");
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(fullPath);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != entry.Length || stream.Length > 4 * 1024 * 1024)
            throw new InvalidDataException($"Assembly metadata changed after inspection: '{path}'.");
        byte[] bytes = new byte[checked((int)entry.Length)];
        stream.ReadExactly(bytes);
        if (stream.Length != entry.Length)
            throw new InvalidDataException($"Assembly metadata changed after inspection: '{path}'.");
        if (bytes.LongLength != entry.Length || !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Assembly metadata changed after inspection: '{path}'.");
        return bytes;
    }

    private static string DefaultAssembly(string path, string platform)
    {
        bool editor = path.Split('/').Contains("Editor", StringComparer.OrdinalIgnoreCase);
        if (editor && platform != "Editor")
            return string.Empty;
        bool firstpass = path.StartsWith("Assets/Plugins/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Assets/Standard Assets/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Assets/Pro Standard Assets/", StringComparison.OrdinalIgnoreCase);
        return "Assembly-CSharp" + (editor ? "-Editor" : string.Empty) + (firstpass ? "-firstpass" : string.Empty);
    }

    private static bool IsEditor(NodeState node, string platform)
        => node.Definition is null
            ? node.Name is "Assembly-CSharp-Editor" or "Assembly-CSharp-Editor-firstpass"
            : platform == "Editor" && node.Definition.IncludePlatforms.Length != 0 &&
                node.Definition.IncludePlatforms.All(name => name.Equals("Editor", StringComparison.OrdinalIgnoreCase));

    private void AddPredefinedDependency(NodeState node, Dictionary<string, NodeState> selected, string name)
    {
        if (selected.ContainsKey(name) && node.Name != name)
            node.Dependencies.Add(name);
    }

    private void AddClosure(string name, Dictionary<string, NodeState> selected, HashSet<string> active, int depth)
    {
        if (depth > 128)
        {
            Add("UNITYHOST2017", name, string.Empty, "Assembly dependency depth exceeds the graph limit.");
            return;
        }
        if (!selected.TryGetValue(name, out NodeState? node))
        {
            Add("UNITYHOST2008", name, string.Empty, "Selected root assembly is unknown or excluded for this target.");
            return;
        }
        if (!active.Add(name))
            return;
        foreach (string dependency in node.Dependencies)
            AddClosure(dependency, selected, active, depth + 1);
    }

    private void Visit(string name, Dictionary<string, NodeState> selected, HashSet<string> active,
        Dictionary<string, int> visit, List<string> ordered, int depth)
    {
        if (depth > 128)
        {
            Add("UNITYHOST2017", name, selected[name].DefinitionPath, "Assembly dependency depth exceeds the graph limit.");
            return;
        }
        if (visit.GetValueOrDefault(name) == 2)
            return;
        if (visit.GetValueOrDefault(name) == 1)
        {
            Add("UNITYHOST2009", name, selected[name].DefinitionPath, "Assembly dependencies contain a cycle.");
            return;
        }
        visit[name] = 1;
        foreach (string dependency in selected[name].Dependencies.Where(active.Contains).Order(StringComparer.Ordinal))
            Visit(dependency, selected, active, visit, ordered, depth + 1);
        visit[name] = 2;
        ordered.Add(name);
    }

    private void Add(string code, string assembly, string path, string message, bool isError = true)
    {
        if (_diagnostics.Count >= MaxGraphDiagnostics)
            throw new InvalidDataException("The assembly graph diagnostic limit was exceeded.");
        _diagnostics.Add(new(code, assembly, path, message, isError));
    }

    private sealed class NodeState(string name, string definitionPath, UnityAssemblyDefinition? definition, string[] defines)
    {
        public string Name { get; } = name;
        public string DefinitionPath { get; } = definitionPath;
        public UnityAssemblyDefinition? Definition { get; } = definition;
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Dependencies { get; } = new(StringComparer.Ordinal);
        public HashSet<string> External { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Defines { get; } = new(defines, StringComparer.Ordinal);
    }
}

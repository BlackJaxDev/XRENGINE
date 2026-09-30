using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length != 2)
    throw new ArgumentException("Expected repository root and report directory.");

string root = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
bool Excluded(string path)
{
    string relative = Relative(path);
    return relative.StartsWith("Build/", StringComparison.OrdinalIgnoreCase) ||
        relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
        relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase);
}
IEnumerable<string> RepositoryFiles(string pattern, string? start = null)
{
    var pending = new Stack<string>();
    pending.Push(start ?? root);
    while (pending.Count > 0)
    {
        string directory = pending.Pop();
        foreach (string file in Directory.EnumerateFiles(directory, pattern))
            yield return file;
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name.Equals("Build", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                continue;
            pending.Push(child);
        }
    }
}

var categories = new (string Name, string Pattern)[]
{
    ("PhysX", @"\b(?:MagicPhysX|Physx[A-Z]|PhysX)"),
    ("Jolt", @"\b(?:JoltPhysics|Jolt[A-Z])"),
    ("Jitter", @"\bJitter2?\b"),
    ("OpenAL", @"\b(?:OpenAL|ALContext|ALSource)\b"),
    ("NAudio", @"\bNAudio\b"),
    ("Steam Audio", @"\b(?:SteamAudio|Phonon|IPL[A-Z])"),
    ("FFmpeg", @"\b(?:FFmpeg|ffmpeg|AVCodec|AVFormat)"),
    ("Magick", @"\b(?:ImageMagick|Magick[A-Z]|MagickNET)"),
    ("ImGui", @"\b(?:ImGuiNET|ImGui[A-Z]|ImGui\.)"),
    ("Window", @"\b(?:Silk\.NET\.(?:Windowing|GLFW|SDL)|IWindow|GlfwWindow|SdlWindow)"),
    ("Silk input", @"\bSilk\.NET\.(?:Input|XInput)\b"),
    ("OpenXR", @"\b(?:OpenXR|OpenXr|XrInstance)"),
    ("OpenVR", @"\b(?:OpenVR|OpenVr|Valve\.VR)"),
    ("DirectStorage", @"\bDirectStorage\b"),
    ("CoACD", @"\b(?:CoACD|Coacd)\b"),
    ("Sockets", @"\b(?:System\.Net\.Sockets|Socket(?:AsyncEventArgs|Exception)?|UdpClient|TcpClient|TcpListener)\b"),
    ("Ultralight", @"\bUltralight(?:Net)?\b"),
    ("Skia/Svg", @"\b(?:SkiaSharp|Svg\.Skia|SKBitmap|SKCanvas)\b"),
    ("Rive", @"\bRive(?:Sharp)?\b"),
    ("FreeType", @"\b(?:SharpFont|FreeType)\b"),
    ("Management", @"\bSystem\.Management\b"),
    ("CUDA", @"\b(?:CUDA|Cuda|NvComp)\b"),
    ("Meshoptimizer", @"\bMeshoptimizer(?:\.NET)?\b"),
    ("XInput", @"\b(?:XInput|DXNET\.XInput)\b"),
    ("Sync waits", @"\b(?:\.Wait\s*\(|\.Result\b|GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(|WaitOne\s*\(|Thread\.Sleep\s*\()"),
    ("new Thread", @"\bnew\s+(?:System\.Threading\.)?Thread\s*\(")
};
var patterns = categories.ToDictionary(x => x.Name,
    x => new Regex(x.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant));

var projectFiles = RepositoryFiles("*.csproj")
    .Where(x => !Excluded(x)).OrderBy(Relative, StringComparer.Ordinal).ToArray();
var projectDirs = projectFiles.ToDictionary(x => x, Path.GetDirectoryName);
var counts = new List<CountRow>();
var inventory = new List<InventoryRow>();
var typeDeclarations = new Dictionary<(string Category, string FullName), PublicType>();
foreach (string project in projectFiles)
{
    Console.WriteLine($"Scanning {Relative(project)}...");
    string projectDir = projectDirs[project]!;
    string projectName = Path.GetFileNameWithoutExtension(project);
    string[] nestedProjectDirs = projectDirs.Where(x => x.Key != project &&
        x.Value!.StartsWith(projectDir + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)).Select(x => x.Value!).ToArray();
    var files = RepositoryFiles("*.cs", projectDir)
        .Where(x => !Excluded(x) && !nestedProjectDirs.Any(dir =>
            x.StartsWith(dir + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
        .OrderBy(Relative, StringComparer.Ordinal).ToArray();
    var totals = categories.ToDictionary(x => x.Name, _ => 0);
    foreach (string file in files)
    {
        string source = File.ReadAllText(file);
        string path = Relative(file);
        var matches = categories.Where(x => patterns[x.Name].IsMatch(source))
            .Select(x => x.Name).ToArray();
        foreach (string match in matches)
            totals[match]++;
        inventory.Add(new InventoryRow(projectName, path, matches));
        if (matches.Length == 0) continue;

        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        foreach (MemberDeclarationSyntax declaration in tree.GetRoot()
            .DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(x => x is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
        {
            var modifiers = declaration switch
            {
                BaseTypeDeclarationSyntax type => type.Modifiers,
                DelegateDeclarationSyntax delegateType => delegateType.Modifiers,
                _ => default
            };
            if (!modifiers.Any(x => x.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword))
                continue;
            string simpleName = declaration switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                DelegateDeclarationSyntax delegateType => delegateType.Identifier.ValueText,
                _ => throw new InvalidOperationException()
            };
            int arity = declaration switch
            {
                TypeDeclarationSyntax type => type.TypeParameterList?.Parameters.Count ?? 0,
                DelegateDeclarationSyntax delegateType => delegateType.TypeParameterList?.Parameters.Count ?? 0,
                _ => 0
            };
            var namespaceNames = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse().Select(x => x.Name.ToString());
            var containerNames = declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                .Reverse().Select(x => x.Identifier.ValueText +
                    (x is TypeDeclarationSyntax generic && generic.TypeParameterList is { } parameters
                        ? "`" + parameters.Parameters.Count : ""));
            string namespaceName = string.Join(".", namespaceNames);
            string typeName = string.Join("+", containerNames.Append(simpleName +
                (arity == 0 ? "" : "`" + arity)));
            string fullName = namespaceName.Length == 0 ? typeName : namespaceName + "." + typeName;
            foreach (string category in matches)
            {
                var key = (category, fullName);
                if (!typeDeclarations.TryGetValue(key, out PublicType? existing))
                    typeDeclarations.Add(key, new PublicType(category, fullName,
                        simpleName, projectName, [path]));
                else if (!existing.Declarations.Contains(path, StringComparer.Ordinal))
                    existing.Declarations.Add(path);
            }
        }
    }
    foreach (var category in categories)
        counts.Add(new CountRow(projectName, category.Name, totals[category.Name]));
}

var packages = new List<object>();
Console.WriteLine("Reading project package and native items...");
var nativeAssets = new List<object>();
foreach (string project in projectFiles)
{
    XDocument xml = XDocument.Load(project);
    string name = Path.GetFileNameWithoutExtension(project);
    foreach (XElement item in xml.Descendants().Where(x => x.Name.LocalName == "PackageReference"))
        packages.Add(new { project = name, path = Relative(project),
            package = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update") ?? "",
            version = (string?)item.Attribute("Version") ??
                item.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value ?? "(central/inherited)" });
    foreach (XElement item in xml.Descendants().Where(x =>
        x.Attribute("Include") is not null || x.Attribute("Update") is not null))
    {
        string value = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update") ?? "";
        if (item.Name.LocalName == "PackageReference") continue;
        if (!Regex.IsMatch(value, @"(?i)(?:runtimes[/\\]|native[/\\]|\.(?:dll|so|dylib|a|lib|pdb|license)(?:$|[;*]))"))
            continue;
        nativeAssets.Add(new { project = name, path = Relative(project),
            item = item.Name.LocalName, value,
            copyToOutput = (string?)item.Element("CopyToOutputDirectory") ?? "",
            copyToPublish = (string?)item.Element("CopyToPublishDirectory") ?? "" });
    }
}

var persistedFiles = RepositoryFiles("*")
    .Where(x => !Excluded(x) && (Relative(x).StartsWith("Assets/", StringComparison.Ordinal) ||
        Relative(x).StartsWith("Samples/", StringComparison.Ordinal) ||
        Relative(x).StartsWith("XREngine.UnitTests/", StringComparison.Ordinal) ||
        x.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ||
        x.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)))
    .Where(x => new FileInfo(x).Length <= 8_000_000 &&
        Regex.IsMatch(x, @"(?i)\.(?:asset|json|jsonc|cs|xml|yaml|yml|txt|xrproj)$"))
    .OrderBy(Relative, StringComparer.Ordinal).ToArray();
var evidence = new List<EvidenceRow>();
Console.WriteLine($"Scanning {persistedFiles.Length} persisted/test files for {typeDeclarations.Count} type candidates...");
var typeBySimpleName = typeDeclarations.Values.GroupBy(x => x.SimpleName,
    StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
foreach (string file in persistedFiles)
{
    string content;
    try { content = File.ReadAllText(file); }
    catch (DecoderFallbackException) { continue; }
    bool persisted = !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    var mentionedNames = Regex.Matches(content, @"\b[A-Za-z_]\w*\b")
        .Select(x => x.Value).Where(typeBySimpleName.ContainsKey)
        .Distinct(StringComparer.Ordinal);
    foreach (string mentionedName in mentionedNames)
    {
        foreach (PublicType type in typeBySimpleName[mentionedName])
        {
        foreach ((string kind, string name) in new[] { ("full", type.FullName), ("simple", type.SimpleName) })
        {
            int index = content.IndexOf(name, StringComparison.Ordinal);
            if (index < 0) continue;
            int line = 1;
            for (int i = 0; i < index; i++) if (content[i] == '\n') line++;
            evidence.Add(new EvidenceRow(type.Category, type.FullName, kind,
                Relative(file), line, persisted));
        }
        }
    }
}

var options = new JsonSerializerOptions { WriteIndented = true };
var persistedFullNames = evidence.Where(x => x.PersistedData && x.Match == "full")
    .Select(x => (x.Category, x.FullName)).ToHashSet();
var persistedSimpleNames = evidence.Where(x => x.PersistedData && x.Match == "simple")
    .Select(x => (x.Category, x.FullName)).ToHashSet();
void WriteJson(string name, object data) => File.WriteAllText(Path.Combine(output, name),
    JsonSerializer.Serialize(data, options) + Environment.NewLine);
WriteJson("native-subsystem-inventory.json", new { methodology =
    "Physical project directories; per-file lexical API matches (one count per category per file). Package/native items are literal project XML entries, not evaluated MSBuild items.",
    counts, files = inventory, packages, nativeAssets });
WriteJson("native-subsystem-identity.json", new { methodology =
    "Roslyn syntax declarations with explicit public modifier in files with a lexical subsystem match. Partial declarations merge by category and CLR-style full namespace/nested type metadata name. Occurrences are exact case-sensitive text in Assets, Samples, unit tests, .asset and .jsonc; hits are candidate identity evidence, not proof of serialization or runtime binding. Conditional compilation, generated sources, imported Compile items, aliases and semantic ownership are not evaluated.",
    publicTypes = typeDeclarations.Values.OrderBy(x => x.Category).ThenBy(x => x.FullName)
        .Select(x => new { x.Category, x.FullName, x.SimpleName, x.Project, x.Declarations,
            serializationEvidence = persistedFullNames.Contains((x.Category, x.FullName))
                ? "persisted full-name occurrence"
                : persistedSimpleNames.Contains((x.Category, x.FullName))
                    ? "persisted simple-name candidate" : "not observed" }), evidence });

static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
var csv = new StringBuilder("project,category,file_count\n");
foreach (CountRow row in counts)
    csv.Append(Csv(row.Project)).Append(',').Append(Csv(row.Category)).Append(',')
        .Append(row.Files).Append('\n');
File.WriteAllText(Path.Combine(output, "native-subsystem-counts.csv"), csv.ToString());
var md = new StringBuilder("# Native subsystem inventory\n\n")
    .Append("Counts are lexical `.cs` file matches inside physical project directories. ")
    .Append("Package and native item lists are literal project XML, without MSBuild evaluation.\n\n")
    .Append("| Project | ").Append(string.Join(" | ", categories.Select(x => x.Name)))
    .Append(" |\n| --- | ").Append(string.Join(" | ", categories.Select(_ => "---:"))).Append(" |\n");
foreach (string name in projectFiles.Select(x => Path.GetFileNameWithoutExtension(x)))
    md.Append("| ").Append(name).Append(" | ")
        .Append(string.Join(" | ", categories.Select(x =>
            totalsValue(name, x.Name).ToString())))
        .Append(" |\n");
int totalsValue(string project, string category) =>
    counts.First(x => x.Project == project && x.Category == category).Files;
md.Append("\nSee JSON for file matches, package references, and native asset items. ")
  .Append("See `native-subsystem-identity.json` for public type declarations and occurrence evidence. ")
  .Append("Text matches do not establish serialized use.\n");
File.WriteAllText(Path.Combine(output, "native-subsystem-inventory.md"), md.ToString());
Console.WriteLine($"Wrote {counts.Count} category counts, {typeDeclarations.Count} public type candidates, and {evidence.Count} occurrence records to {Relative(output)}.");

sealed record InventoryRow(string Project, string Path, string[] Categories);
sealed record CountRow(string Project, string Category, int Files);
sealed record PublicType(string Category, string FullName, string SimpleName,
    string Project, List<string> Declarations);
sealed record EvidenceRow(string Category, string FullName, string Match,
    string Path, int Line, bool PersistedData);

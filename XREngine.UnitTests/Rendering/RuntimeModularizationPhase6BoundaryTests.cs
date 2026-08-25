using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase6BoundaryTests
{
    private const string OwnershipManifestPath =
        "docs/work/progress/runtime/runtime-modularization-phase6-source-ownership.tsv";
    private const string ConsumerManifestPath =
        "docs/work/progress/runtime/runtime-modularization-phase6-consumer-api-baseline.tsv";

    private static readonly HashSet<string> ApprovedOwners = new(StringComparer.Ordinal)
    {
        "Removed",
        "XREngine.Animation",
        "XREngine.Data",
        "XREngine.Editor",
        "XREngine.Runtime.Bootstrap",
        "XREngine.Runtime.Core",
        "XREngine.Runtime.InputIntegration",
        "XREngine.Runtime.ModelingBridge",
        "XREngine.Runtime.Rendering",
    };

    private static readonly Dictionary<string, HashSet<string>> ApprovedFinalDestinationEdges =
        new(StringComparer.Ordinal)
        {
            ["XREngine.Extensions"] = [],
            ["XREngine.Data"] = ["XREngine.Extensions"],
            ["XREngine.Animation"] = ["XREngine.Data"],
            ["XREngine.Audio"] = ["XREngine.Data"],
            ["XREngine.Modeling"] = ["XREngine.Data"],
            ["XREngine.Fbx"] = ["XREngine.Data"],
            ["XREngine.Gltf"] = [],
            ["XREngine.Input"] = ["XREngine.Data", "XREngine.Extensions"],
            ["XREngine.Runtime.Core"] = ["XREngine.Data", "XREngine.Extensions"],
            ["XREngine.Runtime.Rendering"] =
                ["XREngine.Runtime.Core", "XREngine.Data", "XREngine.Extensions"],
            ["XREngine.Runtime.Rendering.OpenGL"] =
                ["XREngine.Runtime.Rendering", "XREngine.Runtime.Core", "XREngine.Data", "XREngine.Extensions"],
            ["XREngine.Runtime.Rendering.Vulkan"] =
                ["XREngine.Runtime.Rendering", "XREngine.Runtime.Core", "XREngine.Data", "XREngine.Extensions"],
            ["XREngine.Runtime.AnimationIntegration"] =
                ["OscCore", "XREngine.Animation", "XREngine.Data", "XREngine.Runtime.Core", "XREngine.Runtime.Rendering"],
            ["XREngine.Runtime.AudioIntegration"] =
                ["XREngine.Audio", "XREngine.Data", "XREngine.Runtime.Core", "XREngine.Runtime.Rendering"],
            ["XREngine.Runtime.InputIntegration"] =
                ["XREngine.Input", "XREngine.Data", "XREngine.Extensions", "XREngine.Runtime.Core", "XREngine.Runtime.Rendering"],
            ["XREngine.Runtime.ModelingBridge"] =
                ["XREngine.Animation", "XREngine.Fbx", "XREngine.Gltf", "XREngine.Modeling", "XREngine.Data", "XREngine.Runtime.Rendering"],
            ["XREngine.Runtime.Bootstrap"] =
            [
                "XREngine.Animation",
                "XREngine.Audio",
                "XREngine.Data",
                "XREngine.Extensions",
                "XREngine.Fbx",
                "XREngine.Gltf",
                "XREngine.Input",
                "XREngine.Modeling",
                "XREngine.Runtime.AnimationIntegration",
                "XREngine.Runtime.AudioIntegration",
                "XREngine.Runtime.Core",
                "XREngine.Runtime.InputIntegration",
                "XREngine.Runtime.ModelingBridge",
                "XREngine.Runtime.Rendering",
                "XREngine.Runtime.Rendering.OpenGL",
                "XREngine.Runtime.Rendering.Vulkan",
            ],
        };

    [Test]
    public void SourceOwnershipManifest_TracksEveryFacadeSourceThroughMigration()
    {
        string root = ResolveWorkspaceRoot();
        IReadOnlyList<TsvRow> rows = ReadTsv(Path.Combine(root, OwnershipManifestPath));
        Assert.That(rows, Has.Count.GreaterThan(0));

        Dictionary<string, TsvRow> bySource = new(StringComparer.OrdinalIgnoreCase);
        foreach (TsvRow row in rows)
        {
            string sourcePath = NormalizePath(row["SourcePath"]);
            Assert.That(bySource.TryAdd(sourcePath, row), Is.True, $"Duplicate ownership row '{sourcePath}'.");
            Assert.That(row["DeclaredTypes"], Is.Not.Empty, $"'{sourcePath}' has no declared-type disposition.");
            Assert.That(row["Disposition"], Is.AnyOf("Move", "Split", "Delete", "Refactor"));
            Assert.That(row["MigrationStatus"], Is.AnyOf("Pending", "Migrated", "Deleted"));
            Assert.That(
                row["FinalOwners"],
                Does.Not.Match("(?i)miscellaneous|temporary|unclassified"),
                $"'{sourcePath}' has a non-owner classification.");

            string[] owners = SplitList(row["FinalOwners"]);
            Assert.That(owners, Is.Not.Empty, $"'{sourcePath}' has no final owner.");
            Assert.That(owners, Is.All.Matches<string>(ApprovedOwners.Contains), $"'{sourcePath}' has an unapproved owner.");

            string sourceFullPath = Path.GetFullPath(Path.Combine(root, sourcePath));
            string[] destinations = SplitList(row["DestinationPaths"]);
            switch (row["MigrationStatus"])
            {
                case "Pending":
                    Assert.That(File.Exists(sourceFullPath), Is.True, $"Pending source '{sourcePath}' disappeared.");
                    Assert.That(destinations, Is.Empty, $"Pending source '{sourcePath}' must not claim completed destinations.");
                    break;
                case "Migrated":
                    Assert.That(File.Exists(sourceFullPath), Is.False, $"Migrated source '{sourcePath}' still exists.");
                    Assert.That(destinations, Is.Not.Empty, $"Migrated source '{sourcePath}' has no destination path.");
                    foreach (string destination in destinations)
                    {
                        string destinationFullPath = Path.GetFullPath(Path.Combine(root, destination));
                        Assert.That(
                            IsWithinRoot(destinationFullPath, Path.Combine(root, "XRENGINE")),
                            Is.False,
                            $"Migrated source '{sourcePath}' points back into the facade.");
                        Assert.That(File.Exists(destinationFullPath), Is.True, $"Migration destination '{destination}' is missing.");
                    }
                    break;
                case "Deleted":
                    Assert.That(File.Exists(sourceFullPath), Is.False, $"Deleted source '{sourcePath}' still exists.");
                    Assert.That(row["Disposition"], Is.EqualTo("Delete"));
                    Assert.That(owners, Is.EqualTo(new[] { "Removed" }));
                    Assert.That(destinations, Is.Empty);
                    break;
            }
        }

        foreach (string currentSource in EnumerateFacadeSources(root))
            Assert.That(bySource.ContainsKey(currentSource), Is.True, $"Facade source '{currentSource}' is not in the manifest.");
    }

    [Test]
    public void DirectFacadeConsumers_FollowTheirExplicitMigrationStates()
    {
        string root = ResolveWorkspaceRoot();
        IReadOnlyList<TsvRow> rows = ReadTsv(Path.Combine(root, ConsumerManifestPath));
        Assert.That(rows, Has.Count.EqualTo(7));

        string facadeProject = Path.GetFullPath(Path.Combine(root, "XRENGINE", "XREngine.csproj"));
        Dictionary<string, string> currentConsumers = EnumerateRepositoryProjectFiles(root)
            .Where(project => ReferencesProject(project, facadeProject))
            .ToDictionary(
                project => NormalizePath(Path.GetRelativePath(root, project)),
                project => project,
                StringComparer.OrdinalIgnoreCase);
        HashSet<string> manifestProjects = new(StringComparer.OrdinalIgnoreCase);

        foreach (TsvRow row in rows)
        {
            string projectPath = NormalizePath(row["ProjectPath"]);
            Assert.That(manifestProjects.Add(projectPath), Is.True, $"Duplicate consumer row '{projectPath}'.");
            Assert.That(row["MigrationStatus"], Is.AnyOf("Pending", "Migrated"));

            string projectFullPath = Path.GetFullPath(Path.Combine(root, projectPath));
            Assert.That(File.Exists(projectFullPath), Is.True, $"Consumer project '{projectPath}' is missing.");
            bool referencesFacade = currentConsumers.ContainsKey(projectPath);
            string[] replacements = SplitList(row["ReplacementProjectReferences"]);
            if (row["MigrationStatus"] == "Pending")
            {
                Assert.That(referencesFacade, Is.True, $"Pending consumer '{projectPath}' lost its facade reference.");
                Assert.That(replacements, Is.Empty);
                continue;
            }

            Assert.That(referencesFacade, Is.False, $"Migrated consumer '{projectPath}' still references the facade.");
            Assert.That(replacements, Is.Not.Empty, $"Migrated consumer '{projectPath}' has no replacement references.");
            HashSet<string> actualReferences = ReadProjectReferenceNames(projectFullPath);
            Assert.That(replacements, Is.All.Matches<string>(actualReferences.Contains));
        }

        Assert.That(
            currentConsumers.Keys,
            Is.SubsetOf(manifestProjects),
            "A new direct facade consumer was added outside the Phase 6 migration manifest.");
    }

    [Test]
    public void DestinationProjects_StayWithinTheFacadeFreeFinalGraph()
    {
        string root = ResolveWorkspaceRoot();
        foreach ((string projectName, HashSet<string> approvedReferences) in ApprovedFinalDestinationEdges)
        {
            string projectPath = Path.Combine(root, projectName, $"{projectName}.csproj");
            HashSet<string> references = ReadProjectReferenceNames(projectPath);
            bool referencesFacade = references.RemoveWhere(
                reference => string.Equals(reference, "XREngine", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(reference, "XRENGINE", StringComparison.OrdinalIgnoreCase)) > 0;

            Assert.That(
                references,
                Is.SubsetOf(approvedReferences),
                $"'{projectName}' acquired an edge outside the final Phase 6 graph.");
            if (!string.Equals(projectName, "XREngine.Runtime.Bootstrap", StringComparison.Ordinal))
                Assert.That(referencesFacade, Is.False, $"Lower destination '{projectName}' references the facade.");
        }
    }

    [Test]
    public void LegacyAssemblyIdentityCorpus_CannotGrowSilently()
    {
        string root = ResolveWorkspaceRoot();
        HashSet<string> approvedFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "XREngine.UnitTests/Prefabs/PrefabModelSerializationTests.cs",
            "XREngine.UnitTests/Rendering/RuntimeModularizationPhase3RenderingTests.cs",
            "XREngine.UnitTests/Rendering/RuntimeModularizationPhase4SerializationCompatibilityTests.cs",
            "XREngine.UnitTests/Rendering/RuntimeModularizationPhase5SerializationCompatibilityTests.cs",
        };
        Regex legacyIdentity = new("," + @"\s*" + "XREngine" + @"(?![.\w])", RegexOptions.CultureInvariant);

        HashSet<string> matches = [];
        foreach (string file in EnumerateIdentityInputs(root))
        {
            string source = File.ReadAllText(file);
            if (legacyIdentity.IsMatch(source))
                matches.Add(NormalizePath(Path.GetRelativePath(root, file)));
        }

        Assert.That(matches, Is.SubsetOf(approvedFiles), "A new legacy XREngine assembly identity entered the repository corpus.");
        Assert.That(matches, Is.EquivalentTo(approvedFiles));
    }

    [Test]
    public void FacadeCargo_CanOnlyShrinkFromTheAcceptedBaseline()
    {
        string root = ResolveWorkspaceRoot();
        string facadeProject = Path.Combine(root, "XRENGINE", "XREngine.csproj");
        if (!File.Exists(facadeProject))
            return;

        XDocument document = XDocument.Load(facadeProject);
        HashSet<string> packages = ReadIncludes(document, "PackageReference");
        HashSet<string> projectReferences = ReadProjectReferenceNames(facadeProject);
        HashSet<string> targets = document.Descendants("Target")
            .Select(element => (string?)element.Attribute("Name"))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> friendAssemblies = ReadIncludes(document, "InternalsVisibleTo");

        string[] acceptedPackages =
        [
            "AssimpNetter", "JoltPhysicsSharp", "LZMA-SDK", "MagicPhysX", "MemoryPack", "Newtonsoft.Json",
            "Silk.NET.Core", "Silk.NET.DirectStorage", "Silk.NET.DirectStorage.Native", "Silk.NET.Input",
            "Silk.NET.Windowing", "Silk.NET.Windowing.Common", "Silk.NET.Windowing.Extensions",
            "Silk.NET.Windowing.Glfw", "Silk.NET.Windowing.Sdl", "System.IO.Hashing",
            "System.Security.Cryptography.ProtectedData", "Vecc.YamlDotNet.Analyzers.StaticGenerator", "YamlDotNet",
        ];
        string[] acceptedProjectReferences =
        [
            "XREngine.Animation", "XREngine.Audio", "XREngine.Data", "XREngine.Extensions", "XREngine.Fbx",
            "XREngine.Input", "XREngine.Runtime.AnimationIntegration", "XREngine.Runtime.AudioIntegration",
            "XREngine.Runtime.Core", "XREngine.Runtime.InputIntegration", "XREngine.Runtime.ModelingBridge",
            "XREngine.Runtime.Rendering",
        ];

        Assert.That(packages, Is.SubsetOf(acceptedPackages));
        Assert.That(projectReferences, Is.SubsetOf(acceptedProjectReferences));
        Assert.That(targets, Is.SubsetOf(new[] { "EnsureCoACD", "CopyRestirNative" }));
        Assert.That(friendAssemblies, Is.SubsetOf(new[] { "XREngine.UnitTests", "XREngine.Runtime.Bootstrap" }));

        bool allSourcesPending = ReadTsv(Path.Combine(root, OwnershipManifestPath))
            .All(row => row["MigrationStatus"] == "Pending");
        if (!allSourcesPending)
            return;

        Assert.That(packages, Is.EquivalentTo(acceptedPackages));
        Assert.That(projectReferences, Is.EquivalentTo(acceptedProjectReferences));
        Assert.That(targets, Is.EquivalentTo(new[] { "EnsureCoACD", "CopyRestirNative" }));
        Assert.That(friendAssemblies, Is.EquivalentTo(new[] { "XREngine.UnitTests", "XREngine.Runtime.Bootstrap" }));
        Assert.That(File.Exists(Path.Combine(root, "XRENGINE", "runtimes", "win-x64", "native", "lib_coacd.dll")), Is.True);
        Assert.That(File.Exists(Path.Combine(root, "XRENGINE", "nis.license.txt")), Is.True);

        int typeForwardCount = Directory.EnumerateFiles(Path.Combine(root, "XRENGINE", "Properties"), "*.cs")
            .Sum(file => Regex.Matches(File.ReadAllText(file), @"\[assembly:\s*TypeForwardedTo").Count);
        Assert.That(typeForwardCount, Is.EqualTo(103));
    }

    [Test]
    public void FacadeRemovalGate_RequiresCompletedSourceConsumerAndRetentionState()
    {
        string root = ResolveWorkspaceRoot();
        string facadeProject = Path.Combine(root, "XRENGINE", "XREngine.csproj");
        if (File.Exists(facadeProject))
            return;

        IReadOnlyList<TsvRow> sourceRows = ReadTsv(Path.Combine(root, OwnershipManifestPath));
        IReadOnlyList<TsvRow> consumerRows = ReadTsv(Path.Combine(root, ConsumerManifestPath));
        Assert.That(sourceRows, Has.None.Matches<TsvRow>(row => row["MigrationStatus"] == "Pending"));
        Assert.That(consumerRows, Has.None.Matches<TsvRow>(row => row["MigrationStatus"] == "Pending"));

        string removedProject = Path.GetFullPath(facadeProject);
        Assert.That(EnumerateRepositoryProjectFiles(root), Has.None.Matches<string>(project => ReferencesProject(project, removedProject)));
        Assert.That(File.ReadAllText(Path.Combine(root, "XRENGINE.slnx")), Does.Not.Contain("XREngine/XREngine.csproj"));
        Assert.That(File.ReadAllText(Path.Combine(root, "XRENGINE.sln")), Does.Not.Contain("XREngine\\XREngine.csproj"));

        string bootstrapProject = File.ReadAllText(Path.Combine(
            root, "XREngine.Runtime.Bootstrap", "XREngine.Runtime.Bootstrap.csproj"));
        string aotGenerator = File.ReadAllText(Path.Combine(root, "Tools", "Generate-AotFactoryRegistrations.ps1"));
        string editorCodeManager = File.ReadAllText(Path.Combine(root, "XREngine.Editor", "CodeManager.cs"));
        string editorProjectInitializer = File.ReadAllText(Path.Combine(root, "XREngine.Editor", "EditorProjectInitializer.cs"));
        Assert.That(bootstrapProject, Does.Not.Contain("..\\XRENGINE\\**\\*.cs"));
        Assert.That(aotGenerator, Does.Not.Contain("'..\\XRENGINE'"));
        Assert.That(editorCodeManager, Does.Not.Contain("\"XREngine.dll\""));
        Assert.That(editorProjectInitializer, Does.Not.Contain("\"XRENGINE\", \"XREngine.csproj\""));
    }

    private static IReadOnlyList<TsvRow> ReadTsv(string path)
    {
        string[] lines = File.ReadAllLines(path);
        Assert.That(lines, Has.Length.GreaterThan(1), $"TSV contract '{path}' is empty.");
        string[] headers = lines[0].Split('\t', StringSplitOptions.None);
        return lines.Skip(1)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(line => new TsvRow(headers, line.Split('\t', StringSplitOptions.None), path))
            .ToArray();
    }

    private static IEnumerable<string> EnumerateFacadeSources(string root)
    {
        string facadeRoot = Path.Combine(root, "XRENGINE");
        if (!Directory.Exists(facadeRoot))
            yield break;

        foreach (string file in Directory.EnumerateFiles(facadeRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = NormalizePath(Path.GetRelativePath(root, file));
            if (relative.StartsWith("XRENGINE/bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("XRENGINE/obj/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("XRENGINE/Build/", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return relative;
        }
    }

    private static IEnumerable<string> EnumerateRepositoryProjectFiles(string root)
    {
        IEnumerable<string> searchRoots = Directory.EnumerateDirectories(root, "XREngine*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(root, "Samples")) ? [Path.Combine(root, "Samples")] : []);
        foreach (string searchRoot in searchRoots)
        foreach (string project in Directory.EnumerateFiles(searchRoot, "*.csproj", SearchOption.AllDirectories))
        {
            string relative = NormalizePath(Path.GetRelativePath(root, project));
            if (relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/Build/", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return project;
        }
    }

    private static IEnumerable<string> EnumerateIdentityInputs(string root)
    {
        string[] relativeRoots = ["Assets", "Samples", "XREngine.UnitTests", "XREngine.Server/Assets", ".vscode/schemas", "XREngine.Editor"];
        HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".asset", ".cs", ".csproj", ".json", ".jsonc", ".props", ".ps1", ".targets", ".xrproj", ".yaml", ".yml",
        };
        foreach (string relativeRoot in relativeRoots)
        {
            string searchRoot = Path.Combine(root, relativeRoot);
            if (!Directory.Exists(searchRoot))
                continue;
            foreach (string file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                string relative = NormalizePath(Path.GetRelativePath(root, file));
                if (!extensions.Contains(Path.GetExtension(file)) ||
                    relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains("/Build/", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return file;
            }
        }
    }

    private static bool ReferencesProject(string projectPath, string expectedProjectPath)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        return XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include!)))
            .Any(path => string.Equals(path, expectedProjectPath, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> ReadProjectReferenceNames(string projectPath)
        => XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(static include => Path.GetFileNameWithoutExtension(include!))
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ReadIncludes(XDocument document, string elementName)
        => document.Descendants(elementName)
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(static include => include!)
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsWithinRoot(string path, string expectedRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedRoot));
        string candidate = Path.GetFullPath(path);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SplitList(string value)
        => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizePath(string path)
        => path.Replace('\\', '/');

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find workspace root from '{AppContext.BaseDirectory}'.");
    }

    private sealed class TsvRow
    {
        private readonly Dictionary<string, string> _values;

        public TsvRow(string[] headers, string[] values, string sourcePath)
        {
            Assert.That(values, Has.Length.EqualTo(headers.Length), $"Malformed TSV row in '{sourcePath}'.");
            _values = headers.Select((header, index) => (header, values[index]))
                .ToDictionary(static pair => pair.header, static pair => pair.Item2, StringComparer.Ordinal);
        }

        public string this[string key]
            => _values.TryGetValue(key, out string? value)
                ? value
                : throw new InvalidDataException($"TSV contract is missing column '{key}'.");
    }
}

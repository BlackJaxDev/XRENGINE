using NUnit.Framework;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks the reviewed source policy stays tied to real portable project files.</summary>
[TestFixture]
public sealed class PortableKernelPolicyTests
{
    [Test]
    public void ReviewedReflectionEntries_IdentifyExistingSourceSymbols()
    {
        string root = FindRepositoryRoot();
        string policy = Path.Combine(root, "Build", "Portable", "SourceApiPolicy.tsv");
        foreach (string line in File.ReadLines(policy))
        {
            if (!line.StartsWith("allow\t", StringComparison.Ordinal))
                continue;

            string[] fields = line.Split('\t');
            Assert.That(fields, Has.Length.EqualTo(4), line);
            string relativePath = fields[1].Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(root, relativePath);
            Assert.That(File.Exists(fullPath), Is.True, fields[1]);
            Assert.That(File.ReadAllText(fullPath), Does.Contain(fields[2]),
                $"Reviewed symbol '{fields[2]}' is absent from '{fields[1]}'.");
            Assert.That(fields[3], Is.Not.Empty, fields[1]);
        }
    }

    [Test]
    public void PortableManifest_UsesOneNeutralProjectSourceSet()
    {
        string root = FindRepositoryRoot();
        string manifest = Path.Combine(root, "Build", "Portable", "PortableProjects.tsv");
        foreach (string line in File.ReadLines(manifest))
        {
            string name = line.Trim();
            if (name.Length == 0 || name.StartsWith('#'))
                continue;

            string projectFile = Path.Combine(root, name, $"{name}.csproj");
            Assert.That(File.Exists(projectFile), Is.True, name);
            string source = File.ReadAllText(projectFile);
            Assert.That(source, Does.Contain("<TargetFramework>net10.0</TargetFramework>"), name);
            Assert.That(source, Does.Not.Contain("XREnginePortableRuntime"), name);
            Assert.That(source, Does.Not.Contain("<Compile Remove="), name);
            if (name == "XREngine.Runtime.Rendering")
            {
                const string generatedCommandSource = "<Compile Include=\"$(GeneratedRenderCommandRegistrations)\" />";
                Assert.That(source.Split(generatedCommandSource, StringSplitOptions.None), Has.Length.EqualTo(2), name);
                source = source.Replace(generatedCommandSource, string.Empty, StringComparison.Ordinal);
            }
            Assert.That(source, Does.Not.Contain("<Compile Include="), name);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Build", "Portable", "PortableProjects.tsv")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository's portable project manifest.");
    }
}

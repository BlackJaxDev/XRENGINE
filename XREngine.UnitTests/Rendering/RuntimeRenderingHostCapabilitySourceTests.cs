using NUnit.Framework;
using Shouldly;
using System.Text.RegularExpressions;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeRenderingHostCapabilitySourceTests
{
    [Test]
    public void RuntimeRendering_DoesNotReadCompositeCurrentFacade()
    {
        string runtimeRenderingRoot = FindWorkspaceDirectory("XREngine.Runtime.Rendering");
        List<string> violations = [];
        HashSet<string> allowedFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("Runtime", "RuntimeRenderingHostServices.cs"),
            Path.Combine("Runtime", "RuntimeRenderingHostInstallationScope.cs"),
        };

        foreach (string filePath in Directory.EnumerateFiles(runtimeRenderingRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(runtimeRenderingRoot, filePath);
            if (allowedFiles.Contains(relativePath))
                continue;

            int lineNumber = 0;
            foreach (string line in File.ReadLines(filePath))
            {
                lineNumber++;
                string code = Regex.Replace(line, "\"(?:\\\\.|[^\"\\\\])*\"", string.Empty);
                if (code.Contains("RuntimeRenderingHostServices.Current", StringComparison.Ordinal))
                    violations.Add($"{relativePath}:{lineNumber}");
            }
        }

        violations.ShouldBeEmpty(
            "Runtime.Rendering production code must not read or alias the composite Current facade; use focused capability accessors.");
    }

    [Test]
    public void CompositeFacade_ContainsOnlyFocusedCapabilityInheritance()
    {
        string interfacePath = Path.Combine(
            FindWorkspaceDirectory("XREngine.Runtime.Rendering"),
            "Runtime",
            "Interfaces",
            "IRuntimeRenderingHostServices.cs");
        string source = File.ReadAllText(interfacePath);

        source.ShouldContain("IRuntimeRenderSettingsServices");
        source.ShouldContain("IRuntimeRenderFrameTimingServices");
        source.ShouldContain("IRuntimeRenderSchedulingServices");
        source.ShouldContain("IRuntimeRenderDiagnosticsServices");
        source.ShouldContain("IRuntimeRenderStatisticsServices");
        source.ShouldContain("IRuntimeRenderDebugDrawingServices");
        source.ShouldContain("IRuntimeRenderProfilingServices");
        source.ShouldContain("IRuntimeRenderAssetServices");
        source.ShouldContain("IRuntimeRendererFactoryServices");
        source.ShouldContain("IRuntimeRenderPresentationServices");
        source.ShouldContain("IRuntimeRenderBackendInteropServices");
        source.ShouldNotContain("StartProfileScope(");
        source.ShouldNotContain("CreateRenderer(");
        source.ShouldNotContain("EnqueueRenderThreadTask(");
    }

    private static string FindWorkspaceDirectory(string directoryName)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, directoryName);
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate workspace directory '{directoryName}'.");
    }
}

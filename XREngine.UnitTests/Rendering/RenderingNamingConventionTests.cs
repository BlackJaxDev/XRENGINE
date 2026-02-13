using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderingNamingConventionTests
{
    [Test]
    public void MeshRendering_PathIntentSuffixes_AreExplicit()
    {
        string root = ResolveWorkspaceRoot();
        string meshRoot = Path.Combine(root, "XRENGINE", "Rendering", "Pipelines", "Commands", "MeshRendering");

        AssertPathIntentSuffix(meshRoot, "Traditional");
        AssertPathIntentSuffix(meshRoot, "Meshlet");
        AssertPathIntentSuffix(meshRoot, "Shared");
    }

    [Test]
    public void GpuRenderingDomainFiles_UseRoleSuffixes()
    {
        string root = ResolveWorkspaceRoot();
        string domainRoot = Path.Combine(root, "XRENGINE", "Rendering", "Commands", "GPURendering");

        var expectedRoleSuffix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Policy"] = "Policy",
            ["Dispatch"] = "Dispatcher",
            ["Resources"] = "Resources",
            ["Validation"] = "Validator",
            ["Telemetry"] = "Stats",
        };

        foreach (var pair in expectedRoleSuffix)
        {
            string folder = Path.Combine(domainRoot, pair.Key);
            if (!Directory.Exists(folder))
                continue;

            foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                stem.EndsWith(pair.Value, StringComparison.OrdinalIgnoreCase)
                    .ShouldBeTrue($"'{RelativePath(root, file)}' should end with role suffix '{pair.Value}'.");
            }
        }
    }

    [Test]
    public void GenericNames_AreBanned_InGpuRenderingAndComputeShaders()
    {
        string root = ResolveWorkspaceRoot();
        string renderingRoot = Path.Combine(root, "XRENGINE", "Rendering");
        string computeRoot = Path.Combine(root, "Build", "CommonAssets", "Shaders", "Compute");

        var banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Helper",
            "Helpers",
            "Misc",
            "Temp",
        };

        foreach (string file in Directory.EnumerateFiles(renderingRoot, "*.cs", SearchOption.AllDirectories))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            banned.Contains(stem)
                .ShouldBeFalse($"Banned generic host file name detected: '{RelativePath(root, file)}'.");
        }

        foreach (string file in Directory.EnumerateFiles(computeRoot, "*.comp", SearchOption.AllDirectories))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            banned.Contains(stem)
                .ShouldBeFalse($"Banned generic shader file name detected: '{RelativePath(root, file)}'.");
        }
    }

    [Test]
    public void ComputeFunctionalFolders_UseOperationNamingRules()
    {
        string root = ResolveWorkspaceRoot();
        string computeRoot = Path.Combine(root, "Build", "CommonAssets", "Shaders", "Compute");

        var rules = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Culling"] = ["Cull", "Culling", "Extract"],
            ["Indirect"] = ["Indirect", "Build", "Copy", "Reset"],
            ["Occlusion"] = ["Occlusion", "HiZ"],
            ["Sorting"] = ["Sort", "Sorting", "Radix"],
            ["Debug"] = ["Debug", "Gather"],
        };

        foreach (var rule in rules)
        {
            string folder = Path.Combine(computeRoot, rule.Key);
            if (!Directory.Exists(folder))
                continue;

            foreach (string file in Directory.EnumerateFiles(folder, "*.comp", SearchOption.AllDirectories))
            {
                string stem = Path.GetFileNameWithoutExtension(file);

                bool hasRecognizedPrefix = stem.StartsWith("GPURender", StringComparison.OrdinalIgnoreCase) ||
                                           rule.Value.Any(token => stem.StartsWith(token, StringComparison.OrdinalIgnoreCase));
                hasRecognizedPrefix.ShouldBeTrue(
                    $"'{RelativePath(root, file)}' should start with 'GPURender' or a domain prefix ({string.Join(", ", rule.Value)}).");

                bool hasOperationToken = rule.Value.Any(token => stem.Contains(token, StringComparison.OrdinalIgnoreCase));
                hasOperationToken.ShouldBeTrue($"'{RelativePath(root, file)}' should include one of: {string.Join(", ", rule.Value)}.");
            }
        }
    }

    private static void AssertPathIntentSuffix(string meshRoot, string intent)
    {
        string folder = Path.Combine(meshRoot, intent);
        Directory.Exists(folder).ShouldBeTrue($"Expected mesh rendering intent folder is missing: {folder}");

        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            stem.Contains(intent, StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue($"Mesh rendering file '{file}' should include intent suffix '{intent}'.");
        }
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XRENGINE.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find workspace root from base directory '{AppContext.BaseDirectory}'.");
    }

    private static string RelativePath(string root, string filePath)
        => filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? filePath[(root.Length + 1)..].Replace('\\', '/')
            : filePath.Replace('\\', '/');
}

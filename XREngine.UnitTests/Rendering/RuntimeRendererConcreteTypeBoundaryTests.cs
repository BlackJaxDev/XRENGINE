using NUnit.Framework;
using Shouldly;
using System.Text.RegularExpressions;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeRendererConcreteTypeBoundaryTests
{
    [Test]
    public void StableRuntimeRendering_RestrictsConcreteRendererReferencesToBackendIntegrationAllowlist()
    {
        string[] expected =
        [
            "Rendering/API/Rendering/OpenXR/Extensions.cs",
            "Rendering/API/Rendering/OpenXR/Instance.cs",
            "Rendering/API/Rendering/OpenXR/OpenXRAPI.Foveation.cs",
            "Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenGL.cs",
            "Rendering/API/Rendering/OpenXR/OpenXRAPI.Resolution.cs",
            "Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs",
            "Rendering/API/Rendering/OpenXR/OpenXRAPI.StrictSpsBoundaryCapture.cs",
            "Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs",
            "Rendering/API/Rendering/OpenXR/XrGraphicsBindings.cs",
            "Rendering/DLSS/StreamlineNative.cs",
            "Rendering/PhysicsCompute/OpenGLPhysicsChainComputeBackend.cs",
            "Rendering/PhysicsCompute/VulkanPhysicsChainComputeBackend.cs",
            "Scene/Components/UI/Core/UltralightGpuWebRendererBackend.cs",
        ];

        string renderingRoot = Path.Combine(FindWorkspaceRoot(), "XREngine.Runtime.Rendering");
        string backendRoot = Normalize(Path.Combine(renderingRoot, "Rendering", "API", "Rendering"));
        string[] actual = Directory
            .EnumerateFiles(renderingRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string normalized = Normalize(path);
                if (normalized.StartsWith($"{backendRoot}/OpenGL/", StringComparison.Ordinal) ||
                    normalized.StartsWith($"{backendRoot}/Vulkan/", StringComparison.Ordinal))
                    return false;

                return ContainsConcreteRendererType(File.ReadAllText(path));
            })
            .Select(path => Normalize(Path.GetRelativePath(renderingRoot, path)))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        actual.ShouldBe(expected, ignoreOrder: false);
    }

    [Test]
    public void FacadeAndApplicationProjects_DoNotReferenceConcreteRendererTypes()
    {
        string root = FindWorkspaceRoot();
        string[] projects = ["XRENGINE", "XREngine.Server", "XREngine.VRClient"];

        foreach (string project in projects)
        {
            string[] violations = Directory
                .EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories)
                .Where(static path => !IsBuildOutput(path))
                .Where(path =>
                    ContainsConcreteRendererType(File.ReadAllText(path)))
                .Select(path => Path.GetRelativePath(root, path))
                .ToArray();

            violations.ShouldBeEmpty($"Concrete renderer references in {project} must use IDs or capabilities.");
        }
    }

    [Test]
    public void StableVendorAndDiagnosticPipelines_UseBackendCapabilities()
    {
        string root = FindWorkspaceRoot();
        string[] files =
        [
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs",
            "XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher/SkinningPrepassDispatcher.RendererResources.Diagnostics.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs",
            "XREngine.Runtime.Rendering/Rendering/Resources/RenderPipelineResourceManager.cs",
        ];

        foreach (string relativePath in files)
        {
            string source = File.ReadAllText(Path.Combine(root, relativePath));
            source.ShouldNotContain("OpenGLRenderer");
            source.ShouldNotContain("VulkanRenderer");
        }
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull();
        return directory.FullName;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static bool ContainsConcreteRendererType(string source)
        => Regex.IsMatch(source, @"\b(?:OpenGLRenderer|VulkanRenderer)\b", RegexOptions.CultureInvariant);

    private static bool IsBuildOutput(string path)
    {
        string normalized = Normalize(path);
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/Build/", StringComparison.OrdinalIgnoreCase);
    }
}

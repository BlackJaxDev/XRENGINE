using System.Xml.Linq;
using NUnit.Framework;
using Shouldly;
using XREngine.Audio;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering.Models;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase4DependencyBoundaryTests
{
    [Test]
    public void RuntimeRendering_ProjectReferencesOnlyApprovedKernelAssemblies()
    {
        string root = ResolveWorkspaceRoot();
        string projectPath = Path.Combine(root, "XREngine.Runtime.Rendering", "XREngine.Runtime.Rendering.csproj");
        XDocument project = XDocument.Load(projectPath);

        string[] references = project
            .Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension((string)element.Attribute("Include")!))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        references.ShouldBe(
        [
            "XREngine.Data",
            "XREngine.Extensions",
            "XREngine.Runtime.Core",
        ]);
    }

    [Test]
    public void RuntimeRendering_SourceDoesNotBindToFeatureImplementations()
    {
        string root = ResolveWorkspaceRoot();
        string renderingRoot = Path.Combine(root, "XREngine.Runtime.Rendering");
        string source = string.Join(
            '\n',
            Directory.EnumerateFiles(renderingRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        source.ShouldNotContain("using XREngine.Fbx;");
        source.ShouldNotContain("using XREngine.Modeling;");
        source.ShouldNotContain("using XREngine.Input.Devices.Glfw;");
        source.ShouldNotContain("InputInterface input");
        source.ShouldNotContain("as PawnComponent");
        source.ShouldNotContain("ListenerContext>");
        source.ShouldNotContain("AudioSource? _primaryAudioSource");
    }

    [Test]
    public void LowerInputAndMediaContracts_AreOwnedByData()
    {
        typeof(EKey).Assembly.GetName().Name.ShouldBe("XREngine.Data");
        typeof(EMouseButton).Assembly.GetName().Name.ShouldBe("XREngine.Data");
        typeof(IInputRegistration).Assembly.GetName().Name.ShouldBe("XREngine.Data");
        typeof(RuntimeVrPoseState).Assembly.GetName().Name.ShouldBe("XREngine.Data");
        typeof(IAudioStreamingComponent).Assembly.GetName().Name.ShouldBe("XREngine.Data");
        typeof(IAudioPlaybackSource).Assembly.GetName().Name.ShouldBe("XREngine.Data");
        typeof(IRuntimeAudioListenerWorld).Assembly.GetName().Name.ShouldBe("XREngine.Data");
    }

    [Test]
    public void ConcreteModelImportPipeline_IsOwnedByModelingBridge()
    {
        typeof(ModelImportOptions).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.ModelingBridge");
        typeof(ModelImporter).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.ModelingBridge");
        typeof(RuntimeModelImportServices).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.ModelingBridge");

        string root = ResolveWorkspaceRoot();
        File.Exists(Path.Combine(root, "XRENGINE", "Core", "ModelImporter.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(root, "XRENGINE", "Core", "NativeFbxSceneImporter.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(root, "XRENGINE", "Core", "NativeGltfSceneImporter.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(root, "XRENGINE", "Core", "ModelImportMeshIslandSplitter.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(root, "XREngine.Runtime.ModelingBridge", "Importing", "ModelImporter.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(root, "XREngine.Runtime.ModelingBridge", "Importing", "NativeFbxSceneImporter.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(root, "XREngine.Runtime.ModelingBridge", "Importing", "NativeGltfSceneImporter.cs")).ShouldBeTrue();
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find workspace root from '{AppContext.BaseDirectory}'.");
    }
}

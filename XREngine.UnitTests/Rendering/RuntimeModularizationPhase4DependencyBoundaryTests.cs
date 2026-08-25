using System.Xml.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using XREngine.Audio;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase4DependencyBoundaryTests
{
    private static readonly string[] BackendLeafProjectNames =
    [
        "XREngine.Runtime.Rendering.OpenGL",
        "XREngine.Runtime.Rendering.Vulkan",
    ];

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
    public void P48a_BackendLeafProjectsReferenceOnlyStableRenderingKernel()
    {
        string root = ResolveWorkspaceRoot();

        foreach (string leafProjectName in BackendLeafProjectNames)
        {
            string projectPath = Path.Combine(root, leafProjectName, $"{leafProjectName}.csproj");
            XDocument project = XDocument.Load(projectPath);

            string[] references = project
                .Descendants("ProjectReference")
                .Select(element => Path.GetFileNameWithoutExtension((string)element.Attribute("Include")!))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            references.ShouldBe(["XREngine.Runtime.Rendering"]);
        }
    }

    [Test]
    public void P48a_StableRenderingKernelDoesNotReferenceBackendLeafProjects()
    {
        string root = ResolveWorkspaceRoot();
        XDocument project = XDocument.Load(
            Path.Combine(root, "XREngine.Runtime.Rendering", "XREngine.Runtime.Rendering.csproj"));

        string[] references = project
            .Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension((string)element.Attribute("Include")!))
            .ToArray();

        references.ShouldNotContain("XREngine.Runtime.Rendering.OpenGL");
        references.ShouldNotContain("XREngine.Runtime.Rendering.Vulkan");
    }

    [Test]
    public void P48c_BackendFactoriesAreOwnedByTheirLeafAssembliesWithoutConcreteTypeTests()
    {
        using RendererBackendCatalog catalog = new();
        using IDisposable registrations = BuiltInRendererBackendModules.RegisterAll(catalog);

        catalog.GetRequired(RendererBackendId.OpenGL)
            .Factory.GetType().Assembly.GetName().Name
            .ShouldBe("XREngine.Runtime.Rendering.OpenGL");
        catalog.GetRequired(RendererBackendId.Vulkan)
            .Factory.GetType().Assembly.GetName().Name
            .ShouldBe("XREngine.Runtime.Rendering.Vulkan");
        typeof(IRendererBackendFactory).Assembly.GetName().Name
            .ShouldBe("XREngine.Runtime.Rendering");
    }

    [Test]
    public void RenderingHostDepthPreference_DoesNotReenterInstalledFactory()
    {
        string root = ResolveWorkspaceRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "XREngine.Runtime.Bootstrap",
            "RenderingHost",
            "Engine.RuntimeRenderingHostServices.cs"));
        Match resolver = Regex.Match(
            source,
            @"public XRCamera\.EDepthMode ResolveSceneCameraDepthModePreference\(\)\s*=>\s*(?<target>[^;]+);",
            RegexOptions.CultureInvariant);

        resolver.Success.ShouldBeTrue();
        string target = resolver.Groups["target"].Value;
        target.ShouldBe(
            "EngineRenderingSettingsApplication.ResolveSceneCameraDepthModePreference()");
        target.ShouldNotContain(
            "RuntimeEngine.Rendering.ResolveSceneCameraDepthModePreference");
        target.ShouldNotContain(
            "RuntimeRenderingHostServices.Factories");
    }

    [Test]
    public void P48a_SolutionContainsStableKernelAndBothBackendLeaves()
    {
        string root = ResolveWorkspaceRoot();
        XDocument solution = XDocument.Load(Path.Combine(root, "XRENGINE.slnx"));
        string[] projectPaths = solution
            .Descendants("Project")
            .Select(element => ((string)element.Attribute("Path")!).Replace('\\', '/'))
            .ToArray();

        projectPaths.ShouldContain("XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj");
        projectPaths.ShouldContain("XREngine.Runtime.Rendering.OpenGL/XREngine.Runtime.Rendering.OpenGL.csproj");
        projectPaths.ShouldContain("XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj");
    }

    [Test]
    public void P48a_BackendImplementationTreesAreOwnedByLeafProjects()
    {
        string root = ResolveWorkspaceRoot();
        string kernelApiRoot = Path.Combine(
            root,
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering");

        Directory.Exists(Path.Combine(kernelApiRoot, "OpenGL")).ShouldBeFalse();
        Directory.Exists(Path.Combine(kernelApiRoot, "Vulkan")).ShouldBeFalse();
        Directory.Exists(Path.Combine(
            root,
            "XREngine.Runtime.Rendering.OpenGL",
            "Rendering",
            "API",
            "Rendering",
            "OpenGL")).ShouldBeTrue();
        Directory.Exists(Path.Combine(
            root,
            "XREngine.Runtime.Rendering.Vulkan",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan")).ShouldBeTrue();
    }

    [Test]
    public void P48a_BackendNativePackagesAreOwnedByLeafProjects()
    {
        string root = ResolveWorkspaceRoot();
        string[] kernelPackages = ReadPackageReferences(
            Path.Combine(root, "XREngine.Runtime.Rendering", "XREngine.Runtime.Rendering.csproj"));
        string[] openGlPackages = ReadPackageReferences(
            Path.Combine(root, "XREngine.Runtime.Rendering.OpenGL", "XREngine.Runtime.Rendering.OpenGL.csproj"));
        string[] vulkanPackages = ReadPackageReferences(
            Path.Combine(root, "XREngine.Runtime.Rendering.Vulkan", "XREngine.Runtime.Rendering.Vulkan.csproj"));

        kernelPackages.Any(IsOpenGlPackage).ShouldBeFalse();
        kernelPackages.Any(IsVulkanPackage).ShouldBeFalse();
        openGlPackages.ShouldContain("Silk.NET.OpenGL");
        openGlPackages.ShouldContain("Silk.NET.WGL.Extensions.ARB");
        vulkanPackages.ShouldContain("Silk.NET.Vulkan");
        vulkanPackages.ShouldContain("Silk.NET.Shaderc");
        vulkanPackages.ShouldContain("Silk.NET.Vulkan.Loader.Native");
    }

    [Test]
    public void P48c_ConsumerProjectsDoNotReferenceBackendLeavesDirectly()
    {
        string root = ResolveWorkspaceRoot();
        string[] consumerProjects =
        [
            "XRENGINE/XREngine.csproj",
            "XREngine.Editor/XREngine.Editor.csproj",
            "XREngine.Server/XREngine.Server.csproj",
            "XREngine.VRClient/XREngine.VRClient.csproj",
        ];

        foreach (string relativeProjectPath in consumerProjects)
        {
            XDocument project = XDocument.Load(Path.Combine(
                root,
                relativeProjectPath.Replace('/', Path.DirectorySeparatorChar)));
            string[] references = project
                .Descendants("ProjectReference")
                .Select(element => Path.GetFileNameWithoutExtension(
                    (string)element.Attribute("Include")!))
                .ToArray();

            references.ShouldNotContain("XREngine.Runtime.Rendering.OpenGL");
            references.ShouldNotContain("XREngine.Runtime.Rendering.Vulkan");
        }
    }

    [Test]
    public void P48c_BootstrapOwnsConditionalStaticBackendComposition()
    {
        string root = ResolveWorkspaceRoot();
        string projectPath = Path.Combine(
            root,
            "XREngine.Runtime.Bootstrap",
            "XREngine.Runtime.Bootstrap.csproj");
        XDocument project = XDocument.Load(projectPath);
        XElement[] backendReferences = project
            .Descendants("ProjectReference")
            .Where(element => BackendLeafProjectNames.Contains(
                Path.GetFileNameWithoutExtension((string)element.Attribute("Include")!),
                StringComparer.Ordinal))
            .ToArray();

        backendReferences.Length.ShouldBe(2);
        backendReferences.ShouldAllBe(element =>
            !string.IsNullOrWhiteSpace((string?)element.Attribute("Condition")));

        string source = File.ReadAllText(Path.Combine(
            root,
            "XREngine.Runtime.Bootstrap",
            "RenderingHost",
            "BuiltInRendererBackendModules.cs"));
        source.ShouldContain("XRENGINE_STATIC_OPENGL");
        source.ShouldContain("XRENGINE_STATIC_VULKAN");
        source.ShouldContain("OpenGlRendererBackendModule.Register(catalog)");
        source.ShouldContain("VulkanRendererBackendModule.Register(catalog)");
        source.ShouldNotContain("AssemblyLoadContext");
        source.ShouldNotContain("GetTypes(");
    }

    [Test]
    public void P48c_ConsumerProjectsDoNotOwnBackendNativePackages()
    {
        string root = ResolveWorkspaceRoot();
        string[] consumerProjects =
        [
            "XRENGINE/XREngine.csproj",
            "XREngine.Editor/XREngine.Editor.csproj",
            "XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj",
            "XREngine.Server/XREngine.Server.csproj",
            "XREngine.VRClient/XREngine.VRClient.csproj",
        ];

        foreach (string relativeProjectPath in consumerProjects)
        {
            string[] packages = ReadPackageReferences(Path.Combine(
                root,
                relativeProjectPath.Replace('/', Path.DirectorySeparatorChar)));
            packages.Any(IsOpenGlPackage).ShouldBeFalse();
            packages.Any(IsVulkanPackage).ShouldBeFalse();
        }
    }

    [Test]
    public void P48a_BackendNativeAndEmbeddedContentIsOwnedByLeafProjects()
    {
        string root = ResolveWorkspaceRoot();
        string kernelProject = File.ReadAllText(
            Path.Combine(root, "XREngine.Runtime.Rendering", "XREngine.Runtime.Rendering.csproj"));
        string openGlProject = File.ReadAllText(
            Path.Combine(root, "XREngine.Runtime.Rendering.OpenGL", "XREngine.Runtime.Rendering.OpenGL.csproj"));
        string vulkanProject = File.ReadAllText(
            Path.Combine(root, "XREngine.Runtime.Rendering.Vulkan", "XREngine.Runtime.Rendering.Vulkan.csproj"));
        string legacyProject = File.ReadAllText(
            Path.Combine(root, "XRENGINE", "XREngine.csproj"));

        kernelProject.ShouldNotContain("VulkanMemoryAllocatorBridge.Native.dll");
        kernelProject.ShouldNotContain("shader_fill.vert");
        openGlProject.ShouldContain(@"Rendering\UI\Ultralight\Shaders\shader_fill.vert");
        vulkanProject.ShouldContain("VulkanMemoryAllocatorBridge.Native.dll");
        vulkanProject.ShouldContain("<NvidiaSdkNative Include=");
        legacyProject.ShouldNotContain("<NvidiaSdkNative Include=");
    }

    [Test]
    public void RuntimeCore_ProjectReferencesOnlyApprovedLowerAssemblies()
    {
        string root = ResolveWorkspaceRoot();
        string projectPath = Path.Combine(root, "XREngine.Runtime.Core", "XREngine.Runtime.Core.csproj");
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
        ]);
    }

    [Test]
    public void RuntimeCore_SourceDoesNotUseRuntimeRenderingFacade()
    {
        string root = ResolveWorkspaceRoot();
        string coreRoot = Path.Combine(root, "XREngine.Runtime.Core");
        string source = string.Join(
            '\n',
            Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        source.ShouldNotContain("RuntimeEngine.Rendering");
    }

    [Test]
    public void RuntimeRendering_SourceDoesNotBindToFeatureImplementations()
    {
        string root = ResolveWorkspaceRoot();
        string renderingRoot = Path.Combine(root, "XREngine.Runtime.Rendering");
        string source = string.Join(
            '\n',
            Directory.EnumerateFiles(renderingRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        source.ShouldNotContain("using XREngine.Fbx;");
        source.ShouldNotContain("using XREngine.Modeling;");
        source.ShouldNotContain("using XREngine.Input.Devices.Glfw;");
        source.ShouldNotContain("InputInterface input");
        source.ShouldNotContain("as PawnComponent");
        source.ShouldNotContain("ListenerContext>");
        source.ShouldNotContain("AudioSource? _primaryAudioSource");
        Regex.IsMatch(
                source,
                @"(?<![A-Za-z])Engine\.(Rendering|VRState|Windows|RenderThread)",
                RegexOptions.CultureInvariant)
            .ShouldBeFalse();
    }

    [Test]
    public void P46_RenderingEngineBehavior_IsOwnedByRuntimeRendering()
    {
        typeof(RuntimeEngine).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(RuntimeEngine.Rendering.EngineSettings).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(RuntimeVrState).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(RuntimeBvhStats).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");

        string root = ResolveWorkspaceRoot();
        string legacyRenderingRoot = Path.Combine(root, "XRENGINE", "Engine", "Subclasses", "Rendering");
        string[] removedLegacyPartials =
        [
            "Engine.Rendering.cs",
            "Engine.Rendering.Debug.cs",
            "Engine.Rendering.State.cs",
            "Engine.Rendering.Stats.cs",
            "Engine.Rendering.BvhStats.cs",
            "Engine.Rendering.Constants.cs",
            "Engine.Rendering.SecondaryContext.cs",
            "Engine.Rendering.VulkanUpscaleBridge.cs",
            "Engine.Rendering.Settings.cs",
        ];

        foreach (string fileName in removedLegacyPartials)
            File.Exists(Path.Combine(legacyRenderingRoot, fileName)).ShouldBeFalse();
    }

    [Test]
    public void P47_VrStateCompatibilityFacade_IsRemoved()
    {
        string root = ResolveWorkspaceRoot();
        string engineRoot = Path.Combine(root, "XRENGINE", "Engine");
        string bootstrapHostRoot = Path.Combine(root, "XREngine.Runtime.Bootstrap", "SubsystemHost");
        string lifecycleSource = File.ReadAllText(Path.Combine(bootstrapHostRoot, "EngineVrLifecycle.cs"));
        string networkingSource = File.ReadAllText(Path.Combine(engineRoot, "Engine.Networking.cs"));
        string windowsSource = File.ReadAllText(Path.Combine(engineRoot, "Engine.Windows.cs"));
        string jsonSource = File.ReadAllText(Path.Combine(bootstrapHostRoot, "VrManifestJsonSerialization.cs"));

        File.Exists(Path.Combine(engineRoot, "Engine.VRState.cs")).ShouldBeFalse();
        lifecycleSource.ShouldContain("internal static class EngineVrLifecycle");
        lifecycleSource.ShouldNotContain("public static class VRState");
        lifecycleSource.ShouldNotContain("public struct VRInputData");
        networkingSource.ShouldNotContain("result = VRState.");
        windowsSource.ShouldNotContain("&& !VRState.IsInVR");
        jsonSource.ShouldContain("typeof(RuntimeVrState.VRInputData)");
        jsonSource.ShouldNotContain("typeof(Engine.VRState.VRInputData)");
    }

    [Test]
    public void P47_RuntimeRendering_DoesNotFriendBootstrap()
    {
        string root = ResolveWorkspaceRoot();
        string assemblyInfo = File.ReadAllText(
            Path.Combine(root, "XREngine.Runtime.Rendering", "Properties", "AssemblyInfo.cs"));

        assemblyInfo.ShouldContain("InternalsVisibleTo(\"XREngine\")");
        assemblyInfo.ShouldContain("InternalsVisibleTo(\"XREngine.UnitTests\")");
        assemblyInfo.ShouldNotContain("InternalsVisibleTo(\"XREngine.Runtime.Bootstrap\")");
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

    private static string[] ReadPackageReferences(string projectPath)
        => XDocument
            .Load(projectPath)
            .Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static packageName => packageName is not null)
            .Select(static packageName => packageName!)
            .ToArray();

    private static bool IsOpenGlPackage(string packageName)
        => packageName.StartsWith("Silk.NET.OpenGL", StringComparison.OrdinalIgnoreCase)
            || packageName.StartsWith("Silk.NET.OpenGLES", StringComparison.OrdinalIgnoreCase)
            || packageName.StartsWith("Silk.NET.WGL", StringComparison.OrdinalIgnoreCase);

    private static bool IsVulkanPackage(string packageName)
        => packageName.StartsWith("Silk.NET.Vulkan", StringComparison.OrdinalIgnoreCase)
            || packageName.Equals("Silk.NET.Shaderc", StringComparison.OrdinalIgnoreCase);
}

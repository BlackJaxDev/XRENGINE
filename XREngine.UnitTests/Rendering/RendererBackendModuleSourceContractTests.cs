using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RendererBackendModuleSourceContractTests
{
    [Test]
    public void StableWindowCreation_UsesInstalledCatalog_NotConcreteConstructors()
    {
        string windowSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        string hostSource = ReadWorkspaceFile(
            "XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeRenderingHostServices.cs");

        windowSource.ShouldContain("RendererBackends.CreateRequired(");
        windowSource.ShouldContain("RendererBackendCapabilities.DesktopPresentation");
        hostSource.ShouldContain("_rendererBackends.CreateRequired(");
        hostSource.ShouldNotContain("new OpenGLRenderer(");
        hostSource.ShouldNotContain("new VulkanRenderer(");
    }

    [Test]
    public void StableEnginePolicy_DoesNotUseConcreteRendererTypeTests()
    {
        string renderingPolicy = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.SecondaryContext.cs");
        string hostServices = ReadWorkspaceFile(
            "XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeRenderingHostServices.cs");

        renderingPolicy.ShouldNotContain("is VulkanRenderer");
        renderingPolicy.ShouldContain("BackendId == RendererBackendId.Vulkan");
        hostServices.ShouldNotContain("is OpenGLRenderer");
        hostServices.ShouldNotContain("is VulkanRenderer");
        hostServices.ShouldContain("GetPrimaryRendererCapability<");
    }

    [Test]
    public void StableRenderingKernel_HasNoConcreteBackendTypeNamesOrLeafReferences()
    {
        string stableRoot = Path.Combine(FindWorkspaceRoot(), "XREngine.Runtime.Rendering");
        string[] offenders = Directory
            .EnumerateFiles(stableRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
            {
                string source = File.ReadAllText(path);
                return System.Text.RegularExpressions.Regex.IsMatch(
                    source,
                    @"\b(?:OpenGLRenderer|VulkanRenderer)\b");
            })
            .Select(path => Path.GetRelativePath(stableRoot, path))
            .ToArray();
        offenders.ShouldBeEmpty();

        string project = File.ReadAllText(
            Path.Combine(stableRoot, "XREngine.Runtime.Rendering.csproj"));
        project.ShouldNotContain("XREngine.Runtime.Rendering.OpenGL");
        project.ShouldNotContain("XREngine.Runtime.Rendering.Vulkan");
    }

    [Test]
    public void LeafBackends_DoNotReferenceEachOther()
    {
        string root = FindWorkspaceRoot();
        string openGlProject = File.ReadAllText(
            Path.Combine(
                root,
                "XREngine.Runtime.Rendering.OpenGL",
                "XREngine.Runtime.Rendering.OpenGL.csproj"));
        string vulkanProject = File.ReadAllText(
            Path.Combine(
                root,
                "XREngine.Runtime.Rendering.Vulkan",
                "XREngine.Runtime.Rendering.Vulkan.csproj"));

        openGlProject.ShouldNotContain("XREngine.Runtime.Rendering.Vulkan");
        vulkanProject.ShouldNotContain("XREngine.Runtime.Rendering.OpenGL");
    }

    [Test]
    public void StaticBuiltInsAndCollectibleModulesShareFactoryAndRegistrationContracts()
    {
        string builtIns = ReadWorkspaceFile(
            "XREngine.Runtime.Bootstrap/RenderingHost/BuiltInRendererBackendModules.cs");
        string registration = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RendererModules/RendererBackendRegistration.cs");

        builtIns.ShouldNotContain("AssemblyLoadContext");
        builtIns.ShouldNotContain("GetTypes(");
        builtIns.ShouldContain("OpenGlRendererBackendModule.Register(catalog)");
        builtIns.ShouldContain("VulkanRendererBackendModule.Register(catalog)");
        registration.ShouldContain("IRendererBackendFactory");
        registration.ShouldContain("IRendererBackendLifecycle");
    }

    [Test]
    public void EditorConcreteRendererReferences_AreRestrictedToExactWrapperInspectorAllowlist()
    {
        string[] expected =
        [
            "ComponentEditors/GLObjectEditorAttribute.cs",
            "ComponentEditors/GLObjectEditorRegistry.cs",
            "ComponentEditors/GLObjectEditors.cs",
            "IMGUI/EditorImGuiUI.InspectorPanel.cs",
            "IMGUI/EditorImGuiUI.Mipmap2DInspector.cs",
            "IMGUI/EditorImGuiUI.PropertyEditor.cs",
            "IMGUI/EditorImGuiUI.ShaderProgramLinksPanel.cs",
            "UI/Panels/Inspector/Editors/InspectorPropertyEditors.Custom.cs",
            "UI/Panels/Inspector/Editors/InspectorPropertyEditors.cs",
        ];

        string editorRoot = Path.Combine(FindWorkspaceRoot(), "XREngine.Editor");
        string[] actual = Directory
            .EnumerateFiles(editorRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
            {
                string source = File.ReadAllText(path);
                return System.Text.RegularExpressions.Regex.IsMatch(
                    source,
                    @"\b(?:OpenGLRenderer|VulkanRenderer)\b");
            })
            .Select(path => Path.GetRelativePath(editorRoot, path).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        actual.ShouldBe(expected, ignoreOrder: false);
    }

    [Test]
    public void StableIndirectSubmission_UsesFocusedBackendCapabilities()
    {
        string hybrid = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string renderPass = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");
        string indirectContract = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RendererModules/IIndirectDrawStateBackendCapability.cs");
        string addressContract = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RendererModules/ISceneDatabaseDeviceAddressBackendCapability.cs");
        string glImplementation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Commands/OpenGLRenderer.IndirectSubmissionCapability.cs");
        string vkImplementation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.IndirectSubmissionCapability.cs");

        hybrid.ShouldNotContain("OpenGLRenderer");
        hybrid.ShouldNotContain("VulkanRenderer");
        renderPass.ShouldNotContain("OpenGLRenderer");
        renderPass.ShouldNotContain("VulkanRenderer");
        hybrid.ShouldNotContain(".VkDataBuffer");
        hybrid.ShouldNotContain(".IndirectDrawStateScope");

        hybrid.ShouldContain("IIndirectDrawStateBackendCapability");
        hybrid.ShouldContain("ISceneDatabaseDeviceAddressBackendCapability");
        renderPass.ShouldContain("IMaterialTableBackendCapability");
        renderPass.ShouldContain("IBufferDiagnosticReadbackBackendCapability");

        indirectContract.ShouldContain("public readonly record struct IndirectDrawStateToken");
        indirectContract.ShouldContain("public readonly struct IndirectDrawStateCapabilityScope");
        addressContract.ShouldContain("interface ISceneDatabaseDeviceAddressBackendCapability");
        glImplementation.ShouldContain("IIndirectDrawStateBackendCapability");
        vkImplementation.ShouldContain("ISceneDatabaseDeviceAddressBackendCapability");
        vkImplementation.ShouldContain("is not VkDataBuffer");
    }

    [Test]
    public void CollectibleBackends_DoNotCreateUnmanagedDelegateThunks()
    {
        string root = FindWorkspaceRoot();
        string[] backendRoots =
        [
            Path.Combine(root, "XREngine.Runtime.Rendering.OpenGL"),
            Path.Combine(root, "XREngine.Runtime.Rendering.Vulkan"),
        ];
        string[] offenders = backendRoots
            .SelectMany(static backendRoot =>
                Directory.EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories))
            .Where(static path =>
                File.ReadAllText(path).Contains(
                    "Marshal.GetFunctionPointerForDelegate",
                    StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToArray();

        offenders.ShouldBeEmpty(
            "unmanaged entry points must live in a stable bridge; a thunk targeting collectible code roots its load context");

        string bridge = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Interop/RendererImGuiViewportCallbackBridge.cs");
        string adapter = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/UI/OpenGLRenderer.ImGuiViewportCallbackAdapter.cs");
        bridge.ShouldContain("[UnmanagedCallersOnly(");
        bridge.ShouldContain("Register(");
        adapter.ShouldContain("IRendererImGuiViewportCallbacks");
    }

    [Test]
    public void FailureInjectionHooks_CoverEveryReloadBoundaryCategory()
    {
        string source = string.Join(
            Environment.NewLine,
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RendererReload/RendererReplacementCoordinator.cs"),
            ReadWorkspaceFile("XREngine.Editor/Rendering/HotReload/RendererBackendBuildService.cs"),
            ReadWorkspaceFile("XREngine.Editor/Rendering/HotReload/RendererBackendModuleLoader.cs"),
            ReadWorkspaceFile("XREngine.Editor/Rendering/HotReload/RendererHotReloadService.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLShader.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkOrchestration.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkShader.cs"));

        foreach (XREngine.Rendering.RendererReloadInjectedFailure failure in
                 Enum.GetValues<XREngine.Rendering.RendererReloadInjectedFailure>())
        {
            if (failure == XREngine.Rendering.RendererReloadInjectedFailure.None)
                continue;

            source.Contains(
                    $"RendererReloadInjectedFailure.{failure}",
                    StringComparison.Ordinal)
                .ShouldBeTrue(
                    $"the {failure} failure category must be wired to a real reload boundary");
        }
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(FindWorkspaceRoot(), relativePath));
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull();
        return directory.FullName;
    }
}

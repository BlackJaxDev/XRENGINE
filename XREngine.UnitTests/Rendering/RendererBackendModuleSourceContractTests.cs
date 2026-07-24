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
            "XRENGINE/Engine/Engine.RuntimeRenderingHostServices.cs");

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
            "XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.cs");
        string hostServices = ReadWorkspaceFile(
            "XRENGINE/Engine/Engine.RuntimeRenderingHostServices.cs");

        renderingPolicy.ShouldNotContain("is VulkanRenderer");
        renderingPolicy.ShouldContain("BackendId == RendererBackendId.Vulkan");
        hostServices.ShouldNotContain("is OpenGLRenderer");
        hostServices.ShouldNotContain("is VulkanRenderer");
        hostServices.ShouldContain("GetPrimaryRendererCapability<");
    }

    [Test]
    public void StaticBuiltInsAndCollectibleModulesShareFactoryAndRegistrationContracts()
    {
        string builtIns = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RendererModules/BuiltInRendererBackendModules.cs");
        string registration = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RendererModules/RendererBackendRegistration.cs");

        builtIns.ShouldNotContain("AssemblyLoadContext");
        builtIns.ShouldNotContain("GetTypes(");
        builtIns.ShouldContain("catalog.Register(");
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
                return source.Contains("OpenGLRenderer", StringComparison.Ordinal)
                    || source.Contains("VulkanRenderer", StringComparison.Ordinal);
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
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Commands/OpenGLRenderer.IndirectSubmissionCapability.cs");
        string vkImplementation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.IndirectSubmissionCapability.cs");

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

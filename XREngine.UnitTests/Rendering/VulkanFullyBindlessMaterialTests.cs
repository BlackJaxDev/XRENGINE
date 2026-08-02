using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanFullyBindlessMaterialTests
{
    [Test]
    public void BindlessMaterialMode_EnvOverrideWins()
    {
        string? previous = Environment.GetEnvironmentVariable(VulkanFeatureProfile.BindlessMaterialModeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(VulkanFeatureProfile.BindlessMaterialModeEnvVar, "Required");

            VulkanFeatureProfile.ResolveBindlessMaterialMode(
                    EVulkanBindlessMaterialMode.Auto,
                    legacyEnabled: true)
                .ShouldBe(EVulkanBindlessMaterialMode.Required);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VulkanFeatureProfile.BindlessMaterialModeEnvVar, previous);
        }
    }

    [Test]
    public void VulkanBindlessMaterialDescriptorTable_SourceContracts_ArePresent()
    {
        string tableSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.BindlessMaterialTextureTable.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");
        string profileSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Vulkan/VulkanFeatureProfile.cs");
        string hostInterfaceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderSettingsServices.cs");
        string commandBufferSource = SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string bindingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Descriptors/VulkanBindlessMaterialDescriptorBinding.cs");
        string frameOpSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/IndirectDrawOp.cs");

        profileSource.ShouldContain("enum EVulkanBindlessMaterialMode");
        profileSource.ShouldContain("enum EVulkanBindlessMaterialCapabilityTier");
        profileSource.ShouldContain("XREngineEnvironmentVariables.VulkanBindlessMaterialMode");
        profileSource.ShouldContain("VulkanBindlessMaterialCapability");

        tableSource.ShouldContain("ResolveMaterialTextureDescriptorReference");
        tableSource.ShouldContain("MaterialTextureReferenceResolution.Pending");
        tableSource.ShouldContain("MaterialTextureReferenceResolution.Failed");
        tableSource.ShouldContain("slot.Dirty");
        tableSource.ShouldContain("TryEnsureGlobalMaterialTextureDescriptorTable");
        tableSource.ShouldContain("DescriptorPoolCreateFlags.UpdateAfterBindBit");
        tableSource.ShouldContain("DescriptorSetVariableDescriptorCountAllocateInfo");
        tableSource.ShouldContain("DstArrayElement = descriptorIndex");
        tableSource.ShouldContain("GetPlaceholderImageInfo(DescriptorType.CombinedImageSampler");
        tableSource.ShouldContain("GlobalMaterialTextureRetireDelayFrames");
        tableSource.ShouldContain("ValidateRequiredVulkanBindlessMaterialCapability");
        tableSource.ShouldContain("TryBindGlobalMaterialTextureDescriptorSet");
        tableSource.ShouldContain("BeginGlobalMaterialTextureDescriptorScope");
        tableSource.ShouldContain("CaptureGlobalMaterialTextureDescriptorBindingForNextFrameOp");
        tableSource.ShouldContain("GlobalMaterialTextureDescriptorCapacity");
        tableSource.ShouldContain("GlobalMaterialTextureDescriptorWritesTotal");
        tableSource.ShouldContain("GlobalMaterialTextureDescriptorFallbackReferencesTotal");

        bindingSource.ShouldContain("VulkanBindlessMaterialDescriptorBinding");
        frameOpSource.ShouldContain("VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures");
        commandBufferSource.ShouldContain("op.BindlessMaterialTextures is { } bindlessMaterialTextures");
        commandBufferSource.ShouldContain("TryBindGlobalMaterialTextureDescriptorSet(");

        logicalDeviceSource.ShouldContain("DestroyGlobalMaterialTextureDescriptorTable();");
        logicalDeviceSource.ShouldContain("ValidateRequiredVulkanBindlessMaterialCapability();");
        logicalDeviceSource.ShouldContain("VulkanBindlessMaterialCapability bindlessMaterialCapability = RefreshBindlessMaterialCapability();");

        hostInterfaceSource.ShouldContain("EVulkanBindlessMaterialMode VulkanBindlessMaterialMode");
        hostInterfaceSource.ShouldContain("bool EnableVulkanBindlessMaterialTable");
    }

    [Test]
    public void VulkanBindlessMaterialShaderAndRowBuild_SourceContracts_ArePresent()
    {
        string materialLayoutSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Materials/MaterialBindingLayout.cs");
        string materialTableSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.cs");
        string materialReferenceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTextureReference.cs");
        string passSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        materialLayoutSource.ShouldContain("EMaterialTableTextureReferenceMode.VulkanDescriptorIndexTable");
        materialLayoutSource.ShouldContain("layout(set = 2, binding = 31) uniform sampler2D XR_BindlessMaterialTextures[];");
        materialLayoutSource.ShouldContain("XR_BindlessMaterialTextures[nonuniformEXT(descriptorIndex)]");

        materialReferenceSource.ShouldContain("FromVulkanDescriptorIndex");
        materialTableSource.ShouldContain("ResolveShaderTextureIndex");

        passSource.ShouldContain("EMaterialTableTextureReferenceMode.VulkanDescriptorIndexTable");
        passSource.ShouldContain("materialCapability.TryEnsureMaterialTextureTable");
        passSource.ShouldContain("materialCapability?.ResolveMaterialTextureReference");
        passSource.ShouldContain("materialCapability?.FlushMaterialTextureTableUpdates();");

        hybridSource.ShouldContain("ResolveMaterialTableTextureReferenceMode");
        hybridSource.ShouldContain("EMaterialTableTextureReferenceMode.OpenGLBindlessHandleTable");
        hybridSource.ShouldContain("EMaterialTableTextureReferenceMode.VulkanDescriptorIndexTable");
        hybridSource.ShouldContain("#extension GL_EXT_nonuniform_qualifier : require");
        hybridSource.ShouldContain("bindVulkanMaterialTextureDescriptorTable: textureReferenceMode == EMaterialTableTextureReferenceMode.VulkanDescriptorIndexTable");
        hybridSource.ShouldContain("BeginGlobalMaterialTextureDescriptorScope");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}

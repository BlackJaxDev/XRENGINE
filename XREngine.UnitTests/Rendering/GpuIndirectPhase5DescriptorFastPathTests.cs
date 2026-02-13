using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuIndirectPhase5DescriptorFastPathTests
{
    [Test]
    public void Phase5_DescriptorIndexingPolicy_SourceContracts_ArePresent()
    {
        string profileSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanFeatureProfile.cs");
        string deviceSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs");

        profileSource.ShouldContain("ResolveDescriptorIndexingPreference");
        profileSource.ShouldContain("EnableDescriptorIndexing");
        profileSource.ShouldContain("ResolveDescriptorContractValidationPreference");
        profileSource.ShouldContain("ActiveGeometryFetchMode");

        deviceSource.ShouldContain("QueryDescriptorIndexingCapabilities()");
        deviceSource.ShouldContain("descriptorIndexingRequestedByProfile");
        deviceSource.ShouldContain("descriptorIndexingCapabilityReady");
        deviceSource.ShouldContain("_supportsDescriptorIndexing = enableDescriptorIndexing;");
    }

    [Test]
    public void Phase5_MaterialTableAndResidency_SourceContracts_ArePresent()
    {
        string passSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs");
        string tableSource = ReadWorkspaceFile("XRENGINE/Rendering/Materials/GPUMaterialTable.cs");

        passSource.ShouldContain("PrepareMaterialTableAndValidateResidency");
        passSource.ShouldContain("SetMaterialTable(_materialTable);");
        passSource.ShouldContain("Material residency guarantee failed before indirect draw submission.");
        passSource.ShouldContain("Vulkan geometry fetch prototype is selected but atlas path remains active pending benchmark sign-off.");

        tableSource.ShouldContain("public bool Remove(uint materialID)");
        tableSource.ShouldContain("public uint TrimTrailingUnused(uint minimumCapacity = 128u)");
        tableSource.ShouldContain("private readonly HashSet<uint> _activeMaterialIds");
    }

    [Test]
    public void Phase5_DescriptorContractValidation_SourceContracts_ArePresent()
    {
        string contractSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanDescriptorContracts.cs");
        string programSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs");

        contractSource.ShouldContain("internal static class VulkanDescriptorContracts");
        contractSource.ShouldContain("TryValidateContract");
        contractSource.ShouldContain("Material descriptor tier (set=2) is present but has no image resources");

        programSource.ShouldContain("VulkanDescriptorContracts.TryValidateContract");
        programSource.ShouldContain("Descriptor contract validation failed for program");
    }

    [Test]
    public void Phase5_MaterialTable_ControlledCompaction_TrimsUnusedTail()
    {
        using GPUMaterialTable table = new(initialCapacity: 16);

        table.AddOrUpdate(2u, new GPUMaterialEntry { Flags = 1u });
        table.AddOrUpdate(12u, new GPUMaterialEntry { Flags = 1u });
        table.Capacity.ShouldBeGreaterThanOrEqualTo(13u);

        table.Remove(12u).ShouldBeTrue();
        uint newCapacity = table.TrimTrailingUnused(minimumCapacity: 4u);

        newCapacity.ShouldBeGreaterThanOrEqualTo(4u);
        newCapacity.ShouldBeLessThanOrEqualTo(16u);
        table.ActiveMaterialIds.ShouldContain(2u);
        table.ActiveMaterialIds.ShouldNotContain(12u);
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

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

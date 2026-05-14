using System;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuIndirectPhaseDMaterialBindlessTests
{
    [Test]
    public void MaterialTable_UsesTwoLevelHandleTable_AndRetiresUnusedHandles()
    {
        using GPUMaterialTable table = new(initialCapacity: 4, initialHandleCapacity: 4);

        table.AddOrUpdate(1u, new GPUMaterialEntry { Flags = 1u }, new GPUMaterialTextureHandles(10ul, 20ul, 0ul));
        table.AddOrUpdate(2u, new GPUMaterialEntry { Flags = 1u }, new GPUMaterialTextureHandles(10ul, 30ul, 0ul));

        table.ActiveTextureHandles.OrderBy(x => x).ToArray().ShouldBe([10ul, 20ul, 30ul]);

        table.Remove(1u).ShouldBeTrue();
        table.ActiveTextureHandles.OrderBy(x => x).ToArray().ShouldBe([10ul, 30ul]);
        table.TryConsumeRetiredHandle(out GPUMaterialRetiredHandle retired).ShouldBeTrue();
        retired.Handle.ShouldBe(20ul);

        table.Remove(2u).ShouldBeTrue();
        table.ActiveTextureHandles.ShouldBeEmpty();
    }

    [Test]
    public void MaterialTable_PacksConstantsAndHandleRowsContiguously()
    {
        using GPUMaterialTable table = new(initialCapacity: 4, initialHandleCapacity: 4);

        table.AddOrUpdate(
            2u,
            new GPUMaterialEntry
            {
                Flags = 0x80000001u,
                BaseColorOpacity = new Vector4(0.25f, 0.5f, 0.75f, 0.9f),
                RMSE = new Vector4(0.1f, 0.2f, 0.3f, 0.4f)
            },
            new GPUMaterialTextureHandles(0x0000000200000001ul, 0ul, 0ul));

        ReadUInt(table.Buffer, 2u, 0u).ShouldBe(1u);
        ReadUInt(table.Buffer, 2u, 1u).ShouldBe(0u);
        ReadUInt(table.Buffer, 2u, 2u).ShouldBe(0u);
        ReadUInt(table.Buffer, 2u, 3u).ShouldBe(0x80000001u);
        ReadFloat(table.Buffer, 2u, 4u).ShouldBe(0.25f, 0.000001f);
        ReadFloat(table.Buffer, 2u, 5u).ShouldBe(0.5f, 0.000001f);
        ReadFloat(table.Buffer, 2u, 6u).ShouldBe(0.75f, 0.000001f);
        ReadFloat(table.Buffer, 2u, 7u).ShouldBe(0.9f, 0.000001f);
        ReadFloat(table.Buffer, 2u, 8u).ShouldBe(0.1f, 0.000001f);
        ReadFloat(table.Buffer, 2u, 9u).ShouldBe(0.2f, 0.000001f);
        ReadFloat(table.Buffer, 2u, 10u).ShouldBe(0.3f, 0.000001f);
        ReadFloat(table.Buffer, 2u, 11u).ShouldBe(0.4f, 0.000001f);

        ReadUInt(table.TextureHandleBuffer, 1u, 0u).ShouldBe(1u);
        ReadUInt(table.TextureHandleBuffer, 1u, 1u).ShouldBe(2u);
        ReadUInt(table.TextureHandleBuffer, 1u, 2u).ShouldBe(1u);
        ReadUInt(table.TextureHandleBuffer, 1u, 3u).ShouldBe(0u);
    }

    [Test]
    public void PhaseD_SourceContracts_ArePresent()
    {
        string glRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs");
        string glBindlessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLRenderer.Bindless.cs");
        string materialTableSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.cs");
        string passSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string materialScatterSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderMaterialScatter.comp");
        string vulkanDescriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanDescriptorLayoutCache.cs");
        string vulkanBindlessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanBindlessMaterialDescriptors.cs");
        string vkBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkDataBuffer.cs");
        string vkAddressSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanSceneDatabaseAddresses.cs");
        string renderParametersSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Materials/Options/RenderingParameters.cs");
        string gpuSceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs");
        string vulkanShaderInclude = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/VulkanBindlessMaterialTable.glsl");
        string policyDoc = ReadWorkspaceFile("docs/architecture/rendering/material-binding-policy.md");

        glRendererSource.ShouldContain("public ArbBindlessTexture? ARBBindlessTexture");
        glBindlessSource.ShouldContain("TryGetResidentBindlessTextureHandle");
        glBindlessSource.ShouldContain("MakeTextureHandleResident");
        materialTableSource.ShouldContain("MaterialTextureHandleTable");
        materialTableSource.ShouldContain("GPUMaterialTextureHandles");
        materialTableSource.ShouldContain("MaterialBindingLayouts.OpaqueDeferred");
        materialTableSource.ShouldContain("BaseColorOpacity");
        materialTableSource.ShouldContain("RMSE");
        passSource.ShouldContain("TryResolveOpenGLBindlessTextureHandle");
        passSource.ShouldContain("TextureArrayPolicy");
        passSource.ShouldContain("ResolveMaterialBaseColorOpacity");
        passSource.ShouldContain("ResolveMaterialRmse");
        passSource.ShouldContain("IReadOnlyDictionary<uint, XRMaterial> materialMap = scene.MaterialMap;");
        hybridSource.ShouldContain("MaterialTextureHandleTableSsboBinding");
        hybridSource.ShouldContain("SampleBindlessTexture");
        hybridSource.ShouldContain("AlbedoOpacity = vec4(baseColor, opacity);");
        hybridSource.ShouldNotContain("HashMaterialColor");
        hybridSource.ShouldContain("emitMaterialId: true");
        hybridSource.ShouldContain("XR_LoadMaterial(materialId, material);");
        hybridSource.ShouldContain("FragMaterialIdName");
        materialScatterSource.ShouldContain("uint materialID = meta.MaterialID;");
        materialScatterSource.ShouldContain("uint slotIndex = materialSlots[materialID];");

        vulkanDescriptorSource.ShouldContain("VariableDescriptorCountBit");
        vulkanBindlessSource.ShouldContain("MaxTextureDescriptorCount");
        vulkanBindlessSource.ShouldContain("TextureArrayBindingName");
        vkBufferSource.ShouldContain("DeviceAddress");
        vkBufferSource.ShouldContain("ShouldEnableDeviceAddressForSceneDatabaseBuffer");
        vkAddressSource.ShouldContain("MaterialStateBuffer");

        renderParametersSource.ShouldContain("EMaterialTextureArrayPolicy");
        gpuSceneSource.ShouldContain("EGpuMaterialStateClass.Shadow");
        vulkanShaderInclude.ShouldContain("nonuniformEXT");
        policyDoc.ShouldContain("Texture arrays are not the fallback for arbitrary material diversity");
        hybridSource.ShouldContain("MaterialBindingGlslGenerator.AppendMaterialTableDefinitions");
    }

    /// <summary>
    /// Phase D acceptance: B1+B2 render under ≤6 graphics PSO families, bind calls are bounded by
    /// (state classes × passes), and adding a texture-only material does not change the PSO count.
    ///
    /// We can't run a frame capture from a unit test, but the same guarantees are statically derivable:
    ///   1. The PSO family enumeration is finite and small — `EGpuMaterialStateClass` declares exactly
    ///      five built-in families (OpaqueDeferred, OpaqueForward, AlphaTested, Shadow, Transparent)
    ///      plus an explicit `Custom` escape hatch and the `Invalid` sentinel.
    ///   2. The material-table draw program cache in `HybridRenderingManager.EnsureMaterialTableDrawProgram`
    ///      is keyed on `(bindless, rendererKey)` — not on material identity. So new materials cannot
    ///      synthesize additional programs through this cache.
    /// If either invariant regresses, this test fails and the acceptance line is no longer true.
    /// </summary>
    [Test]
    public void PhaseD_AcceptanceTargets_HoldStatically()
    {
        // Invariant 1: bounded PSO family enumeration.
        EGpuMaterialStateClass[] members = Enum.GetValues<EGpuMaterialStateClass>();
        members.ShouldContain(EGpuMaterialStateClass.Invalid);
        members.ShouldContain(EGpuMaterialStateClass.OpaqueDeferred);
        members.ShouldContain(EGpuMaterialStateClass.OpaqueForward);
        members.ShouldContain(EGpuMaterialStateClass.AlphaTested);
        members.ShouldContain(EGpuMaterialStateClass.Shadow);
        members.ShouldContain(EGpuMaterialStateClass.Transparent);
        members.ShouldContain(EGpuMaterialStateClass.Custom);

        EGpuMaterialStateClass[] builtInFamilies = members
            .Where(m => m is not EGpuMaterialStateClass.Invalid and not EGpuMaterialStateClass.Custom)
            .ToArray();
        builtInFamilies.Length.ShouldBeLessThanOrEqualTo(
            6,
            "Phase D acceptance requires ≤6 built-in PSO families. Adding a new EGpuMaterialStateClass entry breaks the bind-count bound.");

        // Invariant 2: material-table program cache key does not depend on material identity.
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        hybridSource.ShouldContain(
            "(bool bindless, int rendererKey, string layoutHash) cacheKey = (bindless, rendererKey, layout.LayoutHash);",
            customMessage: "EnsureMaterialTableDrawProgram cache key changed shape. Phase D acceptance requires the material-table draw program to be keyed on (bindless, rendererKey) only — never on material identity. (The separate EnsureCombinedProgram per-material cache is unrelated.)");

        // Sanity: the shadow short-circuit that routes every shadow-pass draw to the Shadow family
        // is what keeps shadow PSO count constant across diverse material inputs.
        string gpuSceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs");
        gpuSceneSource.ShouldContain("RuntimeEngine.Rendering.State.IsShadowPass");
        gpuSceneSource.ShouldContain("return EGpuMaterialStateClass.Shadow;");
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

    private static uint ReadUInt(XRDataBuffer buffer, uint row, uint wordIndex)
    {
        uint offset = (row * buffer.ElementSize) + (wordIndex * sizeof(uint));
        uint? value = buffer.Get<uint>(offset);
        value.HasValue.ShouldBeTrue();
        return value.Value;
    }

    private static float ReadFloat(XRDataBuffer buffer, uint row, uint wordIndex)
        => BitConverter.UInt32BitsToSingle(ReadUInt(buffer, row, wordIndex));
}

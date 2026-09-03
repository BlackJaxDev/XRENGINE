using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Shaders;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedShaderAccessContractTests
{
    private const string AccessRoot =
        "Build/CommonAssets/Shaders/Advanced/Access/";

    [Test]
    public void Aggregator_ExposesEveryRequiredLogicalRecordAccessor()
    {
        string aggregator = SourceContractWorkspace.ReadFile(
            $"{AccessRoot}AdvancedAccess.glslinc");
        string[] includes =
        [
            "AdvancedHandleAccess.glslinc",
            "AdvancedDrawAccess.glslinc",
            "AdvancedInstanceAccess.glslinc",
            "AdvancedMeshAccess.glslinc",
            "AdvancedMaterialAccess.glslinc",
            "AdvancedViewAccess.glslinc",
            "AdvancedLightAccess.glslinc",
            "AdvancedShadowAccess.glslinc",
            "AdvancedTextureAccess.glslinc",
            "AdvancedDeformationAccess.glslinc",
        ];

        foreach (string include in includes)
            aggregator.ShouldContain($"#include \"{include}\"");

        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedHandleAccess.glslinc")
            .ShouldContain("XR_ADV_ResolveHandle");
        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedDrawAccess.glslinc")
            .ShouldContain("XR_ADV_LoadDraw");
        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedInstanceAccess.glslinc")
            .ShouldContain("XR_ADV_LoadInstance");
        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedMeshAccess.glslinc")
            .ShouldContain("XR_ADV_LoadGeometry");
        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedMaterialAccess.glslinc")
            .ShouldContain("XR_ADV_LoadMaterial");
        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedViewAccess.glslinc")
            .ShouldContain("XR_ADV_LoadView");
        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedLightAccess.glslinc")
            .ShouldContain("XR_ADV_LoadLight");
        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedShadowAccess.glslinc")
            .ShouldContain("XR_ADV_LoadShadow");
        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedTextureAccess.glslinc")
            .ShouldContain("XR_ADV_SampleTexture2D");
        SourceContractWorkspace.ReadFile($"{AccessRoot}AdvancedDeformationAccess.glslinc")
            .ShouldContain("XR_ADV_LoadDeformation");
    }

    [Test]
    public void Preamble_EmitsCpuLayoutAuthorityAndBackendEncodingSelection()
    {
        string openGl = AdvancedShaderAccessLibrary.BuildPreamble(
            RuntimeGraphicsApiKind.OpenGL,
            EAdvancedTextureIndirectionMode.OpenGlBindlessHandles);
        string vulkan = AdvancedShaderAccessLibrary.BuildPreamble(
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing,
            descriptorSet: 3u);

        openGl.ShouldContain("#extension GL_ARB_bindless_texture : require");
        openGl.ShouldContain("#define XR_ADV_BACKEND_OPENGL 1");
        openGl.ShouldContain("#define XR_ADV_TEXTURE_MODE_OPENGL_BINDLESS 1");
        vulkan.ShouldContain("#extension GL_EXT_nonuniform_qualifier : require");
        vulkan.ShouldContain("#define XR_ADV_BACKEND_VULKAN 1");
        vulkan.ShouldContain("#define XR_ADV_TEXTURE_MODE_VULKAN_INDEXING 1");
        vulkan.ShouldContain("#define XR_ADV_GLOBAL_SET 3");
        foreach (string preamble in new[] { openGl, vulkan })
        {
            preamble.ShouldContain("#define XR_ADV_CPU_SIZE_GEOMETRY 320");
            preamble.ShouldContain("#define XR_ADV_CPU_SIZE_HANDLE_LOOKUP 8");
            preamble.ShouldContain("#define XR_ADV_BINDING_HANDLE_LOOKUPS 27");
            preamble.ShouldContain("#define XR_ADV_CPU_OFFSET_GEOMETRY_BOUNDS_SPHERE 240");
            preamble.ShouldContain(
                $"#include \"{AdvancedShaderAccessLibrary.IncludePath}\"");
            preamble.ShouldNotContain("#version");
        }

        Should.Throw<ArgumentException>(() =>
            AdvancedShaderAccessLibrary.BuildPreamble(
                RuntimeGraphicsApiKind.OpenGL,
                EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing));
    }

    [Test]
    public void LayoutInclude_StaticallyChecksSizesOffsetsAndMatrixConvention()
    {
        string layout = SourceContractWorkspace.ReadFile(
            $"{AccessRoot}AdvancedLayout.glslinc");

        layout.ShouldContain("#error \"Advanced CPU/GPU record size mismatch.\"");
        layout.ShouldContain("#error \"Advanced CPU/GPU record byte-offset mismatch.\"");
        layout.ShouldContain("#define XR_ADV_MATRIX_STORAGE_ROW_MAJOR 1");
        layout.ShouldContain("#define XR_ADV_VECTOR_CONVENTION_ROW_VECTOR 1");
        layout.ShouldContain("return value * matrixValue;");
        layout.ShouldContain("std430, row_major");
        Should.NotThrow(AdvancedShaderRecordLayout.ValidateCpuLayouts);
    }

    [Test]
    public void ShaderVisibleRecords_RoundTripTheirPackedBytesAtFourByteAlignment()
    {
        AssertPackedByteRoundTrip<AdvancedGpuHandle>();
        AssertPackedByteRoundTrip<AdvancedGpuHandleLookup>();
        AssertPackedByteRoundTrip<AdvancedGpuHandleRemap>();
        AssertPackedByteRoundTrip<AdvancedBufferReference>();
        AssertPackedByteRoundTrip<AdvancedDrawRecord>();
        AssertPackedByteRoundTrip<AdvancedInstanceRecord>();
        AssertPackedByteRoundTrip<AdvancedGeometryRecord>();
        AssertPackedByteRoundTrip<AdvancedTransformRecord>();
        AssertPackedByteRoundTrip<AdvancedDeformationRecord>();
        AssertPackedByteRoundTrip<AdvancedRenderStateRecord>();
        AssertPackedByteRoundTrip<AdvancedEditorIdentityRecord>();
        AssertPackedByteRoundTrip<AdvancedMaterialRecord>();
        AssertPackedByteRoundTrip<AdvancedShadingKernelRecord>();
        AssertPackedByteRoundTrip<AdvancedMaterialLayoutRecord>();
        AssertPackedByteRoundTrip<AdvancedMaterialLayoutMember>();
        AssertPackedByteRoundTrip<AdvancedMaterialTextureBinding>();
        AssertPackedByteRoundTrip<AdvancedTextureReference>();
        AssertPackedByteRoundTrip<AdvancedSamplerReference>();
        AssertPackedByteRoundTrip<AdvancedViewRecord>();
        AssertPackedByteRoundTrip<AdvancedLightRecord>();
        AssertPackedByteRoundTrip<AdvancedShadowRecord>();
        AssertPackedByteRoundTrip<AdvancedProbeRecord>();
        AssertPackedByteRoundTrip<AdvancedEnvironmentRecord>();
        AssertPackedByteRoundTrip<AdvancedDecalRecord>();
        AssertPackedByteRoundTrip<AdvancedGiResourceRecord>();
        AssertPackedByteRoundTrip<AdvancedTextureRecord>();
        AssertPackedByteRoundTrip<AdvancedSamplerRecord>();
        AssertPackedByteRoundTrip<AdvancedEncodedTextureReference>();
        AssertPackedByteRoundTrip<AdvancedEncodedSamplerReference>();
    }

    [Test]
    public void DiagnosticBoundsMode_IsACompileTimeOptionWithNoProductionDefine()
    {
        string production = AdvancedShaderAccessLibrary.BuildPreamble(
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedTextureIndirectionMode.VulkanDescriptorHeap);
        string diagnostic = AdvancedShaderAccessLibrary.BuildPreamble(
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedTextureIndirectionMode.VulkanDescriptorHeap,
            diagnosticBounds: true);
        string drawAccess = SourceContractWorkspace.ReadFile(
            $"{AccessRoot}AdvancedDrawAccess.glslinc");

        production.ShouldNotContain("#define XR_ADV_DIAGNOSTIC_BOUNDS");
        diagnostic.ShouldContain("#define XR_ADV_DIAGNOSTIC_BOUNDS 1");
        drawAccess.ShouldContain("#ifdef XR_ADV_DIAGNOSTIC_BOUNDS");
        drawAccess.ShouldContain("XR_ADV_RecordOutOfBounds");
        string handleAccess = SourceContractWorkspace.ReadFile(
            $"{AccessRoot}AdvancedHandleAccess.glslinc");
        handleAccess.ShouldContain("bool generationMatches");
        handleAccess.ShouldContain("? lookup.denseIndex");
    }

    [Test]
    public void OpenGlAndVulkan_UseTheSameLogicalAccessorSource()
    {
        string textureAccess = SourceContractWorkspace.ReadFile(
            $"{AccessRoot}AdvancedTextureAccess.glslinc");
        string[] allIncludes = Directory.GetFiles(
            Path.Combine(FindRepositoryRoot(), AccessRoot),
            "*.glslinc",
            SearchOption.TopDirectoryOnly);

        textureAccess.ShouldContain("XR_ADV_TEXTURE_MODE_OPENGL_BINDLESS");
        textureAccess.ShouldContain("XR_ADV_TEXTURE_MODE_VULKAN_INDEXING");
        textureAccess.ShouldContain("XR_ADV_TEXTURE_MODE_VULKAN_HEAP");
        textureAccess.ShouldContain("XR_ADV_TEXTURE_MODE_ARRAY");
        textureAccess.ShouldContain("XR_ADV_SampleTexture2D");
        foreach (string path in allIncludes)
            File.ReadAllText(path).ShouldNotContain("#version");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XRENGINE repository root.");
    }

    private static void AssertPackedByteRoundTrip<T>() where T : unmanaged
    {
        int byteCount = Marshal.SizeOf<T>();
        (byteCount % 4).ShouldBe(0, $"{typeof(T).Name} must retain four-byte alignment.");

        Span<byte> source = stackalloc byte[byteCount];
        Span<byte> destination = stackalloc byte[byteCount];
        for (int index = 0; index < source.Length; index++)
            source[index] = unchecked((byte)((index * 37) + 11));

        T record = MemoryMarshal.Read<T>(source);
        MemoryMarshal.Write(destination, in record);
        destination.SequenceEqual(source)
            .ShouldBeTrue($"{typeof(T).Name} did not preserve its packed byte image.");
    }
}

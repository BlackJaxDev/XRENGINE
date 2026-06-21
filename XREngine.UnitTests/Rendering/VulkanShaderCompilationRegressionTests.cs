using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanShaderCompilationRegressionTests
{
    private static readonly string[] FragmentShaders =
    [
        "Scene3D/CubemapToOctahedron.fs",
        "Scene3D/DebugTransformId.fs",
        "Scene3D/DeferredDecal.fs",
        "Scene3D/DeferredDecalForwardWeightedOit.fs",
        "Scene3D/DeferredLightingDir.fs",
        "Scene3D/DeferredLightCombine.fs",
        "Scene3D/DeferredLightCombineStereo.fs",
        "Scene3D/DeferredLightingPoint.fs",
        "Scene3D/DeferredLightingSpot.fs",
        "Scene3D/IrradianceConvolutionCubemapOcta.fs",
        "Scene3D/IrradianceConvolutionEquirect.fs",
        "Scene3D/IrradianceConvolutionEquirectOcta.fs",
        "Scene3D/IrradianceConvolutionOcta.fs",
        "Scene3D/PostProcess.fs",
        "Scene3D/PrefilterCubemapOcta.fs",
        "Scene3D/PrefilterEquirect.fs",
        "Scene3D/PrefilterEquirectOcta.fs",
        "Scene3D/PrefilterOcta.fs",
        "Common/TexturedDeferred.fs",
        "Common/TexturedAlphaDeferred.fs",
        "Common/TexturedNormalDeferred.fs",
        "Common/TexturedNormalAlphaDeferred.fs",
        "Common/TexturedSpecDeferred.fs",
        "Common/TexturedSpecAlphaDeferred.fs",
        "Common/TexturedNormalSpecDeferred.fs",
        "Common/TexturedNormalSpecAlphaDeferred.fs",
        "Common/UITextBatched.fs",
        "Common/LitColoredForward.fs",
        "Common/LitTexturedForward.fs",
        "Common/LitTexturedAlphaForward.fs",
        "Common/LitTexturedNormalForward.fs",
        "Common/LitTexturedNormalSpecAlphaForward.fs",
        "Common/LitTexturedSpecAlphaForward.fs"
    ];

    private static readonly string[] VertexShaders =
    [
        "Common/UIQuadBatched.vs",
        "Common/UITextBatched.vs",
    ];

    private static readonly string[] DebugVertexShaders =
    [
        "Common/Debug/vs/InstancedDebugPrimitive.vs",
        "Common/Debug/vs/InstancedDebugPrimitiveStereoMV2.vs",
        "Common/Debug/vs/InstancedDebugPrimitiveStereoNV.vs",
    ];

    private static readonly string[] GeometryShaders =
    [
        "Common/Debug/gs/LineInstance.gs",
        "Common/Debug/gs/LineInstanceCompressed.gs",
        "Common/Debug/gs/PointInstance.gs",
        "Common/Debug/gs/PointInstanceCompressed.gs",
        "Common/Debug/gs/TriangleInstance.gs",
        "Common/Debug/gs/TriangleInstanceCompressed.gs",
    ];

    private static readonly string[] GizmoGeometryShaders =
    [
        "Common/GizmoLine.gs",
        "Common/GizmoArrowHead.gs",
    ];

    private static readonly (string ShaderRelativePath, string ColorInputName)[] GizmoFragmentShaders =
    [
        ("Common/GizmoLine.fs", "GizmoLineColor"),
        ("Common/GizmoTriangle.fs", "ArrowColor"),
    ];

    private static readonly string[] UberVertexShaders =
    [
        "Uber/UberShader.vert",
        "Uber/outline.vert",
    ];

    private static readonly string[] UberFragmentShaders =
    [
        "Uber/UberShader.frag",
        "Uber/outline.frag",
    ];

    private static readonly string[] ComputeShaders =
    [
        "Compute/AO/SpatialHashAO.comp",
        "Compute/AO/SpatialHashAOStereo.comp",
    ];

    [TestCaseSource(nameof(VertexShaders))]
    public void VertexShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Vertex, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);

        // Verify the rewritten source still contains SSBO declarations
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("buffer");

        // For UITextBatched, verify gl_InstanceID was rewritten to gl_InstanceIndex
        if (shaderRelativePath.Contains("UITextBatched"))
            rewrittenSource.ShouldContain("gl_InstanceIndex");
    }

    [Test]
    public void SkyboxVertexShader_CompilesToSpirv_ForVulkan()
    {
        LoadedShaderSource loadedShader = LoadShaderSource("Scene3D/Skybox.vs");
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Vertex, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("GetFarClipZ");
    }

    [TestCaseSource(nameof(DebugVertexShaders))]
    public void DebugVertexShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Vertex, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("#define XRENGINE_VULKAN 1");
        rewrittenSource.ShouldContain("flat out int instanceID");
        rewrittenSource.ShouldContain("gl_InstanceIndex");
        rewrittenSource.ShouldNotContain("out gl_PerVertex");
        rewrittenSource.ShouldContain("gl_PointSize = 1.0");
        rewrittenSource.ShouldNotContain("gl_CullDistance");
    }

    [TestCaseSource(nameof(GeometryShaders))]
    public void GeometryShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Geometry, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("#define XRENGINE_VULKAN 1");
        rewrittenSource.ShouldContain("flat in int instanceID");
        rewrittenSource.ShouldContain("gl_Position");
        rewrittenSource.ShouldNotContain("in gl_PerVertex");
        rewrittenSource.ShouldNotContain("out gl_PerVertex");
        rewrittenSource.ShouldNotContain("gl_CullDistance");

        if (shaderRelativePath.Contains("PointInstance", StringComparison.Ordinal) ||
            shaderRelativePath.Contains("LineInstance", StringComparison.Ordinal))
        {
            rewrittenSource.ShouldContain("max_vertices = 4");
        }

        if (shaderRelativePath.Contains("LineInstance", StringComparison.Ordinal))
        {
            rewrittenSource.ShouldContain("#ifdef XRENGINE_VULKAN");
            rewrittenSource.ShouldContain("float da = a.z;");
        }
    }

    [TestCaseSource(nameof(GizmoGeometryShaders))]
    public void GizmoGeometryShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Geometry, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("#define XRENGINE_VULKAN 1");
        rewrittenSource.ShouldContain("uniform XREngine_AutoUniforms");
        rewrittenSource.ShouldContain("vec4 MatColor;");
        rewrittenSource.ShouldContain("int ClipDepthRange;");
        rewrittenSource.ShouldNotContain("in gl_PerVertex");
        rewrittenSource.ShouldNotContain("out gl_PerVertex");
    }

    [TestCaseSource(nameof(GizmoFragmentShaders))]
    public void GizmoFragmentShader_ConsumesGeometryStageMaterialColor_ForVulkan((string ShaderRelativePath, string ColorInputName) shaderCase)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderCase.ShaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Fragment, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("#define XRENGINE_VULKAN 1");
        rewrittenSource.ShouldContain(shaderCase.ColorInputName);
        rewrittenSource.ShouldNotContain("vec4 MatColor;");
    }

    [Test]
    public void ForwardLightingTextureArrays_ReflectExpected2DArrayViews()
    {
        LoadedShaderSource loadedShader = LoadShaderSource("Common/LitTexturedForward.fs");
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Fragment, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out _,
            out _,
            out string? rewrittenSource);

        rewrittenSource.ShouldNotBeNull();
        var bindings = VulkanShaderReflection.ExtractBindings(spirv, ShaderStageFlags.FragmentBit, rewrittenSource);

        DescriptorBindingInfo irradiance = bindings.First(binding => binding.Binding == 7);
        DescriptorBindingInfo prefilter = bindings.First(binding => binding.Binding == 8);

        irradiance.Name.ShouldBe("IrradianceArray");
        irradiance.ExpectedImageViewType.ShouldBe(ImageViewType.Type2DArray);
        prefilter.Name.ShouldBe("PrefilterArray");
        prefilter.ExpectedImageViewType.ShouldBe(ImageViewType.Type2DArray);
    }

    [TestCaseSource(nameof(FragmentShaders))]
    public void FragmentShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Fragment, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out _);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
    }

    [TestCaseSource(nameof(UberVertexShaders))]
    public void UberVertexShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Vertex, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out _);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
    }

    [TestCaseSource(nameof(UberFragmentShaders))]
    public void UberFragmentShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Fragment, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out _);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
    }

    [TestCaseSource(nameof(ComputeShaders))]
    public void ComputeShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Compute, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out _);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
    }

    private static LoadedShaderSource LoadShaderSource(string shaderRelativePath)
    {
        string shaderRoot = ResolveShaderRoot();
        string normalizedRelativePath = shaderRelativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.Combine(shaderRoot, normalizedRelativePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Shader file not found: {fullPath}", fullPath);

        return new LoadedShaderSource(fullPath, File.ReadAllText(fullPath));
    }

    private static string ResolveShaderRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Build", "CommonAssets", "Shaders");
            if (Directory.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Build/CommonAssets/Shaders from test base directory.");
    }

    private readonly record struct LoadedShaderSource(string FullPath, string Source);
}

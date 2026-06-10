using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanShaderRewriteDiagnosticsTests
{
    [TestCase("Common/TexturedDeferred.fs")]
    [TestCase("Common/TexturedNormalDeferred.fs")]
    [TestCase("Scene3D/DeferredLightingDir.fs")]
    [TestCase("Scene3D/PostProcess.fs")]
    public void Rewrite_DoesNotEmitKnownBrokenTokens(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        string rewritten = RewriteForVulkanFragment(loadedShader.Source);

        rewritten.ShouldNotContain("jax//");
        rewritten.ShouldNotContain("layout(...)uniform");
        rewritten.ShouldNotContain("syntax error");
        rewritten.ShouldNotContain("XREngine_AutoUniforms_Fragment_Instance.XREngine_AutoUniforms_Fragment_Instance");

        TestContext.WriteLine($"--- Rewritten: {shaderRelativePath} ---");
        string[] lines = rewritten.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < Math.Min(lines.Length, 180); i++)
            TestContext.WriteLine($"{i + 1,4}: {lines[i]}");
    }

    [Test]
    public void Rewrite_PreservesImageFormatQualifier_WhenInjectingSetAndBinding()
    {
        const string source = """
#version 460
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
layout(r32f, binding = 1) uniform image2D ExposureOut;
void main() { }
""";

        string rewritten = RewriteForVulkanCompute(source);
        string lowered = rewritten.ToLowerInvariant();

        lowered.ShouldContain("layout(r32f");
        lowered.ShouldContain("uniform image2d exposureout");
        lowered.ShouldContain("set = 2");
        lowered.ShouldContain("binding = 1");
    }

    [Test]
    public void ComputeShader_WithR32fImage_CompilesAfterRewrite()
    {
        const string source = """
#version 460
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
layout(binding = 0) uniform sampler2D SourceTex;
layout(r32f, binding = 1) uniform image2D ExposureOut;
void main()
{
    vec4 c = texelFetch(SourceTex, ivec2(0,0), 0);
    imageStore(ExposureOut, ivec2(0,0), vec4(c.r, 0.0, 0.0, 0.0));
}
""";

        var shaderSource = new TextFile
        {
            FilePath = "VulkanAutoExposure2D.comp",
            Text = source
        };

        XRShader shader = new(EShaderType.Compute, shaderSource)
        {
            Name = "VulkanAutoExposure2D.comp"
        };

        byte[] spirv = VulkanShaderCompiler.Compile(shader, out string entryPoint, out _, out _);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Rewrite_UsesStageSpecificBindingsForAutoUniformBlocks()
    {
        uint skyboxVertexBinding = RewriteForVulkanAutoUniformBinding(
            LoadShaderSource("Scene3D/Skybox.vs").Source,
            EShaderType.Vertex);
        uint skyboxFragmentBinding = RewriteForVulkanAutoUniformBinding(
            LoadShaderSource("Scene3D/SkyboxDynamic.fs").Source,
            EShaderType.Fragment);
        uint debugGeometryBinding = RewriteForVulkanAutoUniformBinding(
            LoadShaderSource("Common/Debug/gs/PointInstance.gs").Source,
            EShaderType.Geometry);

        skyboxFragmentBinding.ShouldBe(64u);
        skyboxVertexBinding.ShouldBe(72u);
        debugGeometryBinding.ShouldBe(80u);
        new[] { skyboxVertexBinding, skyboxFragmentBinding, debugGeometryBinding }
            .Distinct()
            .Count()
            .ShouldBe(3);
    }

    [Test]
    public void Rewrite_ComputesStd140StructAndMatrixArrayOffsets()
    {
        const string source = """
#version 460
const int MaxVolumetricFogVolumes = 4;
struct VolumetricFogStruct
{
    bool Enabled;
    float Intensity;
    float MaxDistance;
    float StepSize;
    float JitterStrength;
    int VolumeCount;
};
uniform VolumetricFogStruct VolumetricFog;
uniform mat4 VolumetricFogWorldToLocal[MaxVolumetricFogVolumes];
uniform int VolumetricFogDebugMode;
layout(location = 0) out vec4 OutColor;
void main()
{
    OutColor = VolumetricFog.Enabled
        ? VolumetricFogWorldToLocal[0][0] + vec4(float(VolumetricFogDebugMode))
        : vec4(0.0);
}
""";

        object blockInfo = RewriteForVulkanAutoUniformBlockInfo(source, EShaderType.Fragment);
        uint size = GetProperty<uint>(blockInfo, "Size");
        object fog = GetMember(blockInfo, "VolumetricFog");
        object matrices = GetMember(blockInfo, "VolumetricFogWorldToLocal");
        object debugMode = GetMember(blockInfo, "VolumetricFogDebugMode");

        GetProperty<uint>(fog, "Offset").ShouldBe(0u);
        GetProperty<uint>(fog, "Size").ShouldBe(32u);
        GetProperty<object?>(fog, "StructMembers").ShouldNotBeNull();

        GetProperty<uint>(matrices, "Offset").ShouldBe(32u);
        GetProperty<uint>(matrices, "ArrayStride").ShouldBe(64u);
        GetProperty<uint>(matrices, "Size").ShouldBe(256u);

        GetProperty<uint>(debugMode, "Offset").ShouldBe(288u);
        size.ShouldBe(304u);
    }

    [Test]
    public void Rewrite_ComputesStd140Vec3ScalarPacking()
    {
        const string source = """
#version 460
struct ColorGradeStruct
{
    vec3 Tint;
    float Exposure;
    float Contrast;
    float Gamma;
    float Hue;
    float Saturation;
    float Brightness;
};
uniform bool OutputHDR;
uniform ColorGradeStruct ColorGrade;
uniform vec3 Samples[2];
uniform float AfterSamples;
layout(location = 0) out vec4 OutColor;
void main()
{
    OutColor = vec4(ColorGrade.Tint * ColorGrade.Exposure + Samples[1] + vec3(AfterSamples), 1.0);
}
""";

        object blockInfo = RewriteForVulkanAutoUniformBlockInfo(source, EShaderType.Fragment);
        uint size = GetProperty<uint>(blockInfo, "Size");
        object outputHdr = GetMember(blockInfo, "OutputHDR");
        object colorGrade = GetMember(blockInfo, "ColorGrade");
        object samples = GetMember(blockInfo, "Samples");
        object afterSamples = GetMember(blockInfo, "AfterSamples");

        GetProperty<uint>(outputHdr, "Offset").ShouldBe(0u);
        GetProperty<uint>(colorGrade, "Offset").ShouldBe(16u);
        GetProperty<uint>(colorGrade, "Size").ShouldBe(48u);

        GetProperty<uint>(GetStructMember(colorGrade, "Tint"), "Offset").ShouldBe(0u);
        GetProperty<uint>(GetStructMember(colorGrade, "Exposure"), "Offset").ShouldBe(12u);
        GetProperty<uint>(GetStructMember(colorGrade, "Contrast"), "Offset").ShouldBe(16u);
        GetProperty<uint>(GetStructMember(colorGrade, "Brightness"), "Offset").ShouldBe(32u);

        GetProperty<uint>(samples, "Offset").ShouldBe(64u);
        GetProperty<uint>(samples, "ArrayStride").ShouldBe(16u);
        GetProperty<uint>(samples, "Size").ShouldBe(32u);
        GetProperty<uint>(afterSamples, "Offset").ShouldBe(96u);
        size.ShouldBe(112u);
    }

    private static string RewriteForVulkanFragment(string source)
    {
        Type? autoUniformType = typeof(VulkanShaderCompiler).Assembly
            .GetType("XREngine.Rendering.Vulkan.VulkanShaderAutoUniforms", throwOnError: true);

        MethodInfo? rewriteMethod = GetRewriteMethod(autoUniformType!);

        rewriteMethod.ShouldNotBeNull();

        object? result = rewriteMethod!.Invoke(null, [source, EShaderType.Fragment]);
        result.ShouldNotBeNull();

        PropertyInfo? sourceProperty = result!.GetType().GetProperty("Source", BindingFlags.Instance | BindingFlags.Public);
        sourceProperty.ShouldNotBeNull();

        string? rewrittenSource = sourceProperty!.GetValue(result) as string;
        rewrittenSource.ShouldNotBeNull();
        return rewrittenSource!;
    }

    private static uint RewriteForVulkanAutoUniformBinding(string source, EShaderType shaderType)
    {
        object blockInfo = RewriteForVulkanAutoUniformBlockInfo(source, shaderType);
        return GetProperty<uint>(blockInfo, "Binding");
    }

    private static object RewriteForVulkanAutoUniformBlockInfo(string source, EShaderType shaderType)
    {
        Type? autoUniformType = typeof(VulkanShaderCompiler).Assembly
            .GetType("XREngine.Rendering.Vulkan.VulkanShaderAutoUniforms", throwOnError: true);

        MethodInfo? rewriteMethod = GetRewriteMethod(autoUniformType!);

        rewriteMethod.ShouldNotBeNull();

        object? result = rewriteMethod!.Invoke(null, [source, shaderType]);
        result.ShouldNotBeNull();

        PropertyInfo? blockInfoProperty = result!.GetType().GetProperty("BlockInfo", BindingFlags.Instance | BindingFlags.Public);
        blockInfoProperty.ShouldNotBeNull();

        object? blockInfo = blockInfoProperty!.GetValue(result);
        blockInfo.ShouldNotBeNull();
        return blockInfo!;
    }

    private static object GetMember(object blockInfo, string memberName)
    {
        object? members = GetProperty<object>(blockInfo, "Members");
        members.ShouldNotBeNull();

        foreach (object member in (System.Collections.IEnumerable)members!)
        {
            string? name = GetProperty<string>(member, "Name");
            if (name == memberName)
                return member;
        }

        Assert.Fail($"Auto uniform member '{memberName}' was not found.");
        throw new InvalidOperationException($"Auto uniform member '{memberName}' was not found.");
    }

    private static object GetStructMember(object member, string fieldName)
    {
        object? members = GetProperty<object?>(member, "StructMembers");
        members.ShouldNotBeNull();

        foreach (object field in (System.Collections.IEnumerable)members!)
        {
            string? name = GetProperty<string>(field, "Name");
            if (name == fieldName)
                return field;
        }

        Assert.Fail($"Auto uniform struct member '{fieldName}' was not found.");
        throw new InvalidOperationException($"Auto uniform struct member '{fieldName}' was not found.");
    }

    private static T? GetProperty<T>(object instance, string propertyName)
    {
        PropertyInfo? property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        property.ShouldNotBeNull();
        return (T?)property!.GetValue(instance);
    }

    private static string RewriteForVulkanCompute(string source)
    {
        Type? autoUniformType = typeof(VulkanShaderCompiler).Assembly
            .GetType("XREngine.Rendering.Vulkan.VulkanShaderAutoUniforms", throwOnError: true);

        MethodInfo? rewriteMethod = GetRewriteMethod(autoUniformType!);

        rewriteMethod.ShouldNotBeNull();

        object? result = rewriteMethod!.Invoke(null, [source, EShaderType.Compute]);
        result.ShouldNotBeNull();

        PropertyInfo? sourceProperty = result!.GetType().GetProperty("Source", BindingFlags.Instance | BindingFlags.Public);
        sourceProperty.ShouldNotBeNull();

        string? rewrittenSource = sourceProperty!.GetValue(result) as string;
        rewrittenSource.ShouldNotBeNull();
        return rewrittenSource!;
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

    private static MethodInfo? GetRewriteMethod(Type autoUniformType)
        => autoUniformType.GetMethod(
            "Rewrite",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(EShaderType)],
            modifiers: null);

    private readonly record struct LoadedShaderSource(string FullPath, string Source);
}

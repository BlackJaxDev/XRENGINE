using System;
using System.Collections.Generic;
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
public sealed class VulkanGpuDrivenPhase02Tests
{
    private static readonly string[] GpuDrivenComputeShaders =
    [
        "Compute/GPURenderResetCounters.comp",
        "Compute/GPURenderCulling.comp",
        "Compute/GPURenderCullingSoA.comp",
        "Compute/GPURenderIndirect.comp",
        "Compute/GPURenderBuildKeys.comp",
        "Compute/GPURenderBuildBatches.comp",
        "Scene3D/RenderPipeline/bvh_frustum_cull.comp"
    ];

    [TestCaseSource(nameof(GpuDrivenComputeShaders))]
    public void GpuDrivenComputeShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        var result = CompileComputeShader(shaderRelativePath);

        result.Source.ShouldContain("void main");
        result.Source.ShouldContain("local_size");
        result.EntryPoint.ShouldBe("main");
        result.Spirv.ShouldNotBeNull();
        result.Spirv.Length.ShouldBeGreaterThan(0);

        IReadOnlyList<DescriptorBindingInfo> spirvBindings = VulkanShaderReflection.ExtractBindings(
            result.Spirv,
            ShaderStageFlags.ComputeBit,
            result.RewrittenSource ?? result.Source);

        spirvBindings.ShouldNotBeEmpty();
        ValidateUniqueBindingKeys(spirvBindings, shaderRelativePath, "SPIR-V reflection");
    }

    [TestCaseSource(nameof(GpuDrivenComputeShaders))]
    public void GpuDrivenComputeShader_BindingIndices_MatchSourceLayoutDeclarations(string shaderRelativePath)
    {
        var result = CompileComputeShader(shaderRelativePath);

        string sourceForLayoutChecks = result.RewrittenSource ?? result.Source;

        IReadOnlyList<DescriptorBindingInfo> sourceBindings = VulkanShaderReflection.ExtractBindings(
            Array.Empty<byte>(),
            ShaderStageFlags.ComputeBit,
            sourceForLayoutChecks);

        IReadOnlyList<DescriptorBindingInfo> spirvBindings = VulkanShaderReflection.ExtractBindings(
            result.Spirv,
            ShaderStageFlags.ComputeBit,
            sourceForLayoutChecks);

        sourceBindings.ShouldNotBeEmpty();
        spirvBindings.ShouldNotBeEmpty();

        var sourceMap = ToBindingMap(sourceBindings, shaderRelativePath, "GLSL layout");
        var spirvMap = ToBindingMap(spirvBindings, shaderRelativePath, "SPIR-V reflection");

        var spirvKeys = spirvMap.Keys.Where(key => !IsAutoUniformBinding(spirvMap[key])).ToArray();
        var extraInSpirv = spirvKeys.Except(sourceMap.Keys).OrderBy(k => k.Set).ThenBy(k => k.Binding).ToArray();

        extraInSpirv.ShouldBeEmpty($"{shaderRelativePath}: bindings found in SPIR-V reflection but not declared in GLSL: {FormatKeys(extraInSpirv)}");

        foreach (BindingKey key in spirvKeys)
        {
            sourceMap.ContainsKey(key).ShouldBeTrue($"{shaderRelativePath}: reflected binding set={key.Set}, binding={key.Binding} was not declared in GLSL layout.");
        }
    }

    private static CompiledShaderResult CompileComputeShader(string shaderRelativePath)
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
            out string? rewrittenSource);

        return new CompiledShaderResult(loadedShader.Source, rewrittenSource, entryPoint, spirv);
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

    private static void ValidateUniqueBindingKeys(IReadOnlyList<DescriptorBindingInfo> bindings, string shaderRelativePath, string sourceName)
    {
        var seen = new HashSet<BindingKey>();
        foreach (DescriptorBindingInfo binding in bindings)
        {
            binding.Count.ShouldBeGreaterThan(0u, $"{shaderRelativePath}: {sourceName} reported descriptor with zero count at set {binding.Set}, binding {binding.Binding}.");
            var key = new BindingKey(binding.Set, binding.Binding);
            seen.Add(key).ShouldBeTrue($"{shaderRelativePath}: {sourceName} reported duplicate descriptor binding key set={binding.Set}, binding={binding.Binding}.");
        }
    }

    private static Dictionary<BindingKey, DescriptorBindingInfo> ToBindingMap(IReadOnlyList<DescriptorBindingInfo> bindings, string shaderRelativePath, string sourceName)
    {
        ValidateUniqueBindingKeys(bindings, shaderRelativePath, sourceName);
        return bindings.ToDictionary(binding => new BindingKey(binding.Set, binding.Binding));
    }

    private static string FormatKeys(IReadOnlyList<BindingKey> keys)
        => keys.Count == 0
            ? "(none)"
            : string.Join(", ", keys.Select(k => $"set={k.Set},binding={k.Binding}"));

    private static bool IsAutoUniformBinding(DescriptorBindingInfo binding)
        => binding.Name.StartsWith("XREngine_AutoUniforms_", StringComparison.Ordinal);

    private readonly record struct BindingKey(uint Set, uint Binding);
    private readonly record struct CompiledShaderResult(string Source, string? RewrittenSource, string EntryPoint, byte[] Spirv);
    private readonly record struct LoadedShaderSource(string FullPath, string Source);
}
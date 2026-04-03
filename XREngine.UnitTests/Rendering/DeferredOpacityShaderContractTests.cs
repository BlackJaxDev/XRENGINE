using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class DeferredOpacityShaderContractTests
{
    [Test]
    [TestCase("Common/TexturedDeferred.fs")]
    [TestCase("Common/TexturedNormalDeferred.fs")]
    [TestCase("Common/TexturedSpecDeferred.fs")]
    [TestCase("Common/TexturedNormalSpecDeferred.fs")]
    public void MaskedCapableDeferredShaders_UseMaterialOpacityNotTextureAlpha(string shaderRelativePath)
    {
        string source = LoadShaderSource(shaderRelativePath);

        // Must have alpha-cutoff / dither support for masked mode.
        source.ShouldContain("XRENGINE_AlphaCutoffAndDither");
        source.ShouldContain("AlphaCutoff");

        // G-buffer alpha must be the material Opacity uniform, NOT texture alpha,
        // because many albedo textures carry non-transparency data in their alpha channel.
        source.ShouldContain("Opacity);");
        source.ShouldNotContain("effectiveOpacity");
        source.ShouldNotContain(".a * Opacity");
    }

    [Test]
    [TestCase("Common/TexturedAlphaDeferred.fs", "texture(Texture1, FragUV0).r")]
    [TestCase("Common/TexturedNormalAlphaDeferred.fs", "texture(Texture2, FragUV0).r")]
    [TestCase("Common/TexturedSpecAlphaDeferred.fs", "texture(Texture2, FragUV0).r")]
    [TestCase("Common/TexturedNormalSpecAlphaDeferred.fs", "texture(Texture3, FragUV0).r")]
    public void AlphaMaskDeferredShaders_SampleSeparateOpacityTexture(string shaderRelativePath, string alphaMaskSample)
    {
        string source = LoadShaderSource(shaderRelativePath);

        source.ShouldContain("XRENGINE_AlphaCutoffAndDither");
        source.ShouldContain(alphaMaskSample);
        source.ShouldContain("Opacity);");
        source.ShouldNotContain(".a * Opacity");
    }

    [Test]
    [TestCase("Common/TexturedEmissiveDeferred.fs")]
    [TestCase("Common/TexturedMatcapDeferred.fs")]
    [TestCase("Common/TexturedMetallicDeferred.fs")]
    [TestCase("Common/TexturedMetallicRoughnessDeferred.fs")]
    [TestCase("Common/TexturedNormalMetallicDeferred.fs")]
    [TestCase("Common/TexturedNormalMetallicRoughnessDeferred.fs")]
    [TestCase("Common/TexturedRoughnessDeferred.fs")]
    [TestCase("Common/TexturedSilhouettePOMDeferred.fs")]
    public void OpaqueDeferredPbrShaders_DoNotUseAlbedoAlphaForOpacity(string shaderRelativePath)
    {
        string source = LoadShaderSource(shaderRelativePath);

        source.ShouldContain("Opacity);");
        source.ShouldNotContain(".a * Opacity");
        source.ShouldNotContain("effectiveOpacity");
        source.ShouldNotContain("XRENGINE_AlphaCutoffAndDither");
    }

    private static string LoadShaderSource(string shaderRelativePath)
    {
        string fullPath = ResolveWorkspacePath(Path.Combine("Build", "CommonAssets", "Shaders", shaderRelativePath));
        File.Exists(fullPath).ShouldBeTrue($"Expected shader file does not exist: {fullPath}");
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
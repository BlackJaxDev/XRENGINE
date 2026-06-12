using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ForwardDepthPrePassGBufferRestoreTests
{
    [TestCase("DefaultRenderPipeline")]
    [TestCase("DefaultRenderPipeline2")]
    public void ForwardDepthPrePass_RestoresDeferredGBufferBeforeLighting(string pipelineName)
    {
        string constants = LoadPipelineFile($"{pipelineName}.cs").Replace("\r\n", "\n");
        string textures = LoadPipelineFile($"{pipelineName}.Textures.cs").Replace("\r\n", "\n");
        string fbos = LoadPipelineFile($"{pipelineName}.FBOs.cs").Replace("\r\n", "\n");
        string commandChain = LoadPipelineFile($"{pipelineName}.CommandChain.cs").Replace("\r\n", "\n");

        constants.ShouldContain("DeferredGBufferPreForwardCopyFBOName");
        constants.ShouldContain("DeferredGBufferPreForwardNormalTextureName");
        constants.ShouldContain("DeferredGBufferPreForwardDepthStencilTextureName");
        textures.ShouldContain("CreateDeferredGBufferPreForwardNormalTexture");
        textures.ShouldContain("CreateDeferredGBufferPreForwardDepthStencilTexture");
        fbos.ShouldContain("CreateDeferredGBufferPreForwardCopyFBO");
        fbos.ShouldContain("DeferredGBufferPreForwardNormalTextureName");
        fbos.ShouldContain("DeferredGBufferPreForwardDepthStencilTextureName");

        AssertContainsInOrder(
            commandChain,
            "AppendAmbientOcclusionResolve(c);",
            "AppendForwardDepthPrePassGBufferRestore(c);",
            "AppendLightingPass(c);");

        string forwardPrePass = SliceMethod(commandChain, "private void AppendForwardDepthPrePass(ViewportRenderCommandContainer c)");
        AssertContainsInOrder(
            forwardPrePass,
            "shareChoice.Add<VPRC_BlitFrameBuffer>().SetOptions(",
            "ForwardDepthPrePassMergeFBOName,",
            "DeferredGBufferPreForwardCopyFBOName,",
            "blitColor: true,",
            "blitDepth: true,",
            "blitStencil: false,",
            "shareIfElse.ConditionEvaluator");

        string restorePrePass = SliceMethod(commandChain, "private void AppendForwardDepthPrePassGBufferRestore(ViewportRenderCommandContainer c)");
        AssertContainsInOrder(
            restorePrePass,
            "restoreCommands.Add<VPRC_BlitFrameBuffer>().SetOptions(",
            "DeferredGBufferPreForwardCopyFBOName,",
            "ForwardDepthPrePassMergeFBOName,",
            "blitColor: true,",
            "blitDepth: true,",
            "blitStencil: false,");
    }

    [TestCase("DefaultRenderPipeline")]
    [TestCase("DefaultRenderPipeline2")]
    public void ForwardDepthPrePass_SettingsArePipelineOwnedAndSizeDedicatedTargets(string pipelineName)
    {
        string constants = LoadPipelineFile($"{pipelineName}.cs").Replace("\r\n", "\n");
        string textures = LoadPipelineFile($"{pipelineName}.Textures.cs").Replace("\r\n", "\n");
        string commandChain = LoadPipelineFile($"{pipelineName}.CommandChain.cs").Replace("\r\n", "\n");
        string pipelineSource = constants + "\n" + commandChain;

        constants.ShouldContain("public bool ForwardDepthPrePassEnabled");
        constants.ShouldContain("public bool ForwardPrePassSharesGBufferTargets");
        constants.ShouldContain("public EDepthNormalPrePassResolution ForwardDepthNormalPrePassResolution");
        constants.ShouldContain("[RenderPipelineCameraSetting(Order = 100)]");
        constants.ShouldContain("GetDesiredFBOSizeForwardDepthNormalPrePass");

        pipelineSource.ShouldContain("ConditionEvaluator = () => ForwardDepthPrePassEnabled");
        pipelineSource.ShouldContain("ConditionEvaluator = () => ForwardPrePassSharesGBufferTargets");
        pipelineSource.ShouldContain("ForwardDepthPrePassFBOName,\n            CreateForwardDepthPrePassFBO,\n            GetDesiredFBOSizeForwardDepthNormalPrePass");
        pipelineSource.ShouldContain("ForwardContactPrePassCopyFBOName,\n                CreateForwardContactPrePassCopyFBO,\n                GetDesiredFBOSizeForwardDepthNormalPrePass");
        pipelineSource.ShouldNotContain("EditorPreferences.Debug.ForwardDepthPrePassEnabled");
        pipelineSource.ShouldNotContain("EditorPreferences.Debug.ForwardPrePassSharesGBufferTargets");

        textures.ShouldContain("GetDesiredFBOSizeForwardDepthNormalPrePass");
        textures.ShouldContain("CreateForwardPrePassDepthStencilTexture");
        textures.ShouldContain("CreateForwardContactDepthStencilTexture");
        textures.ShouldContain("CreateForwardPrePassNormalTexture");
        textures.ShouldContain("CreateForwardContactNormalTexture");
    }

    private static void AssertContainsInOrder(string source, params string[] expected)
    {
        int previousIndex = -1;
        foreach (string text in expected)
        {
            int index = source.IndexOf(text, previousIndex + 1, StringComparison.Ordinal);
            index.ShouldBeGreaterThan(previousIndex, $"Expected '{text}' after index {previousIndex}.");
            previousIndex = index;
        }
    }

    private static string SliceMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find method signature '{signature}'.");

        int openBrace = source.IndexOf('{', start);
        openBrace.ShouldBeGreaterThanOrEqualTo(start, $"Could not find method body for '{signature}'.");

        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
                depth--;

            if (depth == 0)
                return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"Could not find method end for '{signature}'.");
    }

    private static string LoadPipelineFile(string fileName)
    {
        string fullPath = Path.Combine(ResolveRepoRoot(), "XREngine.Runtime.Rendering", "Rendering", "Pipelines", "Types", fileName);
        File.Exists(fullPath).ShouldBeTrue($"Pipeline file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "XRENGINE.slnx");
            if (File.Exists(candidate))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root from test base directory.");
    }
}

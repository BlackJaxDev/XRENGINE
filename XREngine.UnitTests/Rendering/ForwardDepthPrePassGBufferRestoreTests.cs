using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ForwardDepthPrePassGBufferRestoreTests
{
    [TestCase("DefaultRenderPipeline")]
    public void ForwardDepthPrePass_SeedsSeparateSceneSurfaceBeforeLighting(string pipelineName)
    {
        string constants = LoadPipelineFile($"{pipelineName}.cs").Replace("\r\n", "\n");
        string textures = LoadPipelineFile($"{pipelineName}.Textures.cs").Replace("\r\n", "\n");
        string fbos = LoadPipelineFile($"{pipelineName}.FBOs.cs").Replace("\r\n", "\n");
        string commandChain = LoadPipelineFile($"{pipelineName}.CommandChain.cs").Replace("\r\n", "\n");

        constants.ShouldContain("ForwardDepthPrePassFBOName");
        constants.ShouldContain("ForwardPrePassNormalTextureName");
        constants.ShouldContain("ForwardPrePassDepthStencilTextureName");
        textures.ShouldContain("CreateForwardPrePassNormalTexture");
        textures.ShouldContain("CreateForwardPrePassDepthStencilTexture");
        fbos.ShouldContain("CreateForwardDepthPrePassFBO");
        fbos.ShouldContain("ForwardPrePassNormalTextureName");
        fbos.ShouldContain("ForwardPrePassDepthStencilTextureName");

        AssertContainsInOrder(
            commandChain,
            "AppendDeferredGBufferPass(",
            "AppendForwardDepthPrePass(",
            "AppendAmbientOcclusionResolve(",
            "AppendLightingPass(");

        string forwardPrePass = SliceMethod(commandChain, "private void AppendForwardDepthPrePass(ViewportRenderCommandContainer c)");
        AssertContainsInOrder(
            forwardPrePass,
            "surfaceCommands.Add<VPRC_BlitFrameBuffer>().SetOptions(",
            "DeferredGBufferFBOName,",
            "ForwardDepthPrePassFBOName,",
            "EReadBufferMode.ColorAttachment1,",
            "blitColor: true,",
            "blitDepth: true,",
            "blitStencil: false,",
            "prePassChoice.TrueCommands = CreateForwardPrePassSharedCommands();");
        constants.ShouldContain("x.SetOptions(ForwardDepthPrePassFBOName, true, false, false, false)");
        constants.ShouldContain("c.Add<VPRC_ForwardDepthNormalPrePass>().SetOptions(");
        commandChain.ShouldNotContain("AppendForwardDepthPrePassGBufferRestore(");
    }

    [TestCase("DefaultRenderPipeline")]
    public void ForwardDepthPrePass_UsesPipelineSettingsAndFullInternalSceneSurface(string pipelineName)
    {
        string constants = LoadPipelineFile($"{pipelineName}.cs").Replace("\r\n", "\n");
        string textures = LoadPipelineFile($"{pipelineName}.Textures.cs").Replace("\r\n", "\n");
        string commandChain = LoadPipelineFile($"{pipelineName}.CommandChain.cs").Replace("\r\n", "\n");
        string resources = LoadPipelineFile($"{pipelineName}.Resources.cs").Replace("\r\n", "\n");
        string pipelineSource = constants + "\n" + commandChain;

        constants.ShouldContain("public bool ForwardDepthPrePassEnabled");
        constants.ShouldContain("public bool ForwardPrePassSharesGBufferTargets");
        constants.ShouldContain("public EDepthNormalPrePassResolution ForwardDepthNormalPrePassResolution");
        constants.ShouldContain("[RenderPipelineCameraSetting(Order = 100)]");
        constants.ShouldContain("The default pipeline complete-scene depth+normal surface is always full internal resolution.");

        commandChain.ShouldContain("prePassChoice.ConditionEvaluator = ShouldRunForwardDepthPrePass;");
        commandChain.ShouldContain("surfaceChoice.ConditionEvaluator = ShouldPrepareForwardSceneSurface;");
        pipelineSource.ShouldNotContain("ConditionEvaluator = () => ForwardPrePassSharesGBufferTargets");
        resources.ShouldContain("builder.FrameBuffer(ForwardDepthPrePassFBOName)");
        resources.ShouldContain(".Factory(CreateForwardDepthPrePassFBO)");
        resources.ShouldContain("RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();");
        resources.ShouldContain(".Color(0, ForwardPrePassNormalTextureName)");
        resources.ShouldContain(".DepthStencil(ForwardPrePassDepthStencilTextureName)");
        pipelineSource.ShouldNotContain("EditorPreferences.Debug.ForwardDepthPrePassEnabled");
        pipelineSource.ShouldNotContain("EditorPreferences.Debug.ForwardPrePassSharesGBufferTargets");

        textures.ShouldContain("CreateForwardPrePassDepthStencilTexture");
        textures.ShouldContain("CreateForwardPrePassNormalTexture");
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
        => global::XREngine.UnitTests.SourceContractWorkspace.ReadFile(
            $"XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/{fileName}");

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

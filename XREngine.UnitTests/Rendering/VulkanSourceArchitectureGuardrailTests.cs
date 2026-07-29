using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Prevents the Vulkan runtime from accumulating new renderer-owned state and
/// multi-type source files while the existing organization debt is retired.
/// Baselines are one-way exceptions: removing an entry's debt is always valid.
/// </summary>
[TestFixture]
public sealed partial class VulkanSourceArchitectureGuardrailTests
{
    private static readonly HashSet<string> StatefulRendererPartialBaseline =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VulkanRenderer.BufferViewLifetime.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Queries/VulkanRenderer.QueryArenas.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Queries/VulkanRenderer.QueryCapabilities.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Queries/VulkanRenderer.SpecializedQueryProviders.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Samplers/VulkanRenderer.SamplerLifetime.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/VulkanRenderer.RenderObjectFactory.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.HotReloadCleanup.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Instance.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.ObsHookCompatibility.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.PhysicalDevice.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Surface.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Validation.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferDirtyReasons.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferTrackingBatch.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainWorkers.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandPool.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.FrameOpDiagnostics.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.IndirectDraw.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Readback.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderState.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLayoutCache.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorUpdateTemplates.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.BindlessMaterialTextureTable.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.ComputeDescriptors.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorHeap.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorPool.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSetLayout.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSets.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Streaming/VulkanRenderer.TextureStreamingHooks.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanRenderer.StreamlineRequirements.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanRenderer.StreamlineUiResources.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/VulkanRenderer.AutoExposure.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/KhrDeviceFault/VulkanRenderer.KhrDeviceFault.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DesktopPresentationPolicy.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.ExtendedReporting.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Preflight.Policy.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Presentation.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.State.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.SwapchainPolicy.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameTiming.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.GpuStatsReadback.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.PresentScaling.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.RetiredSwapchainGeneration.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ScreenshotReadback.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SubmissionMarkers.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SyncObjects.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.EyeRecordWorkers.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineCache.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelinePrewarmDatabase.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineVariantManifest.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderer.RenderPasses.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanDynamicUniformRingBuffer.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanSceneDatabaseAddresses.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Framebuffers/VulkanRenderer.FrameBufferRenderPasses.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Framebuffers/VulkanRenderer.SwapchainFramebuffers.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Pipelines/VulkanRenderer.PipelineLayoutLifetime.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Textures/VulkanRenderer.ImageViewLifetime.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Textures/VulkanRenderer.PlaceholderTexture.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Textures/VulkanRenderer.SwapchainImageViews.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Types/QueueFamilyIndices.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs",
        };

    private static readonly HashSet<string> MultiTypeFileBaseline =
        new(StringComparer.OrdinalIgnoreCase)
        {
        };

    [Test]
    public void VulkanRendererPartials_DoNotIntroduceNewStateOwners()
    {
        string[] violations =
        [
            .. SourceContractWorkspace.GetVulkanSourceFiles()
                .Where(static file => DeclaresStatefulVulkanRendererPartial(file.Source))
                .Select(static file => file.RelativePath)
                .Where(path => !StatefulRendererPartialBaseline.Contains(path)),
        ];

        violations.ShouldBeEmpty(
            "New VulkanRenderer partial files must remain behavior-only. " +
            "Move state to a focused owner type or document an explicit baseline exception.\n" +
            string.Join(Environment.NewLine, violations));
    }

    [Test]
    public void VulkanSourceFiles_DoNotIntroduceNewTopLevelTypeDumpingGrounds()
    {
        string[] violations =
        [
            .. SourceContractWorkspace.GetVulkanSourceFiles()
                .Where(static file => CountTopLevelTypeDeclarations(file.Source) > 1)
                .Select(static file => file.RelativePath)
                .Where(path => !MultiTypeFileBaseline.Contains(path)),
        ];

        violations.ShouldBeEmpty(
            "Each Vulkan source file should declare one top-level type. " +
            "Split the types or document an explicit baseline exception.\n" +
            string.Join(Environment.NewLine, violations));
    }

    private static bool DeclaresStatefulVulkanRendererPartial(string source)
    {
        string maskedSource = MaskTriviaAndLiterals(source);
        int[] braceDepths = BuildBraceDepthMap(maskedSource);

        foreach (Match declaration in VulkanRendererDeclarationRegex().Matches(maskedSource))
        {
            int bodyStart = maskedSource.IndexOf('{', declaration.Index + declaration.Length);
            if (bodyStart < 0)
                continue;

            int memberDepth = braceDepths[bodyStart] + 1;
            int bodyEnd = FindClosingBrace(maskedSource, bodyStart);
            if (bodyEnd < 0)
                continue;

            foreach (Match field in FieldDeclarationRegex().Matches(maskedSource, bodyStart + 1))
            {
                if (field.Index >= bodyEnd)
                    break;
                if (braceDepths[field.Index] != memberDepth)
                    continue;
                if (field.Value.Contains(" const ", StringComparison.Ordinal) ||
                    field.Value.TrimStart().StartsWith("const ", StringComparison.Ordinal))
                    continue;

                return true;
            }

            foreach (Match property in PropertyDeclarationRegex().Matches(maskedSource, bodyStart + 1))
            {
                if (property.Index >= bodyEnd)
                    break;
                if (braceDepths[property.Index] != memberDepth)
                    continue;

                int propertyBodyStart = maskedSource.IndexOf(
                    '{',
                    property.Index + property.Length - 1);
                int propertyBodyEnd = FindClosingBrace(maskedSource, propertyBodyStart);
                if (propertyBodyEnd > propertyBodyStart &&
                    AutoPropertyAccessorRegex().IsMatch(
                        maskedSource[propertyBodyStart..(propertyBodyEnd + 1)]))
                    return true;
            }
        }

        return false;
    }

    private static int CountTopLevelTypeDeclarations(string source)
    {
        string maskedSource = MaskTriviaAndLiterals(source);
        int[] braceDepths = BuildBraceDepthMap(maskedSource);
        int declarationDepth = FileScopedNamespaceRegex().IsMatch(maskedSource) ? 0 : 1;
        int count = 0;

        foreach (Match declaration in TypeDeclarationRegex().Matches(maskedSource))
        {
            if (braceDepths[declaration.Index] == declarationDepth)
                count++;
        }

        return count;
    }

    private static int[] BuildBraceDepthMap(string source)
    {
        int[] depths = new int[source.Length];
        int depth = 0;
        for (int i = 0; i < source.Length; i++)
        {
            depths[i] = depth;
            depth += source[i] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };
        }

        return depths;
    }

    private static int FindClosingBrace(string source, int openingBrace)
    {
        int depth = 0;
        for (int i = openingBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return i;
        }

        return -1;
    }

    private static string MaskTriviaAndLiterals(string source)
    {
        StringBuilder masked = new(source);
        LexicalState state = LexicalState.Code;

        for (int i = 0; i < source.Length; i++)
        {
            char current = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            switch (state)
            {
                case LexicalState.Code when current == '/' && next == '/':
                    masked[i] = masked[i + 1] = ' ';
                    state = LexicalState.LineComment;
                    i++;
                    break;
                case LexicalState.Code when current == '/' && next == '*':
                    masked[i] = masked[i + 1] = ' ';
                    state = LexicalState.BlockComment;
                    i++;
                    break;
                case LexicalState.Code when current == '@' && next == '"':
                    masked[i] = masked[i + 1] = ' ';
                    state = LexicalState.VerbatimString;
                    i++;
                    break;
                case LexicalState.Code when current == '"':
                    masked[i] = ' ';
                    state = LexicalState.String;
                    break;
                case LexicalState.Code when current == '\'':
                    masked[i] = ' ';
                    state = LexicalState.Character;
                    break;
                case LexicalState.LineComment when current is '\r' or '\n':
                    state = LexicalState.Code;
                    break;
                case LexicalState.LineComment:
                    masked[i] = ' ';
                    break;
                case LexicalState.BlockComment when current == '*' && next == '/':
                    masked[i] = masked[i + 1] = ' ';
                    state = LexicalState.Code;
                    i++;
                    break;
                case LexicalState.BlockComment:
                    MaskUnlessNewLine(masked, i, current);
                    break;
                case LexicalState.String when current == '\\':
                    masked[i] = ' ';
                    if (i + 1 < source.Length)
                        masked[++i] = ' ';
                    break;
                case LexicalState.String when current == '"':
                    masked[i] = ' ';
                    state = LexicalState.Code;
                    break;
                case LexicalState.String:
                    MaskUnlessNewLine(masked, i, current);
                    break;
                case LexicalState.VerbatimString when current == '"' && next == '"':
                    masked[i] = masked[i + 1] = ' ';
                    i++;
                    break;
                case LexicalState.VerbatimString when current == '"':
                    masked[i] = ' ';
                    state = LexicalState.Code;
                    break;
                case LexicalState.VerbatimString:
                    MaskUnlessNewLine(masked, i, current);
                    break;
                case LexicalState.Character when current == '\\':
                    masked[i] = ' ';
                    if (i + 1 < source.Length)
                        masked[++i] = ' ';
                    break;
                case LexicalState.Character when current == '\'':
                    masked[i] = ' ';
                    state = LexicalState.Code;
                    break;
                case LexicalState.Character:
                    MaskUnlessNewLine(masked, i, current);
                    break;
            }
        }

        return masked.ToString();
    }

    private static void MaskUnlessNewLine(StringBuilder source, int index, char value)
    {
        if (value is not '\r' and not '\n')
            source[index] = ' ';
    }

    [GeneratedRegex(@"\bpartial\s+(?:(?:sealed|abstract)\s+)?class\s+VulkanRenderer\b")]
    private static partial Regex VulkanRendererDeclarationRegex();

    [GeneratedRegex(
        @"(?m)^[ \t]*(?:\[[^\]\r\n]+\][ \t]*)*(?:(?:public|private|protected|internal|static|readonly|volatile|unsafe|new|required|const)[ \t]+)+[A-Za-z_][\w<>,.?\[\](): \t]*[ \t]+[A-Za-z_]\w*[ \t]*(?:=(?!>)|;)")]
    private static partial Regex FieldDeclarationRegex();

    [GeneratedRegex(
        @"(?m)^[ \t]*(?:\[[^\]\r\n]+\][ \t]*)*(?:(?:public|private|protected|internal|static|readonly|unsafe|new|required)[ \t]+)+[A-Za-z_][\w<>,.?\[\](): \t]*[ \t]+[A-Za-z_]\w*[ \t]*\{")]
    private static partial Regex PropertyDeclarationRegex();

    [GeneratedRegex(@"\b(?:get|set|init)\s*;")]
    private static partial Regex AutoPropertyAccessorRegex();

    [GeneratedRegex(
        @"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+[A-Za-z_]\w*")]
    private static partial Regex TypeDeclarationRegex();

    [GeneratedRegex(@"\bnamespace\s+[A-Za-z_][\w.]*\s*;")]
    private static partial Regex FileScopedNamespaceRegex();

    private enum LexicalState
    {
        Code,
        LineComment,
        BlockComment,
        String,
        VerbatimString,
        Character,
    }
}

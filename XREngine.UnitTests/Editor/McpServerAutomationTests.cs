using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class McpServerAutomationTests
{
    [Test]
    public void McpServer_ExposesViewportScreenshotTool()
    {
        string viewportActions = ReadWorkspaceFile("XREngine.Editor/Mcp/Actions/EditorMcpActions.Viewport.cs");
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs");
        string vulkanReadback = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Readback.cs");
        string docs = ReadWorkspaceFile("docs/developer-guides/ai/mcp-server.md");

        viewportActions.ShouldContain("[XRMcp(Name = \"capture_viewport_screenshot\")]");
        viewportActions.ShouldContain("renderer.TryQueueScreenshotReadback(captureRegion");
        viewportActions.ShouldContain("renderer.ScreenshotRequiresVerticalFlip");
        rendererSource.ShouldContain("public virtual bool ScreenshotRequiresVerticalFlip => true;");
        vulkanReadback.ShouldContain("public override bool ScreenshotRequiresVerticalFlip => false;");
        vulkanReadback.ShouldContain("if (!withTransparency)");
        vulkanReadback.ShouldContain("ForceOpaqueAlpha");
        docs.ShouldContain("capture_viewport_screenshot");
    }

    [Test]
    public void McpServer_ExposesBoundedViewportSequenceCaptureTools()
    {
        string actions = ReadWorkspaceFile("XREngine.Editor/Mcp/Actions/EditorMcpActions.ViewportSequence.cs");
        string sessionLifecycle = ReadWorkspaceFile("XREngine.Editor/Mcp/Capture/ViewportSequenceCaptureSession.cs");
        string sessionCapture = ReadWorkspaceFile("XREngine.Editor/Mcp/Capture/ViewportSequenceCaptureSession.Capture.cs");
        string sessionFinalization = ReadWorkspaceFile("XREngine.Editor/Mcp/Capture/ViewportSequenceCaptureSession.Finalization.cs");
        string options = ReadWorkspaceFile("XREngine.Editor/Mcp/Capture/ViewportSequenceCaptureOptions.cs");
        string docs = ReadWorkspaceFile("docs/developer-guides/ai/mcp-server.md");

        actions.ShouldContain("[XRMcp(Name = \"start_viewport_sequence_capture\"");
        actions.ShouldContain("[XRMcp(Name = \"get_viewport_sequence_capture\"");
        actions.ShouldContain("[XRMcp(Name = \"list_viewport_sequence_captures\"");
        actions.ShouldContain("[XRMcp(Name = \"cancel_viewport_sequence_capture\"");
        sessionLifecycle.ShouldContain("PostRenderViewportsCallback += OnPostRenderViewports");
        sessionCapture.ShouldContain("PostRenderViewportsCallback -= OnPostRenderViewports");
        sessionCapture.ShouldContain("Consecutive-frame capture stopped instead of silently dropping a frame");
        sessionFinalization.ShouldContain("ViewportSequenceCaptureImageAnalyzer.Analyze");
        sessionFinalization.ShouldContain("ViewportSequenceCaptureContactSheetWriter.TryWrite");
        options.ShouldContain("MaximumTotalOutputPixels");
        options.ShouldContain("MaximumInFlightReadbackBytes");
        docs.ShouldContain("start_viewport_sequence_capture");
        docs.ShouldContain("manifest.json");
        docs.ShouldContain("contact-sheet.png");
    }

    [Test]
    public void VulkanViewportCapture_UsesBoundedNonblockingFencePolling()
    {
        string vulkanReadback = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ScreenshotReadback.cs");
        string windowLoop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        string sequenceManager = ReadWorkspaceFile(
            "XREngine.Editor/Mcp/Capture/ViewportSequenceCaptureManager.cs");
        string sequenceCapture = ReadWorkspaceFile(
            "XREngine.Editor/Mcp/Capture/ViewportSequenceCaptureSession.Capture.cs");
        string viewportActions = ReadWorkspaceFile(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.Viewport.cs");

        vulkanReadback.ShouldContain("private const int ScreenshotReadbackRingSize = 8;");
        vulkanReadback.ShouldContain("MaximumScreenshotReadbackRawBytes");
        vulkanReadback.ShouldContain("SubmitToQueueTracked(");
        vulkanReadback.ShouldContain("Api!.GetFenceStatus(device, slot.Fence)");
        vulkanReadback.ShouldContain("CmdResolveImageTracked(");
        vulkanReadback.ShouldContain("FailPendingScreenshotReadbacksForDeviceLoss");
        vulkanReadback.ShouldContain("Task.Run(() => ProcessScreenshotReadbackPixels");
        vulkanReadback.ShouldNotContain("WaitForFences");
        vulkanReadback.ShouldNotContain("QueueWaitIdle");

        windowLoop.ShouldContain("frameRenderer.PollScreenshotReadbacks();");
        sequenceManager.ShouldNotContain("Vulkan viewport sequence readback is disabled");
        sequenceCapture.ShouldContain("_viewport.EnterRenderPipelineReadbackScope()");
        sequenceCapture.ShouldContain("renderer.TryQueueScreenshotReadback");
        viewportActions.ShouldNotContain("Vulkan viewport screenshot readback is temporarily disabled");
        viewportActions.ShouldContain("renderer.TryQueueScreenshotReadback");
    }

    [Test]
    public void McpServer_ExposesTextureStreamingTelemetryTools()
    {
        string textureActions = ReadWorkspaceFile("XREngine.Editor/Mcp/Actions/EditorMcpActions.TextureStreaming.cs");

        textureActions.ShouldContain("[XRMcp(Name = \"get_texture_streaming_summary\"");
        textureActions.ShouldContain("[XRMcp(Name = \"list_texture_streaming_textures\"");
        textureActions.ShouldContain("XRTexture2D.GetImportedTextureStreamingTelemetry()");
        textureActions.ShouldContain("XRTexture2D.GetImportedTextureStreamingTextureTelemetry()");
        textureActions.ShouldContain("resident_generation");
        textureActions.ShouldContain("published_generation");
        textureActions.ShouldContain("upload_generation");
        textureActions.ShouldContain("retirement_generation");
    }

    [Test]
    public void McpServer_CanPersistAndLaunchWithPermissionPolicy()
    {
        string host = ReadWorkspaceFile("XREngine.Editor/Mcp/McpServerHost.cs");
        string prefs = ReadWorkspaceFile("XRENGINE/Settings/EditorPreferences.cs");
        string overrides = ReadWorkspaceFile("XRENGINE/Settings/EditorPreferencesOverrides.cs");

        host.ShouldContain("--mcp-allow-all");
        host.ShouldContain("--mcp-no-prompts");
        host.ShouldContain("--mcp-permission-policy");
        host.ShouldContain("SetCliOverride(overrides.McpPermissionPolicyOverride, cliPermissionPolicy.Value);");
        host.ShouldContain("SetCliOverride(overrides.McpServerEnabledOverride, true);");
        host.ShouldContain("SetCliOverride(overrides.McpServerPortOverride, cliPort.Value);");
        host.ShouldContain("Engine.RefreshEffectiveEditorPreferences();");
        host.ShouldContain("McpPermissionPolicy.AllowAll");
        host.ShouldContain("permissionPolicy = prefs.McpPermissionPolicy.ToString()");
        host.ShouldContain("private static void SetCliOverride<T>(OverrideableSetting<T> setting, T value)");

        prefs.ShouldContain("public McpPermissionPolicy McpPermissionPolicy");
        prefs.ShouldContain("overrides.McpPermissionPolicyOverride is { HasOverride: true }");
        prefs.ShouldContain("overrides.McpDispatchModeOverride is { HasOverride: true }");

        overrides.ShouldContain("OverrideableSetting<McpPermissionPolicy>");
        overrides.ShouldContain("public OverrideableSetting<McpPermissionPolicy> McpPermissionPolicyOverride");
        overrides.ShouldContain("public OverrideableSetting<McpDispatchMode> McpDispatchModeOverride");
    }

    [Test]
    public void McpLiveInspection_ResolvesLoadedAssetsForObjectTools()
    {
        string liveInspection = ReadWorkspaceFile("XREngine.Editor/Mcp/Actions/EditorMcpActions.LiveInspection.cs");

        liveInspection.ShouldContain("Engine.Assets.GetAssetByID(guid)");
        liveInspection.ShouldContain("runtime cache or loaded asset table");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}

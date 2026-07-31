using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class EditorWindowCloseRecoveryTests
{
    [TestCase(0, false, false)]
    [TestCase(1, false, false)]
    [TestCase(2, false, false)]
    [TestCase(3, false, true)]
    [TestCase(10, false, true)]
    [TestCase(0, true, true)]
    public void ClosePrompt_IsBypassedOnlyWhenRenderingCannotShowIt(
        int consecutiveRenderFailures,
        bool renderPermanentlyDisabled,
        bool expected)
        => EditorImGuiUI.ShouldBypassClosePromptForRenderFailure(
                consecutiveRenderFailures,
                renderPermanentlyDisabled)
            .ShouldBe(expected);

    [Test]
    public void ClosePrompt_RenderPathRemainsAvailableWhenEditorShellIsSuppressed()
    {
        string source = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.ImGui.cs");
        int renderEditorStart = source.IndexOf("public static void RenderEditor()", StringComparison.Ordinal);
        int bypassHelperStart = source.IndexOf(
            "internal static bool ShouldBypassClosePromptForRenderFailure(",
            renderEditorStart,
            StringComparison.Ordinal);
        renderEditorStart.ShouldBeGreaterThanOrEqualTo(0);
        bypassHelperStart.ShouldBeGreaterThan(renderEditorStart);

        string renderEditorBody = source[renderEditorStart..bypassHelperStart];
        int editorGate = renderEditorBody.IndexOf("if (!ShouldRenderEditorImGui())", StringComparison.Ordinal);
        int closeOnlyRender = renderEditorBody.IndexOf("RenderClosePromptOnly();", editorGate, StringComparison.Ordinal);
        int earlyReturn = renderEditorBody.IndexOf("return;", closeOnlyRender, StringComparison.Ordinal);
        editorGate.ShouldBeGreaterThanOrEqualTo(0);
        closeOnlyRender.ShouldBeGreaterThan(editorGate);
        earlyReturn.ShouldBeGreaterThan(closeOnlyRender);
    }

    [Test]
    public void ImGuiUiCreation_InstallsClosePolicyBeforeRegisteringDrawCallback()
    {
        string source = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs");
        int initialize = source.IndexOf("EditorImGuiUI.Initialize();", StringComparison.Ordinal);
        int registerDraw = source.IndexOf(
            "dearImGuiComponent?.Draw += EditorImGuiUI.RenderEditor;",
            StringComparison.Ordinal);

        initialize.ShouldBeGreaterThanOrEqualTo(0);
        registerDraw.ShouldBeGreaterThan(initialize);
    }

    [Test]
    public void WindowCloseRequest_AlwaysBeginsConfirmationWhenRenderingIsAvailable()
    {
        string source = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.ImGui.cs");
        int handlerStart = source.IndexOf(
            "private static Engine.WindowCloseRequestResult HandleWindowCloseRequested",
            StringComparison.Ordinal);
        int beginPromptStart = source.IndexOf("private static void BeginClosePrompt", handlerStart, StringComparison.Ordinal);
        handlerStart.ShouldBeGreaterThanOrEqualTo(0);
        beginPromptStart.ShouldBeGreaterThan(handlerStart);

        string handlerBody = source[handlerStart..beginPromptStart];
        handlerBody.ShouldContain("XRAsset[] dirtyAssets = Engine.Assets?.DirtyAssets.Values.ToArray() ?? [];");
        handlerBody.ShouldContain("BeginClosePrompt(window, dirtyAssets);");
        handlerBody.ShouldNotContain("dirtyAssets.Length == 0");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, "XRENGINE.slnx");
            if (File.Exists(candidate))
                return File.ReadAllText(Path.Combine(directory, relativePath));

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the XRENGINE workspace root.");
    }
}

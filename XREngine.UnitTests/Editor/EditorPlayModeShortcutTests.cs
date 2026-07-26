using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Input.Devices;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class EditorPlayModeShortcutTests
{
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void ShiftF5_IsRecognizedAsTheGlobalForceExitShortcut(bool leftShiftDown, bool rightShiftDown)
        => EditorPlayModeController.IsForceExitShortcut(EKey.F5, leftShiftDown, rightShiftDown).ShouldBeTrue();

    [Test]
    public void F5WithoutShift_IsNotRecognizedAsTheGlobalForceExitShortcut()
        => EditorPlayModeController.IsForceExitShortcut(EKey.F5, false, false).ShouldBeFalse();

    [Test]
    public void ShiftWithAnotherKey_IsNotRecognizedAsTheGlobalForceExitShortcut()
        => EditorPlayModeController.IsForceExitShortcut(EKey.F6, true, false).ShouldBeFalse();

    [Test]
    public void GlobalForceExitShortcut_UsesTheDirectNonPromptingExitPath()
    {
        string source = ReadWorkspaceFile("XREngine.Editor/EditorPlayModeController.cs");

        source.ShouldContain("XRWindow.AnyWindowKeyDown += OnAnyWindowKeyDown;");
        source.ShouldContain("XRWindow.AnyWindowKeyDown -= OnAnyWindowKeyDown;");
        source.ShouldNotContain("LocalInputInterface.GlobalRegisters");

        int forceExitStart = source.IndexOf("private static void ForceExitPlayModeFromShortcut()", StringComparison.Ordinal);
        forceExitStart.ShouldBeGreaterThanOrEqualTo(0);
        int stepFrameStart = source.IndexOf("private static void HandleStepFrameShortcut()", forceExitStart, StringComparison.Ordinal);
        stepFrameStart.ShouldBeGreaterThan(forceExitStart);

        string forceExitBody = source[forceExitStart..stepFrameStart];
        forceExitBody.ShouldContain("EditorState.ExitPlayMode();");
        forceExitBody.ShouldNotContain("EditorState.RequestExitPlayMode();");
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

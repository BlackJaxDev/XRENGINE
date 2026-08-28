using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class EditorPlayModeLifecycleContractTests
{
    [Test]
    public void EditorStartup_BeginsInEditModeBeforeOptionalConfiguredPlay()
    {
        string source = ReadWorkspaceFile("XREngine.Editor/Program.cs");

        source.ShouldContain("Engine.Run(startupSettings, gameState, beginPlayingAllWorlds: false);");
        source.ShouldContain("if (!EditorUnitTests.Toggles.StartInPlayModeWithoutTransitions)");
        source.ShouldContain("Engine.PlayMode.ForcePlayWithoutTransitions = true;");
    }

    [Test]
    public void ExitPlayMode_RestartsWorldsBeforePublishingEditState()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.PlayMode.cs");
        int exitStart = source.IndexOf("public static Task ExitPlayModeAsync()", StringComparison.Ordinal);
        int toggleStart = source.IndexOf("public static void TogglePlayMode()", exitStart, StringComparison.Ordinal);
        exitStart.ShouldBeGreaterThanOrEqualTo(0);
        toggleStart.ShouldBeGreaterThan(exitStart);

        string exitBody = source[exitStart..toggleStart];
        int snapshotRestore = exitBody.IndexOf("Controller.RaisePostSnapshotRestore(restoredTarget);", StringComparison.Ordinal);
        int beginEditMode = exitBody.IndexOf("worldInstance.BeginEditMode().GetAwaiter().GetResult();", StringComparison.Ordinal);
        int publishEditState = exitBody.IndexOf("State = EPlayModeState.Edit;", StringComparison.Ordinal);

        snapshotRestore.ShouldBeGreaterThanOrEqualTo(0);
        beginEditMode.ShouldBeGreaterThan(snapshotRestore);
        publishEditState.ShouldBeGreaterThan(beginEditMode);
    }

    [Test]
    public void NativeFpsOverlay_PreservesItsEditorShellTickAcrossPlayTransitions()
    {
        string source = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs");
        int addFpsStart = source.IndexOf("public static UITextComponent AddFPSText", StringComparison.Ordinal);
        int nextMethod = source.IndexOf("private static void ConfigureFpsOverlayAnchor", addFpsStart, StringComparison.Ordinal);
        addFpsStart.ShouldBeGreaterThanOrEqualTo(0);
        nextMethod.ShouldBeGreaterThan(addFpsStart);

        string addFpsBody = source[addFpsStart..nextMethod];
        addFpsBody.ShouldContain("text.UnregisterTicksOnStop = false;");
        addFpsBody.ShouldContain("text.RegisterAnimationTick<UITextComponent>(TickFPS);");
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

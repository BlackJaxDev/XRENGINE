using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpenXrSteamVrParityToolingContractTests
{
    [Test]
    public void SteamVrSmokeTooling_UsesOpenXrModeAndRuntimeDiagnostics()
    {
        string runner = ReadWorkspaceFile("Tools/OpenXR/Run-OpenXrSteamVrSmoke.ps1");
        string tasks = ReadWorkspaceFile(".vscode/tasks.json");
        string launch = ReadWorkspaceFile(".vscode/launch.json");
        string todo = ReadWorkspaceFile("docs/work/todo/rendering/vr/openxr-steamvr-openvr-parity-todo.md");

        runner.ShouldContain("SteamVR OpenXR smoke diagnostics");
        runner.ShouldContain("XRE_UNIT_TEST_VR_MODE");
        runner.ShouldContain("\"OpenXR\"");
        runner.ShouldContain("steamvr-openxr-startup-diagnostics.json");
        runner.ShouldContain("XR_KHR_opengl_enable");
        runner.ShouldContain("XR_KHR_vulkan_enable");
        runner.ShouldContain("XR_KHR_vulkan_enable2");
        runner.ShouldContain("vrserver");
        runner.ShouldContain("vrmonitor");
        runner.ShouldContain("monadoServiceRecoveryInstalled");
        runner.ShouldContain("$false");

        tasks.ShouldContain("Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug");
        tasks.ShouldContain("Test-OpenXR-SteamVR-Smoke");
        launch.ShouldContain("Editor (Unit Testing OpenXR SteamVR)");
        todo.ShouldNotContain("- [ ]");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, platformPath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate workspace file '{relativePath}'.", relativePath);
    }
}

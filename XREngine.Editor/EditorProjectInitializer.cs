using System;
using System.IO;
using XREngine;
using XREngine.Core.Files;

internal static class EditorProjectInitializer
{
    private const string StartupAssetFileName = "startup.asset";

    public static bool InitializeNewProject(string projectDirectory, string projectName, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            string normalizedDirectory = Path.GetFullPath(projectDirectory);
            if (string.IsNullOrWhiteSpace(projectName))
            {
                error.WriteLine("Project name cannot be empty.");
                return false;
            }

            string descriptorPath = Path.Combine(normalizedDirectory, $"{projectName}.{XRProject.ProjectExtension}");
            if (File.Exists(descriptorPath))
            {
                error.WriteLine($"A project already exists at '{descriptorPath}'.");
                return false;
            }

            output.WriteLine($"Creating XRProject '{projectName}' at '{normalizedDirectory}'...");
            XRProject? project = Engine.CreateProject(normalizedDirectory, projectName);
            if (project is null)
            {
                error.WriteLine("Failed to create the XRProject descriptor.");
                return false;
            }

            if (!Engine.LoadProject(project))
            {
                error.WriteLine("Project descriptor was created, but the project failed to load.");
                return false;
            }

            EnsureStartupAsset(projectName, output);

            CodeManager.Instance.RemakeSolutionAsDLL(false);
            output.WriteLine($"Generated solution at '{CodeManager.Instance.GetSolutionPath()}'.");
            return true;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Failed to initialize project: {ex}");
            return false;
        }
    }

    private static void EnsureStartupAsset(string projectName, TextWriter output)
    {
        Directory.CreateDirectory(Engine.Assets.GameAssetsPath);

        Engine.GameSettings ??= new GameStartupSettings();
        Engine.GameSettings.Name = projectName;
        Engine.GameSettings.FilePath ??= Path.Combine(Engine.Assets.GameAssetsPath, StartupAssetFileName);
        Engine.Assets.Save(Engine.GameSettings, bypassJobThread: true);

        output.WriteLine($"Created startup asset at '{Engine.GameSettings.FilePath}'.");
    }
}

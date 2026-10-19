using Assimp;

namespace XREngine;

/// <summary>
/// Opens long Windows model paths through Assimp's managed file IO callbacks.
/// </summary>
internal static class AssimpModelFileImporter
{
    private const int WindowsMaximumLegacyPathLength = 260;

    public static Assimp.Scene Import(AssimpContext context, string sourcePath, PostProcessSteps steps)
    {
        if (!OperatingSystem.IsWindows())
            return context.ImportFile(sourcePath, steps);

        string fullPath = Path.GetFullPath(sourcePath);
        if (fullPath.Length < WindowsMaximumLegacyPathLength)
            return context.ImportFile(sourcePath, steps);

        string sourceDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Model path '{sourcePath}' has no parent directory.");
        using FileIOSystem fileSystem = new([sourceDirectory]);
        context.SetIOSystem(fileSystem);
        try
        {
            return context.ImportFile(fullPath, steps);
        }
        finally
        {
            context.RemoveIOSystem();
        }
    }
}

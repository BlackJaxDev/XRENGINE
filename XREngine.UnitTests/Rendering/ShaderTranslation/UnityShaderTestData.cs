using System.IO;
using NUnit.Framework;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

/// <summary>
/// Finds the synthetic Unity shaders under <c>XREngine.UnitTests/TestData/UnityShaders</c>.
/// </summary>
internal static class UnityShaderTestData
{
    public static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root (XRENGINE.slnx).");
        }
    }

    public static string CompatDirectory => Path.Combine(RepositoryRoot, "Build", "CommonAssets", "Shaders", "UnityCompat");

    public static string PathOf(string fileName) => Path.Combine(RepositoryRoot, "XREngine.UnitTests", "TestData", "UnityShaders", fileName);

    public static string Read(string fileName) => File.ReadAllText(PathOf(fileName));
}

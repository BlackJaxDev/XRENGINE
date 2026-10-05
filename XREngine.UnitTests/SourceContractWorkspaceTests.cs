using NUnit.Framework;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class SourceContractWorkspaceTests
{
    [Test]
    public void MissingCanonicalPath_DoesNotResolveAnExistingFileByName()
    {
        const string movedPath = "XREngine.UnitTests/MissingSourceContractFolder/SourceContractWorkspace.cs";
        Assert.Throws<FileNotFoundException>(() => SourceContractWorkspace.ReadFile(movedPath));
        Assert.Throws<FileNotFoundException>(() => SourceContractWorkspace.ReadExactFile(movedPath));
    }

    [Test]
    public void CanonicalPath_ResolvesTheRequiredRepositoryFile()
    {
        string source = SourceContractWorkspace.ReadExactFile("XREngine.UnitTests/SourceContractWorkspace.cs");
        Assert.That(source, Does.Contain("internal static class SourceContractWorkspace"));
    }

    [Test]
    public void TraversalOutsideRepository_IsRejected()
        => Assert.Throws<ArgumentException>(() => SourceContractWorkspace.ResolveCanonicalFile("../outside.cs"));
}

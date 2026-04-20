using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLTextureContractTests
{
    [Test]
    public void GLTexture_BatchesPropertyUpdateInvokesIntoSingleRenderThreadFlush()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture.cs");

        source.ShouldContain("private static readonly ConcurrentQueue<GLTexture<T>> s_pendingPropertyUpdateTextures = new();");
        source.ShouldContain("private static int s_propertyUpdateBatchQueued;");
        source.ShouldContain("Engine.EnqueueMainThreadTask(FlushQueuedPropertyUpdates, \"GLTexture.UpdateProperty\");");
        source.ShouldContain("while (s_pendingPropertyUpdateTextures.TryDequeue(out GLTexture<T>? texture))");
        source.ShouldContain("Interlocked.Exchange(ref _pendingPropertyUpdates, 0);");
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

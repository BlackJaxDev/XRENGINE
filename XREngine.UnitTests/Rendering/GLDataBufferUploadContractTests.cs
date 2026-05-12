using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLDataBufferUploadContractTests
{
    [Test]
    public void PushSubData_ReturnsWhenGrowthTriggersQueuedFullUpload()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Buffers/GLDataBuffer.cs");

        source.ShouldContain("if (_hasPendingUpload)");
        source.ShouldContain("return;");
        source.ShouldContain("RequestedRangeFits(offset, length, lastPushed)");
        source.ShouldContain("private static bool RequestedRangeFits(int offset, uint length, uint bufferLength)");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}

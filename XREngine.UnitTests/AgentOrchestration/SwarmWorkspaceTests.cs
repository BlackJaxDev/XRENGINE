using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class SwarmWorkspaceTests
{
    [Test]
    public void StaleHashLeavesTheExternalFileUnchanged()
    {
        using var repository = new TemporaryRepository();
        repository.WriteText("Source.cs", "class Before { }\n");
        SwarmWorkspace workspace = repository.CreateWorkspace();
        AgentContextFileSnapshot snapshot = Capture(workspace, "Source.cs").Single();
        repository.WriteText("Source.cs", "class External { }\n");

        SwarmApplyResult result = workspace.Apply(
            [Change(snapshot, "class After { }\n")],
            [snapshot],
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.AppliedPaths.ShouldBeEmpty();
        repository.ReadText("Source.cs").ShouldBe("class External { }\n");
    }

    [Test]
    public void ExistingFileAutoApplyPreservesUtf8BomAndCrLf()
    {
        using var repository = new TemporaryRepository();
        repository.WriteBytes("Source.cs", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class Before { }\r\n")]);
        SwarmWorkspace workspace = repository.CreateWorkspace();
        AgentContextFileSnapshot snapshot = Capture(workspace, "Source.cs").Single();

        SwarmApplyResult result = workspace.Apply(
            [Change(snapshot, "class After { }\n")],
            [snapshot],
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        byte[] raw = repository.ReadBytes("Source.cs");
        raw.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }).ShouldBeTrue();
        Encoding.UTF8.GetString(raw[3..]).ShouldBe("class After { }\r\n");
    }

    [Test]
    public void NewFileIsCreatedOnlyWhenItsMissingSnapshotStillMatches()
    {
        using var repository = new TemporaryRepository();
        SwarmWorkspace workspace = repository.CreateWorkspace();
        AgentContextFileSnapshot missing = Capture(workspace, "New.cs").Single();

        SwarmApplyResult result = workspace.Apply(
            [Change(missing, "class NewFile { }\n")],
            [missing],
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        repository.ReadText("New.cs").ShouldBe("class NewFile { }\n");
    }

    [Test]
    public void RacedNewFileExistenceIsReportedWithoutOverwritingIt()
    {
        using var repository = new TemporaryRepository();
        SwarmWorkspace workspace = repository.CreateWorkspace();
        AgentContextFileSnapshot missing = Capture(workspace, "New.cs").Single();
        repository.WriteText("New.cs", "class External { }\n");

        SwarmApplyResult result = workspace.Apply(
            [Change(missing, "class NewFile { }\n")],
            [missing],
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        repository.ReadText("New.cs").ShouldBe("class External { }\n");
    }

    [Test]
    public void CaptureRejectsBlockedPaths()
    {
        using var repository = new TemporaryRepository();
        SwarmWorkspace workspace = repository.CreateWorkspace();

        Should.Throw<ArgumentException>(() => Capture(workspace, ".git/config.cs"));
    }

    private static IReadOnlyList<AgentContextFileSnapshot> Capture(SwarmWorkspace workspace, params string[] paths)
        => workspace.Capture(
            new AgentRunRequest
            {
                Objective = "Test swarm workspace capture.",
                Budget = new AgentRunBudget
                {
                    MaxContextFiles = 64,
                    MaxContextFileBytes = 1_048_576,
                    MaxContextBytes = 1_048_576,
                    MaxContextRenderedBytes = 2_097_152,
                },
            },
            new AgentSwarmOptions { AllowedPaths = paths });

    private static AgentSwarmCodeChange Change(AgentContextFileSnapshot snapshot, string newText)
        => new()
        {
            Path = snapshot.Path,
            BaseSha256 = snapshot.Sha256,
            OldText = snapshot.Content,
            NewText = newText,
            NodeId = "leaf",
        };

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Root = Path.Combine(Path.GetTempPath(), $"xrengine-swarm-workspace-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public SwarmWorkspace CreateWorkspace()
            => new(new RepositoryPathPolicy(Root));

        public void WriteText(string relativePath, string content)
            => File.WriteAllText(Path.Combine(Root, relativePath), content, new UTF8Encoding(false));

        public void WriteBytes(string relativePath, byte[] content)
            => File.WriteAllBytes(Path.Combine(Root, relativePath), content);

        public string ReadText(string relativePath)
            => File.ReadAllText(Path.Combine(Root, relativePath), Encoding.UTF8);

        public byte[] ReadBytes(string relativePath)
            => File.ReadAllBytes(Path.Combine(Root, relativePath));

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}

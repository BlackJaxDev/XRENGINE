using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class AgentSwarmChangeMergerTests
{
    [Test]
    public void DisjointEditsUseImmutableOffsetsAndProduceOneWholeFileChange()
    {
        AgentContextFileSnapshot snapshot = ExistingSnapshot("Source.cs", "alpha\nbeta\ngamma\n", "hash");
        IReadOnlyList<AgentSwarmCodeChange> merged = AgentSwarmChangeMerger.Merge(
        [
            Change("Source.cs", "hash", "alpha", "ALPHA"),
            Change("Source.cs", "hash", "gamma", "GAMMA"),
        ],
        [snapshot]);

        merged.Count.ShouldBe(1);
        merged[0].OldText.ShouldBe(snapshot.Content);
        merged[0].NewText.ShouldBe("ALPHA\nbeta\nGAMMA\n");
    }

    [Test]
    public void OverlappingEditsAreRejected()
    {
        Should.Throw<ArgumentException>(() => AgentSwarmChangeMerger.Merge(
        [
            Change("Source.cs", "hash", "beta", "BETA"),
            Change("Source.cs", "hash", "beta\ngamma", "replacement"),
        ],
        [ExistingSnapshot("Source.cs", "alpha\nbeta\ngamma\n", "hash")]));
    }

    [Test]
    public void DuplicateNewFileCreationAndBaseMismatchAreRejected()
    {
        AgentContextFileSnapshot missing = ExistingSnapshot("New.cs", string.Empty, "missing");
        Should.Throw<ArgumentException>(() => AgentSwarmChangeMerger.Merge(
        [Change("New.cs", "missing", string.Empty, "one"), Change("New.cs", "missing", string.Empty, "two")], [missing]));
        Should.Throw<ArgumentException>(() => AgentSwarmChangeMerger.Merge(
            [Change("New.cs", "other", string.Empty, "one")], [missing]));
    }

    [Test]
    public void EmptyFileCreationIsPreserved()
    {
        IReadOnlyList<AgentSwarmCodeChange> merged = AgentSwarmChangeMerger.Merge(
            [Change("New.cs", "missing", string.Empty, "class NewFile { }\n")],
            [ExistingSnapshot("New.cs", string.Empty, "missing")]);

        merged.Single().NewText.ShouldBe("class NewFile { }\n");
    }

    private static AgentContextFileSnapshot ExistingSnapshot(string path, string content, string sha256)
        => new() { Path = path, Content = content, Sha256 = sha256 };

    private static AgentSwarmCodeChange Change(string path, string sha256, string oldText, string newText)
        => new() { Path = path, BaseSha256 = sha256, OldText = oldText, NewText = newText, NodeId = "leaf" };
}

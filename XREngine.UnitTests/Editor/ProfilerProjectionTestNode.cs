namespace XREngine.UnitTests.Editor;

internal sealed class ProfilerProjectionTestNode(params ProfilerProjectionTestNode[] children)
{
    public IReadOnlyList<ProfilerProjectionTestNode> Children { get; } = children;
}

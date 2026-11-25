namespace XREngine.UnitTests.Editor;

/// <summary>Rejects application formatting calls from retained detail rows.</summary>
internal sealed class NativeAnimationThrowingDetail
{
    public override string ToString()
        => throw new InvalidOperationException("Detail formatting called user code.");
}

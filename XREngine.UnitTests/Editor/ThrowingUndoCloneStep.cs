using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

internal sealed class ThrowingUndoCloneStep : Undo.IUndoStep
{
    public void ApplyOld() { }
    public void ApplyNew() { }
    public Undo.IUndoStep Clone() => throw new InvalidOperationException("Expected step clone failure.");
    public Undo.UndoChangeInfo ToInfo() => new(null, "Fail clone", null, string.Empty, null, null);
}

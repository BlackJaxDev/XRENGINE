using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Admits one exact owned property replay without changing ordinary Undo recording.</summary>
internal interface IUndoPropertyReplayGuard
{
    IDisposable Enter(XRBase target, string propertyName);
    void RequireCurrent();
    void Failed(Exception failure);
}

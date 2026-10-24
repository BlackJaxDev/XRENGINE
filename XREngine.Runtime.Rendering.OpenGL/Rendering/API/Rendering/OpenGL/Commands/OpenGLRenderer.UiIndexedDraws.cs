using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private UIIndexedDrawScopeState _uiIndexedDrawScope;
    private ulong _nextUiIndexedDrawScopeId;

    internal override bool TryBeginUiIndexedDrawScope(UIPublishedWorldMeshCommand command,
        ReadOnlySpan<UIIndexedDrawIntent> expected, out ulong scopeId)
    {
        scopeId = unchecked(++_nextUiIndexedDrawScopeId);
        if (scopeId == 0UL)
            scopeId = unchecked(++_nextUiIndexedDrawScopeId);
        if (AcceptsBackendWork && _uiIndexedDrawScope.TryBegin(scopeId,
                CurrentUiPublicationReceiptId, BackendGeneration, expected))
            return true;
        scopeId = 0UL;
        FailCurrentUiPublicationAuthoring("UI.Draw.ScopeRejected: the indexed draw has no exclusive active receipt.");
        return false;
    }

    internal override void AcknowledgeUiIndexedDraw(ulong scopeId,
        in UIIndexedDrawIntent actual, bool nativeSucceeded)
    {
        if (!_uiIndexedDrawScope.Acknowledge(scopeId, CurrentUiPublicationReceiptId,
                BackendGeneration, actual, nativeSucceeded))
            FailCurrentUiPublicationAuthoring("UI.Draw.NativeMismatch: the native draw did not match its frozen intent.");
    }

    internal override bool CompleteUiIndexedDrawScope(ulong scopeId)
    {
        bool complete = _uiIndexedDrawScope.Complete(scopeId,
            CurrentUiPublicationReceiptId, BackendGeneration);
        if (!complete)
            FailCurrentUiPublicationAuthoring("UI.Draw.NativeIncomplete: a required indexed draw did not complete.");
        return complete;
    }

    internal override void AbortUiIndexedDrawScope()
    {
        if (_uiIndexedDrawScope.ScopeId == 0UL)
            return;
        ulong receiptId = _uiIndexedDrawScope.ReceiptId;
        _uiIndexedDrawScope = default;
        AbortUiPublicationReceipt(receiptId,
            "UI.Draw.ScopeAborted: native indexed draw authoring did not finish before output completion.");
    }

    /// <summary>Issues a draw and checks errors only when a frozen UI intent requires proof.</summary>
    private unsafe void DrawUiCheckedElements(GLMeshRenderer mesh, EPrimitiveType topology,
        GLEnum nativeTopology, uint indexCount, IndexSize elementType, uint instances)
    {
        ulong scopeId = _uiIndexedDrawScope.ScopeId;
        bool cleanBefore = scopeId == 0UL || Api.GetError() == GLEnum.NoError;
        Api.DrawElementsInstanced(nativeTopology, indexCount, ToGLEnum(elementType), null, instances);
        if (scopeId == 0UL)
            return;
        bool cleanAfter = Api.GetError() == GLEnum.NoError;
        UIIndexedDrawIntent actual = new(mesh.MeshRenderer,
            mesh.Mesh?.GeometryRevision ?? -1, topology, elementType, indexCount, instances, 0);
        AcknowledgeUiIndexedDraw(scopeId, actual, cleanBefore && cleanAfter && instances != 0);
    }
}

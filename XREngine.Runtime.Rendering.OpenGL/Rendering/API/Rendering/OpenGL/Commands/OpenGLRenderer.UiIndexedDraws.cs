using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private UIIndexedDrawScopeState _uiIndexedDrawScope;
    private ulong _nextUiIndexedDrawScopeId;
    private ulong _uiBackdropConsumerScopeId;
    private ulong _nextUiBackdropConsumerScopeId;
    private ulong _uiBackdropConsumerDispatchToken;
    private XRWindow? _uiBackdropConsumerWindow;
    private IRuntimeWindowGlContext? _uiBackdropConsumerContext;
    private long _uiBackdropConsumerContextGeneration;
    private BoundingRectangle _uiBackdropConsumerRegion;
    private int _uiBackdropConsumerDrawCount;
    private bool _uiBackdropConsumerNativeSucceeded;

    internal override bool TryBeginUiBackdropConsumerDrawScope(
        XRWindow window, BoundingRectangle region, ulong dispatchToken, out ulong scopeId)
    {
        scopeId = 0UL;
        if (_uiBackdropConsumerScopeId != 0UL || !AcceptsBackendWork ||
            !ReferenceEquals(XRWindow, window) || dispatchToken == 0UL ||
            dispatchToken != window.ActiveOpenGlDispatchToken ||
            region.Width <= 0 || region.Height <= 0 ||
            !HasCurrentUiBackdropWindowContext(window))
            return false;
        scopeId = ++_nextUiBackdropConsumerScopeId;
        if (scopeId == 0UL)
            scopeId = ++_nextUiBackdropConsumerScopeId;
        _uiBackdropConsumerScopeId = scopeId;
        _uiBackdropConsumerWindow = window;
        _uiBackdropConsumerContext = window.DesktopGlContext;
        _uiBackdropConsumerContextGeneration = _uiBackdropConsumerContext?.OwnerGeneration ?? 0L;
        _uiBackdropConsumerRegion = region;
        _uiBackdropConsumerDispatchToken = dispatchToken;
        _uiBackdropConsumerDrawCount = 0;
        _uiBackdropConsumerNativeSucceeded = false;
        return true;
    }

    internal override bool CompleteUiBackdropConsumerDrawScope(ulong scopeId)
    {
        bool success = scopeId != 0UL && scopeId == _uiBackdropConsumerScopeId &&
            _uiBackdropConsumerDrawCount == 1 && _uiBackdropConsumerNativeSucceeded;
        AbortUiBackdropConsumerDrawScope();
        return success;
    }

    internal override void AbortUiBackdropConsumerDrawScope()
    {
        _uiBackdropConsumerScopeId = 0UL;
        _uiBackdropConsumerWindow = null;
        _uiBackdropConsumerContext = null;
        _uiBackdropConsumerContextGeneration = 0L;
        _uiBackdropConsumerRegion = default;
        _uiBackdropConsumerDispatchToken = 0UL;
        _uiBackdropConsumerDrawCount = 0;
        _uiBackdropConsumerNativeSucceeded = false;
    }

    internal override bool TryBeginUiIndexedDrawScope(UIPublishedWorldMeshCommand command,
        ReadOnlySpan<UIIndexedDrawIntent> expected, out ulong scopeId)
        => TryBeginUiIndexedDrawScope(expected, out scopeId);

    internal override bool TryBeginUiIndexedDrawScope(
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
        GLEnum nativeTopology, uint indexCount, IndexSize elementType, uint instances,
        XRMaterial? effectiveMaterial, XRRenderProgram? nativeMaterialProgram,
        uint nativeProgramId)
    {
        ulong scopeId = _uiIndexedDrawScope.ScopeId;
        UIBackdropOutputOverwriteCandidate candidate = default;
        bool hasOverwriteCandidate = topology == EPrimitiveType.Triangles &&
            TryGetUiBackdropOutputCandidate(mesh, out candidate);
        if (hasOverwriteCandidate)
            InvalidateUiBackdropOutputOverwriteProof();
        bool overwriteCandidate = hasOverwriteCandidate &&
            UIBackdropOutputContract.IsCanonicalWindowPresentCandidate(
                in candidate, mesh.MeshRenderer, mesh.Mesh, effectiveMaterial) &&
            IsCanonicalUiBackdropNativeProgram(in candidate, nativeMaterialProgram, nativeProgramId) &&
            CanOverwriteUiBackdropOutput(in candidate, mesh, indexCount, instances);
        bool consumerDraw = _uiBackdropConsumerScopeId != 0UL;
        bool consumerTargetValid = !consumerDraw ||
            (++_uiBackdropConsumerDrawCount == 1 &&
             _uiBackdropConsumerWindow is { } consumerWindow &&
             _uiBackdropConsumerDispatchToken == consumerWindow.ActiveOpenGlDispatchToken &&
             ReferenceEquals(_uiBackdropConsumerContext, consumerWindow.DesktopGlContext) &&
             _uiBackdropConsumerContextGeneration == consumerWindow.DesktopGlContext?.OwnerGeneration &&
             HasCurrentUiBackdropWindowContext(consumerWindow) &&
             RawGL.GetInteger(GLEnum.DrawFramebufferBinding) == 0 &&
             RawGL.GetInteger(GLEnum.DrawBuffer) == (int)GLEnum.Back &&
             UiBackdropConsumerViewportMatches(_uiBackdropConsumerRegion));
        if (consumerDraw && !consumerTargetValid)
        {
            _uiBackdropConsumerNativeSucceeded = false;
            if (scopeId != 0UL)
            {
                UIIndexedDrawIntent rejected = new(mesh.MeshRenderer,
                    mesh.Mesh?.GeometryRevision ?? -1, topology, elementType,
                    indexCount, instances, 0);
                AcknowledgeUiIndexedDraw(scopeId, rejected, false);
            }
            return;
        }
        bool checkNative = scopeId != 0UL || overwriteCandidate || consumerDraw;
        bool cleanBefore = !checkNative || Api.GetError() == GLEnum.NoError;
        Api.DrawElementsInstanced(nativeTopology, indexCount, ToGLEnum(elementType), null, instances);
        bool cleanAfter = !checkNative || Api.GetError() == GLEnum.NoError;
        if (consumerDraw)
            _uiBackdropConsumerNativeSucceeded = consumerTargetValid && cleanBefore && cleanAfter &&
                instances != 0u && indexCount != 0u;
        if (overwriteCandidate && cleanBefore && cleanAfter)
            RecordUiBackdropOutputOverwrite(in candidate);
        if (scopeId == 0UL)
            return;
        UIIndexedDrawIntent actual = new(mesh.MeshRenderer,
            mesh.Mesh?.GeometryRevision ?? -1, topology, elementType, indexCount, instances, 0);
        AcknowledgeUiIndexedDraw(scopeId, actual, cleanBefore && cleanAfter && instances != 0);
    }

    private bool IsCanonicalUiBackdropNativeProgram(
        in UIBackdropOutputOverwriteCandidate candidate,
        XRRenderProgram? program, uint nativeProgramId)
    {
        if (program is null || candidate.VertexShader is null ||
            candidate.FragmentShader is null || nativeProgramId == 0u ||
            RawGL.GetInteger(GLEnum.CurrentProgram) != (int)nativeProgramId)
            return false;
        if (program.Shaders.Count != 2)
            return false;
        bool hasVertex = false;
        bool hasFragment = false;
        for (int index = 0; index < program.Shaders.Count; index++)
        {
            XRShader shader = program.Shaders[index];
            if (shader.Type == EShaderType.Vertex &&
                ReferenceEquals(shader, candidate.VertexShader) &&
                shader.SourceRevision == candidate.VertexShaderRevision)
                hasVertex = true;
            else if (shader.Type == EShaderType.Fragment &&
                ReferenceEquals(shader, candidate.FragmentShader) &&
                shader.SourceRevision == candidate.FragmentShaderRevision)
                hasFragment = true;
            else
                return false;
        }
        return hasVertex && hasFragment;
    }

    private unsafe bool UiBackdropConsumerViewportMatches(in BoundingRectangle region)
    {
        int* viewport = stackalloc int[4];
        RawGL.GetInteger(GLEnum.Viewport, viewport);
        return viewport[0] == region.X && viewport[1] == region.Y &&
            viewport[2] == region.Width && viewport[3] == region.Height;
    }

}

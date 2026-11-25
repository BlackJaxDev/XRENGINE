using Silk.NET.OpenGL;
using System.Runtime.InteropServices;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    [DllImport("opengl32.dll", ExactSpelling = true)]
    private static extern nint wglGetCurrentContext();

    [DllImport("opengl32.dll", ExactSpelling = true)]
    private static extern nint wglGetCurrentDC();

    private bool HasCurrentUiBackdropWindowContext(XRWindow window)
    {
        if (!OperatingSystem.IsWindows() || !ReferenceEquals(XRWindow, window) ||
            window.DesktopGlContext is not { } context ||
            context.ContextHandle == 0 || context.DeviceContextHandle == 0 ||
            !ReferenceEquals(window.Renderer, this))
            return false;
        context.AssertOwnerThread();
        return wglGetCurrentContext() == context.ContextHandle &&
            wglGetCurrentDC() == context.DeviceContextHandle;
    }

    private UIBackdropOutputOverwriteProof _currentUiBackdropOutputProof;

    internal override void InvalidateUiBackdropOutputOverwriteProof()
        => _currentUiBackdropOutputProof = default;

    internal override bool TryGetCurrentUiBackdropOutputOverwriteProof(
        XRWindow window, out UIBackdropOutputOverwriteProof proof)
    {
        proof = _currentUiBackdropOutputProof;
        if (ReferenceEquals(proof.Renderer, this) && ReferenceEquals(proof.Window, window) &&
            ReferenceEquals(XRWindow, window) && proof.BackendGeneration == BackendGeneration &&
            proof.WindowGlContextGeneration == window.DesktopGlContext?.OwnerGeneration &&
            proof.DispatchToken != 0UL && proof.DispatchToken == window.ActiveOpenGlDispatchToken &&
            HasCurrentUiBackdropWindowContext(window))
            return true;
        proof = default;
        return false;
    }

    private static bool TryGetUiBackdropOutputCandidate(
        GLMeshRenderer mesh, out UIBackdropOutputOverwriteCandidate candidate)
    {
        IRenderBindingPublisher[] publishers = mesh.MeshRenderer.BindingPublishers.CaptureSnapshot();
        for (int index = 0; index < publishers.Length; index++)
        {
            if (publishers[index] is IWindowOutputOverwriteCandidateProvider provider &&
                provider.TryGetWindowOutputOverwriteCandidate(out candidate))
                return true;
        }
        candidate = default;
        return false;
    }

    private unsafe bool CanOverwriteUiBackdropOutput(
        in UIBackdropOutputOverwriteCandidate candidate,
        GLMeshRenderer mesh, uint indexCount, uint instances)
    {
        if (candidate.Window is not { } window ||
            !ReferenceEquals(candidate.Renderer, this) ||
            !ReferenceEquals(window, XRWindow) ||
            candidate.BackendGeneration != BackendGeneration ||
            candidate.DispatchToken == 0UL ||
            candidate.DispatchToken != window.ActiveOpenGlDispatchToken ||
            !ReferenceEquals(candidate.WindowGlContext, window.DesktopGlContext) ||
            candidate.WindowGlContextGeneration != window.DesktopGlContext?.OwnerGeneration ||
            !HasCurrentUiBackdropWindowContext(window) ||
            mesh.ConditionalRenderQuery is not null ||
            indexCount != 3u || instances != 1u ||
            candidate.Region.Width <= 0 || candidate.Region.Height <= 0)
            return false;

        if (RawGL.GetInteger(GLEnum.DrawFramebufferBinding) != 0 ||
            RawGL.GetInteger(GLEnum.DrawBuffer) != (int)GLEnum.Back ||
            RawGL.IsEnabled(GLEnum.Blend) ||
            RawGL.IsEnabled(GLEnum.DepthTest) ||
            RawGL.IsEnabled(GLEnum.StencilTest) ||
            RawGL.IsEnabled(GLEnum.RasterizerDiscard) ||
            RawGL.IsEnabled(GLEnum.CullFace) ||
            RawGL.IsEnabled(GLEnum.ColorLogicOp) ||
            RawGL.IsEnabled(GLEnum.SampleAlphaToCoverage) ||
            RawGL.IsEnabled(GLEnum.SampleCoverage) ||
            RawGL.IsEnabled(GLEnum.SampleMask))
            return false;

        int* polygonMode = stackalloc int[2];
        RawGL.GetInteger(GLEnum.PolygonMode, polygonMode);
        if (polygonMode[0] != (int)GLEnum.Fill || polygonMode[1] != (int)GLEnum.Fill)
            return false;
        for (int index = 0; index < 8; index++)
            if (RawGL.IsEnabled((GLEnum)((int)GLEnum.ClipDistance0 + index)))
                return false;

        int* viewport = stackalloc int[4];
        RawGL.GetInteger(GLEnum.Viewport, viewport);
        if (!MatchesRegion(viewport, candidate.Region))
            return false;
        if (RawGL.IsEnabled(GLEnum.ScissorTest))
        {
            int* scissor = stackalloc int[4];
            RawGL.GetInteger(GLEnum.ScissorBox, scissor);
            if (scissor[0] > candidate.Region.X || scissor[1] > candidate.Region.Y ||
                (long)scissor[0] + scissor[2] < (long)candidate.Region.X + candidate.Region.Width ||
                (long)scissor[1] + scissor[3] < (long)candidate.Region.Y + candidate.Region.Height)
                return false;
        }

        int* colorMask = stackalloc int[4];
        RawGL.GetInteger(GLEnum.ColorWritemask, 0, colorMask);
        return colorMask[0] != 0 && colorMask[1] != 0 &&
            colorMask[2] != 0 && colorMask[3] != 0;
    }

    private static unsafe bool MatchesRegion(int* viewport, in BoundingRectangle region)
        => viewport[0] == region.X && viewport[1] == region.Y &&
           viewport[2] == region.Width && viewport[3] == region.Height;

    private void RecordUiBackdropOutputOverwrite(
        in UIBackdropOutputOverwriteCandidate candidate)
    {
        if (candidate.Window is not { } window)
            return;
        _currentUiBackdropOutputProof = new UIBackdropOutputOverwriteProof(
            this, window, BackendGeneration,
            window.DesktopGlContext?.OwnerGeneration ?? 0L,
            candidate.DispatchToken, candidate.Region);
    }
}

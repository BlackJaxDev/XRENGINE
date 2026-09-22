using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Pairs frozen logical views with the current temporal sample before native
/// stage authoring, without replacing the history ledger's logical descriptors.
/// </summary>
internal static class RenderFrameTemporalViews
{
    [InlineArray(RenderFrameViewSet.MaxViewCount)]
    private struct ViewBuffer
    {
        private RenderFrameViewDescriptor _element0;
    }

    internal static RenderFrameViewSet Create(in RenderFrameViewSet source,
        in VPRC_TemporalAccumulationPass.TemporalUniformData temporal)
    {
        ViewBuffer storage = default;
        var builder = new RenderFrameViewSetBuilder(storage);
        for (int i = 0; i < source.ViewCount; i++)
        {
            RenderFrameViewDescriptor view = source.GetView(i);
            bool right = view.IsRightEyeFamily;
            Vector2 jitter = right ? temporal.RightEyeCurrentJitter : temporal.CurrentJitter;
            Vector2 previousJitter = right ? temporal.RightEyePreviousJitter : temporal.PreviousJitter;
            Matrix4x4 unjittered = view.ProjectionMatrixUnjittered == default
                ? view.ProjectionMatrix : view.ProjectionMatrixUnjittered;
            Vector2 extent = new(Math.Max(1, view.ViewRect.Width), Math.Max(1, view.ViewRect.Height));
            Vector2 clipJitter = ProjectionJitterRequest.TexelSpace(jitter, extent).ToClipSpace();
            Vector2 previousClipJitter = ProjectionJitterRequest.TexelSpace(previousJitter, extent).ToClipSpace();
            Matrix4x4 projection = XRCamera.ApplyProjectionJitter(unjittered, clipJitter,
                MathF.Abs(unjittered.M34) < 1e-6f);
            bool ready = view.HasValidTemporalHistory &&
                (right ? temporal.RightEyeHistoryReady : temporal.LeftEyeHistoryReady);
            // Preserve the exact logical view's history (wide/inset views can
            // share an eye camera). Reapply jitter in homogeneous clip space.
            Matrix4x4 previousUnjittered = view.PreviousViewProjectionMatrixUnjittered;
            Matrix4x4 previous = previousUnjittered * Matrix4x4.CreateTranslation(previousClipJitter.X, previousClipJitter.Y, 0f);
            builder.Add(view with
            {
                ProjectionMatrix = projection,
                ProjectionMatrixUnjittered = unjittered,
                CurrentJitter = clipJitter,
                PreviousJitter = ready ? previousClipJitter : clipJitter,
                PreviousViewProjectionMatrix = ready ? previous : view.ViewMatrix * projection,
                PreviousViewProjectionMatrixUnjittered = ready ? previousUnjittered : view.ViewMatrix * unjittered,
                HistoryStatus = ready ? view.HistoryStatus : ERenderFrameViewHistoryStatus.Unavailable,
            });
        }
        return builder.Build(source.RenderMode, source.VisibilityPolicy,
            source.VisibilityGroupCount, source.DebugName);
    }
}

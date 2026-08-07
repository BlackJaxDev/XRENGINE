using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        private void MarkSkippedResizeFrameObserved(long frameStartTimestamp)
        {
            RecordDesktopFrameTickObserved(frameStartTimestamp);
        }

        private static void RecordOverlayFrameOutput(
            EFrameOutputKind outputKind,
            string name,
            bool rendered,
            int commandCount,
            long elapsedTicks)
        {
            double cpuMs = elapsedTicks <= 0L ? 0.0 : elapsedTicks * 1000.0 / Stopwatch.Frequency;
            IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
            EVrOutputViewKind viewKind = presentation.IsInVR && presentation.VrMirrorMode != EVrMirrorMode.FullIndependentRender
                ? EVrOutputViewKind.CyclopeanDesktop
                : EVrOutputViewKind.DesktopEditor;
            bool mirror = presentation.IsInVR &&
                viewKind == EVrOutputViewKind.CyclopeanDesktop &&
                presentation.VrMirrorMode is EVrMirrorMode.BlitSubmittedEye or EVrMirrorMode.CyclopeanReconstruct;
            var pacing = FrameOutputPacingDecision.Due(viewKind, outputKind, RuntimeEngine.Rendering.State.RenderFrameId);
            var telemetry = new FrameOutputTelemetry(
                outputKind,
                viewKind,
                EFrameOutputPhase.Overlay,
                pacing,
                name,
                string.Empty,
                true,
                rendered,
                false,
                mirror,
                false,
                viewKind == EVrOutputViewKind.CyclopeanDesktop && presentation.VrMirrorMode != EVrMirrorMode.FullIndependentRender,
                commandCount,
                0,
                0,
                0,
                cpuMs,
                0.0);
            presentation.RecordRenderFrameOutput(telemetry);
        }

    }
}

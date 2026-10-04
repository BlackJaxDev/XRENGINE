using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderScopeOwnershipTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void MainAttributeExit_ReleasesOnlyItsOwnRenderAndCropRegions(bool applyRenderArea)
    {
        using AbstractRenderer.ThreadCurrentScope rendererScope = AbstractRenderer.EnterThreadCurrentScope(null);
        XRRenderPipelineInstance.RenderingState state = new();
        BoundingRectangle enclosingRender = new(3, 5, 320, 180);
        BoundingRectangle enclosingCrop = new(11, 13, 280, 140);
        BoundingRectangle outerCrop = new(17, 19, 70, 40);
        XRRenderBuffer? outerBuffer = null;
        XRRenderBuffer? innerBuffer = null;
        XRFrameBuffer? outerTarget = null;
        XRFrameBuffer? innerTarget = null;

        try
        {
            outerBuffer = new XRRenderBuffer(96, 64, ERenderBufferStorage.Rgba32f);
            innerBuffer = new XRRenderBuffer(48, 32, ERenderBufferStorage.Rgba32f);
            outerTarget = Target("Outer", outerBuffer);
            innerTarget = Target("Inner", innerBuffer);

            using (state.PushRenderArea(enclosingRender))
            using (state.PushCropArea(enclosingCrop))
            {
                using (PushMain(state, outerTarget, applyRenderArea: true))
                {
                    state.CurrentRenderRegion.ShouldBe(new BoundingRectangle(0, 0, 96, 64));
                    state.PushCropAreaState(outerCrop);

                    using (PushMain(state, innerTarget, applyRenderArea))
                    {
                        state.CurrentRenderRegion.ShouldBe(applyRenderArea
                            ? new BoundingRectangle(0, 0, 48, 32)
                            : new BoundingRectangle(0, 0, 96, 64));
                        state.CurrentCropRegion.ShouldBe(outerCrop);

                        // A frame abort can leave queued state pops unexecuted.
                        state.PushRenderAreaState(new BoundingRectangle(2, 4, 24, 16));
                        state.PushCropAreaState(new BoundingRectangle(6, 8, 12, 10));
                    }

                    state.CurrentRenderRegion.ShouldBe(new BoundingRectangle(0, 0, 96, 64));
                    state.CurrentCropRegion.ShouldBe(outerCrop);
                }

                state.CurrentRenderRegion.ShouldBe(enclosingRender);
                state.CurrentCropRegion.ShouldBe(enclosingCrop);
            }

            state.CurrentRenderRegion.ShouldBe(BoundingRectangle.Empty);
            state.CurrentCropRegion.ShouldBe(BoundingRectangle.Empty);
        }
        finally
        {
            innerTarget?.Destroy(now: true);
            outerTarget?.Destroy(now: true);
            innerBuffer?.Destroy(now: true);
            outerBuffer?.Destroy(now: true);
        }
    }

    private static XRFrameBuffer Target(string name, XRRenderBuffer buffer)
        => new((buffer,
            EFrameBufferAttachment.ColorAttachment0, 0, 0)) { Name = name };

    private static StateObject PushMain(
        XRRenderPipelineInstance.RenderingState state,
        XRFrameBuffer target,
        bool applyRenderArea)
        => state.PushMainAttributes(
            viewport: null,
            scene: null,
            camera: null,
            stereoRightEyeCamera: null,
            target: target,
            shadowPass: false,
            stereoPass: false,
            globalMaterialOverride: null,
            screenSpaceUI: null,
            meshRenderCommands: null,
            applyRenderArea: applyRenderArea);
}

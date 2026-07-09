using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderStateThreadIsolationTests
{
    [Test]
    public void RuntimeRenderingState_StacksAreThreadLocal()
    {
        XRRenderPipelineInstance mainPipeline = new();
        XRRenderPipelineInstance workerPipeline = new();
        XRCamera mainCamera = new();
        XRCamera workerCamera = new();

        using IDisposable? mainPipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipeline(mainPipeline);
        using IDisposable mainPassScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(101);
        using IDisposable mainTransformScope = RuntimeEngine.Rendering.State.PushTransformId(202);
        RuntimeEngine.Rendering.State.RenderingCameraOverride = mainCamera;
        RuntimeEngine.Rendering.State.PushMirrorPass();

        try
        {
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline.ShouldBeSameAs(mainPipeline);
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(101);
            RuntimeEngine.Rendering.State.CurrentTransformId.ShouldBe(202u);
            RuntimeEngine.Rendering.State.RenderingCameraOverride.ShouldBeSameAs(mainCamera);
            RuntimeEngine.Rendering.State.MirrorPassIndex.ShouldBe(1);

            RunOnWorkerThread(() =>
            {
                RuntimeEngine.Rendering.State.CurrentRenderingPipeline.ShouldBeNull();
                RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(int.MinValue);
                RuntimeEngine.Rendering.State.CurrentTransformId.ShouldBe(0u);
                RuntimeEngine.Rendering.State.RenderingCameraOverride.ShouldBeNull();
                RuntimeEngine.Rendering.State.MirrorPassIndex.ShouldBe(0);

                {
                    using IDisposable? workerPipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipeline(workerPipeline);
                    using IDisposable workerPassScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(303);
                    using IDisposable workerTransformScope = RuntimeEngine.Rendering.State.PushTransformId(404);
                    RuntimeEngine.Rendering.State.RenderingCameraOverride = workerCamera;
                    RuntimeEngine.Rendering.State.PushMirrorPass();

                    try
                    {
                        RuntimeEngine.Rendering.State.CurrentRenderingPipeline.ShouldBeSameAs(workerPipeline);
                        RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(303);
                        RuntimeEngine.Rendering.State.CurrentTransformId.ShouldBe(404u);
                        RuntimeEngine.Rendering.State.RenderingCameraOverride.ShouldBeSameAs(workerCamera);
                        RuntimeEngine.Rendering.State.MirrorPassIndex.ShouldBe(1);
                    }
                    finally
                    {
                        RuntimeEngine.Rendering.State.PopMirrorPass();
                        RuntimeEngine.Rendering.State.RenderingCameraOverride = null;
                    }
                }

                RuntimeEngine.Rendering.State.CurrentRenderingPipeline.ShouldBeNull();
                RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(int.MinValue);
                RuntimeEngine.Rendering.State.CurrentTransformId.ShouldBe(0u);
                RuntimeEngine.Rendering.State.RenderingCameraOverride.ShouldBeNull();
                RuntimeEngine.Rendering.State.MirrorPassIndex.ShouldBe(0);
            });

            RuntimeEngine.Rendering.State.CurrentRenderingPipeline.ShouldBeSameAs(mainPipeline);
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(101);
            RuntimeEngine.Rendering.State.CurrentTransformId.ShouldBe(202u);
            RuntimeEngine.Rendering.State.RenderingCameraOverride.ShouldBeSameAs(mainCamera);
            RuntimeEngine.Rendering.State.MirrorPassIndex.ShouldBe(1);
        }
        finally
        {
            RuntimeEngine.Rendering.State.PopMirrorPass();
            RuntimeEngine.Rendering.State.RenderingCameraOverride = null;
        }
    }

    [Test]
    public void LegacyEngineRenderingState_StacksAreThreadLocal()
    {
        XRRenderPipelineInstance mainPipeline = new();
        XRRenderPipelineInstance workerPipeline = new();
        XRCamera mainCamera = new();
        XRCamera workerCamera = new();

        using StateObject mainPipelineScope = Engine.Rendering.State.PushRenderingPipeline(mainPipeline);
        using StateObject mainPassScope = Engine.Rendering.State.PushRenderGraphPassIndex(111);
        using StateObject mainTransformScope = Engine.Rendering.State.PushTransformId(222);
        Engine.Rendering.State.RenderingCameraOverride = mainCamera;
        Engine.Rendering.State.PushMirrorPass();

        try
        {
            Engine.Rendering.State.CurrentRenderingPipeline.ShouldBeSameAs(mainPipeline);
            Engine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(111);
            Engine.Rendering.State.CurrentTransformId.ShouldBe(222u);
            Engine.Rendering.State.RenderingCameraOverride.ShouldBeSameAs(mainCamera);
            Engine.Rendering.State.MirrorPassIndex.ShouldBe(1);

            RunOnWorkerThread(() =>
            {
                Engine.Rendering.State.CurrentRenderingPipeline.ShouldBeNull();
                Engine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(int.MinValue);
                Engine.Rendering.State.CurrentTransformId.ShouldBe(0u);
                Engine.Rendering.State.RenderingCameraOverride.ShouldBeNull();
                Engine.Rendering.State.MirrorPassIndex.ShouldBe(0);

                {
                    using StateObject workerPipelineScope = Engine.Rendering.State.PushRenderingPipeline(workerPipeline);
                    using StateObject workerPassScope = Engine.Rendering.State.PushRenderGraphPassIndex(333);
                    using StateObject workerTransformScope = Engine.Rendering.State.PushTransformId(444);
                    Engine.Rendering.State.RenderingCameraOverride = workerCamera;
                    Engine.Rendering.State.PushMirrorPass();

                    try
                    {
                        Engine.Rendering.State.CurrentRenderingPipeline.ShouldBeSameAs(workerPipeline);
                        Engine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(333);
                        Engine.Rendering.State.CurrentTransformId.ShouldBe(444u);
                        Engine.Rendering.State.RenderingCameraOverride.ShouldBeSameAs(workerCamera);
                        Engine.Rendering.State.MirrorPassIndex.ShouldBe(1);
                    }
                    finally
                    {
                        Engine.Rendering.State.PopMirrorPass();
                        Engine.Rendering.State.RenderingCameraOverride = null;
                    }
                }

                Engine.Rendering.State.CurrentRenderingPipeline.ShouldBeNull();
                Engine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(int.MinValue);
                Engine.Rendering.State.CurrentTransformId.ShouldBe(0u);
                Engine.Rendering.State.RenderingCameraOverride.ShouldBeNull();
                Engine.Rendering.State.MirrorPassIndex.ShouldBe(0);
            });

            Engine.Rendering.State.CurrentRenderingPipeline.ShouldBeSameAs(mainPipeline);
            Engine.Rendering.State.CurrentRenderGraphPassIndex.ShouldBe(111);
            Engine.Rendering.State.CurrentTransformId.ShouldBe(222u);
            Engine.Rendering.State.RenderingCameraOverride.ShouldBeSameAs(mainCamera);
            Engine.Rendering.State.MirrorPassIndex.ShouldBe(1);
        }
        finally
        {
            Engine.Rendering.State.PopMirrorPass();
            Engine.Rendering.State.RenderingCameraOverride = null;
        }
    }

    [Test]
    public void RuntimeRenderingState_PassFlagsAreThreadLocal()
    {
        RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
        RuntimeEngine.Rendering.State.IsLightProbePass = true;
        RuntimeEngine.Rendering.State.ReverseWinding = true;
        RuntimeEngine.Rendering.State.ReverseCulling = true;

        try
        {
            RuntimeEngine.Rendering.State.IsSceneCapturePass.ShouldBeTrue();
            RuntimeEngine.Rendering.State.IsLightProbePass.ShouldBeTrue();
            RuntimeEngine.Rendering.State.ReverseWinding.ShouldBeTrue();
            RuntimeEngine.Rendering.State.ReverseCulling.ShouldBeTrue();

            RunOnWorkerThread(() =>
            {
                RuntimeEngine.Rendering.State.IsSceneCapturePass.ShouldBeFalse();
                RuntimeEngine.Rendering.State.IsLightProbePass.ShouldBeFalse();
                RuntimeEngine.Rendering.State.ReverseWinding.ShouldBeFalse();
                RuntimeEngine.Rendering.State.ReverseCulling.ShouldBeFalse();

                RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
                RuntimeEngine.Rendering.State.IsLightProbePass = true;
                RuntimeEngine.Rendering.State.ReverseWinding = true;
                RuntimeEngine.Rendering.State.ReverseCulling = true;

                RuntimeEngine.Rendering.State.IsSceneCapturePass.ShouldBeTrue();
                RuntimeEngine.Rendering.State.IsLightProbePass.ShouldBeTrue();
                RuntimeEngine.Rendering.State.ReverseWinding.ShouldBeTrue();
                RuntimeEngine.Rendering.State.ReverseCulling.ShouldBeTrue();
            });

            RuntimeEngine.Rendering.State.IsSceneCapturePass.ShouldBeTrue();
            RuntimeEngine.Rendering.State.IsLightProbePass.ShouldBeTrue();
            RuntimeEngine.Rendering.State.ReverseWinding.ShouldBeTrue();
            RuntimeEngine.Rendering.State.ReverseCulling.ShouldBeTrue();
        }
        finally
        {
            RuntimeEngine.Rendering.State.IsSceneCapturePass = false;
            RuntimeEngine.Rendering.State.IsLightProbePass = false;
            RuntimeEngine.Rendering.State.ReverseWinding = false;
            RuntimeEngine.Rendering.State.ReverseCulling = false;
        }
    }

    [Test]
    public void RuntimeRenderingState_MirrorPassRestoresPreviousCaptureState()
    {
        RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
        RuntimeEngine.Rendering.State.ReverseCulling = false;

        try
        {
            RuntimeEngine.Rendering.State.PushMirrorPass();

            try
            {
                RuntimeEngine.Rendering.State.IsSceneCapturePass.ShouldBeTrue();
                RuntimeEngine.Rendering.State.MirrorPassIndex.ShouldBe(1);
                RuntimeEngine.Rendering.State.ReverseCulling.ShouldBeTrue();
            }
            finally
            {
                RuntimeEngine.Rendering.State.PopMirrorPass();
            }

            RuntimeEngine.Rendering.State.IsSceneCapturePass.ShouldBeTrue();
            RuntimeEngine.Rendering.State.ReverseCulling.ShouldBeFalse();
        }
        finally
        {
            RuntimeEngine.Rendering.State.IsSceneCapturePass = false;
            RuntimeEngine.Rendering.State.ReverseCulling = false;
        }
    }

    private static void RunOnWorkerThread(Action action)
    {
        Exception? workerException = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                workerException = ex;
            }
        });

        thread.Start();
        thread.Join();

        if (workerException is not null)
            throw workerException;
    }

}

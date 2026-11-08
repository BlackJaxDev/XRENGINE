using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;
using XREngine.Rendering;

namespace XREngine.UnitTests.Cutscenes;

/// <summary>
/// Covers <see cref="CutsceneBackendProgramPrewarmer"/> with a fake backend probe and a manual render-thread scheduler. No
/// test here runs a real OpenGL or Vulkan backend: the unit tests have no GL context and no Vulkan device.
/// </summary>
[TestFixture]
public sealed class CutsceneBackendProgramPrewarmerTests
{
    /// <summary>A backend probe whose state for each renderer the test sets. A renderer with no state is not started.</summary>
    private sealed class FakeBackend
    {
        public Dictionary<XRMeshRenderer, (ERenderDrawReadiness State, string Reason)> States { get; } = new(ReferenceEqualityComparer.Instance);

        public HashSet<XRMeshRenderer> Throwing { get; } = new(ReferenceEqualityComparer.Instance);

        public int ProbeCount { get; private set; }

        public XRRenderPipelineInstance? LastView { get; private set; }

        public XREngine.Rendering.Models.Materials.RenderingParameters? LastRenderOptionsOverride { get; private set; }

        public List<XRMeshRenderer> Ended { get; } = [];

        public void End(XRMeshRenderer renderer)
            => Ended.Add(renderer);

        public ERenderDrawReadiness Probe(XRMeshRenderer renderer, XRMaterial material, XREngine.Rendering.Models.Materials.RenderingParameters? renderOptionsOverride, XRRenderPipelineInstance? view, out string reason)
        {
            ProbeCount++;
            LastView = view;
            LastRenderOptionsOverride = renderOptionsOverride;
            if (Throwing.Contains(renderer))
                throw new InvalidOperationException("wrapper publication is not ready");

            if (States.TryGetValue(renderer, out (ERenderDrawReadiness State, string Reason) value))
            {
                reason = value.Reason;
                return value.State;
            }
            reason = "not started";
            return ERenderDrawReadiness.NotStarted;
        }
    }

    /// <summary>A scheduler that keeps the coroutine steps, so the test runs one render-thread frame at a time.</summary>
    private sealed class ManualScheduler
    {
        private readonly List<Func<bool>> _steps = [];

        public int ScheduleCount { get; private set; }

        public int ActiveCount => _steps.Count;

        public void Schedule(Func<bool> step)
        {
            ScheduleCount++;
            _steps.Add(step);
        }

        public void RunFrame()
        {
            for (int i = _steps.Count - 1; i >= 0; i--)
                if (_steps[i]())
                    _steps.RemoveAt(i);
        }
    }

    private static XRMeshRenderer MakeRenderer(XRMaterial material)
        => new(XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY), material);

    private static CutsceneBackendProgramPrewarmer Make(FakeBackend backend, ManualScheduler scheduler, Func<XRMaterial, bool>? isVariantReady = null)
        => new(backend.Probe, scheduler.Schedule, isVariantReady ?? (_ => true), backend.End);

    [Test]
    public void State_FollowsTheBackend_AndReadyComesOnlyAfterTheBackendIsReady()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);

        prewarmer.Request(material, renderer, null);
        scheduler.ScheduleCount.ShouldBe(1);

        // The update thread reads only stored results, so nothing is ready before the render thread checks the draw.
        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        prewarmer.GetState(material, renderer, null, out _).ShouldBe(ECutsceneProgramState.NotStarted);
        backend.ProbeCount.ShouldBe(0);

        backend.States[renderer] = (ERenderDrawReadiness.NotStarted, "no frame drew the renderer");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, null, out string? reason).ShouldBe(ECutsceneProgramState.NotStarted);
        reason.ShouldBe("no frame drew the renderer");

        backend.States[renderer] = (ERenderDrawReadiness.Compiling, "graphics pipeline compile is queued");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, null, out reason).ShouldBe(ECutsceneProgramState.Compiling);
        reason.ShouldBe("graphics pipeline compile is queued");

        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, null, out _).ShouldBe(ECutsceneProgramState.Ready);

        // Ready is not final: the coroutine keeps checking until the open stops it.
        scheduler.ActiveCount.ShouldBe(1);
    }

    [Test]
    public void ABackendReady_ThatChangesBackToCompiling_IsFollowed()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);
        prewarmer.Request(material, renderer, null);

        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, null, out _).ShouldBe(ECutsceneProgramState.Ready);

        // A relink or a new pipeline variant makes the draw cold again.
        backend.States[renderer] = (ERenderDrawReadiness.Compiling, "graphics pipeline compile was queued");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, null, out string? reason).ShouldBe(ECutsceneProgramState.Compiling);
        reason.ShouldBe("graphics pipeline compile was queued");
    }

    [Test]
    public void WhileTheUberVariantBuilds_TheDrawIsCompiling_AndTheBackendIsNotAsked()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        bool variantReady = false;
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler, _ => variantReady);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);
        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready for the canonical program");
        prewarmer.Request(material, renderer, null);

        scheduler.RunFrame();
        backend.ProbeCount.ShouldBe(0);
        prewarmer.GetState(material, renderer, null, out string? reason).ShouldBe(ECutsceneProgramState.Compiling);
        reason.ShouldNotBeNull().ShouldContain("variant");

        // The variant activates. The stale backend result of the canonical program must not count: the next check decides.
        variantReady = true;
        prewarmer.GetState(material, renderer, null, out _).ShouldBe(ECutsceneProgramState.Compiling);
        backend.States[renderer] = (ERenderDrawReadiness.Compiling, "the variant pipeline compiles");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, null, out _).ShouldBe(ECutsceneProgramState.Compiling);

        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, null, out _).ShouldBe(ECutsceneProgramState.Ready);
    }

    [TestCase(ERenderDrawReadiness.Failed, ECutsceneProgramState.Failed)]
    [TestCase(ERenderDrawReadiness.NotSupported, ECutsceneProgramState.NotSupported)]
    public void FailedAndNotSupported_AreFinal_AndKeepTheReason(ERenderDrawReadiness backendState, ECutsceneProgramState expected)
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);
        backend.States[renderer] = (backendState, "backend reason");

        prewarmer.Request(material, renderer, null);
        scheduler.RunFrame();

        prewarmer.GetState(material, renderer, null, out string? reason).ShouldBe(expected);
        reason.ShouldBe("backend reason");

        // A final state is not checked again, and no later backend state hides it.
        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        int probes = backend.ProbeCount;
        scheduler.RunFrame();
        backend.ProbeCount.ShouldBe(probes);
        prewarmer.GetState(material, renderer, null, out _).ShouldBe(expected);
    }

    [Test]
    public void AThrowingCheck_MarksThatDrawFailed_AndTheOtherDrawsContinue()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial first = new() { Name = "First" };
        XRMaterial second = new() { Name = "Second" };
        XRMeshRenderer firstRenderer = MakeRenderer(first);
        XRMeshRenderer secondRenderer = MakeRenderer(second);
        backend.Throwing.Add(firstRenderer);
        backend.States[secondRenderer] = (ERenderDrawReadiness.Compiling, "compiling");
        prewarmer.Request(first, firstRenderer, null);
        prewarmer.Request(second, secondRenderer, null);

        scheduler.RunFrame();

        prewarmer.GetState(first, firstRenderer, null, out string? reason).ShouldBe(ECutsceneProgramState.Failed);
        reason.ShouldNotBeNull().ShouldContain("wrapper publication is not ready");
        prewarmer.GetState(second, secondRenderer, null, out _).ShouldBe(ECutsceneProgramState.Compiling);
        scheduler.ActiveCount.ShouldBe(1);

        backend.States[secondRenderer] = (ERenderDrawReadiness.Ready, "Ready");
        scheduler.RunFrame();
        prewarmer.GetState(second, secondRenderer, null, out _).ShouldBe(ECutsceneProgramState.Ready);
    }

    [Test]
    public void TheView_IsPassedToTheBackend()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);
        prewarmer.Request(material, renderer, null);
        prewarmer.View.ShouldBeNull();

        scheduler.RunFrame();

        backend.ProbeCount.ShouldBe(1);
        backend.LastView.ShouldBeNull();
    }

    [Test]
    public void Reset_DropsTheDraws_AndNewDrawsAreChecked()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer oldRenderer = MakeRenderer(material);
        XRMeshRenderer newRenderer = MakeRenderer(material);
        backend.States[oldRenderer] = (ERenderDrawReadiness.Ready, "Ready");
        prewarmer.Request(material, oldRenderer, null);
        scheduler.RunFrame();

        prewarmer.Reset();
        int probes = backend.ProbeCount;
        scheduler.RunFrame();
        backend.ProbeCount.ShouldBe(probes);

        backend.States[newRenderer] = (ERenderDrawReadiness.Compiling, "compiling");
        prewarmer.Request(material, newRenderer, null);
        scheduler.RunFrame();
        prewarmer.GetState(material, newRenderer, null, out _).ShouldBe(ECutsceneProgramState.Compiling);

        // The old renderer is not tracked any more: a query starts a new, not-checked entry.
        prewarmer.GetState(material, oldRenderer, null, out _).ShouldBe(ECutsceneProgramState.NotStarted);
    }

    [Test]
    public void Stop_EndsTheCoroutine_AndTheDrawKeepsItsLastState()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);
        backend.States[renderer] = (ERenderDrawReadiness.Compiling, "compiling");
        prewarmer.Request(material, renderer, null);
        scheduler.RunFrame();

        prewarmer.Stop();
        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        scheduler.RunFrame();

        scheduler.ActiveCount.ShouldBe(0);
        prewarmer.GetState(material, renderer, null, out _).ShouldBe(ECutsceneProgramState.Compiling);
    }

    [Test]
    public void Stop_EndsTheBackendRequestsOfEveryDraw()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial first = new() { Name = "First" };
        XRMaterial second = new() { Name = "Second" };
        XRMeshRenderer firstRenderer = MakeRenderer(first);
        XRMeshRenderer secondRenderer = MakeRenderer(second);
        prewarmer.Request(first, firstRenderer, null);
        prewarmer.Request(second, secondRenderer, null);
        scheduler.RunFrame();
        backend.Ended.ShouldBeEmpty();

        prewarmer.Stop();
        scheduler.RunFrame();

        backend.Ended.ShouldBe([firstRenderer, secondRenderer], ignoreOrder: true);
        scheduler.ActiveCount.ShouldBe(0);
    }

    [Test]
    public void Reset_EndsTheBackendRequestsOfTheDroppedDraws()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer oldRenderer = MakeRenderer(material);
        prewarmer.Request(material, oldRenderer, null);
        scheduler.RunFrame();

        prewarmer.Reset();
        scheduler.RunFrame();

        backend.Ended.ShouldBe([oldRenderer]);
        scheduler.ActiveCount.ShouldBe(1);
    }

    [Test]
    public void Reset_KeepsTheRequestsOfARendererThatTheNewDrawsUseAgain()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);
        prewarmer.Request(material, renderer, null);
        scheduler.RunFrame();

        prewarmer.Reset();
        prewarmer.Request(material, renderer, null);
        scheduler.RunFrame();

        backend.Ended.ShouldBeEmpty();
    }

    [Test]
    public void TheRenderOptionsOverride_IsPassedToTheBackend()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);
        XREngine.Rendering.Models.Materials.RenderingParameters options = new();
        prewarmer.Request(material, renderer, options);

        scheduler.RunFrame();

        backend.LastRenderOptionsOverride.ShouldBeSameAs(options);
    }

    [Test]
    public void NoRenderer_IsNotStarted_AndRunsNoBackendCheck()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = Make(backend, scheduler);
        XRMaterial material = new() { Name = "Proxy" };

        prewarmer.Request(material, null, null);

        prewarmer.GetState(material, null, null, out string? reason).ShouldBe(ECutsceneProgramState.NotStarted);
        reason.ShouldNotBeNullOrEmpty();
        scheduler.ScheduleCount.ShouldBe(0);
        backend.ProbeCount.ShouldBe(0);
    }

    [TestCase(ERenderDrawReadiness.NotStarted, ECutsceneProgramState.NotStarted)]
    [TestCase(ERenderDrawReadiness.Compiling, ECutsceneProgramState.Compiling)]
    [TestCase(ERenderDrawReadiness.Ready, ECutsceneProgramState.Ready)]
    [TestCase(ERenderDrawReadiness.Failed, ECutsceneProgramState.Failed)]
    [TestCase(ERenderDrawReadiness.NotSupported, ECutsceneProgramState.NotSupported)]
    public void BackendStates_MapOneToOne(ERenderDrawReadiness backendState, ECutsceneProgramState expected)
        => CutsceneBackendProgramPrewarmer.ToProgramState(backendState).ShouldBe(expected);
}

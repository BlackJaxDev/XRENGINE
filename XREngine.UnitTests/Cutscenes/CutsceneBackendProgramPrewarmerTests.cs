using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;
using XREngine.Rendering;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneBackendProgramPrewarmerTests
{
    /// <summary>A backend probe whose state for each renderer the test sets. A renderer with no state is not started.</summary>
    private sealed class FakeBackend
    {
        public Dictionary<XRMeshRenderer, (ERenderDrawReadiness State, string Reason)> States { get; } = new(ReferenceEqualityComparer.Instance);

        public int ProbeCount { get; private set; }

        public ERenderDrawReadiness Probe(XRMeshRenderer renderer, XRMaterial material, out string reason)
        {
            ProbeCount++;
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

    [Test]
    public void State_FollowsTheBackend_AndReadyComesOnlyAfterTheBackendIsReady()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = new(backend.Probe, scheduler.Schedule);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);

        prewarmer.Request(material, renderer);
        scheduler.ScheduleCount.ShouldBe(1);

        // The update thread reads only stored results, so nothing is ready before the render thread checks the draw.
        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        prewarmer.GetState(material, renderer, out _).ShouldBe(ECutsceneProgramState.NotStarted);
        backend.ProbeCount.ShouldBe(0);

        backend.States[renderer] = (ERenderDrawReadiness.NotStarted, "no frame drew the renderer");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, out string? reason).ShouldBe(ECutsceneProgramState.NotStarted);
        reason.ShouldBe("no frame drew the renderer");

        backend.States[renderer] = (ERenderDrawReadiness.Compiling, "graphics pipeline compile is queued");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, out reason).ShouldBe(ECutsceneProgramState.Compiling);
        reason.ShouldBe("graphics pipeline compile is queued");
        scheduler.ActiveCount.ShouldBe(1);

        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        scheduler.RunFrame();
        prewarmer.GetState(material, renderer, out _).ShouldBe(ECutsceneProgramState.Ready);

        // Every draw has a final state, so the coroutine ended.
        scheduler.ActiveCount.ShouldBe(0);
    }

    [TestCase(ERenderDrawReadiness.Failed, ECutsceneProgramState.Failed)]
    [TestCase(ERenderDrawReadiness.NotSupported, ECutsceneProgramState.NotSupported)]
    public void FailedAndNotSupported_AreFinal_AndKeepTheReason(ERenderDrawReadiness backendState, ECutsceneProgramState expected)
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = new(backend.Probe, scheduler.Schedule);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);
        backend.States[renderer] = (backendState, "backend reason");

        prewarmer.Request(material, renderer);
        scheduler.RunFrame();

        prewarmer.GetState(material, renderer, out string? reason).ShouldBe(expected);
        reason.ShouldBe("backend reason");
        scheduler.ActiveCount.ShouldBe(0);

        // A final state is not checked again, and no later backend state hides it.
        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        int probes = backend.ProbeCount;
        scheduler.RunFrame();
        backend.ProbeCount.ShouldBe(probes);
        prewarmer.GetState(material, renderer, out _).ShouldBe(expected);
    }

    [Test]
    public void EveryDraw_MustBeReady_BeforeTheCoroutineEnds()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = new(backend.Probe, scheduler.Schedule);
        XRMaterial first = new() { Name = "First" };
        XRMaterial second = new() { Name = "Second" };
        XRMeshRenderer firstRenderer = MakeRenderer(first);
        XRMeshRenderer secondRenderer = MakeRenderer(second);
        prewarmer.Request(first, firstRenderer);
        prewarmer.Request(second, secondRenderer);
        scheduler.ScheduleCount.ShouldBe(1);

        backend.States[firstRenderer] = (ERenderDrawReadiness.Ready, "Ready");
        backend.States[secondRenderer] = (ERenderDrawReadiness.Compiling, "ProgramsPending");
        scheduler.RunFrame();
        prewarmer.GetState(first, firstRenderer, out _).ShouldBe(ECutsceneProgramState.Ready);
        prewarmer.GetState(second, secondRenderer, out _).ShouldBe(ECutsceneProgramState.Compiling);
        scheduler.ActiveCount.ShouldBe(1);

        backend.States[secondRenderer] = (ERenderDrawReadiness.Ready, "Ready");
        scheduler.RunFrame();
        prewarmer.GetState(second, secondRenderer, out _).ShouldBe(ECutsceneProgramState.Ready);
        scheduler.ActiveCount.ShouldBe(0);
    }

    [Test]
    public void ADrawAddedAfterTheCoroutineEnded_StartsItAgain()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = new(backend.Probe, scheduler.Schedule);
        XRMaterial first = new() { Name = "First" };
        XRMaterial second = new() { Name = "Second" };
        XRMeshRenderer firstRenderer = MakeRenderer(first);
        XRMeshRenderer secondRenderer = MakeRenderer(second);
        backend.States[firstRenderer] = (ERenderDrawReadiness.Ready, "Ready");
        backend.States[secondRenderer] = (ERenderDrawReadiness.Ready, "Ready");

        prewarmer.Request(first, firstRenderer);
        scheduler.RunFrame();
        scheduler.ActiveCount.ShouldBe(0);

        // A query of a draw that was not requested requests it.
        prewarmer.GetState(second, secondRenderer, out _).ShouldBe(ECutsceneProgramState.NotStarted);
        scheduler.ScheduleCount.ShouldBe(2);
        scheduler.RunFrame();
        prewarmer.GetState(second, secondRenderer, out _).ShouldBe(ECutsceneProgramState.Ready);
    }

    [Test]
    public void Stop_EndsTheCoroutine_AndTheDrawStaysNotReady()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = new(backend.Probe, scheduler.Schedule);
        XRMaterial material = new() { Name = "Proxy" };
        XRMeshRenderer renderer = MakeRenderer(material);
        backend.States[renderer] = (ERenderDrawReadiness.Compiling, "compiling");
        prewarmer.Request(material, renderer);
        scheduler.RunFrame();

        prewarmer.Stop();
        backend.States[renderer] = (ERenderDrawReadiness.Ready, "Ready");
        scheduler.RunFrame();

        scheduler.ActiveCount.ShouldBe(0);
        prewarmer.GetState(material, renderer, out _).ShouldBe(ECutsceneProgramState.Compiling);
    }

    [Test]
    public void NoRenderer_IsNotStarted_AndRunsNoBackendCheck()
    {
        FakeBackend backend = new();
        ManualScheduler scheduler = new();
        CutsceneBackendProgramPrewarmer prewarmer = new(backend.Probe, scheduler.Schedule);
        XRMaterial material = new() { Name = "Proxy" };

        prewarmer.Request(material, null);

        prewarmer.GetState(material, null, out string? reason).ShouldBe(ECutsceneProgramState.NotStarted);
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

using System;
using System.Threading;
using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private readonly OpenXrSmokeLifecycleFaultStage _smokeLifecycleFaultStage = ResolveSmokeLifecycleFaultStage();
    private int _smokeLifecycleFaultConsumed;
    private long _smokeLifecycleFaultTriggerFrame;
    private string? _smokeLifecycleFaultDiagnostic;
    private int _smokeLifecycleFaultObservedRetiredGenerations;
    private int _smokeLifecycleFaultObservedGenerationCapacity;
    private string? _smokeLifecycleFaultObservedAdmission;
    private int _smokeLifecycleFaultTerminalHoldReleased;
    private int _smokeLifecycleFaultPendingWorkObserved;
    private int _smokeLifecycleFaultPendingWorkCount;
    private string? _smokeLifecycleFaultPendingWorkReceiptSource;

    private static OpenXrSmokeLifecycleFaultStage ResolveSmokeLifecycleFaultStage()
    {
        string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrSmokeLifecycleFaultStage);
        if (string.IsNullOrWhiteSpace(value)) return OpenXrSmokeLifecycleFaultStage.None;
        if (!Enum.TryParse(value, ignoreCase: true, out OpenXrSmokeLifecycleFaultStage stage) ||
            !Enum.IsDefined(stage) || stage == OpenXrSmokeLifecycleFaultStage.None)
            throw new InvalidOperationException($"Unknown OpenXR smoke lifecycle fault stage '{value}'. Expected RetiredGenerationCapacity, PostDetachReplacementFailure, or LossPending.");
        return stage;
    }

    private bool IsSmokeLifecycleFaultArmed(OpenXrSmokeLifecycleFaultStage stage)
        => _smokeLifecycleFaultStage == stage && Volatile.Read(ref _smokeLifecycleFaultConsumed) == 0 &&
           SmokeCompletedFrameCount > 0;

    private bool TryConsumeSmokeLifecycleFault(OpenXrSmokeLifecycleFaultStage stage, string diagnostic)
    {
        if (!IsSmokeLifecycleFaultArmed(stage) || Interlocked.CompareExchange(ref _smokeLifecycleFaultConsumed, 1, 0) != 0)
            return false;
        _smokeLifecycleFaultDiagnostic = diagnostic;
        Volatile.Write(ref _smokeLifecycleFaultTriggerFrame, SmokeCompletedFrameCount);
        return true;
    }

    private void TryInjectSmokeLossPendingAfterLiveWork()
    {
        IXrGraphicsBinding? graphicsBinding = _graphicsBinding;
        int pendingWorkCount = graphicsBinding?.PendingOpenXrSubmissionCount ?? 0;
        if (graphicsBinding?.HasPendingOpenXrSubmissionOwnership != true || pendingWorkCount <= 0 ||
            !TryConsumeSmokeLifecycleFault(OpenXrSmokeLifecycleFaultStage.LossPending, "Simulated state-machine LOSS_PENDING after accepted live OpenXR work with pending Vulkan submission ownership."))
            return;
        Volatile.Write(ref _smokeLifecycleFaultPendingWorkObserved, 1);
        Volatile.Write(ref _smokeLifecycleFaultPendingWorkCount, pendingWorkCount);
        _smokeLifecycleFaultPendingWorkReceiptSource = graphicsBinding!.PendingOpenXrSubmissionReceiptSource;
        _sessionState = SessionState.LossPending;
        RecordSmokeSessionState(_sessionState);
        MarkRuntimeLoss(OpenXrRuntimeLossReason.SessionLossPending, "Smoke simulated LOSS_PENDING");
    }

    private OpenXrSmokeLifecycleFaultSnapshot CaptureSmokeLifecycleFaultSnapshot()
        => new()
        {
            Stage = _smokeLifecycleFaultStage,
            Armed = _smokeLifecycleFaultStage != OpenXrSmokeLifecycleFaultStage.None,
            Consumed = Volatile.Read(ref _smokeLifecycleFaultConsumed) != 0,
            TriggerFrame = Volatile.Read(ref _smokeLifecycleFaultTriggerFrame),
            LifecycleEpoch = Volatile.Read(ref _smokeSessionLifecycleEpoch),
            ObservedRetiredGenerationCount = Volatile.Read(ref _smokeLifecycleFaultObservedRetiredGenerations),
            ObservedGenerationCapacity = Volatile.Read(ref _smokeLifecycleFaultObservedGenerationCapacity),
            ObservedAdmission = _smokeLifecycleFaultObservedAdmission,
            Source = _smokeLifecycleFaultStage == OpenXrSmokeLifecycleFaultStage.None
                ? null
                : "SimulatedProductionStateMachine",
            PendingWorkObserved = Volatile.Read(ref _smokeLifecycleFaultPendingWorkObserved) != 0,
            PendingWorkCount = Volatile.Read(ref _smokeLifecycleFaultPendingWorkCount),
            PendingWorkReceiptSource = _smokeLifecycleFaultPendingWorkReceiptSource,
            TerminalHoldReleased = Volatile.Read(ref _smokeLifecycleFaultTerminalHoldReleased) != 0,
            Diagnostic = _smokeLifecycleFaultDiagnostic,
        };

    internal bool ShouldHoldSmokeRetiredGenerations()
        => IsSmokeLifecycleFaultArmed(OpenXrSmokeLifecycleFaultStage.RetiredGenerationCapacity) &&
           Volatile.Read(ref _smokeLifecycleFaultTerminalHoldReleased) == 0;

    internal bool ObserveSmokeRetiredGenerationCapacity(int count, int capacity, string admission)
    {
        RecordSmokeRetiredGenerationObservation(count, capacity, admission);
        if (count >= capacity)
            return TryConsumeSmokeLifecycleFault(OpenXrSmokeLifecycleFaultStage.RetiredGenerationCapacity,
                "Observed four real retired Vulkan OpenXR generations; admission deferred before active detachment.");
        return false;
    }

    internal void RecordSmokeRetiredGenerationObservation(int count, int capacity, string admission)
    {
        Volatile.Write(ref _smokeLifecycleFaultObservedRetiredGenerations, count);
        Volatile.Write(ref _smokeLifecycleFaultObservedGenerationCapacity, capacity);
        _smokeLifecycleFaultObservedAdmission = admission;
    }

    internal void ReleaseSmokeRetiredGenerationHoldForTerminalDrain()
    {
        if (_smokeLifecycleFaultStage == OpenXrSmokeLifecycleFaultStage.RetiredGenerationCapacity &&
            Volatile.Read(ref _smokeLifecycleFaultConsumed) == 0)
        {
            _smokeLifecycleFaultDiagnostic = "Terminal drain released the smoke retired-generation observation hold.";
            Interlocked.Exchange(ref _smokeLifecycleFaultTerminalHoldReleased, 1);
        }
    }
}

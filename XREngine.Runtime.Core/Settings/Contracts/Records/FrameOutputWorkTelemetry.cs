namespace XREngine;

public readonly record struct FrameOutputWorkTelemetry(
    int SceneSnapshots = 0,
    int VisibilityBuilds = 0,
    int CompiledPlanCacheHits = 0,
    int CompiledPlanCacheMisses = 0,
    int SharedPassReuses = 0,
    int RecordedWorkItems = 0,
    int ReusedWorkItems = 0,
    int DuplicatedWorkItems = 0,
    int CpuBudgetDeferrals = 0,
    int GpuBudgetDeferrals = 0,
    int StaleResultReuses = 0,
    int MissedDeadlines = 0,
    int UnapprovedPolicyEvents = 0,
    int SubmissionRejections = 0,
    int PlannerPrunes = 0,
    int GlobalInFlightWaits = 0,
    int ForceFlushes = 0);

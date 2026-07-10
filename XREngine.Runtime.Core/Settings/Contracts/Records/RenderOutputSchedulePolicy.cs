namespace XREngine;

public readonly record struct RenderOutputSchedulePolicy(
    ERenderOutputPriority Priority,
    float DesiredRateHz,
    double DeadlineMs,
    double MaxCpuBudgetMs,
    double MaxGpuBudgetMs,
    uint MaxContentAgeFrames,
    bool HardDeadline)
{
    public bool HasCpuBudget => MaxCpuBudgetMs > 0.0;
    public bool HasGpuBudget => MaxGpuBudgetMs > 0.0;
}

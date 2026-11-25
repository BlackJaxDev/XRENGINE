using NUnit.Framework;
using NUnit.Framework.Interfaces;
using XREngine;
using XREngine.Execution;

[assembly: PumpThreadAffineJobs]

/// <summary>
/// Runs the queued app-thread and collect-visible-swap jobs after each test. The test host has no
/// app or render loop, so these jobs otherwise stay queued. Each queued job keeps one slot of the
/// bounded job queue, and when all slots are taken, worker-thread jobs such as asset saves wait
/// forever. The test thread acts as the app thread here, as it does for the UI under test.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PumpThreadAffineJobsAttribute : Attribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test) { }

    public void AfterTest(ITest test)
    {
        JobManager jobs = RuntimeWorkScheduler.Jobs;
        if (jobs.IsCallerThreadExecutor)
            return;
        jobs.ProcessAppThreadJobs();
        jobs.ProcessCollectVisibleSwapJobs(int.MaxValue);
    }
}

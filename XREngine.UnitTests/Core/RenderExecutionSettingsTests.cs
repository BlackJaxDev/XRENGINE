using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class RenderExecutionSettingsTests
{
    [Test]
    public void Defaults_PreserveExistingWorkerBehaviorAndLeaveRenderMigrationDisabled()
    {
        var settings = new RenderExecutionSettings();

        settings.GeneralWorkerThreadCount.ShouldBe(EngineExecutionTopology.AutomaticWorkerCount);
        settings.GeneralWorkerThreadCap.ShouldBe(EngineExecutionTopology.DefaultGeneralWorkerCap);
        settings.RenderWorkerThreadCount.ShouldBe(0);
        settings.RenderWorkerThreadCap.ShouldBe(EngineExecutionTopology.DefaultRenderWorkerCap);
        settings.ReservedForegroundThreadCount.ShouldBe(EngineExecutionTopology.AutomaticWorkerCount);
        settings.AllowCpuOversubscription.ShouldBeFalse();
        settings.RenderWorkerQos.ShouldBe(ERenderWorkerQos.OsDefault);
    }

    [Test]
    public void EngineSettings_LegacyJobAliasesShareExecutionSubtree()
    {
        var settings = new RuntimeEngine.Rendering.EngineSettings();

        settings.JobWorkers.ShouldBeNull();
        settings.JobWorkerCap.ShouldBeNull();

        settings.JobWorkers = 7;
        settings.JobWorkerCap = 12;

        settings.GeneralWorkerThreadCount.ShouldBe(7);
        settings.GeneralWorkerThreadCap.ShouldBe(12);

        settings.JobWorkers = null;
        settings.JobWorkerCap = null;

        settings.GeneralWorkerThreadCount.ShouldBe(EngineExecutionTopology.AutomaticWorkerCount);
        settings.GeneralWorkerThreadCap.ShouldBe(EngineExecutionTopology.DefaultGeneralWorkerCap);
    }

    [TestCase(XREngineEnvironmentVariables.RenderWorkerThreads, RuntimeEnvironmentValueKind.Integer)]
    [TestCase(XREngineEnvironmentVariables.ReservedForegroundThreads, RuntimeEnvironmentValueKind.Integer)]
    [TestCase(XREngineEnvironmentVariables.RenderWorkerQos, RuntimeEnvironmentValueKind.Enum)]
    public void ExecutionEnvironmentCatalog_UsesTypedMetadata(
        string variableName,
        RuntimeEnvironmentValueKind expectedKind)
    {
        RuntimeEnvironmentVariableDescriptor? descriptor =
            XREngineEnvironmentVariableCatalog.Find(variableName);

        descriptor.ShouldNotBeNull();
        descriptor.ValueKind.ShouldBe(expectedKind);
        descriptor.ApplyMode.ShouldBe(RuntimeEnvironmentApplyMode.ProcessRestart);
    }
}

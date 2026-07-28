using NUnit.Framework;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.UnitTests.Rendering;

[assembly: LevelOfParallelism(1)]

/// <summary>
/// Installs the application-level rendering services required by runtime-facing tests.
/// </summary>
[SetUpFixture]
public sealed class TestCompositionRoot
{
    private IRuntimeRenderingHostServices? _previousRenderingHostServices;
    private IRuntimeShaderServices? _previousShaderServices;

    [OneTimeSetUp]
    public void InstallRuntimeServices()
    {
        _previousRenderingHostServices = RuntimeRenderingHostServices.Current;
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [OneTimeTearDown]
    public void RestoreRuntimeServices()
    {
        RuntimeShaderServices.Current = _previousShaderServices;
        RuntimeRenderingHostServices.Current = _previousRenderingHostServices!;
    }
}

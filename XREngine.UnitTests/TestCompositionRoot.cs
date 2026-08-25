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
    private IDisposable? _assetServices;
    private IDisposable? _testRenderingHost;

    [OneTimeSetUp]
    public void InstallRuntimeServices()
    {
        _previousRenderingHostServices = RuntimeRenderingHostServices.Current;
        _previousShaderServices = RuntimeShaderServices.Current;
        _assetServices = RuntimeAssetBootstrap.InstallEngineAssetServices();
        IRuntimeRenderingHostServices renderingHost =
            RuntimeRenderingBootstrap.CreateEngineHostServices();
        _testRenderingHost = renderingHost as IDisposable;
        RuntimeRenderingHostServices.Current = renderingHost;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [OneTimeTearDown]
    public void RestoreRuntimeServices()
    {
        RuntimeShaderServices.Current = _previousShaderServices;
        RuntimeRenderingHostServices.Current = _previousRenderingHostServices!;
        _testRenderingHost?.Dispose();
        _testRenderingHost = null;
        _assetServices?.Dispose();
        _assetServices = null;
    }
}

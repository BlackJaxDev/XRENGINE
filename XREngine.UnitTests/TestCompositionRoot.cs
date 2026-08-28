using NUnit.Framework;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.Models.Caching;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene.Prefabs;
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
    private IDisposable? _modelAssetPipelineRegistration;
    private IDisposable? _testRenderingHost;

    [OneTimeSetUp]
    public void InstallRuntimeServices()
    {
        _previousRenderingHostServices = RuntimeRenderingHostServices.Current;
        _previousShaderServices = RuntimeShaderServices.Current;
        _assetServices = RuntimeAssetBootstrap.InstallEngineAssetServices();
        _modelAssetPipelineRegistration = ModelAssetPipelineRegistration.Install(Engine.Assets, typeof(XRPrefabSource));
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
        _modelAssetPipelineRegistration?.Dispose();
        _modelAssetPipelineRegistration = null;
        _assetServices?.Dispose();
        _assetServices = null;
    }
}

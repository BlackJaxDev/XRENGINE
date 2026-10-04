using System.Numerics;
using XREngine.Scene;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private XRTexture2DArray? _disabledUberEnvironment;
    private XRDataBuffer? _disabledUberProbeData;
    private ObjectCacheOwnership? _uberEnvironmentDefaultOwnership;

    /// <summary>Uses the shared instance-owned probe publication and its original grid buffers.</summary>
    private void PublishUberBaseEnvironment(WebGpuRenderProgram program, uint flags)
    {
        EnsureUberEnvironmentDefaults();
        for (uint binding = 20; binding <= 24; binding++)
            _disabledUberProbeData!.BindTo(program.Data, binding);
        program.Data.Uniform("ProbeCount", 0);
        program.Data.Uniform("TetraCount", 0);
        program.Data.Uniform("UseProbeGrid", false);
        program.Data.Uniform("ForwardPbrResourcesEnabled", false);
        program.Data.Uniform("SpecularOcclusionEnabled", false);
        program.Data.Uniform("ProbeGridOrigin", Vector3.Zero);
        program.Data.Uniform("ProbeGridCellSize", 1.0f);

        IPbrLightingResourceProvider? provider = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline
            as IPbrLightingResourceProvider;
        bool enabled = (flags & 9u) == 9u && provider is not null &&
            provider.BindPbrLightingResources(program.Data, deferredProbeBufferBindings: true);
        if (enabled)
        {
            program.RequireUberBaseEnvironmentSampling();
            Lights3DCollection.SetForwardAmbientOcclusionUniforms(program.Data);
            return;
        }
        program.Data.Sampler("BRDF", EnsureDisabledAmbientOcclusion(), 0);
        program.Data.Sampler("IrradianceArray", _disabledUberEnvironment!, 0);
        program.Data.Sampler("PrefilterArray", _disabledUberEnvironment!, 0);
    }

    private void EnsureUberEnvironmentDefaults()
    {
        if (_disabledUberEnvironment is not null) return;
        using ObjectCachePublicationScope ownership = XRObjectBase.BeginIndependentObjectCachePublication();
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        XRTexture2DArray image = new(1, 1, 1, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
        {
            Name = "Disabled Uber probe environment", AutoGenerateMipmaps = false, Resizable = false,
            MinFilter = ETexMinFilter.Nearest, MagFilter = ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge, VWrap = ETexWrapMode.ClampToEdge,
            MinLOD = 0, MaxLOD = 0,
        };
        XRDataBuffer buffer = new("Disabled Uber probe data", EBufferTarget.ShaderStorageBuffer,
            4, EComponentType.UInt, 1, false, false);
        ReadOnlySpan<uint> zeros = stackalloc uint[4];
        buffer.SetDataRaw(zeros);
        SetField(ref _uberEnvironmentDefaultOwnership, ownership.CompleteWithOwnership());
        SetField(ref _disabledUberEnvironment, image);
        SetField(ref _disabledUberProbeData, buffer);
    }

    private void DestroyUberEnvironmentDefaults()
    {
        _disabledUberProbeData?.Dispose();
        _uberEnvironmentDefaultOwnership?.Dispose();
        SetField(ref _disabledUberProbeData, null);
        SetField(ref _disabledUberEnvironment, null);
        SetField(ref _uberEnvironmentDefaultOwnership, null);
    }
}

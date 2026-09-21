using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Scene;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>Owns the bounded direct-light UBO used by DDGI hit shading.</summary>
internal sealed class DDGILightResources
{
    public const uint UniformBinding = 8;
    public const int Capacity = 255;
    private const uint RecordCount = Capacity + 1u;
    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, DDGILightResources> Resources = new();

    private XRDataBuffer<DDGILightGPU>? _buffer;

    private DDGILightResources(XRRenderPipelineInstance pipeline)
        => pipeline.CacheClearing += Clear;

    /// <summary>Uploads every active dynamic scene light and binds the UBO at binding eight.</summary>
    public static bool Bind(XRRenderProgram program, XRRenderPipelineInstance pipeline, IRuntimeRenderWorld world)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(world);
        return Resources.GetValue(pipeline, static instance => new DDGILightResources(instance)).UploadAndBind(program, pipeline, world);
    }

    private bool UploadAndBind(XRRenderProgram program, XRRenderPipelineInstance pipeline, IRuntimeRenderWorld world)
    {
        EnsureBuffer();
        if (_buffer is null)
            return false;

        Lights3DCollection? lights = world.Lights;
        int directionalCount = CountActive(lights?.DynamicDirectionalLights);
        int pointCount = CountActive(lights?.DynamicPointLights);
        int spotCount = CountActive(lights?.DynamicSpotLights);
        int total = directionalCount + pointCount + spotCount;
        if (total > Capacity)
        {
            Debug.RenderingWarningEvery(
                "DDGI.LightCapacity",
                TimeSpan.FromSeconds(2),
                "DDGI direct lighting requires {0} lights but its guaranteed 16 KiB UBO supports {1}; hit shading is paused rather than dropping lights.",
                total,
                Capacity);
            return false;
        }

        _buffer.Set(0u, new DDGILightGPU
        {
            PositionOrDirectionAndType = new Vector4(directionalCount, pointCount, spotCount, 0.0f),
        });
        uint index = 1u;
        if (lights is not null)
        {
            index = UploadDirectional(lights.DynamicDirectionalLights, index);
            index = UploadPoints(lights.DynamicPointLights, index);
            _ = UploadSpots(lights.DynamicSpotLights, index);
        }
        _buffer.PushSubData(0, checked((uint)(total + 1) * (uint)Unsafe.SizeOf<DDGILightGPU>()));
        DDGIResourceImports.BindDirectLights(pipeline, _buffer);
        program.BindBuffer(_buffer, UniformBinding);
        return true;
    }

    private void EnsureBuffer()
    {
        if (_buffer is not null && !_buffer.IsDestroyed)
            return;
        _buffer = new XRDataBuffer<DDGILightGPU>("DDGILightBlock", EBufferTarget.UniformBuffer, RecordCount)
        {
            Usage = EBufferUsage.StreamDraw,
        };
        _buffer.SetBlockIndex(UniformBinding);
        _buffer.PushData();
    }

    private uint UploadDirectional(EventList<DirectionalLightComponent> lights, uint index)
    {
        for (int i = 0; i < lights.Count; i++)
        {
            DirectionalLightComponent light = lights[i];
            if (!light.IsActiveInHierarchy)
                continue;
            _buffer!.Set(index++, CreateDirectional(light));
        }
        return index;
    }

    private uint UploadPoints(EventList<PointLightComponent> lights, uint index)
    {
        for (int i = 0; i < lights.Count; i++)
        {
            PointLightComponent light = lights[i];
            if (!light.IsActiveInHierarchy)
                continue;
            _buffer!.Set(index++, CreatePoint(light));
        }
        return index;
    }

    private uint UploadSpots(EventList<SpotLightComponent> lights, uint index)
    {
        for (int i = 0; i < lights.Count; i++)
        {
            SpotLightComponent light = lights[i];
            if (!light.IsActiveInHierarchy)
                continue;
            _buffer!.Set(index++, CreateSpot(light));
        }
        return index;
    }

    private static int CountActive<TLight>(EventList<TLight>? lights) where TLight : LightComponent
    {
        if (lights is null)
            return 0;
        int count = 0;
        for (int i = 0; i < lights.Count; i++)
            if (lights[i].IsActiveInHierarchy)
                count++;
        return count;
    }

    private static DDGILightGPU CreateDirectional(DirectionalLightComponent light)
        => new()
        {
            PositionOrDirectionAndType = new Vector4(light.Transform.WorldForward, 0.0f),
            ColorAndIntensity = new Vector4(light.Color.R, light.Color.G, light.Color.B, light.DiffuseIntensity),
            RadiusOuterExponentAndFlags = new Vector4(0.0f, 0.0f, 0.0f, light.CastsShadows ? 1.0f : 0.0f),
        };

    private static DDGILightGPU CreatePoint(PointLightComponent light)
        => new()
        {
            PositionOrDirectionAndType = new Vector4(light.Transform.RenderTranslation, 1.0f),
            ColorAndIntensity = new Vector4(light.Color.R, light.Color.G, light.Color.B, light.DiffuseIntensity * light.Brightness),
            RadiusOuterExponentAndFlags = new Vector4(light.Radius, 0.0f, 0.0f, light.CastsShadows ? 1.0f : 0.0f),
        };

    private static DDGILightGPU CreateSpot(SpotLightComponent light)
        => new()
        {
            PositionOrDirectionAndType = new Vector4(light.Transform.RenderTranslation, 2.0f),
            ColorAndIntensity = new Vector4(light.Color.R, light.Color.G, light.Color.B, light.DiffuseIntensity * light.Brightness),
            DirectionAndInnerCutoff = new Vector4(light.Transform.RenderForward, light.InnerCutoff),
            RadiusOuterExponentAndFlags = new Vector4(light.Distance, light.OuterCutoff, light.Exponent, light.CastsShadows ? 1.0f : 0.0f),
        };

    private void Clear()
    {
        _buffer?.Dispose();
        _buffer = null;
    }
}

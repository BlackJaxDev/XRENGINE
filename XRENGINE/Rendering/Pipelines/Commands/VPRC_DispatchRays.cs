using XREngine.Rendering.GI;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Dispatches the engine's existing native ray-tracing bridge using authored shader-binding-table parameters.
/// </summary>
public class VPRC_DispatchRays : ViewportRenderCommand
{
    public uint RayTracingPipelineId { get; set; }
    public bool UseSingleShaderBindingTable { get; set; } = true;

    public uint ShaderBindingTableBufferId { get; set; }
    public uint ShaderBindingTableOffset { get; set; }
    public uint ShaderBindingTableStride { get; set; }

    public uint RaygenBufferId { get; set; }
    public uint RaygenOffset { get; set; }
    public uint RaygenStride { get; set; }
    public uint MissBufferId { get; set; }
    public uint MissOffset { get; set; }
    public uint MissStride { get; set; }
    public uint HitGroupBufferId { get; set; }
    public uint HitGroupOffset { get; set; }
    public uint HitGroupStride { get; set; }
    public uint CallableBufferId { get; set; }
    public uint CallableOffset { get; set; }
    public uint CallableStride { get; set; }

    public uint Width { get; set; } = 1u;
    public uint Height { get; set; } = 1u;
    public uint Depth { get; set; } = 1u;

    public string SuccessVariableName { get; set; } = "DispatchRaysSucceeded";
    public string FailureReasonVariableName { get; set; } = "DispatchRaysFailure";
    public string WidthVariableName { get; set; } = "DispatchRaysWidth";
    public string HeightVariableName { get; set; } = "DispatchRaysHeight";
    public string DepthVariableName { get; set; } = "DispatchRaysDepth";

    protected override void Execute()
    {
        bool success = TryDispatch(out string failure, out uint width, out uint height, out uint depth);

        var variables = ActivePipelineInstance.Variables;
        variables.Set(SuccessVariableName, success);
        variables.Set(FailureReasonVariableName, failure);
        variables.Set(WidthVariableName, width);
        variables.Set(HeightVariableName, height);
        variables.Set(DepthVariableName, depth);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        context.GetOrCreateSyntheticPass(GetType().Name, ERenderGraphPassStage.Compute);
    }

    protected virtual bool TryResolveDispatchDimensions(out uint width, out uint height, out uint depth, out string failure)
    {
        width = Width;
        height = Height;
        depth = Depth;

        if (width == 0 || height == 0 || depth == 0)
        {
            failure = "Ray dispatch dimensions must be greater than zero.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    protected virtual bool TryDispatch(out string failure, out uint width, out uint height, out uint depth)
    {
        if (!TryResolveDispatchDimensions(out width, out height, out depth, out failure))
            return false;

        if (!Engine.Rendering.State.IsVulkan)
        {
            failure = "Ray dispatch currently requires the Vulkan renderer.";
            return false;
        }

        if (RayTracingPipelineId == 0)
        {
            failure = "RayTracingPipelineId must be non-zero.";
            return false;
        }

        if (!TryCreateParameters(width, height, depth, out RestirGI.TraceParameters parameters, out failure))
            return false;

        if (!RestirGI.TryInit())
        {
            failure = "Failed to initialize the native ray-tracing bridge.";
            return false;
        }

        if (!RestirGI.TryBind(RayTracingPipelineId))
        {
            failure = $"Failed to bind ray-tracing pipeline {RayTracingPipelineId}.";
            return false;
        }

        if (!RestirGI.TryDispatch(parameters))
        {
            failure = "Native ray dispatch failed.";
            return false;
        }

        AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.TextureFetch);
        failure = string.Empty;
        return true;
    }

    private bool TryCreateParameters(uint width, uint height, uint depth, out RestirGI.TraceParameters parameters, out string failure)
    {
        if (UseSingleShaderBindingTable)
        {
            if (ShaderBindingTableBufferId == 0 || ShaderBindingTableStride == 0)
            {
                parameters = default;
                failure = "A single-table dispatch requires a non-zero shader binding table buffer id and stride.";
                return false;
            }

            parameters = RestirGI.TraceParameters.CreateSingleTable(
                ShaderBindingTableBufferId,
                ShaderBindingTableOffset,
                ShaderBindingTableStride,
                width,
                height,
                depth);
            failure = string.Empty;
            return true;
        }

        if (RaygenBufferId == 0 || MissBufferId == 0 || HitGroupBufferId == 0)
        {
            parameters = default;
            failure = "Explicit dispatch requires non-zero raygen, miss, and hit-group buffer ids.";
            return false;
        }

        parameters = new RestirGI.TraceParameters
        {
            RaygenBuffer = RaygenBufferId,
            RaygenOffset = RaygenOffset,
            RaygenStride = RaygenStride,
            MissBuffer = MissBufferId,
            MissOffset = MissOffset,
            MissStride = MissStride,
            HitGroupBuffer = HitGroupBufferId,
            HitGroupOffset = HitGroupOffset,
            HitGroupStride = HitGroupStride,
            CallableBuffer = CallableBufferId,
            CallableOffset = CallableOffset,
            CallableStride = CallableStride,
            Width = width,
            Height = height,
            Depth = depth,
        };

        failure = string.Empty;
        return true;
    }
}
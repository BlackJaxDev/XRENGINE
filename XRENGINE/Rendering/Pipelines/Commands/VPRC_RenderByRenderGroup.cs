using System;
using System.Globalization;
using XREngine.Rendering.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

public enum ERenderGroupSource
{
    MaterialName,
    MeshName,
    MaterialRenderPass,
    CommandType,
}

/// <summary>
/// Renders commands from a pass whose authored metadata resolves to a matching group key.
/// This exposes a serializable grouping primitive using existing engine data such as material
/// names, mesh names, material render passes, or command type names.
/// </summary>
[RenderPipelineScriptCommand]
public class VPRC_RenderByRenderGroup : ViewportRenderCommand
{
    public int RenderPass { get; set; }
    public ERenderGroupSource GroupSource { get; set; } = ERenderGroupSource.MaterialName;
    public string? GroupName { get; set; }
    public bool ExactMatch { get; set; } = true;
    public bool IgnoreCase { get; set; } = true;
    public bool InvertMatch { get; set; }

    public override bool NeedsCollecVisible => true;

    protected override void Execute()
    {
        if (string.IsNullOrWhiteSpace(GroupName))
            return;

        using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(RenderPass);
        ActivePipelineInstance.MeshRenderCommands.RenderCPUFiltered(RenderPass, MatchesGroup);
    }

    private bool MatchesGroup(RenderCommand command)
    {
        if (!TryResolveGroupValue(command, out string? value) || string.IsNullOrWhiteSpace(value) || GroupName is null)
            return false;

        StringComparison comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool isMatch = ExactMatch
            ? string.Equals(value, GroupName, comparison)
            : value.Contains(GroupName, comparison);
        return InvertMatch ? !isMatch : isMatch;
    }

    private bool TryResolveGroupValue(RenderCommand command, out string? value)
    {
        value = null;
        switch (GroupSource)
        {
            case ERenderGroupSource.MaterialName:
            {
                if (command is not IRenderCommandMesh meshCommand)
                    return false;

                XRMaterial? material = meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material;
                value = material?.Name;
                return !string.IsNullOrWhiteSpace(value);
            }
            case ERenderGroupSource.MeshName:
            {
                if (command is not IRenderCommandMesh meshCommand)
                    return false;

                value = meshCommand.Mesh?.Name ?? meshCommand.Mesh?.Mesh?.Name;
                return !string.IsNullOrWhiteSpace(value);
            }
            case ERenderGroupSource.MaterialRenderPass:
            {
                if (command is not IRenderCommandMesh meshCommand)
                    return false;

                XRMaterial? material = meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material;
                if (material is null)
                    return false;

                value = material.RenderPass.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            case ERenderGroupSource.CommandType:
                value = command.GetType().Name;
                return true;
            default:
                return false;
        }
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        string passName = $"RenderByRenderGroup_{RenderPass}_{GroupSource}";
        var builder = context.Metadata.ForPass(RenderPass, passName, ERenderGraphPassStage.Graphics);
        builder
            .UseEngineDescriptors()
            .UseMaterialDescriptors();

        if (context.CurrentRenderTarget is { } target)
        {
            builder.WithName($"{passName}_{target.Name}");

            builder.UseColorAttachment(
                MakeFboColorResource(target.Name),
                target.ColorAccess,
                target.ConsumeColorLoadOp(),
                target.GetColorStoreOp());

            builder.UseDepthAttachment(
                MakeFboDepthResource(target.Name),
                target.DepthAccess,
                target.ConsumeDepthLoadOp(),
                target.GetDepthStoreOp());
        }
    }
}

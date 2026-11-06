using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static void DrawWorkbenchPreview()
    {
        XRRenderPipelineInstance? instance = _workbench.Model.Instance;
        bool flip = _workbench.FlipPreview;
        if (ImGui.Checkbox("Flip", ref flip))
            _workbench.FlipPreview = flip;
        ImGui.SameLine();

        if (instance is null)
        {
            ImGui.TextDisabled("Preview needs a live pipeline instance.");
            return;
        }

        if (!Engine.IsRenderThread)
        {
            ImGui.TextDisabled("Preview is available only on the render thread.");
            return;
        }

        string? resource = ResolveWorkbenchPreviewResource();
        if (resource is null)
        {
            ImGui.TextDisabled("Select a resource or a pass to preview its texture or framebuffer.");
            return;
        }

        ImGui.TextUnformatted(resource);
        if (!RenderPipelineWorkbenchResourceNames.TryParse(resource, out RenderPipelineWorkbenchResourceName name))
        {
            ImGui.TextDisabled("The resource is not a texture or a framebuffer attachment.");
            return;
        }

        DrawWorkbenchResourcePreview(instance, name, flip);
    }

    /// <summary>Returns the resource that the preview shows: the selected resource, or the first written attachment of the selected pass.</summary>
    private static string? ResolveWorkbenchPreviewResource()
    {
        if (_workbench.Selection.ResourceName is { } resource)
            return resource;

        RenderPipelineWorkbenchModel model = _workbench.Model;
        int passIndex = _workbench.Selection.PassIndex;
        if (passIndex < 0 && model.TryGetStructureCommand(_workbench.Selection.NodeId, out var entry) && entry.PassIndices.Count > 0)
            passIndex = entry.PassIndices[0];

        if (passIndex < 0 || !model.PassGraph.NodeIndexByPass.TryGetValue(passIndex, out int nodeIndex))
            return null;

        RenderPassMetadata pass = model.PassGraph.Nodes[nodeIndex].Metadata;
        string? fallback = null;
        foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
        {
            if (usage.Access == ERenderGraphAccess.Read)
                continue;
            if (usage.ResourceType == ERenderPassResourceType.ColorAttachment)
                return usage.ResourceName;
            fallback ??= usage.ResourceName;
        }

        return fallback;
    }

    private static void DrawWorkbenchResourcePreview(XRRenderPipelineInstance instance, RenderPipelineWorkbenchResourceName name, bool flip)
    {
        switch (name.Kind)
        {
            case ERenderPipelineWorkbenchResourceKind.Texture:
                if (instance.Resources.TryGetTexture(name.BaseName, out XRTexture? texture))
                    RenderTexturePreviewPanel.DrawTexturePreview(texture, flip);
                else
                    ImGui.TextDisabled("The instance has no texture with this name.");
                break;
            case ERenderPipelineWorkbenchResourceKind.FrameBufferAttachment:
                if (instance.Resources.TryGetFrameBuffer(name.BaseName, out XRFrameBuffer? frameBuffer))
                    RenderTexturePreviewPanel.DrawFrameBufferOutputs(frameBuffer, flip);
                else
                    ImGui.TextDisabled("The instance has no framebuffer with this name.");
                break;
            default:
                ImGui.TextDisabled("This resource has no preview.");
                break;
        }
    }
}

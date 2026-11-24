using System.Collections.Concurrent;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Parses logical render graph resource names (see <see cref="RenderGraphResourceNames"/>) into a kind,
/// a base name, and an attachment. Results are cached, so repeated calls do not allocate.
/// </summary>
public static class RenderPipelineWorkbenchResourceNames
{
    private static readonly ConcurrentDictionary<string, RenderPipelineWorkbenchResourceName> Cache = new(StringComparer.Ordinal);
    private static readonly Func<string, RenderPipelineWorkbenchResourceName> ParseName = Parse;

    /// <summary>Parses <paramref name="logicalName"/>. Returns false for a name with no known prefix.</summary>
    public static bool TryParse(string logicalName, out RenderPipelineWorkbenchResourceName name)
    {
        name = Cache.GetOrAdd(logicalName, ParseName);
        return name.Kind != ERenderPipelineWorkbenchResourceKind.Other;
    }

    private static RenderPipelineWorkbenchResourceName Parse(string logicalName)
    {
        if (string.Equals(logicalName, RenderGraphResourceNames.OutputRenderTarget, StringComparison.Ordinal))
            return new RenderPipelineWorkbenchResourceName(ERenderPipelineWorkbenchResourceKind.Output, logicalName, string.Empty);

        if (logicalName.StartsWith("tex::", StringComparison.Ordinal))
            return new RenderPipelineWorkbenchResourceName(ERenderPipelineWorkbenchResourceKind.Texture, logicalName[5..], string.Empty);

        if (logicalName.StartsWith("fbo::", StringComparison.Ordinal))
        {
            int split = logicalName.LastIndexOf("::", StringComparison.Ordinal);
            if (split > 5)
                return new RenderPipelineWorkbenchResourceName(
                    ERenderPipelineWorkbenchResourceKind.FrameBufferAttachment,
                    logicalName[5..split],
                    logicalName[(split + 2)..]);
        }

        return new RenderPipelineWorkbenchResourceName(ERenderPipelineWorkbenchResourceKind.Other, logicalName, string.Empty);
    }
}

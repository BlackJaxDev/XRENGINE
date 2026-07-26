using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// A deterministic set of pass definitions sharing a single material's
/// authored parameters, textures, and uber feature state.
/// </summary>
public sealed record MaterialPassSet
{
    public static MaterialPassSet Empty { get; } = new();

    public MaterialPassDefinition[] Passes { get; init; } = [];
    public string[] DisabledSourcePasses { get; init; } = [];
    public int SourceRenderQueue { get; init; } = -1;
    public int QueuePriority { get; init; }
    public RenderingParameters? ForwardAddRenderOptions { get; init; }
    public EMaterialForwardAddPolicy ForwardAddPolicy { get; init; } =
        EMaterialForwardAddPolicy.FoldedIntoForwardPlusBase;

    public bool TryGetPass(EMaterialPassIdentity identity, out MaterialPassDefinition pass)
    {
        foreach (MaterialPassDefinition candidate in Passes)
        {
            if (candidate.Identity != identity)
                continue;

            pass = candidate;
            return true;
        }

        pass = null!;
        return false;
    }

    /// <summary>
    /// Copies enabled pass references into caller-owned storage. Render
    /// submission can therefore consume a pass set without allocating.
    /// </summary>
    public int CopyEnabledPasses(Span<MaterialPassDefinition> destination)
    {
        int count = 0;
        foreach (MaterialPassDefinition pass in Passes)
        {
            if (!pass.Enabled)
                continue;
            if ((uint)count >= (uint)destination.Length)
                throw new ArgumentException("Destination is too small for the enabled material passes.", nameof(destination));

            destination[count++] = pass;
        }

        return count;
    }
}

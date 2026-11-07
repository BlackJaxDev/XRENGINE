using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Maps the scene pass sorter names of the <c>passes</c> block to sorter types. The list is explicit, so a script
/// never creates a type by name.
/// </summary>
public static class RenderPipelineScriptSorters
{
    /// <summary>The name for a pass with no sorter.</summary>
    public const string None = "none";

    /// <summary>The name of <see cref="NearToFarRenderCommandSorter"/>.</summary>
    public const string NearToFar = "nearToFar";

    /// <summary>The name of <see cref="FarToNearRenderCommandSorter"/>.</summary>
    public const string FarToNear = "farToNear";

    /// <summary>The name of <see cref="OpaqueStateBucketRenderCommandSorter"/>.</summary>
    public const string OpaqueStateBucket = "opaqueStateBucket";

    /// <summary>Gets the sorter names, in picker order.</summary>
    public static IReadOnlyList<string> Names { get; } = [None, NearToFar, FarToNear, OpaqueStateBucket];

    /// <summary>Returns the name of <paramref name="sorter"/>, or null when the sorter type has no name.</summary>
    public static string? GetName(IComparer<RenderCommand>? sorter)
        => sorter switch
        {
            null => None,
            NearToFarRenderCommandSorter => NearToFar,
            FarToNearRenderCommandSorter => FarToNear,
            OpaqueStateBucketRenderCommandSorter => OpaqueStateBucket,
            _ => null,
        };

    /// <summary>Creates the sorter for <paramref name="name"/>. The match ignores case.</summary>
    /// <returns>False when the name is not known.</returns>
    public static bool TryCreate(string name, out IComparer<RenderCommand>? sorter)
    {
        if (string.Equals(name, None, StringComparison.OrdinalIgnoreCase))
        {
            sorter = null;
            return true;
        }

        if (string.Equals(name, NearToFar, StringComparison.OrdinalIgnoreCase))
        {
            sorter = new NearToFarRenderCommandSorter();
            return true;
        }

        if (string.Equals(name, FarToNear, StringComparison.OrdinalIgnoreCase))
        {
            sorter = new FarToNearRenderCommandSorter();
            return true;
        }

        if (string.Equals(name, OpaqueStateBucket, StringComparison.OrdinalIgnoreCase))
        {
            sorter = new OpaqueStateBucketRenderCommandSorter();
            return true;
        }

        sorter = null;
        return false;
    }
}

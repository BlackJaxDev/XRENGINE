using XREngine.Cutscenes;
using XREngine.Data;
using XREngine.Scene;

namespace XREngine.Editor.Cutscenes;

/// <summary>The inputs and limits of a residency manifest bake. See <see cref="CutsceneBakeTool"/>.</summary>
public sealed class CutsceneBakeOptions
{
    /// <summary>The default T0 budget: 64 MiB. The design estimate for 2000 textures and 400 mesh proxies is about 53 MB.</summary>
    public const long DefaultT0BudgetBytes = 64L * 1024L * 1024L;

    /// <summary>The default number of assets that a T0 report lists.</summary>
    public const int DefaultReportAssetCount = 10;

    /// <summary>Creates options.</summary>
    /// <param name="resolver">The object that finds and creates the bound scene nodes.</param>
    public CutsceneBakeOptions(ICutsceneBindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Resolver = resolver;
    }

    /// <summary>The object that finds and creates the bound scene nodes.</summary>
    public ICutsceneBindingResolver Resolver { get; }

    /// <summary>The nodes of the runtime slots, or null.</summary>
    public IReadOnlyDictionary<string, SceneNode>? RuntimeSlots { get; init; }

    /// <summary>
    /// The root nodes of the scene that the cutscene plays in, or null. The bake visits these hierarchies and the
    /// hierarchies of the bound nodes, so an object that no track binds is also in the manifest when a camera sees it.
    /// </summary>
    public IReadOnlyList<SceneNode>? SceneRoots { get; init; }

    /// <summary>The collector that finds the assets of each node, or null for <see cref="CutsceneRenderableAssetCollector"/>.</summary>
    public ICutsceneResidencyAssetCollector? Collector { get; init; }

    /// <summary>The screen coverage at or above which an asset needs T2. Below it, an asset on screen needs T1.</summary>
    public float TargetCoverage { get; init; } = CutsceneResidencyManifestBuilder.DefaultTargetCoverage;

    /// <summary>The largest allowed sum of the T0 sizes of the manifest assets, in bytes. It must be greater than zero.</summary>
    public long T0BudgetBytes { get; init; } = DefaultT0BudgetBytes;

    /// <summary>The number of largest assets that the T0 report lists.</summary>
    public int ReportAssetCount { get; init; } = DefaultReportAssetCount;

    /// <summary>Opens the streaming decoder of an audio clip file, or null for the default decoders.</summary>
    public Func<string, IStreamingAudioDecoder>? OpenAudioDecoder { get; init; }

    /// <summary>The list that receives the problems of the bake, or null.</summary>
    public List<CutsceneCompileDiagnostic>? Diagnostics { get; init; }
}

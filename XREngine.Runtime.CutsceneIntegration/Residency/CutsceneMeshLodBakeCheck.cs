using System.Text;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Finds the sub-meshes of a cutscene scene whose T0 data is not much smaller than the full mesh, and reports them in the
/// bake diagnostics. T0 of a sub-mesh is its coarsest LOD, so progressive mesh loading needs LODs.
/// </summary>
/// <remarks>
/// <para>
/// A sub-mesh with one LOD loads its full mesh at T0. Automatic LOD generation is off by default, also for the models that a
/// cutscene binds, and a simplification can also give one LOD. The check reports the count of such sub-meshes in one warning.
/// </para>
/// <para>
/// A simplification does not always reduce a mesh. A sub-mesh whose coarsest LOD has more than
/// <see cref="ProxyReductionThreshold"/> of the index count of LOD 0 gives one more warning, with the names of the first
/// <see cref="ListedSubMeshCount"/> such sub-meshes.
/// </para>
/// </remarks>
public static class CutsceneMeshLodBakeCheck
{
    /// <summary>The largest fraction of the LOD 0 index count that the coarsest LOD can have before the bake reports it.</summary>
    public const double ProxyReductionThreshold = 0.5;

    /// <summary>The number of sub-mesh names that the warning about proxies that did not reduce lists.</summary>
    public const int ListedSubMeshCount = 5;

    /// <summary>Checks the sub-meshes of the model components under the roots. Each sub-mesh counts one time.</summary>
    /// <param name="roots">The roots of the scene hierarchies of the cutscene.</param>
    /// <param name="diagnostics">The list that receives the warnings.</param>
    public static void Check(IReadOnlyList<SceneNode> roots, List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(diagnostics);

        HashSet<SubMesh> seen = new(ReferenceEqualityComparer.Instance);
        int singleLod = 0;
        List<string> notReduced = [];
        for (int r = 0; r < roots.Count; r++)
            Visit(roots[r], seen, ref singleLod, notReduced);

        if (singleLod > 0)
            diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1,
                $"{singleLod} sub-meshes of the cutscene have one LOD, so T0 loads their full mesh. Turn on LOD generation in the model import settings, or author LODs, to load a smaller proxy first."));

        if (notReduced.Count > 0)
        {
            StringBuilder names = new();
            for (int i = 0; i < notReduced.Count && i < ListedSubMeshCount; i++)
                names.Append(i == 0 ? string.Empty : ", ").Append('\'').Append(notReduced[i]).Append('\'');
            if (notReduced.Count > ListedSubMeshCount)
                names.Append(", and ").Append(notReduced.Count - ListedSubMeshCount).Append(" more");
            diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1,
                $"{notReduced.Count} sub-meshes of the cutscene have a lowest LOD with more than {ProxyReductionThreshold:P0} of the LOD 0 index count, so their T0 proxy is almost the full mesh: {names}."));
        }
    }

    private static void Visit(SceneNode node, HashSet<SubMesh> seen, ref int singleLod, List<string> notReduced)
    {
        var components = node.Components;
        for (int c = 0; c < components.Count; c++)
        {
            if (components[c] is not ModelComponent { Model: { } model })
                continue;

            var meshes = model.Meshes;
            for (int m = 0; m < meshes.Count; m++)
                if (meshes[m] is { } subMesh && seen.Add(subMesh))
                    Inspect(subMesh, ref singleLod, notReduced);
        }

        var children = node.Transform.Children;
        for (int i = 0; i < children.Count; i++)
            if (children[i]?.SceneNode is { } child)
                Visit(child, seen, ref singleLod, notReduced);
    }

    private static void Inspect(SubMesh subMesh, ref int singleLod, List<string> notReduced)
    {
        SubMeshLOD? first = null;
        SubMeshLOD? last = null;
        int count = 0;
        foreach (SubMeshLOD lod in subMesh.LODs)
        {
            first ??= lod;
            last = lod;
            count++;
        }

        if (count == 0)
            return;
        if (count == 1)
        {
            singleLod++;
            return;
        }

        long fullIndices = GetIndexCount(first!);
        long proxyIndices = GetIndexCount(last!);
        if (fullIndices > 0 && proxyIndices > fullIndices * ProxyReductionThreshold)
            notReduced.Add(string.IsNullOrWhiteSpace(subMesh.Name) ? subMesh.ID.ToString("N") : subMesh.Name);
    }

    private static long GetIndexCount(SubMeshLOD lod)
        => lod.CookedDescriptor?.IndexCount ?? (long)(lod.Mesh?.IndexCount ?? 0);
}

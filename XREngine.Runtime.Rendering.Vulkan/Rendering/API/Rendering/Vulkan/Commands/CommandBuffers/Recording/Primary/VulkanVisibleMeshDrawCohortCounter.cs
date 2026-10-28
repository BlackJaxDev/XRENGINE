using System.Collections.Generic;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Counts the mesh draws and the unique materials in a recorded primary
/// operation stream. The set is reused for each count, so a warm count does
/// not allocate. A lock serializes counts because more than one primary can
/// record at the same time.
/// </summary>
internal static class VulkanVisibleMeshDrawCohortCounter
{
    private static readonly HashSet<XRMaterial> UniqueMaterials =
        new(ReferenceEqualityComparer.Instance);
    private static readonly object CountLock = new();

    /// <summary>
    /// Counts the mesh draw operations in <paramref name="ops"/> and the
    /// distinct materials that they use. A draw uses its material override
    /// when it has one, and otherwise the material of its mesh renderer.
    /// </summary>
    public static void Count(
        in FrameOperationSequence ops,
        out int visibleDrawCount,
        out int uniqueMaterialCount)
    {
        visibleDrawCount = 0;
        uniqueMaterialCount = 0;
        int length = ops.Length;
        if (length == 0)
            return;

        lock (CountLock)
        {
            UniqueMaterials.Clear();
            for (int i = 0; i < length; i++)
            {
                if (ops.GetHeader(i).OpCode != EVulkanPrimaryPlanNodeKind.MeshDraw)
                    continue;

                visibleDrawCount++;
                PendingMeshDraw draw = ops.GetMeshDraw(i).Draw;
                XRMaterial? material =
                    draw.MaterialOverride ?? draw.Renderer.MeshRenderer.Material;
                if (material is not null)
                    UniqueMaterials.Add(material);
            }

            uniqueMaterialCount = UniqueMaterials.Count;
            UniqueMaterials.Clear();
        }
    }
}

using System.IO;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Diagnostics;
using XREngine.Rendering;

namespace XREngine.Scene.Importers;

[XR3rdPartyExtensions(typeof(XRDefault3rdPartyImportOptions), "mat")]
public sealed class UnityMaterialAsset : XRMaterial
{
    public override bool Load3rdParty(string filePath)
        => Import3rdParty(filePath, null);

    public override bool Import3rdParty(string filePath, object? importOptions)
    {
        UnityMaterialImportResult result = UnityMaterialImporter.ImportWithReport(filePath);
        foreach (string warning in result.Warnings)
            Debug.LogWarning(warning);

        if (result.Material is not XRMaterial imported)
            return false;

        CopyFrom(imported);
        OriginalPath = filePath;
        OriginalLastWriteTimeUtc = File.Exists(filePath)
            ? File.GetLastWriteTimeUtc(filePath)
            : null;
        return true;
    }

    private void CopyFrom(XRMaterial imported)
    {
        Name = imported.Name;
        RenderPass = imported.RenderPass;
        RenderOptions = imported.RenderOptions;
        Parameters = [.. imported.Parameters];
        Textures = [.. imported.Textures];
        Shaders = [.. imported.Shaders];
        UberAuthoredState = imported.UberAuthoredState;
        BillboardMode = imported.BillboardMode;
        TransparencyMode = imported.TransparencyMode;
        AlphaCutoff = imported.AlphaCutoff;
        TransparentSortPriority = imported.TransparentSortPriority;
        TransparentTechniqueOverride = imported.TransparentTechniqueOverride;
    }
}

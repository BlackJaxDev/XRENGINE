using System.IO;
using XREngine.Core.Files;
using XREngine.Core.Attributes;
using XREngine.Data;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

[XR3rdPartyExtensions(typeof(XRDefault3rdPartyImportOptions), "mat")]
[XRTypeRedirect("XREngine.Scene.Importers.UnityMaterialAsset")]
public sealed class SerializedMaterialAsset : XRMaterial
{
    public MaterialConversionReport? LastConversionReport { get; set; }
    public MaterialImportedStateSnapshot? ImportedState { get; set; }
    public MaterialLocalOverrideSet LocalOverrides { get; set; } = new();

    public override bool Load3rdParty(string filePath)
        => Import3rdParty(filePath, null);

    public override bool Import3rdParty(string filePath, object? importOptions)
    {
        SerializedMaterialImportResult result = SerializedMaterialImporter.ImportWithReport(filePath);
        foreach (string warning in result.Warnings)
            Debug.LogWarning(warning);

        if (result.Material is not XRMaterial imported)
            return false;

        CopyFrom(imported);
        LastConversionReport = result.ConversionReport;
        if (result.ConversionReport is not null)
        {
            ImportedState = MaterialImportedStateSnapshot.Capture(imported, result.ConversionReport);
            LocalOverrides = new();
            MaterialConversionReportRegistry.Instance.Set(this, result.ConversionReport);
        }
        OriginalPath = Path.GetFullPath(filePath);
        OriginalLastWriteTimeUtc = File.Exists(filePath)
            ? File.GetLastWriteTimeUtc(filePath)
            : null;
        return true;
    }

    internal void CopyFrom(XRMaterial imported)
    {
        Name = imported.Name;
        RenderPass = imported.RenderPass;
        RenderOptions = imported.RenderOptions;
        Parameters = [.. imported.Parameters];
        Textures = [.. imported.Textures];
        Shaders = [.. imported.Shaders];
        UberAuthoredState = imported.UberAuthoredState;
        PassSet = imported.PassSet;
        BillboardMode = imported.BillboardMode;
        TransparencyMode = imported.TransparencyMode;
        AlphaCutoff = imported.AlphaCutoff;
        TransparentSortPriority = imported.TransparentSortPriority;
        TransparentTechniqueOverride = imported.TransparentTechniqueOverride;
    }
}

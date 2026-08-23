using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using XREngine.Core.Files;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>
    /// Reloads the cooked meshlet payload for an already resident mesh. Geometry
    /// replacement still belongs to the model reimport path; accepting a payload
    /// whose source geometry differs would make the atlas and descriptor table
    /// disagree, so the owner validation below rejects it explicitly.
    /// </summary>
    [RequiresUnreferencedCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    public override void Reload(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        byte[] bytes = File.ReadAllBytes(filePath);
        if (RuntimeCookedBinarySerializer.Deserialize(typeof(XRMesh), bytes) is not XRMesh loaded)
            throw new InvalidDataException($"Cooked mesh reload returned no XRMesh for '{filePath}'.");

        try
        {
            if (loaded.MeshletPayload is { } payload)
                AttachValidatedCookedMeshletPayload(payload);
            else
                MeshletPayload = null;
        }
        finally
        {
            loaded.Destroy(now: true);
        }
    }
}

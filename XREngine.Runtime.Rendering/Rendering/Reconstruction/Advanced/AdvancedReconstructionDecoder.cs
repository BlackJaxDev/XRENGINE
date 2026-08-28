using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// CPU reference for the shader's generation-checked visibility decode.
/// </summary>
public static class AdvancedReconstructionDecoder
{
    public static bool TryResolve(
        in AdvancedVisibilityEncodedSurface encoded,
        AdvancedSharedGpuSceneDatabase database,
        ReadOnlySpan<AdvancedViewRecord> views,
        out AdvancedReconstructionResolvedRecords resolved,
        out EAdvancedReconstructionInvalidReason invalidReason)
    {
        ArgumentNullException.ThrowIfNull(database);
        resolved = default;
        invalidReason =
            EAdvancedReconstructionInvalidReason.BackgroundOrInvalidPayload;
        if (!encoded.IsValid || database.PublicationFaulted)
            return false;

        AdvancedVisibilityDecodedMetadata metadata = encoded.Metadata.Decode();
        if (metadata.PayloadVersion !=
            AdvancedVisibilityBufferContract.PayloadVersion)
        {
            invalidReason = EAdvancedReconstructionInvalidReason.PayloadVersion;
            return false;
        }

        AdvancedVisibilityLogicalSurface surface = encoded.DecodeLogical();
        if (!database.HandleLookups.TryResolveStableIndex(
                surface.DrawTableIndex,
                database.HandleLookups.Layout.Draws,
                out _,
                out uint drawGeneration))
        {
            invalidReason =
                EAdvancedReconstructionInvalidReason.DrawNotResident;
            return false;
        }

        AdvancedGpuHandle drawHandle = new(
            surface.DrawTableIndex,
            drawGeneration);
        if (!database.Scene.Draws.TryGet(
                drawHandle,
                out AdvancedDrawRecord draw) ||
            !database.Scene.Instances.TryGet(
                draw.Instance,
                out AdvancedInstanceRecord instance) ||
            !database.Scene.Transforms.TryGet(
                draw.CurrentTransform,
                out AdvancedTransformRecord currentTransform) ||
            !database.Scene.Transforms.TryGet(
                draw.PreviousTransform,
                out AdvancedTransformRecord previousTransform))
        {
            invalidReason =
                EAdvancedReconstructionInvalidReason.StaleDependencyGeneration;
            return false;
        }
        if (!database.Scene.Geometry.TryGet(
                draw.Geometry,
                out AdvancedGeometryRecord geometry))
        {
            invalidReason =
                EAdvancedReconstructionInvalidReason.GeometryMissing;
            return false;
        }
        if (!geometry.IsResident &&
            !database.Scene.Geometry.TryResolveVisibilityGeometry(
                draw.Geometry,
                out geometry))
        {
            invalidReason =
                EAdvancedReconstructionInvalidReason.GeometryNonResident;
            return false;
        }
        if (!PrimitiveFits(geometry, surface.Primitive))
        {
            invalidReason =
                EAdvancedReconstructionInvalidReason.PrimitiveOutOfRange;
            return false;
        }
        AdvancedDeformationRecord deformation = default;
        bool hasDeformation = draw.Deformation.IsValid;
        if (hasDeformation &&
            !database.Scene.Deformations.TryGet(
                draw.Deformation,
                out deformation))
        {
            invalidReason =
                EAdvancedReconstructionInvalidReason.StaleDependencyGeneration;
            return false;
        }
        if (!database.Materials.Materials.TryGet(
                draw.Material,
                out AdvancedMaterialRecord material))
        {
            invalidReason =
                EAdvancedReconstructionInvalidReason.MaterialNotResident;
            return false;
        }

        AdvancedGpuHandle kernelHandle = new(
            material.ShadingKernelId,
            material.ShadingKernelGeneration);
        if (!database.Materials.Kernels.TryGet(
                kernelHandle,
                out AdvancedShadingKernelRecord kernel))
        {
            invalidReason =
                EAdvancedReconstructionInvalidReason.ShadingKernelNotResident;
            return false;
        }
        if (surface.ViewIndex >= (uint)views.Length)
        {
            invalidReason =
                EAdvancedReconstructionInvalidReason.ViewOutOfRange;
            return false;
        }

        resolved = new AdvancedReconstructionResolvedRecords(
            drawHandle,
            surface,
            draw,
            instance,
            geometry,
            material,
            kernel,
            currentTransform,
            previousTransform,
            deformation,
            views[checked((int)surface.ViewIndex)],
            hasDeformation);
        invalidReason = EAdvancedReconstructionInvalidReason.None;
        return true;
    }

    private static bool PrimitiveFits(
        in AdvancedGeometryRecord geometry,
        in AdvancedVisibilityDecodedPrimitive primitive)
    {
        if (!primitive.IsValid)
            return false;

        if (primitive.IsMeshletOrCluster)
        {
            if (primitive.MeshletOrClusterIndex < geometry.MeshletFirst)
                return false;
            uint relative =
                primitive.MeshletOrClusterIndex - geometry.MeshletFirst;
            return relative < geometry.MeshletCount &&
                   primitive.LocalPrimitiveIndex <=
                       AdvancedVisibilityPrimitiveIdentity.MaximumLocalPrimitiveIndex;
        }

        ulong firstIndex = (ulong)primitive.PrimitiveIndex * 3UL;
        ulong geometryStart = geometry.IndexData.ElementOffset;
        ulong geometryEnd =
            geometryStart + geometry.IndexData.ElementCount;
        return firstIndex >= geometryStart &&
               firstIndex + 2UL < geometryEnd;
    }
}

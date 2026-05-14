using XREngine.Extensions;
using ImageMagick;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    // ===================== Indirect + Pipeline Abstraction (OpenGL) =====================
    public override void BindVAOForRenderer(XRMeshRenderer.BaseVersion? version)
    {
        if (version is null)
        {
            UnbindMeshRenderer();
            return;
        }
        var glMesh = GenericToAPI<GLMeshRenderer>(version);
        BindMeshRenderer(glMesh);
    }

    public override bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version)
    {
        var glMesh = version != null ? GenericToAPI<GLMeshRenderer>(version) : ActiveMeshRenderer;
        if (glMesh is null)
            return false;
        return (glMesh.TriangleIndicesBuffer?.Data?.ElementCount > 0) ||
               (glMesh.LineIndicesBuffer?.Data?.ElementCount > 0) ||
               (glMesh.PointIndicesBuffer?.Data?.ElementCount > 0);
    }

    public override bool TryGetIndexBufferInfo(XRMeshRenderer.BaseVersion? version, out IndexSize indexElementSize, out uint indexCount)
    {
        indexElementSize = IndexSize.FourBytes;
        indexCount = 0;

        var glMesh = version != null ? GenericToAPI<GLMeshRenderer>(version) : ActiveMeshRenderer;
        if (glMesh is null)
            return false;

        // Check buffers in priority order: triangles, lines, points
        if (glMesh.TriangleIndicesBuffer?.Data?.ElementCount > 0)
        {
            indexElementSize = glMesh.TrianglesElementType;
            indexCount = glMesh.TriangleIndicesBuffer.Data.ElementCount;
            return true;
        }

        if (glMesh.LineIndicesBuffer?.Data?.ElementCount > 0)
        {
            indexElementSize = glMesh.LineIndicesElementType;
            indexCount = glMesh.LineIndicesBuffer.Data.ElementCount;
            return true;
        }

        if (glMesh.PointIndicesBuffer?.Data?.ElementCount > 0)
        {
            indexElementSize = glMesh.PointIndicesElementType;
            indexCount = glMesh.PointIndicesBuffer.Data.ElementCount;
            return true;
        }

        return false;
    }

    public override bool TrySyncMeshRendererIndexBuffer(XRMeshRenderer meshRenderer, XRDataBuffer indexBuffer, IndexSize elementSize)
    {
        var version = meshRenderer.GetDefaultVersion();
        var glMesh = GenericToAPI<GLMeshRenderer>(version);
        if (glMesh is null)
            return false;

        var glIndexBuffer = GenericToAPI<GLDataBuffer>(indexBuffer);
        if (glIndexBuffer is null)
            return false;

        glMesh.SetTriangleIndexBuffer(glIndexBuffer, elementSize);
        Api.VertexArrayElementBuffer(glMesh.BindingId, glIndexBuffer.BindingId);
        return true;
    }

    public override void BindDrawIndirectBuffer(XRDataBuffer buffer)
    {
        var glBuf = GenericToAPI<GLDataBuffer>(buffer);
        if (glBuf is null)
            return;

        glBuf.EnsureStorageAllocatedForGpuCopy();
        Api.BindBuffer(GLEnum.DrawIndirectBuffer, glBuf.BindingId);
    }

    public override void UnbindDrawIndirectBuffer()
    {
        Api.BindBuffer(GLEnum.DrawIndirectBuffer, 0);
    }

    public override void BindParameterBuffer(XRDataBuffer buffer)
    {
        var glBuf = GenericToAPI<GLDataBuffer>(buffer);
        if (glBuf is null)
            return;

        glBuf.EnsureStorageAllocatedForGpuCopy();
        _boundParameterBufferForStats = buffer;

        const GLEnum GL_PARAMETER_BUFFER = (GLEnum)0x80EE;
        Api.BindBuffer(GL_PARAMETER_BUFFER, glBuf.BindingId);
    }

    public override void UnbindParameterBuffer()
    {
        const GLEnum GL_PARAMETER_BUFFER = (GLEnum)0x80EE;
        Api.BindBuffer(GL_PARAMETER_BUFFER, 0);
        _boundParameterBufferForStats = null;
    }

    private (PrimitiveType prim, DrawElementsType elem) GetActivePrimitiveAndElementType()
    {
        PrimitiveType primitiveType = PrimitiveType.Triangles;
        DrawElementsType elementType = DrawElementsType.UnsignedInt;
        var renderer = ActiveMeshRenderer;
        if (renderer is not null)
        {
            if (renderer.UsesPatchTopology && renderer.TriangleIndicesBuffer is not null)
            {
                primitiveType = PrimitiveType.Patches;
                elementType = renderer.TrianglesElementType switch
                {
                    IndexSize.Byte => DrawElementsType.UnsignedByte,
                    IndexSize.TwoBytes => DrawElementsType.UnsignedShort,
                    _ => DrawElementsType.UnsignedInt,
                };
            }
            else if (renderer.TriangleIndicesBuffer is not null)
            {
                primitiveType = PrimitiveType.Triangles;
                elementType = renderer.TrianglesElementType switch
                {
                    IndexSize.Byte => DrawElementsType.UnsignedByte,
                    IndexSize.TwoBytes => DrawElementsType.UnsignedShort,
                    _ => DrawElementsType.UnsignedInt,
                };
            }
            else if (renderer.LineIndicesBuffer is not null)
            {
                primitiveType = PrimitiveType.Lines;
                elementType = renderer.LineIndicesElementType switch
                {
                    IndexSize.Byte => DrawElementsType.UnsignedByte,
                    IndexSize.TwoBytes => DrawElementsType.UnsignedShort,
                    _ => DrawElementsType.UnsignedInt,
                };
            }
            else if (renderer.PointIndicesBuffer is not null)
            {
                primitiveType = PrimitiveType.Points;
                elementType = renderer.PointIndicesElementType switch
                {
                    IndexSize.Byte => DrawElementsType.UnsignedByte,
                    IndexSize.TwoBytes => DrawElementsType.UnsignedShort,
                    _ => DrawElementsType.UnsignedInt,
                };
            }
        }
        return (primitiveType, elementType);
    }

    private void ApplyPatchParameters(GLMeshRenderer? renderer)
    {
        if (!(renderer?.UsesPatchTopology ?? false))
            return;

        Api.PatchParameter(GLEnum.PatchVertices, renderer.PatchVertexCount);
    }

    public override unsafe void MultiDrawElementsIndirect(uint drawCount, uint stride)
    {
        var (prim, elem) = GetActivePrimitiveAndElementType();
        ApplyPatchParameters(ActiveMeshRenderer);
        Api.MultiDrawElementsIndirect(prim, elem, null, drawCount, stride);
        RuntimeEngine.Rendering.Stats.IncrementMultiDrawCalls();
        RuntimeEngine.Rendering.Stats.IncrementDrawCalls((int)drawCount);
    }

    public override unsafe void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset)
    {
        var (prim, elem) = GetActivePrimitiveAndElementType();
        ApplyPatchParameters(ActiveMeshRenderer);
        Api.MultiDrawElementsIndirect(prim, elem, (void*)byteOffset, drawCount, stride);
        RuntimeEngine.Rendering.Stats.IncrementMultiDrawCalls();
        RuntimeEngine.Rendering.Stats.IncrementDrawCalls((int)drawCount);
    }

    public override unsafe void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset, nuint countByteOffset)
    {
        var (prim, elem) = GetActivePrimitiveAndElementType();
        ApplyPatchParameters(ActiveMeshRenderer);
        Api.MultiDrawElementsIndirectCount(prim, elem, (void*)byteOffset, (IntPtr)countByteOffset, maxDrawCount, stride);
        RuntimeEngine.Rendering.Stats.IncrementMultiDrawCalls();
        QueueBoundParameterDrawCountReadback(countByteOffset);
    }

    public unsafe void MultiDrawElementsIndirectCountNVBindless(uint drawCountOffset, uint maxDrawCount, uint stride)
    {
        var (prim, elem) = GetActivePrimitiveAndElementType();
        ApplyPatchParameters(ActiveMeshRenderer);
        NVBindlessMultiDrawIndirectCount?.MultiDrawElementsIndirectBindlessCount(
            prim,
            elem,
            null,
            drawCountOffset,
            maxDrawCount,
            stride,
            1);
        RuntimeEngine.Rendering.Stats.IncrementMultiDrawCalls();
        QueueBoundParameterDrawCountReadback(drawCountOffset);
    }

    public unsafe void MultiDrawElementsIndirectCount(uint drawCountOffset, uint maxDrawCount, uint stride)
    {
        var (primitiveType, elementType) = GetActivePrimitiveAndElementType();
        ApplyPatchParameters(ActiveMeshRenderer);
        // Requires GL 4.6 or ARB_indirect_parameters
        Api.MultiDrawElementsIndirectCount(
            primitiveType,
            elementType,
            null,
            (nint)drawCountOffset,
            maxDrawCount,
            stride);
        RuntimeEngine.Rendering.Stats.IncrementMultiDrawCalls();
        QueueBoundParameterDrawCountReadback(drawCountOffset);
    }

    //public unsafe void MultiDrawElementsIndirectCount(uint maxCommands, uint stride)
    //{
    //    //Get primitive type and element type from currently bound renderer
    //    PrimitiveType primitiveType = PrimitiveType.Triangles;
    //    DrawElementsType elementType = DrawElementsType.UnsignedInt;
    //    var renderer = ActiveMeshRenderer;
    //    if (renderer is not null)
    //    {
    //        if (renderer.TriangleIndicesBuffer is not null)
    //        {
    //            primitiveType = PrimitiveType.Triangles;
    //            elementType = ToDrawElementsType(renderer.TrianglesElementType);
    //        }
    //        else if (renderer.LineIndicesBuffer is not null)
    //        {
    //            primitiveType = PrimitiveType.Lines;
    //            elementType = ToDrawElementsType(renderer.LineIndicesElementType);
    //        }
    //        else if (renderer.PointIndicesBuffer is not null)
    //        {
    //            primitiveType = PrimitiveType.Points;
    //            elementType = ToDrawElementsType(renderer.PointIndicesElementType);
    //        }
    //    }

    //    // Requires GL 4.6 or ARB_indirect_parameters
    //    Api.MultiDrawElementsIndirectCount(
    //        primitiveType,
    //        elementType,
    //        null,
    //        IntPtr.Zero,
    //        maxCommands,
    //        stride);
    //}

    //public unsafe void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset)
    //{
    //    // Determine primitive and element types from currently bound renderer (ActiveMeshRenderer)
    //    PrimitiveType primitiveType = PrimitiveType.Triangles;
    //    DrawElementsType elementType = DrawElementsType.UnsignedInt;
    //    var renderer = ActiveMeshRenderer;
    //    if (renderer is not null)
    //    {
    //        if (renderer.TriangleIndicesBuffer is not null)
    //        {
    //            primitiveType = PrimitiveType.Triangles;
    //            elementType = ToDrawElementsType(renderer.TrianglesElementType);
    //        }
    //        else if (renderer.LineIndicesBuffer is not null)
    //        {
    //            primitiveType = PrimitiveType.Lines;
    //            elementType = ToDrawElementsType(renderer.LineIndicesElementType);
    //        }
    //        else if (renderer.PointIndicesBuffer is not null)
    //        {
    //            primitiveType = PrimitiveType.Points;
    //            elementType = ToDrawElementsType(renderer.PointIndicesElementType);
    //        }
    //    }

    //    Api.MultiDrawElementsIndirect(
    //        primitiveType,
    //        elementType,
    //        (void*)byteOffset,
    //        drawCount,
    //        stride);
    //}

    public override bool SupportsIndirectCountDraw()
    {
        try
        {
            string? verStr = Version;
            if (!string.IsNullOrWhiteSpace(verStr))
            {
                var parts = verStr.Split(' ');
                var num = parts[0].Split('.');
                if (num.Length >= 2 && int.TryParse(num[0], out int maj) && int.TryParse(num[1], out int min))
                {
                    if (maj > 4 || (maj == 4 && min >= 6))
                        return true;
                }
            }
        }
        catch { }
        return Api.IsExtensionPresent("GL_ARB_indirect_parameters");
    }

    private static DrawElementsType ToDrawElementsType(IndexSize type) => type switch
    {
        IndexSize.Byte => DrawElementsType.UnsignedByte,
        IndexSize.TwoBytes => DrawElementsType.UnsignedShort,
        IndexSize.FourBytes => DrawElementsType.UnsignedInt,
        _ => DrawElementsType.UnsignedInt,
    };
}

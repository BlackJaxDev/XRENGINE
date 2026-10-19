using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMeshRenderer
{
    internal const uint UdimTriangleCoverageBinding = 40u;
    private UdimTriangleCoverage? _udimTriangleCoverage;
    private UdimTriangleCoveragePublisher? _udimTriangleCoveragePublisher;
    private bool _udimTriangleCoveragePublisherAttached;

    /// <summary>Prepares exact source-corner tile masks for an authored triangle draw.</summary>
    internal bool TryPrepareUdimTriangleCoverage(XRMesh? mesh, XRMaterial material, bool publishInactive, out string reason)
    {
        ValidateResourcePublicationLease();
        XRMaterial source = material.ShadowBindingSourceMaterial ?? material;
        bool required = NativeUberMaterialSourceEncoder.RequiresPrimitiveTileDiscard(source);
        if (!required)
        {
            if (publishInactive)
                SelectUdimTriangleCoverage(null, 0);
            else if (_udimTriangleCoveragePublisherAttached && _udimTriangleCoveragePublisher is { } existing)
            {
                BindingPublishers.Remove(existing);
                _udimTriangleCoveragePublisherAttached = false;
            }
            reason = string.Empty;
            return true;
        }

        if (mesh is null || mesh.IsDestroyQueued || mesh.IsDestroyed ||
            mesh.Type != EPrimitiveType.Triangles ||
            material.TessCtrlShaders.Count != 0 || material.TessEvalShaders.Count != 0 ||
            (material.GeometryShaders.Count != 0 && !HasTrustedUdimShadowGeometryShader(material)) ||
            material.MeshShaders.Count != 0 ||
            material.TaskShaders.Count != 0)
        {
            reason = "Vertex-mode UDIM discard requires an original triangle-list draw without mixed or topology-changing stages.";
            return false;
        }

        long revision = mesh.GeometryRevision;
        if (_udimTriangleCoverage is { } cached &&
            ReferenceEquals(cached.Mesh, mesh) && cached.GeometryRevision == revision)
        {
            SelectUdimTriangleCoverage(cached.Buffer, cached.TriangleCount);
            reason = string.Empty;
            return true;
        }

        XRMesh.BufferCollection buffers = mesh.Buffers;
        if (!buffers.TryAcquireGeometryReadScope(mesh, out XRMesh.BufferCollection.GeometryReadScope scope))
        {
            reason = "Source triangle geometry is busy or unavailable for UDIM coverage.";
            return false;
        }
        using (scope)
        {
            if (mesh.Type != EPrimitiveType.Triangles ||
                mesh.Triangles is not { Count: > 0 } triangles ||
                mesh.Lines is { Count: > 0 } || mesh.Points is { Count: > 0 })
            {
                reason = "Vertex-mode UDIM discard requires only source triangles.";
                return false;
            }
            UdimTriangleMask[] masks = new UdimTriangleMask[triangles.Count];
            for (int index = 0; index < triangles.Count; index++)
            {
                IndexTriangle triangle = triangles[index];
                if (!TryGetCornerMasks(mesh, triangle.Point0, out UdimTriangleMask first) ||
                    !TryGetCornerMasks(mesh, triangle.Point1, out UdimTriangleMask second) ||
                    !TryGetCornerMasks(mesh, triangle.Point2, out UdimTriangleMask third))
                {
                    reason = "Vertex-mode UDIM discard found an invalid source vertex or non-finite UV.";
                    return false;
                }
                masks[index] = new UdimTriangleMask(
                    first.Uv0 | second.Uv0 | third.Uv0,
                    first.Uv1 | second.Uv1 | third.Uv1,
                    first.Uv2 | second.Uv2 | third.Uv2,
                    first.Uv3 | second.Uv3 | third.Uv3);
            }
            if (mesh.GeometryRevision != revision || !ReferenceEquals(mesh.Triangles, triangles))
            {
                reason = "Source triangle geometry changed while UDIM coverage was prepared.";
                return false;
            }

            XRDataBuffer buffer = new("SourceUdimTriangleCoverage", EBufferTarget.ShaderStorageBuffer, false)
            {
                Usage = EBufferUsage.StaticDraw,
                Resizable = false,
                DisposeOnPush = false,
            };
            buffer.SetDataRaw<UdimTriangleMask>(masks);
            buffer.PushData();
            _udimTriangleCoverage?.Buffer.RetireBindingStorage();
            _udimTriangleCoverage = new UdimTriangleCoverage(mesh, revision, triangles.Count, buffer);
            SelectUdimTriangleCoverage(buffer, triangles.Count);
            reason = string.Empty;
            return true;
        }
    }

    private static bool HasTrustedUdimShadowGeometryShader(XRMaterial material)
    {
        if (material.ShadowBindingSourceMaterial is null || material.GeometryShaders.Count != 1)
            return false;
        string? engineShader = material.PointShadowMaterialKind switch
        {
            EPointShadowMaterialKind.GeometryShader => "PointLightShadowDepth.gs",
            EPointShadowMaterialKind.AtlasGeometryShader => "PointLightAtlasShadowDepth.gs",
            _ => material.DirectionalCascadeShadowMaterialKind switch
            {
                EDirectionalCascadeShadowMaterialKind.GeometryShader => "DirectionalCascadeShadowDepth.gs",
                EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader => "DirectionalCascadeAtlasShadowDepth.gs",
                _ => null,
            },
        };
        return engineShader is not null && ReferenceEquals(
            material.GeometryShaders[0], XRShader.EngineShader(engineShader, EShaderType.Geometry));
    }

    private static bool TryGetCornerMasks(XRMesh mesh, int vertex, out UdimTriangleMask masks)
    {
        masks = default;
        if ((uint)vertex >= (uint)mesh.VertexCount)
            return false;
        for (uint channel = 0; channel < 4; channel++)
        {
            Vector2 uv = channel < mesh.TexCoordCount ? mesh.GetTexCoord((uint)vertex, channel) : Vector2.Zero;
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                return false;
            uint bit = uv.X >= 0f && uv.X < 4f && uv.Y >= 0f && uv.Y < 4f
                ? 1u << ((int)MathF.Floor(uv.X) + 4 * (int)MathF.Floor(uv.Y))
                : 0u;
            switch (channel)
            {
                case 0: masks.Uv0 = bit; break;
                case 1: masks.Uv1 = bit; break;
                case 2: masks.Uv2 = bit; break;
                default: masks.Uv3 = bit; break;
            }
        }
        return true;
    }

    private void SelectUdimTriangleCoverage(XRDataBuffer? buffer, int triangleCount)
    {
        _udimTriangleCoveragePublisher ??= CreateUdimTriangleCoveragePublisher();
        if (!_udimTriangleCoveragePublisherAttached)
        {
            BindingPublishers.Add(_udimTriangleCoveragePublisher);
            _udimTriangleCoveragePublisherAttached = true;
        }
        _udimTriangleCoveragePublisher.Select(buffer, triangleCount);
    }

    private UdimTriangleCoveragePublisher CreateUdimTriangleCoveragePublisher()
    {
        UdimTriangleCoveragePublisher publisher = new();
        return publisher;
    }

    private void DestroyUdimTriangleCoverage()
    {
        _udimTriangleCoverage?.Buffer.RetireBindingStorage();
        _udimTriangleCoverage = null;
        if (_udimTriangleCoveragePublisher is { } publisher)
        {
            if (_udimTriangleCoveragePublisherAttached)
                BindingPublishers.Remove(publisher);
            publisher.Dispose();
            _udimTriangleCoveragePublisher = null;
            _udimTriangleCoveragePublisherAttached = false;
        }
    }

    private readonly record struct UdimTriangleCoverage(XRMesh Mesh, long GeometryRevision, int TriangleCount, XRDataBuffer Buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct UdimTriangleMask(uint uv0, uint uv1, uint uv2, uint uv3)
    {
        public uint Uv0 = uv0;
        public uint Uv1 = uv1;
        public uint Uv2 = uv2;
        public uint Uv3 = uv3;
    }

    private sealed class UdimTriangleCoveragePublisher : IRenderResourceBindingPublisher, IDisposable
    {
        private readonly XRDataBuffer _dummy = CreateDummy();
        private XRDataBuffer? _selected;
        private int _triangleCount;
        private ulong _generation = 1;

        public ERenderBindingFrequency Frequency => ERenderBindingFrequency.Object;
        public ulong Generation => _generation;
        public ulong ResourceGeneration => _generation;
        public bool RequiresReadyDescriptorResources => true;

        public void Select(XRDataBuffer? buffer, int triangleCount)
        {
            if (ReferenceEquals(_selected, buffer) && _triangleCount == triangleCount)
                return;
            _selected = buffer;
            _triangleCount = triangleCount;
            unchecked { _generation++; }
            if (_generation == 0) _generation = 1;
        }

        public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => materialProgram.Uniform("u_SourceUdimTriangleCount", _triangleCount);

        public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => materialProgram.BindBuffer(_selected ?? _dummy, UdimTriangleCoverageBinding);

        public void Dispose() => _dummy.RetireBindingStorage();

        private static XRDataBuffer CreateDummy()
        {
            XRDataBuffer buffer = new("SourceUdimTriangleCoverage.Dummy", EBufferTarget.ShaderStorageBuffer, false)
            {
                Usage = EBufferUsage.StaticDraw,
                Resizable = false,
                DisposeOnPush = false,
            };
            Span<UdimTriangleMask> zero = stackalloc UdimTriangleMask[1];
            zero.Clear();
            buffer.SetDataRaw<UdimTriangleMask>(zero);
            buffer.PushData();
            return buffer;
        }
    }
}
